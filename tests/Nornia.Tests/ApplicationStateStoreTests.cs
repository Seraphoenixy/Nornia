using System.Text.Json;
using Nornia.Desktop.Configuration;

namespace Nornia.Tests;

/// <summary>V2/M3 改进:ApplicationStateStore 内存态 + 写盘去抖合批 + 同值跳过 + 单次 Flush。
/// 原实现每次 commit = 全文件读 + 全文件写;现改为内存态为唯一数据源,写盘经去抖合并,
/// 内容未变不写,关停/显式 Flush 一次落盘。</summary>
public sealed class ApplicationStateStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-state-{Guid.NewGuid():N}");

    public ApplicationStateStoreTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string StateFile(string name) => Path.Combine(_tempDir, name);

    private static ApplicationStateTransaction Bounds(double width) => new(new[]
    {
        new ApplicationStateOperation(ApplicationStateField.SidebarWidth, width),
    });

    [Fact]
    public async Task RapidCommits_CoalesceIntoSingleFileWrite()
    {
        // 连续多次 commit 落在去抖窗口内 → 合并为一次文件写(原实现 = N 次全文件读改写)。
        var path = StateFile("coalesce.json");
        using var store = new ApplicationStateStore(path, 150);

        for (var i = 1; i <= 5; i++)
        {
            await store.CommitAsync(Bounds(100 + i));
        }

        Assert.Equal(0, store.FileWriteCalls); // 去抖窗口内不落盘
        await WaitForAsync(() => store.FileWriteCalls >= 1, 5_000);
        var deadline = Environment.TickCount64 + 500;
        while (Environment.TickCount64 < deadline) { await Task.Delay(10); }

        Assert.Equal(1, store.FileWriteCalls);

        using var reloaded = new ApplicationStateStore(path);
        var state = await reloaded.LoadAsync();
        Assert.Equal(105d, state.SidebarWidth); // 最后一次 commit 生效
    }

    [Fact]
    public async Task SameValueCommit_DoesNotWriteAgain()
    {
        // 同值跳过:内容相对上次成功写入未变时不产生文件写。
        var path = StateFile("samevalue.json");
        using var store = new ApplicationStateStore(path, 50);

        await store.CommitAsync(Bounds(250));
        await WaitForAsync(() => store.FileWriteCalls >= 1, 5_000);
        Assert.Equal(1, store.FileWriteCalls);

        await store.CommitAsync(Bounds(250)); // 同值
        var deadline = Environment.TickCount64 + 400;
        while (Environment.TickCount64 < deadline) { await Task.Delay(10); }

        Assert.Equal(1, store.FileWriteCalls);
    }

    [Fact]
    public async Task LoadAsync_CachesInMemory_SecondLoadDoesNotRereadDisk()
    {
        // 内存态:首次 Load 读盘,之后返回内存快照(应用自身是文件唯一写者)。
        var path = StateFile("memory.json");
        using var store = new ApplicationStateStore(path, 50);

        var first = await store.LoadAsync();
        Assert.Null(first.SidebarWidth);

        File.WriteAllText(path,
            JsonSerializer.Serialize(new ApplicationState(SidebarWidth: 999), new JsonSerializerOptions()));
        var second = await store.LoadAsync();

        Assert.Null(second.SidebarWidth); // 仍为内存态,未重新读盘
    }

    [Fact]
    public async Task FlushAsync_PersistsImmediatelyWithoutWaitingDebounce()
    {
        // 显式 Flush 绕过去抖立即落盘(关停路径依赖)。
        var path = StateFile("flush.json");
        using var store = new ApplicationStateStore(path, 5_000); // 故意极长的去抖窗口

        await store.CommitAsync(Bounds(333));
        Assert.False(File.Exists(path)); // 去抖未到期

        await store.FlushAsync();

        Assert.True(File.Exists(path));
        using var reloaded = new ApplicationStateStore(path);
        Assert.Equal(333d, (await reloaded.LoadAsync()).SidebarWidth);
    }

    [Fact]
    public void Dispose_FlushesPendingStateSynchronously()
    {
        // 关停语义:commit 后立即 Dispose → 状态已落盘(原实现靠每次 commit 同步写,现靠单次 Flush)。
        // Dispose 本身是同步 API,此测试必须阻塞等待——xUnit1031 为有意豁免。
#pragma warning disable xUnit1031 // blocking is inherent to testing the synchronous Dispose flush
        var path = StateFile("dispose.json");
        {
            using var store = new ApplicationStateStore(path, 5_000); // 去抖远大于 Dispose 前剩余时间
            store.CommitAsync(Bounds(444)).GetAwaiter().GetResult();
            Assert.False(File.Exists(path)); // 去抖窗口内尚未写盘
        } // using 作用域结束 → Dispose:强制单次落盘

        Assert.True(File.Exists(path));
        using var reloaded = new ApplicationStateStore(path);
        var state = reloaded.LoadAsync().GetAwaiter().GetResult();
        Assert.Equal(444d, state.SidebarWidth);
#pragma warning restore xUnit1031
    }

    [Fact]
    public async Task WriteFailure_KeepsDirtyAndFlushRetries()
    {
        // 写盘失败(目标路径是目录 → Move/Replace 恒失败)→ 脏标记保留;移除障碍后 Flush 重试成功。
        var path = StateFile("failed.json");
        Directory.CreateDirectory(path);
        using var store = new ApplicationStateStore(path, 30);

        await store.CommitAsync(Bounds(555));
        await WaitForAsync(() => store.LastWriteError is not null, 5_000);
        Assert.NotNull(store.LastWriteError);

        Directory.Delete(path);
        await store.FlushAsync();

        Assert.True(File.Exists(path));
        using var reloaded = new ApplicationStateStore(path);
        Assert.Equal(555d, (await reloaded.LoadAsync()).SidebarWidth);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition did not become true within the timeout");
    }
}
