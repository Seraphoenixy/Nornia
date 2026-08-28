using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Services;
using Nornia.Project.Models;
using Nornia.Project.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

public sealed class EnvironmentRepairTests
{
    [Theory]
    [InlineData("10", "10.0.302", true)]
    [InlineData("3.13", "3.13.13", true)]
    [InlineData(">=22 <23", "22.18.0", true)]
    [InlineData(">=22 <23", "23.0.0", false)]
    [InlineData(">=3.13 <=3.13.9", "3.13.10", false)]
    [InlineData(">=2.50 <3", "2.55.0.windows.3", true)]
    public void VersionConstraint_MatchesSupportedPrefixAndRangeSyntax(string requirement, string installed, bool expected)
    {
        Assert.True(VersionConstraintParser.TryParse(requirement, out var constraint));
        Assert.Equal(expected, constraint!.IsSatisfiedBy(installed));
    }

    [Fact]
    public void VersionConstraint_RejectsPreviewForStableRequirement()
    {
        Assert.True(VersionConstraintParser.TryParse(">=10 <11", out var constraint));
        Assert.False(constraint!.IsSatisfiedBy("10.0.100-preview.1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("^22")]
    [InlineData(">=22 || <23")]
    [InlineData(">=not-a-version")]
    public void VersionConstraint_RejectsUnsupportedSyntax(string requirement)
    {
        Assert.False(VersionConstraintParser.TryParse(requirement, out _));
    }

    [Fact]
    public void CheckEngine_ReportsInvalidConstraintReason()
    {
        var profile = CreateProfile("node", "^22");
        var result = Assert.Single(new EnvironmentCheckEngine().Check(profile, []));

        Assert.Equal(EnvironmentCheckReason.InvalidConstraint, result.Reason);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
    }

    [Fact]
    public void RepairPlanner_CreatesActionableLowerBoundOperation()
    {
        var planner = new EnvironmentRepairPlanner(new RuntimePackageResolver());
        var plan = planner.CreatePlan(
            [new EnvironmentCheckResult(EnvironmentCheckStatus.Fail, "Node.js", ">=22 <23", null, "missing", EnvironmentCheckReason.Missing)],
            []);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(EnvironmentRepairDisposition.InstallOrUpgrade, action.Disposition);
        Assert.Equal("22", action.Operation!.TargetVersion);
        Assert.Equal("OpenJS.NodeJS", action.Operation.PackageId);
    }

    [Fact]
    public void RepairPlanner_LeavesUpperBoundAndUnmappedComponentManual()
    {
        var planner = new EnvironmentRepairPlanner(new RuntimePackageResolver());
        var plan = planner.CreatePlan(
        [
            new EnvironmentCheckResult(EnvironmentCheckStatus.Warning, "Python", "<3.14", "3.14.0", "mismatch", EnvironmentCheckReason.VersionMismatch),
            new EnvironmentCheckResult(EnvironmentCheckStatus.Fail, "Unknown", "25", null, "missing", EnvironmentCheckReason.Missing)
        ], []);

        Assert.All(plan.Actions, action => Assert.Equal(EnvironmentRepairDisposition.Manual, action.Disposition));
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public async Task RepairExecutor_OnlyExecutesProvidedOperationsAndRefreshesInventories()
    {
        var packageProvider = new FakePackageProvider();
        var packageRepository = new FakePackageRepository();
        var runtimeInventory = new FakeRuntimeInventory();
        var executor = new EnvironmentRepairExecutor(
            packageProvider,
            new PackageInventoryService(packageProvider, packageRepository),
            runtimeInventory);

        await executor.ExecuteAsync([new RuntimePackageOperation("Node.js", "22", "OpenJS.NodeJS", "22")]);

        Assert.Equal([("OpenJS.NodeJS", "22")], packageProvider.Installs);
        Assert.Equal(1, packageRepository.ReplaceCalls);
        Assert.Equal(1, runtimeInventory.RefreshCalls);
    }

    private static EnvironmentProfile CreateProfile(string component, string version) => new()
    {
        Runtime = new Dictionary<string, VersionRequirement> { [component] = new() { Version = version } }
    };

    private sealed class FakePackageProvider : IPackageProvider
    {
        public string Name => "Fake";
        public List<(string PackageId, string? Version)> Installs { get; } = [];
        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageInfo>>([]);
        public Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageInfo>>([]);
        public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) { Installs.Add((packageId, version)); return Task.CompletedTask; }
        public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePackageRepository : IPackageRepository
    {
        public int ReplaceCalls { get; private set; }
        public Task<IReadOnlyList<PackageInfo>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageInfo>>([]);
        public Task ReplaceSnapshotAsync(IReadOnlyCollection<PackageInfo> packages, long scannedAt, CancellationToken cancellationToken = default) { ReplaceCalls++; return Task.CompletedTask; }
    }

    private sealed class FakeRuntimeInventory : IRuntimeInventoryService
    {
        public int RefreshCalls { get; private set; }
        public Task<IReadOnlyList<CoreRuntime>> RefreshAsync(CancellationToken cancellationToken = default) { RefreshCalls++; return Task.FromResult<IReadOnlyList<CoreRuntime>>([]); }
        public Task<IReadOnlyList<CoreRuntime>> RefreshForcedAsync(CancellationToken cancellationToken = default) { RefreshCalls++; return Task.FromResult<IReadOnlyList<CoreRuntime>>([]); }
        public Task<IReadOnlyList<CoreRuntime>> GetPersistedAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CoreRuntime>>([]);
    }
}
