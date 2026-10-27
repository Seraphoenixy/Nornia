using Nornia.CLI;
using Nornia.CLI.Commands;
using Nornia.Core.Models;

namespace Nornia.Tests;

/// <summary>覆盖 <c>env rollback &lt;correlation-id&gt;</c> 这一新增入口的契约。
/// 回滚是全项目唯一会**反向改动用户机器**的路径,而这个入口此前完全不存在:
/// correlation_id 被认真记录下来、也被显示给用户,却没有任何命令能消费它。</summary>
[Collection("ConsoleRedirection")]
public sealed class CliRollbackCommandTests
{
    private static readonly Guid BatchId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task Rollback_WithNoArguments_IsAUsageError()
    {
        var command = new EnvRollbackCommand(CliTestHarness.Services(rollbackService: new ScriptedRollbackService(null)));

        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_WithNonGuidId_IsAUsageError()
    {
        var command = new EnvRollbackCommand(CliTestHarness.Services(rollbackService: new ScriptedRollbackService(null)));

        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync(["not-a-guid"], CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_WithUnknownOption_IsAUsageError()
    {
        // --force 之类的未知选项必须报错(退出码 2),而不是像某些旧命令那样静默忽略。
        var command = new EnvRollbackCommand(CliTestHarness.Services(rollbackService: new ScriptedRollbackService(null)));

        await Assert.ThrowsAsync<CliUsageException>(() =>
            command.ExecuteAsync(["--force", BatchId.ToString("N")], CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_WithUnknownBatch_ReportsFailureWithoutTouchingTheMachine()
    {
        var service = new ScriptedRollbackService(plan: null);
        var command = new EnvRollbackCommand(CliTestHarness.Services(rollbackService: service));

        var run = await CliTestHarness.RunAsync(() => command.ExecuteAsync([BatchId.ToString("N")], CancellationToken.None));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(0, service.RollbackCalls);
        Assert.Contains(BatchId.ToString("N"), run.Error);
    }

    [Fact]
    public async Task Rollback_WithoutApply_OnlyPreviews()
    {
        var service = new ScriptedRollbackService(CreatePlan());
        var command = new EnvRollbackCommand(CliTestHarness.Services(rollbackService: service));

        var run = await CliTestHarness.RunAsync(() => command.ExecuteAsync([BatchId.ToString("N")], CancellationToken.None));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, service.RollbackCalls);
        Assert.Contains("Node.js", run.Output);
        // 预览必须把「还要加 --apply」说清楚,否则用户以为已经撤销了。
        Assert.Contains(Nornia.CLI.Localization.CliText.Get("Rollback_PreviewOnly"), run.Output);
    }

    [Fact]
    public async Task Rollback_WithApply_RunsTheBatchAndReportsTheOutcome()
    {
        var service = new ScriptedRollbackService(CreatePlan())
        {
            Result = new EnvironmentRollbackResult(BatchId, 2, 2, 0, [])
        };
        var command = new EnvRollbackCommand(CliTestHarness.Services(rollbackService: service));

        var run = await CliTestHarness.RunAsync(() => command.ExecuteAsync([BatchId.ToString("N"), "--apply"], CancellationToken.None));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(1, service.RollbackCalls);
        Assert.Contains(Nornia.CLI.Localization.CliText.Format("Rollback_Applied", 2, 2, 0, 0), run.Output);
    }

    [Fact]
    public async Task Rollback_WithApply_ReturnsExitCodeOneWhenAStepFails()
    {
        var failure = new EnvironmentRollbackFailure(
            OriginalSequence: 1,
            Component: "Node.js",
            FromVersion: "22.0.0",
            ToVersion: "20.11.0",
            PackageId: "OpenJS.NodeJS",
            PackageVersion: "20.11.0",
            Reason: "包管理器拒绝降级",
            Hint: "winget uninstall --id OpenJS.NodeJS");
        var service = new ScriptedRollbackService(CreatePlan())
        {
            Result = new EnvironmentRollbackResult(BatchId, 2, 1, 0, [failure])
        };
        var command = new EnvRollbackCommand(CliTestHarness.Services(rollbackService: service));

        var run = await CliTestHarness.RunAsync(() => command.ExecuteAsync([BatchId.ToString("N"), "--apply"], CancellationToken.None));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Node.js", run.Error);
        Assert.Contains("包管理器拒绝降级", run.Error);
        // 手动提示必须原样透出,否则用户只知道失败、不知道下一步怎么做。
        Assert.Contains("winget uninstall", run.Error);
    }

    private static EnvironmentRollbackPlan CreatePlan() => new(
        BatchId,
        0,
        [
            new EnvironmentRollbackAction(2, "Python", "3.13.0", "0.0.0-uninstalled", "Python.Python.3.13", null, RollbackStrategy.Automatic),
            new EnvironmentRollbackAction(1, "Node.js", "22.0.0", "20.11.0", "OpenJS.NodeJS", "20.11.0", RollbackStrategy.Manual, "winget install --id OpenJS.NodeJS --version 20.11.0")
        ]);

    private sealed class ScriptedRollbackService(EnvironmentRollbackPlan? plan) : Nornia.Core.Interfaces.IEnvironmentRollbackService
    {
        public EnvironmentRollbackResult Result { get; set; } = new(Guid.Empty, 0, 0, 0, []);
        public int RollbackCalls { get; private set; }

        public Task<EnvironmentRollbackPlan?> BuildPlanAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(plan);

        public Task<EnvironmentRollbackResult> RollbackAsync(EnvironmentRollbackPlan plan, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.FromResult(Result);
        }
    }
}
