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

    private GitRepositoryWatcher CreateWatcher(
        TimeSpan? debounce = null,
        TimeSpan? suppression = null) =>
        new(new InlineDispatcher(), debounce ?? DefaultTestDebounce, suppression ?? TimeSpan.FromMilliseconds(300));

    private static readonly TimeSpan DefaultTestDebounce = TimeSpan.FromMilliseconds(50);

    /// <summary>去抖计时器回调在线程池上排队,固定 sleep 一个"应该够了"的时长在满负载 CI 上会失效;
    /// 统一轮询到条件满足,超时后再断言,失败信息里带上语义。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(condition(), message);
    }

    /// <summary>稳定性观察窗口:等待 3 倍去抖时长,足以让排队中的去抖回调落地,
    /// 用于"不得出现额外通知"的断言。</summary>
    private static Task SettleAsync(TimeSpan debounce) => Task.Delay(debounce * 3);

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
        // 轮询到首次通知(而不是固定 sleep 一个"应该够了"的时长),再确认没有多余通知。
        await WaitUntilAsync(() => Volatile.Read(ref count) > 0, "去抖后未收到通知");

        await SettleAsync(DefaultTestDebounce); // settle: 不得出现第二次通知
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

        await WaitUntilAsync(() => captured is not null, "去抖后未收到通知");

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
        var debounce = TimeSpan.FromMilliseconds(300);
        using var watcher = CreateWatcher(debounce: debounce);
        var count = 0;
        watcher.ChangesDetected += (_, _) => Interlocked.Increment(ref count);
        watcher.Attach(_repoPath);
        // A burst of real filesystem writes (Created + Changed events per file).
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(_repoPath, $"burst{i}.txt"), "content");
        }

        await WaitUntilAsync(() => Volatile.Read(ref count) > 0, "真实文件写入后未收到通知");

        await SettleAsync(debounce); // no further events → no further notifications
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
        watcher.ChangesDetected += (_, _) => Interlocked.Increment(ref count);

        watcher.Attach(Path.Combine(_repoPath, "does-not-exist"));

        var probe = Path.Combine(_repoPath, "src", "A.cs");
        // 未附加时门控同步返回 false:HandleFileSystemChange 直接返回,不会排入去抖计时器,
        // 因此"没有通知"可以立即断言,不需要等待。
        Assert.False(watcher.ShouldReactTo(probe));
        watcher.HandleFileSystemChange(probe);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Detach_IsIdempotentAndStopsNotifications()
    {
        using var watcher = CreateWatcher();
        var count = 0;
        watcher.ChangesDetected += (_, _) => Interlocked.Increment(ref count);
        watcher.Attach(_repoPath);

        watcher.Detach();
        watcher.Detach(); // idempotent

        var probe = Path.Combine(_repoPath, "src", "A.cs");
        Assert.False(watcher.ShouldReactTo(probe)); // Detach 清空 repositoryPath,门控同步关闭
        watcher.HandleFileSystemChange(probe);
        Assert.Equal(0, count);
    }

    [Fact]
    public void WatcherError_RaisesOneNotificationAndStopsWatching()
    {
        using var watcher = CreateWatcher();
        var count = 0;
        watcher.ChangesDetected += (_, _) => Interlocked.Increment(ref count);
        watcher.Attach(_repoPath);

        watcher.HandleWatcherError();

        Assert.Equal(1, count); // the fallback notification lets the VM re-check state

        var probe = Path.Combine(_repoPath, "src", "A.cs");
        Assert.False(watcher.ShouldReactTo(probe)); // 出错即 Detach,后续事件被同步拦截
        watcher.HandleFileSystemChange(probe);
        Assert.Equal(1, count); // watching stopped, nothing further
    }
}
