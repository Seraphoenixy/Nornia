using Nornia.CLI;
using Nornia.CLI.Commands;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

/// <summary>
/// CLI 清单命令。这里的用例锁住一个真实事故:<c>runtime list</c> 原先在持久化快照为空时
/// 会先算好空的结果、再触发强制重扫,然后照旧遍历那个空数组 —— 于是新机器上首启
/// 永远输出「没有运行库」。同一份逻辑在 <c>tool list</c> 里是正确的(过滤在刷新之后),
/// 所以两个命令各有一条对应用例。
/// </summary>
[Collection("ConsoleRedirection")]
public sealed class RuntimeCommandsTests
{
    [Fact]
    public async Task RuntimeList_PrintsComponentsReturnedByForcedRefresh()
    {
        var inventory = new ScriptedRuntimeInventory(
            persisted: [],
            forced: [CreateRuntime("Visual C++ Redistributable", "14.42.34433.0")]);
        var command = new RuntimeListCommand(CliTestHarness.Services(runtimeInventory: inventory));

        var run = await CliTestHarness.RunAsync(() => command.ExecuteAsync([], CancellationToken.None));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(1, inventory.ForcedCalls);
        Assert.Contains("Visual C++ Redistributable", run.Output);
        Assert.Contains("14.42.34433.0", run.Output);
    }

    [Fact]
    public async Task RuntimeList_DoesNotForceRefreshWhenSnapshotAlreadyHasComponents()
    {
        var inventory = new ScriptedRuntimeInventory(
            persisted: [CreateRuntime("Visual C++ Redistributable", "14.42.34433.0")],
            forced: []);
        var command = new RuntimeListCommand(CliTestHarness.Services(runtimeInventory: inventory));

        var run = await CliTestHarness.RunAsync(() => command.ExecuteAsync([], CancellationToken.None));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, inventory.ForcedCalls);
        Assert.Contains("Visual C++ Redistributable", run.Output);
    }

    [Fact]
    public async Task RuntimeList_ReportsEmptyWhenNeitherSourceHasComponents()
    {
        var inventory = new ScriptedRuntimeInventory([], []);
        var command = new RuntimeListCommand(CliTestHarness.Services(runtimeInventory: inventory));

        var run = await CliTestHarness.RunAsync(() => command.ExecuteAsync([], CancellationToken.None));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains(Nornia.CLI.Localization.CliText.Get("Runtime_None"), run.Output);
        Assert.DoesNotContain("NAME\tVERSION\tPATH", run.Output);
    }

    [Fact]
    public async Task RuntimeList_ListsOnlyTheRuntimeCategory()
    {
        var inventory = new ScriptedRuntimeInventory(
            persisted: [],
            forced:
            [
                CreateRuntime("Visual C++ Redistributable", "14.42.34433.0"),
                CreateRuntime(".NET SDK", "10.0.100")
            ]);
        var command = new RuntimeListCommand(CliTestHarness.Services(runtimeInventory: inventory));

        var run = await CliTestHarness.RunAsync(() => command.ExecuteAsync([], CancellationToken.None));

        Assert.Contains("Visual C++ Redistributable", run.Output);
        Assert.DoesNotContain(".NET SDK", run.Output);
    }

    [Fact]
    public async Task ToolList_KeepsItsOwnCorrectOrdering()
    {
        // 对照:tool list 的过滤本来就发生在刷新之后,这条用例防止它被改回去。
        var inventory = new ScriptedRuntimeInventory(
            persisted: [],
            forced: [CreateRuntime(".NET SDK", "10.0.100")]);
        var command = new ToolListCommand(CliTestHarness.Services(runtimeInventory: inventory));

        var run = await CliTestHarness.RunAsync(() => command.ExecuteAsync([], CancellationToken.None));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains(".NET SDK", run.Output);
        Assert.DoesNotContain("Visual C++ Redistributable", run.Output);
    }

    private static CoreRuntime CreateRuntime(string name, string version) => new(
        Guid.NewGuid(),
        name,
        version,
        @"C:\Program Files\Component",
        "x64",
        "registry",
        0,
        RuntimeStatus.Installed);

    private sealed class ScriptedRuntimeInventory(
        IReadOnlyList<CoreRuntime> persisted,
        IReadOnlyList<CoreRuntime> forced) : IRuntimeInventoryService
    {
        public int ForcedCalls { get; private set; }

        public Task<IReadOnlyList<CoreRuntime>> GetPersistedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(persisted);

        public Task<IReadOnlyList<CoreRuntime>> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(forced);

        public Task<IReadOnlyList<CoreRuntime>> RefreshForcedAsync(CancellationToken cancellationToken = default)
        {
            ForcedCalls++;
            return Task.FromResult(forced);
        }
    }
}

/// <summary>占位,标记 Console 重定向这组测试共用同一个串行集合。</summary>
[CollectionDefinition("ConsoleRedirection", DisableParallelization = true)]
public sealed class ConsoleRedirectionCollection;
