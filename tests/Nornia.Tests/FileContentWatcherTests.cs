using Nornia.Desktop.Services;

namespace Nornia.Tests;

/// <summary>Coverage for <see cref="FileContentWatcher"/>: directory-shared FileSystemWatchers,
/// exact target-file filtering, trailing debounce and UI-thread dispatch. Deterministic paths go
/// through <see cref="FileContentWatcher.HandleFileSystemEvent"/>; a few tests drive the real
/// watch + write pipeline end to end.</summary>
public sealed class FileContentWatcherTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-content-watcher-{Guid.NewGuid():N}");

    public FileContentWatcherTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp directory.
        }
    }

    /// <summary>Runs marshaled callbacks inline so tests stay deterministic without a WPF dispatcher.</summary>
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

    /// <summary>Queues marshaled callbacks instead of executing them (assert dispatch visibility).</summary>
    private sealed class QueuingDispatcher : IUiDispatcher
    {
        public List<Action> Invoked { get; } = [];

        public bool CheckAccess() => false;

        public void BeginInvoke(Action action) => Invoked.Add(action);

        public Task InvokeAsync(Action action)
        {
            Invoked.Add(action);
            return Task.CompletedTask;
        }
    }

    private FileContentWatcher CreateWatcher(TimeSpan? debounce = null, IUiDispatcher? dispatcher = null) =>
        new(dispatcher ?? new InlineDispatcher(), debounce ?? TimeSpan.FromMilliseconds(50));

    private static string PathFrom(string dir, string name) => Path.Combine(dir, name);

    [Fact]
    public void DefaultDebounceInterval_Is200Milliseconds() =>
        Assert.Equal(TimeSpan.FromMilliseconds(200), FileContentWatcher.DefaultDebounceInterval);

    /// <summary>去抖计时器回调在线程池上排队,2 核 CI 满负载下会显著晚到;
    /// "睡眠固定时长后断言已触发"会稳定误报,统一用截止时间轮询等待。</summary>
    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(15);
        }
    }

    [Fact]
    public void Watch_TargetedFile_IgnoresOtherFilesInSameDirectory()
    {
        using var watcher = CreateWatcher();
        var target = PathFrom(_tempDir, "a.txt");
        var other = PathFrom(_tempDir, "b.txt");
        File.WriteAllText(target, "a");
        File.WriteAllText(other, "b");
        var count = 0;
        watcher.FileChanged += (_, args) => Interlocked.Increment(ref count);
        watcher.Watch(target);
        watcher.Watch(target); // idempotent

        watcher.HandleFileSystemEvent(other);
        Assert.Equal(0, count); // not a registered file

        watcher.HandleFileSystemEvent(target);
        Assert.Equal(0, count); // still inside the debounce window
        WaitUntil(() => Volatile.Read(ref count) >= 1);
        Assert.Equal(1, count);
    }

    [Fact]
    public void BurstOfEvents_CollapsesIntoSingleNotification()
    {
        using var watcher = CreateWatcher();
        var target = PathFrom(_tempDir, "burst.txt");
        var count = 0;
        watcher.FileChanged += (_, _) => Interlocked.Increment(ref count);
        watcher.Watch(target);

        for (var i = 0; i < 5; i++)
        {
            watcher.HandleFileSystemEvent(target);
        }

        Assert.Equal(0, count); // still inside the debounce window
        WaitUntil(() => Volatile.Read(ref count) >= 1);
        Assert.Equal(1, count); // the whole burst collapsed into one notification
    }

    [Fact]
    public void RealFileSystemWrite_RaisesOnceAfterDebounce()
    {
        using var watcher = CreateWatcher();
        var target = PathFrom(_tempDir, "real.txt");
        File.WriteAllText(target, "v1");
        var count = 0;
        watcher.FileChanged += (_, args) =>
        {
            Assert.Equal(Path.GetFullPath(target), args.FullPath);
            Interlocked.Increment(ref count);
        };
        watcher.Watch(target);

        // A real save: write then replace with fresh content (Changed/Created bursts).
        File.WriteAllText(target, "v2");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (count == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        Thread.Sleep(300); // no further events → no further notifications
        Assert.Equal(1, count);
    }

    [Fact]
    public void CreatedAndDeleted_AreReported()
    {
        using var watcher = CreateWatcher();
        var target = PathFrom(_tempDir, "created.txt");
        var count = 0;
        watcher.FileChanged += (_, _) => Interlocked.Increment(ref count);
        watcher.Watch(target);
        Thread.Sleep(100); // let the per-directory watcher attach

        File.WriteAllText(target, "new"); // Created
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (count == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        Assert.Equal(1, count);
        File.Delete(target); // Deleted
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (count < 2 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void AtomicReplace_RaisesForTarget()
    {
        using var watcher = CreateWatcher();
        var target = PathFrom(_tempDir, "atomic.txt");
        var temp = PathFrom(_tempDir, "atomic.tmp");
        File.WriteAllText(target, "v1");
        File.WriteAllText(temp, "v2");
        var count = 0;
        watcher.FileChanged += (_, args) =>
        {
            if (string.Equals(args.FullPath, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref count);
            }
        };
        watcher.Watch(target);
        Thread.Sleep(100);

        File.Move(temp, target, overwrite: true); // atomic save: rename temp → target
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (count == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        Assert.True(count >= 1, "atomic replace (rename onto target) must raise for the target path");
    }

    [Fact]
    public void Events_AreMarshaledThroughUiDispatcher()
    {
        // A dispatcher that cannot run inline proves the raise goes through BeginInvoke and only
        // then reaches subscribers.
        var dispatcher = new QueuingDispatcher();
        using var watcher = CreateWatcher(dispatcher: dispatcher);
        var target = PathFrom(_tempDir, "dispatched.txt");
        var raised = 0;
        watcher.FileChanged += (_, _) => Interlocked.Increment(ref raised);
        watcher.Watch(target);

        watcher.HandleFileSystemEvent(target);
        WaitUntil(() => dispatcher.Invoked.Count >= 1); // 去抖回调在线程池上排队,满负载下晚到
        Assert.Equal(0, raised); // queued, not executed inline

        foreach (var action in dispatcher.Invoked)
        {
            action();
        }

        Assert.Equal(1, raised); // dispatch executes the raise
    }

    [Fact]
    public void Unwatch_StopsNotificationsAndReleasesDirectoryWatcher()
    {
        using var watcher = CreateWatcher();
        var target = PathFrom(_tempDir, "unwatch.txt");
        var count = 0;
        watcher.FileChanged += (_, _) => Interlocked.Increment(ref count);
        watcher.Watch(target);

        watcher.Unwatch(target);
        watcher.Unwatch(target); // idempotent

        watcher.HandleFileSystemEvent(target);
        Thread.Sleep(300);
        Assert.Equal(0, count);

        // Re-watch after unwatch must work again.
        watcher.Watch(target);
        watcher.HandleFileSystemEvent(target);
        WaitUntil(() => Volatile.Read(ref count) >= 1);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Dispose_StopsAllNotifications()
    {
        using var watcher = CreateWatcher();
        var targetA = PathFrom(_tempDir, "a.txt");
        var targetB = Path.Combine(_tempDir, "sub", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(targetB)!);
        var count = 0;
        watcher.FileChanged += (_, _) => Interlocked.Increment(ref count);
        watcher.Watch(targetA);
        watcher.Watch(targetB);

        watcher.Dispose();
        watcher.Dispose(); // idempotent

        watcher.HandleFileSystemEvent(targetA);
        watcher.HandleFileSystemEvent(targetB);
        Thread.Sleep(300);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Watch_TwoFilesInSameDirectory_ShareOneWatcherAndBothFire()
    {
        using var watcher = CreateWatcher();
        var targetA = PathFrom(_tempDir, "a.txt");
        var targetB = PathFrom(_tempDir, "b.txt");
        var raised = new List<string>();
        watcher.FileChanged += (_, args) =>
        {
            lock (raised)
            {
                raised.Add(args.FullPath);
            }
        };
        watcher.Watch(targetA);
        watcher.Watch(targetB);

        watcher.HandleFileSystemEvent(targetA);
        watcher.HandleFileSystemEvent(targetB);
        WaitUntil(() => raised.Count >= 2);

        lock (raised)
        {
            Assert.Equal(2, raised.Count);
            Assert.Contains(Path.GetFullPath(targetA), raised);
            Assert.Contains(Path.GetFullPath(targetB), raised);
        }
    }
}