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

        public void BeginInvoke(Action action, UiDispatcherPriority priority) => action();
        public bool IsHostAvailable => false;

        public IUiTimer CreateTimer(TimeSpan interval, Action tick, UiDispatcherPriority priority = UiDispatcherPriority.Normal) =>
            new WpfUiDispatcher().CreateTimer(interval, tick, priority);

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private WorkspaceFileWatcher CreateWatcher(TimeSpan? debounce = null) =>
        new(new InlineDispatcher(), debounce ?? DefaultTestDebounce);

    private static readonly TimeSpan DefaultTestDebounce = TimeSpan.FromMilliseconds(50);

    /// <summary>去抖回调在线程池上排队,固定 sleep 在满负载 CI 上会失效;统一轮询到条件满足再断言。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(condition(), message);
    }

    /// <summary>稳定性观察窗口:3 倍去抖时长,用于"不得出现额外通知"的断言。</summary>
    private static Task SettleAsync(TimeSpan debounce) => Task.Delay(debounce * 3);

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
        await WaitUntilAsync(() => Volatile.Read(ref count) > 0, "去抖后未收到通知");
        await SettleAsync(DefaultTestDebounce); // settle: 整批事件必须只折叠成一次通知

        Assert.Equal(1, count); // the whole burst collapsed into one notification
        Assert.Equal(Path.GetFullPath(_root), captured!.RootPath);
        Assert.Equal(5, captured.ChangedPaths.Count);
    }

    [Fact]
    public async Task FileSystemCreatesAndDeletes_AreReportedAfterDebounce()
    {
        var debounce = TimeSpan.FromMilliseconds(300);
        using var watcher = CreateWatcher(debounce: debounce);
        var raises = new List<IReadOnlySet<string>>();
        watcher.FilesChanged += (_, args) => raises.Add(args.ChangedPaths);
        watcher.Attach(_root);

        File.WriteAllText(Path.Combine(_root, "burst0.txt"), "content");
        File.WriteAllText(Path.Combine(_root, "burst1.txt"), "content");

        await WaitUntilAsync(() => raises.Count > 0, "真实文件创建后未收到通知");

        await SettleAsync(debounce); // settle: the burst must not fragment into further raises
        Assert.Single(raises);
        Assert.Contains(raises[0], p => p.EndsWith("burst0.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(raises[0], p => p.EndsWith("burst1.txt", StringComparison.OrdinalIgnoreCase));

        File.Delete(Path.Combine(_root, "burst0.txt"));
        var before = raises.Count;
        await WaitUntilAsync(() => raises.Count > before, "删除后未收到通知");

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
        await WaitUntilAsync(() => raises.Count > 0, "目录创建后未收到通知");

        Directory.Move(oldDir, newDir);
        var before = raises.Count;
        await WaitUntilAsync(() => raises.Count > before, "目录重命名后未收到通知");

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

        // Detach 清空内部 watcher 集合,HandleFileSystemEvent 同步返回且不去武装去抖计时器,
        // 因此"没有通知"是确定性结论,无需等待。
        watcher.HandleFileSystemEvent(Path.Combine(_root, "after-detach.txt"));

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
        await WaitUntilAsync(() => Volatile.Read(ref count) > 0, "重复 Attach 后事件未被投递");

        Assert.Equal(1, count);
    }
}
