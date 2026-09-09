using Nornia.Core;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Providers;
using Nornia.Package.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

public sealed class PackageInventoryServiceTests
{
    [Fact]
    public async Task RefreshAsync_RepeatedCallsAreCoalescedWithinCooldown()
    {
        var provider = new FakePackageProvider();
        var repository = new FakePackageRepository();
        var service = new PackageInventoryService(provider, repository);

        var first = await service.RefreshAsync();
        var second = await service.RefreshAsync();

        Assert.Same(first, second);
        Assert.Equal(1, provider.ListInstalledCalls);
        Assert.Equal(1, repository.ReplaceSnapshotCalls);
    }

    [Fact]
    public async Task RefreshForcedAsync_AlwaysRunsProviderAndPersists()
    {
        var provider = new FakePackageProvider();
        var repository = new FakePackageRepository();
        var service = new PackageInventoryService(provider, repository);

        await service.RefreshAsync();
        await service.RefreshForcedAsync();
        await service.RefreshForcedAsync();

        Assert.Equal(3, provider.ListInstalledCalls);
        Assert.Equal(3, repository.ReplaceSnapshotCalls);
    }

    [Fact]
    public async Task RefreshForcedAsync_DoesNotReuseNonForcedScanInFlight()
    {
        var provider = new BlockingPackageProvider();
        var repository = new FakePackageRepository();
        var service = new PackageInventoryService(provider, repository);

        var initial = service.RefreshAsync();
        await provider.Started.Task;
        var forced = service.RefreshForcedAsync();
        provider.Release.TrySetResult(true);

        var results = await Task.WhenAll(initial, forced);

        Assert.Equal(2, provider.ListInstalledCalls);
        Assert.NotSame(results[0], results[1]);
        Assert.Equal(2, repository.ReplaceSnapshotCalls);
    }

    [Fact]
    public async Task RefreshAsync_FreshSnapshotWithinTtlServesPersistedDataWithoutRunningProvider()
    {
        var provider = new FakePackageProvider();
        var repository = new FakePackageRepository { Persisted = [Package("Git.Git")] };
        var scanState = new FakeScanState
        {
            States = { ["packages"] = new InventoryScanState("packages", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 5, string.Empty) }
        };
        var service = new PackageInventoryService(provider, repository, scanState);

        var packages = await service.RefreshAsync();

        // 快照在 TTL 内:零次 winget list、零次快照重写,直接返回持久化数据。
        Assert.Equal("Git.Git", Assert.Single(packages).Id);
        Assert.Equal(0, provider.ListInstalledCalls);
        Assert.Equal(0, repository.ReplaceSnapshotCalls);
    }

    [Fact]
    public async Task RefreshAsync_ExpiredSnapshotFallsBackToProviderScan()
    {
        var provider = new FakePackageProvider();
        var repository = new FakePackageRepository { Persisted = [Package("Git.Git")] };
        var scanState = new FakeScanState
        {
            States = { ["packages"] = new InventoryScanState("packages", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - NorniaSettings.PersistedScanTtlSeconds - 1, 5, string.Empty) }
        };
        var service = new PackageInventoryService(provider, repository, scanState);

        await service.RefreshAsync();

        Assert.Equal(1, provider.ListInstalledCalls);
        Assert.Equal(1, repository.ReplaceSnapshotCalls);
    }

    [Fact]
    public async Task RefreshAsync_EmptyOrUnreadableSnapshotFallsBackToProviderScan()
    {
        var provider = new FakePackageProvider();
        var emptyRepository = new FakePackageRepository { Persisted = [] };
        var scanState = new FakeScanState
        {
            States = { ["packages"] = new InventoryScanState("packages", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 5, string.Empty) }
        };

        await new PackageInventoryService(provider, emptyRepository, scanState).RefreshAsync();
        Assert.Equal(1, provider.ListInstalledCalls);

        // 持久化读取抛异常(如表未建好)同样落回全扫。
        var failingState = new FakeScanState { GetThrows = true };
        await new PackageInventoryService(provider, new FakePackageRepository { Persisted = [Package("Git.Git")] }, failingState).RefreshAsync();
        Assert.Equal(2, provider.ListInstalledCalls);
    }

    [Fact]
    public async Task ForcedScan_RecordsScanStateAfterSnapshotCommit()
    {
        var provider = new FakePackageProvider();
        var repository = new FakePackageRepository();
        var scanState = new FakeScanState();
        var service = new PackageInventoryService(provider, repository, scanState);

        await service.RefreshForcedAsync();

        var state = Assert.Single(scanState.States).Value;
        Assert.Equal("packages", state.Kind);
        Assert.Equal(repository.LastScannedAt, state.ScannedAt);
        Assert.True(state.DurationMs >= 0);
        Assert.Equal(string.Empty, state.Fingerprint);
    }

    [Fact]
    public async Task RepairExecutor_RefreshesBothInventoriesForcedAfterOperations()
    {
        var provider = new FakePackageProvider();
        var packageRepository = new FakePackageRepository();
        var scanState = new FakeScanState();
        var packageInventory = new PackageInventoryService(provider, packageRepository, scanState);
        var runtimeInventory = new FakeRuntimeInventory();
        var executor = new EnvironmentRepairExecutor(provider, packageInventory, runtimeInventory);

        // 预置一份"新鲜"的包快照:只有强制刷新才会绕过 TTL 门控真正运行 provider。
        await packageInventory.RefreshAsync();
        provider.ResetCounters();
        await scanState.UpsertAsync(new InventoryScanState("packages", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 5, string.Empty));

        await executor.ExecuteAsync([new RuntimePackageOperation("Git", "2.55.0", "Git.Git", "2.55.0")]);

        // 修复改变了环境:包与运行库清单都必须绕过全部缓存层强制重扫。
        Assert.Equal(1, provider.InstallCalls);
        Assert.Equal(1, provider.ListInstalledCalls);
        Assert.Equal(1, runtimeInventory.ForcedRefreshCalls);
    }

    private static PackageInfo Package(string id) => new(id, id, "1.0", null, "winget", true);

    private sealed class FakePackageProvider : IPackageProvider
    {
        public int ListInstalledCalls { get; private set; }
        public int InstallCalls { get; private set; }

        public void ResetCounters()
        {
            ListInstalledCalls = 0;
            InstallCalls = 0;
        }

        public string Name => "fake";

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageInfo>>([]);

        public Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            ListInstalledCalls++;
            return Task.FromResult<IReadOnlyList<PackageInfo>>([Package("Git.Git")]);
        }

        public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            return Task.CompletedTask;
        }

        public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class BlockingPackageProvider : IPackageProvider
    {
        public int ListInstalledCalls { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "fake";

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageInfo>>([]);

        public async Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            ListInstalledCalls++;
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return [Package("Git.Git")];
        }

        public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakePackageRepository : IPackageRepository
    {
        public int ReplaceSnapshotCalls { get; private set; }
        public long LastScannedAt { get; private set; }
        public IReadOnlyList<PackageInfo> Persisted { get; set; } = [];

        public Task ReplaceSnapshotAsync(IReadOnlyCollection<PackageInfo> packages, long scannedAt, CancellationToken cancellationToken = default)
        {
            ReplaceSnapshotCalls++;
            LastScannedAt = scannedAt;
            Persisted = packages.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PackageInfo>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(Persisted);
    }

    private sealed class FakeScanState : IInventoryScanStateRepository
    {
        public Dictionary<string, InventoryScanState> States { get; } = new(StringComparer.Ordinal);
        public bool GetThrows { get; set; }

        public Task<InventoryScanState?> GetAsync(string kind, CancellationToken cancellationToken = default)
        {
            if (GetThrows)
            {
                throw new InvalidOperationException("scan_state unavailable");
            }

            return Task.FromResult(States.TryGetValue(kind, out var state) ? state : null);
        }

        public Task UpsertAsync(InventoryScanState state, CancellationToken cancellationToken = default)
        {
            States[state.Kind] = state;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRuntimeInventory : IRuntimeInventoryService
    {
        public int ForcedRefreshCalls { get; private set; }

        public Task<IReadOnlyList<CoreRuntime>> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoreRuntime>>([]);

        public Task<IReadOnlyList<CoreRuntime>> RefreshForcedAsync(CancellationToken cancellationToken = default)
        {
            ForcedRefreshCalls++;
            return Task.FromResult<IReadOnlyList<CoreRuntime>>([]);
        }

        public Task<IReadOnlyList<CoreRuntime>> GetPersistedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoreRuntime>>([]);
    }
}
