using Nornia.Desktop.Services;

namespace Nornia.Tests;

/// <summary>Covers the workspace structural watcher (explorer auto-refresh feed): debouncing,
/// path collection, rename handling, idempotent attach and detach semantics. Mirrors
/// <see cref="GitRepositoryWatcherTests"/> — a real FileSystemWatcher plus the internal event seam.</summary>
public sealed class WorkspaceFileWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nornia-wswatcher-{Guid.NewGuid():N}");

    public WorkspaceFileWatcherTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp directory.
        }
    }

    /// <summary>Runs marshaled callbacks inline so the tests stay deterministic without a running
    /// WPF dispatcher.</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void BeginInvoke(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private WorkspaceFileWatcher CreateWatcher(TimeSpan? debounce = null) =>
        new(new InlineDispatcher(), debounce ?? TimeSpan.FromMilliseconds(50));

    [Fact]
    public void DefaultDebounceInterval_MatchesScmWatcher() =>
        Assert.Equal(TimeSpan.FromMilliseconds(200), WorkspaceFileWatcher.DefaultDebounceInterval);

    [Fact]
    public async Task BurstOfEvents_RaisesSingleDebouncedNotificationWithAllPaths()
    {
        using var watcher = CreateWatcher();
        var count = 0;
        WorkspaceFilesChangedEventArgs? captured = null;
        watcher.FilesChanged += (_, args) =>
        {
            Interlocked.Increment(ref count);
            captured = args;
        };
        watcher.Attach(_root);

        for (var i = 0; i < 5; i++)
        {
            watcher.HandleFileSystemEvent(Path.Combine(_root, $"src\\file{i}.cs"));
        }

        Assert.Equal(0, count); // still inside the debounce window
        await Task.Delay(400);

        Assert.Equal(1, count); // the whole burst collapsed into one notification
        Assert.Equal(Path.GetFullPath(_root), captured!.RootPath);
        Assert.Equal(5, captured.ChangedPaths.Count);
    }

    [Fact]
    public async Task FileSystemCreatesAndDeletes_AreReportedAfterDebounce()
    {
        using var watcher = CreateWatcher(debounce: TimeSpan.FromMilliseconds(300));
        var raises = new List<IReadOnlySet<string>>();
        watcher.FilesChanged += (_, args) => raises.Add(args.ChangedPaths);
        watcher.Attach(_root);

        File.WriteAllText(Path.Combine(_root, "burst0.txt"), "content");
        File.WriteAllText(Path.Combine(_root, "burst1.txt"), "content");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (raises.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        await Task.Delay(700); // settle: the burst must not fragment into further raises
        Assert.Single(raises);
        Assert.Contains(raises[0], p => p.EndsWith("burst0.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(raises[0], p => p.EndsWith("burst1.txt", StringComparison.OrdinalIgnoreCase));

        File.Delete(Path.Combine(_root, "burst0.txt"));
        var before = raises.Count;
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (raises.Count <= before && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Contains(raises[^1], p => p.EndsWith("burst0.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DirectoryRename_ReportsOldAndNewName()
    {
        using var watcher = CreateWatcher(debounce: TimeSpan.FromMilliseconds(300));
        var raises = new List<IReadOnlySet<string>>();
        watcher.FilesChanged += (_, args) => raises.Add(args.ChangedPaths);
        watcher.Attach(_root);

        var oldDir = Path.Combine(_root, "before");
        var newDir = Path.Combine(_root, "after");
        Directory.CreateDirectory(oldDir);

        // Let the creation settle and be observed (or not) before the rename.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (raises.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Directory.Move(oldDir, newDir);
        var before = raises.Count;
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (raises.Count <= before && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        // A rename refreshes both sides of the tree: the old name's parent and the new name's row.
        var renameRaise = raises[^1];
        Assert.Contains(renameRaise, p => p.EndsWith("before", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(renameRaise, p => p.EndsWith("after", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Detach_StopsNotifications()
    {
        using var watcher = CreateWatcher();
        var count = 0;
        watcher.FilesChanged += (_, _) => Interlocked.Increment(ref count);
        watcher.Attach(_root);
        watcher.Detach();

        watcher.HandleFileSystemEvent(Path.Combine(_root, "after-detach.txt"));
        await Task.Delay(400);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Attach_SameRoot_IsIdempotentAndStillDelivers()
    {
        using var watcher = CreateWatcher();
        watcher.Attach(_root);
        watcher.Attach(_root); // must not create a blind window (no re-arm of a fresh watcher)

        var count = 0;
        watcher.FilesChanged += (_, _) => Interlocked.Increment(ref count);
        watcher.HandleFileSystemEvent(Path.Combine(_root, "x.txt"));
        await Task.Delay(400);

        Assert.Equal(1, count);
    }
}
