using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Services;

namespace Nornia.Tests;

/// <summary>
/// 环境修复回滚。这是全项目唯一会<strong>反向改动用户机器</strong>的路径,此前覆盖为 0。
/// 核心契约:单步失败必须出现在 <see cref="EnvironmentRollbackResult.Failures"/> 里,
/// 而不是只往日志里打一行就当作成功 —— 用户会误以为环境已经复原。
/// </summary>
public sealed class EnvironmentRollbackServiceTests
{
    [Fact]
    public async Task RollbackAsync_ReportsInstallFailureInsteadOfSwallowingIt()
    {
        var correlationId = Guid.NewGuid();
        var logs = new FakeRepairLogRepository([
            Entry(correlationId, 1, "Node.js", previous: "18.0.0", target: "22.0.0", packageId: "OpenJS.NodeJS")
        ]);
        var provider = new ThrowingPackageProvider();
        var sut = new EnvironmentRollbackService(logs, provider);
        var progress = new RecordingProgress();

        var plan = await sut.BuildPlanAsync(correlationId);
        Assert.NotNull(plan);

        var result = await sut.RollbackAsync(plan!, progress);

        Assert.False(result.FullySucceeded);
        Assert.Equal(1, result.TotalSteps);
        Assert.Equal(0, result.UndoneCount);
        Assert.True(result.RequiresManualIntervention);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("Node.js", failure.Component);
        Assert.Equal("OpenJS.NodeJS", failure.PackageId);
        Assert.Contains("boom", failure.Reason);
        // 失败项必须自带可执行的手动补救提示,否则用户无从下手。
        Assert.False(string.IsNullOrWhiteSpace(failure.Hint));
        // 并且必须真的有日志流出(此前只有这一条,容易被误认为成功)。
        Assert.Contains(progress.Outputs, output => output.IsError);
    }

    [Fact]
    public async Task RollbackAsync_UninstallFailureDoesNotAbortRemainingSteps()
    {
        var correlationId = Guid.NewGuid();
        var logs = new FakeRepairLogRepository([
            Entry(correlationId, 1, "Node.js", previous: "0.0.0-uninstalled", target: "22.0.0", packageId: "Missing.Package"),
            Entry(correlationId, 2, "Git", previous: "2.45.0", target: "2.50.0", packageId: "Git.Git")
        ]);
        // 只让卸载失败;重装的那一步必须照旧执行并成功。
        var provider = new ThrowingPackageProvider(uninstallFails: true, installFails: false);
        var sut = new EnvironmentRollbackService(logs, provider);

        var plan = await sut.BuildPlanAsync(correlationId);
        Assert.NotNull(plan);

        var result = await sut.RollbackAsync(plan!);

        Assert.Equal(2, result.TotalSteps);
        Assert.Equal(1, result.UndoneCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("Node.js", failure.Component);
        Assert.Contains("Git.Git", provider.Installed);
    }

    [Fact]
    public async Task RollbackAsync_SkipsManualStepsAndCountsThem()
    {
        var correlationId = Guid.NewGuid();
        var logs = new FakeRepairLogRepository([
            Entry(correlationId, 1, "Windows App Runtime", previous: "6000.1", target: "7000.1",
                packageId: "Microsoft.WindowsAppRuntime", strategy: RollbackStrategy.Manual)
        ]);
        var provider = new ThrowingPackageProvider();
        var sut = new EnvironmentRollbackService(logs, provider);

        var plan = await sut.BuildPlanAsync(correlationId);
        Assert.NotNull(plan);

        var result = await sut.RollbackAsync(plan!);

        Assert.True(result.FullySucceeded);          // 没有「自动步骤失败」
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.UndoneCount);
        // 但依旧不能宣称环境已复原 —— 跳过的步骤需要人工执行。
        Assert.True(result.RequiresManualIntervention);
        Assert.Empty(provider.Installed);
        Assert.Empty(provider.Uninstalled);
    }

    [Fact]
    public async Task RollbackAsync_PropagatesCancellation()
    {
        var correlationId = Guid.NewGuid();
        var logs = new FakeRepairLogRepository([
            Entry(correlationId, 1, "Node.js", previous: "18.0.0", target: "22.0.0", packageId: "OpenJS.NodeJS")
        ]);
        var sut = new EnvironmentRollbackService(logs, new CancellingPackageProvider());

        var plan = await sut.BuildPlanAsync(correlationId);
        Assert.NotNull(plan);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.RollbackAsync(plan!));
    }

    [Fact]
    public async Task RollbackAsync_ReportsFullSuccessWhenEveryStepApplies()
    {
        var correlationId = Guid.NewGuid();
        var logs = new FakeRepairLogRepository([
            Entry(correlationId, 1, "Node.js", previous: "18.0.0", target: "22.0.0", packageId: "OpenJS.NodeJS"),
            Entry(correlationId, 2, "Git", previous: "2.45.0", target: "2.50.0", packageId: "Git.Git")
        ]);
        var provider = new ThrowingPackageProvider(uninstallFails: false, installFails: false);
        var sut = new EnvironmentRollbackService(logs, provider);

        var plan = await sut.BuildPlanAsync(correlationId);
        Assert.NotNull(plan);

        var result = await sut.RollbackAsync(plan!);

        Assert.True(result.FullySucceeded);
        Assert.False(result.RequiresManualIntervention);
        Assert.Equal(2, result.UndoneCount);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task BuildPlanAsync_OrdersActionsMostRecentFirst()
    {
        var correlationId = Guid.NewGuid();
        var logs = new FakeRepairLogRepository([
            Entry(correlationId, 1, "Node.js", previous: "18.0.0", target: "22.0.0", packageId: "OpenJS.NodeJS"),
            Entry(correlationId, 2, "Git", previous: "2.45.0", target: "2.50.0", packageId: "Git.Git")
        ]);
        var sut = new EnvironmentRollbackService(logs, new ThrowingPackageProvider());

        var plan = await sut.BuildPlanAsync(correlationId);

        Assert.NotNull(plan);
        Assert.Equal([2, 1], plan!.Actions.Select(action => action.OriginalSequence).ToArray());
    }

    [Fact]
    public async Task BuildPlanAsync_ReturnsNullForUnknownCorrelationId()
    {
        var sut = new EnvironmentRollbackService(new FakeRepairLogRepository([]), new ThrowingPackageProvider());

        Assert.Null(await sut.BuildPlanAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task BuildPlanAsync_IgnoresFailedRepairSteps()
    {
        var correlationId = Guid.NewGuid();
        var logs = new FakeRepairLogRepository([
            Entry(correlationId, 1, "Node.js", previous: "18.0.0", target: "22.0.0", packageId: "OpenJS.NodeJS", succeeded: false),
            Entry(correlationId, 2, "Git", previous: "2.45.0", target: "2.50.0", packageId: "Git.Git")
        ]);
        var sut = new EnvironmentRollbackService(logs, new ThrowingPackageProvider());

        var plan = await sut.BuildPlanAsync(correlationId);

        // 步骤 1 当初就没成功安装,没有什么可撤销的。
        Assert.NotNull(plan);
        Assert.Equal([2], plan!.Actions.Select(action => action.OriginalSequence).ToArray());
    }

    private static EnvironmentRepairLogEntry Entry(
        Guid correlationId,
        int sequence,
        string component,
        string previous,
        string target,
        string packageId,
        bool succeeded = true,
        RollbackStrategy strategy = RollbackStrategy.Automatic,
        string provider = "winget") =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            correlationId,
            sequence,
            component,
            previous,
            target,
            packageId,
            null,
            provider,
            succeeded,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            strategy);

    private sealed class FakeRepairLogRepository(IReadOnlyList<EnvironmentRepairLogEntry> entries)
        : IEnvironmentRepairLogRepository
    {
        public Task AppendAsync(EnvironmentRepairLogEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<EnvironmentRepairLogEntry>> GetByCorrelationAsync(
            Guid correlationId, CancellationToken cancellationToken = default)
        {
            var matches = entries.Where(entry => entry.CorrelationId == correlationId).ToArray();
            return Task.FromResult<IReadOnlyList<EnvironmentRepairLogEntry>>(matches);
        }
    }

    private sealed class ThrowingPackageProvider(bool uninstallFails = true, bool installFails = true) : IPackageProvider
    {
        public string Name => "fake";
        public List<string> Installed { get; } = [];
        public List<string> Uninstalled { get; } = [];

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageInfo>>([]);

        public Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageInfo>>([]);

        public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!installFails)
            {
                Installed.Add(packageId);
                return Task.CompletedTask;
            }

            throw new InvalidOperationException("boom: install refused");
        }

        public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!uninstallFails)
            {
                Uninstalled.Add(packageId);
                return Task.CompletedTask;
            }

            throw new InvalidOperationException("boom: uninstall refused");
        }

        public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CancellingPackageProvider : IPackageProvider
    {
        public string Name => "fake";

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageInfo>>([]);

        public Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageInfo>>([]);

        public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);

        public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);

        public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingProgress : IProgress<ProcessOutput>
    {
        public List<ProcessOutput> Outputs { get; } = [];

        public void Report(ProcessOutput value) => Outputs.Add(value);
    }
}
