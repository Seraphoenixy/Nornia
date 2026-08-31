using Nornia.Desktop.Services;

namespace Nornia.Tests;

public sealed class GitRepositoryWatcherTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"nornia-watcher-{Guid.NewGuid():N}");

    public GitRepositoryWatcherTests()
    {
        Directory.CreateDirectory(_repoPath);
        Directory.CreateDirectory(Path.Combine(_repoPath, ".git"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repoPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp repository directory.
        }
    }

    /// <summary>Runs marshaled callbacks inline so the tests stay deterministic without a
    /// running WPF dispatcher.</summary>
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

    private GitRepositoryWatcher CreateWatcher(
        TimeSpan? debounce = null,
        TimeSpan? suppression = null) =>
        new(new InlineDispatcher(), debounce ?? TimeSpan.FromMilliseconds(50), suppression ?? TimeSpan.FromMilliseconds(300));

    [Fact]
    public void DefaultDebounceInterval_IsShortEnoughForImmediateScmFeedback() =>
        Assert.Equal(TimeSpan.FromMilliseconds(200), GitRepositoryWatcher.DefaultDebounceInterval);

    [Fact]
    public async Task BurstOfChanges_RaisesSingleDebouncedNotification()
    {
        using var watcher = CreateWatcher();
        var count = 0;
        watcher.ChangesDetected += (_, _) => Interlocked.Increment(ref count);
        watcher.Attach(_repoPath);

        for (var i = 0; i < 5; i++)
        {
            watcher.HandleFileSystemChange(Path.Combine(_repoPath, $"src\\file{i}.cs"));
        }

        Assert.Equal(0, count); // still inside the debounce window
        await Task.Delay(400);
        Assert.Equal(1, count); // the whole burst collapsed into one notification
    }

    [Fact]
    public async Task DebouncedNotification_CarriesPathsAndGitMetadataKinds()
    {
        using var watcher = CreateWatcher();
        GitRepositoryChangesDetectedEventArgs? captured = null;
        watcher.ChangesDetected += (_, args) => captured = args;
        watcher.Attach(_repoPath);

        watcher.HandleFileSystemChange(Path.Combine(_repoPath, "src", "A.cs"));
        watcher.HandleFileSystemChange(Path.Combine(_repoPath, ".git", "index"));
        watcher.HandleFileSystemChange(Path.Combine(_repoPath, ".git", "HEAD"));

        await Task.Delay(250);

        Assert.NotNull(captured);
        Assert.Equal(Path.GetFullPath(_repoPath), captured!.RepositoryPath);
        Assert.Contains("src/A.cs", captured.ChangedPaths);
        Assert.True(captured.IndexChanged);
        Assert.True(captured.HeadOrRefsChanged);
        Assert.False(captured.IsUnknown);
    }

    [Fact]
    public async Task FileSystemEvents_AreDebouncedIntoOneNotification()
    {
        using var watcher = CreateWatcher(debounce: TimeSpan.FromMilliseconds(300));
        var count = 0;
        watcher.ChangesDetected += (_, _) => Interlocked.Increment(ref count);
        watcher.Attach(_repoPath);
        // A burst of real filesystem writes (Created + Changed events per file).
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(_repoPath, $"burst{i}.txt"), "content");
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        await Task.Delay(700); // no further events → no further notifications
        Assert.Equal(1, count);
    }

    [Fact]
    public void GitMetadataPaths_AreFilteredExceptIndexAndHead()
    {
        var root = _repoPath;
        Assert.False(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, ".git", "objects", "ab", "cd"), root));
        Assert.False(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, ".git", "logs", "HEAD"), root));
        Assert.False(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, ".git", "index.lock"), root));
        Assert.False(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, ".git", "objects", "pack", "pack-a.pack"), root));
        Assert.True(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, ".git", "index"), root));
        Assert.True(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, ".git", "HEAD"), root));
        Assert.True(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, ".git", "refs", "heads", "main"), root));
        Assert.True(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, ".git", "packed-refs"), root));
        Assert.True(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, "src", "A.cs"), root));
        Assert.False(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, "..", "outside.txt"), root));
    }

    [Fact]
    public void GeneratedAndTransientWorkingTreePaths_AreFiltered()
    {
        var root = _repoPath;
        Assert.False(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, "bin", "Debug", "app.dll"), root));
        Assert.False(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, "node_modules", "pkg", "index.js"), root));
        Assert.False(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, "src", "file.cs.tmp"), root));
        Assert.True(GitRepositoryWatcher.IsRelevantPath(Path.Combine(root, "src", "file.cs"), root));
    }

    [Fact]
    public void SuppressionWindow_IgnoresIndexEventsButNotWorkingTree()
    {
        using var watcher = CreateWatcher(suppression: TimeSpan.FromMilliseconds(300));
        watcher.Attach(_repoPath);

        Assert.True(watcher.ShouldReactTo(Path.Combine(_repoPath, ".git", "index")));
        Assert.True(watcher.ShouldReactTo(Path.Combine(_repoPath, "src", "A.cs")));
        Assert.True(watcher.ShouldReactTo(Path.Combine(_repoPath, ".git", "HEAD")));

        watcher.BeginSuppressionWindow();

        Assert.False(watcher.ShouldReactTo(Path.Combine(_repoPath, ".git", "index")));
    }

    [Fact]
    public void OperationSuppression_CoversOwnMutationUntilExplicitStatusIsPublished()
    {
        using var watcher = CreateWatcher();
        watcher.Attach(_repoPath);
        var index = Path.Combine(_repoPath, ".git", "index");
        var affected = Path.Combine(_repoPath, "src", "A.cs");
        var unrelated = Path.Combine(_repoPath, "src", "B.cs");

        using (watcher.BeginOperationSuppression(["src/A.cs"]))
        {
            Assert.False(watcher.ShouldReactTo(index));
            Assert.False(watcher.ShouldReactTo(affected));
            Assert.True(watcher.ShouldReactTo(unrelated));
        }

        // Releasing the operation lease retains the short read suppression for Git's index stat
        // cache, but working-tree paths are immediately visible to the watcher again.
        Assert.True(watcher.ShouldReactTo(affected));
    }

    [Fact]
    public void Attach_NonexistentDirectory_IsNoOp()
    {
        using var watcher = CreateWatcher();
        var count = 0;
        watcher.ChangesDetected += (_, _) => count++;

        watcher.Attach(Path.Combine(_repoPath, "does-not-exist"));

        Assert.False(watcher.ShouldReactTo(Path.Combine(_repoPath, "src", "A.cs")));
        watcher.HandleFileSystemChange(Path.Combine(_repoPath, "src", "A.cs"));
        Thread.Sleep(200);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Detach_IsIdempotentAndStopsNotifications()
    {
        using var watcher = CreateWatcher();
        var count = 0;
        watcher.ChangesDetected += (_, _) => count++;
        watcher.Attach(_repoPath);

        watcher.Detach();
        watcher.Detach(); // idempotent

        watcher.HandleFileSystemChange(Path.Combine(_repoPath, "src", "A.cs"));
        Thread.Sleep(200);
        Assert.Equal(0, count);
    }

    [Fact]
    public void WatcherError_RaisesOneNotificationAndStopsWatching()
    {
        using var watcher = CreateWatcher();
        var count = 0;
        watcher.ChangesDetected += (_, _) => count++;
        watcher.Attach(_repoPath);

        watcher.HandleWatcherError();

        Assert.Equal(1, count); // the fallback notification lets the VM re-check state

        watcher.HandleFileSystemChange(Path.Combine(_repoPath, "src", "A.cs"));
        Thread.Sleep(200);
        Assert.Equal(1, count); // watching stopped, nothing further
    }
}
