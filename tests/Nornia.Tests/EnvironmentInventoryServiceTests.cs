using Nornia.Core;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Core.Services;
using Nornia.Runtime.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

public sealed class EnvironmentInventoryServiceTests
{
    [Fact]
    public async Task RefreshAsync_RepeatedCallsAreCoalescedWithinCooldown()
    {
        var discovery = new FakeDiscovery();
        var repository = new FakeRuntimeRepository();
        var service = new EnvironmentInventoryService(discovery, repository);

        var first = await service.RefreshAsync();
        var second = await service.RefreshAsync();

        Assert.Same(first, second);
        Assert.Equal(1, discovery.ScanCalls);
        Assert.Equal(1, repository.UpsertCalls);
    }

    [Fact]
    public async Task RefreshForcedAsync_AlwaysRescansAndPersists()
    {
        var discovery = new FakeDiscovery();
        var repository = new FakeRuntimeRepository();
        var service = new EnvironmentInventoryService(discovery, repository);

        await service.RefreshAsync();
        await service.RefreshForcedAsync();
        await service.RefreshForcedAsync();

        Assert.Equal(3, discovery.ScanCalls);
        Assert.Equal(3, repository.UpsertCalls);
    }

    [Fact]
    public async Task GetPersistedAsync_ReadsThroughRepository()
    {
        var discovery = new FakeDiscovery();
        var repository = new FakeRuntimeRepository { Persisted = [Runtime("A", "1.0")] };
        var service = new EnvironmentInventoryService(discovery, repository);

        var persisted = await service.GetPersistedAsync();

        Assert.Equal("A", Assert.Single(persisted).Name);
        Assert.Equal(0, discovery.ScanCalls);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCacheMissSharesOneScan()
    {
        var discovery = new BlockingDiscovery();
        var repository = new FakeRuntimeRepository();
        var service = new EnvironmentInventoryService(discovery, repository);

        var first = service.RefreshAsync();
        await discovery.Started.Task;
        var second = service.RefreshAsync();
        discovery.Release.TrySetResult(true);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, discovery.ScanCalls);
        Assert.Same(results[0], results[1]);
        Assert.Equal(1, repository.UpsertCalls);
    }

    [Fact]
    public async Task RefreshForcedAsync_DoesNotReuseNonForcedScanInFlight()
    {
        var discovery = new BlockingDiscovery();
        var repository = new FakeRuntimeRepository();
        var service = new EnvironmentInventoryService(discovery, repository);

        var initial = service.RefreshAsync();
        await discovery.Started.Task;
        var forced = service.RefreshForcedAsync();
        discovery.Release.TrySetResult(true);

        var results = await Task.WhenAll(initial, forced);

        Assert.Equal(2, discovery.ScanCalls);
        Assert.NotSame(results[0], results[1]);
        Assert.Equal(2, repository.UpsertCalls);
    }

    [Fact]
    public async Task RefreshAsync_FreshSnapshotWithinTtlServesPersistedDataWithoutScanning()
    {
        var discovery = new FakeDiscovery();
        var repository = new FakeRuntimeRepository { Persisted = [Runtime("A", "1.0")] };
        var scanState = new FakeScanStateRepository
        {
            States = { ["runtimes"] = new InventoryScanState("runtimes", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 5, "FP") }
        };
        var service = new EnvironmentInventoryService(discovery, repository, scanState, new FakeFingerprintProvider("FP"));

        var runtimes = await service.RefreshAsync();

        // 快照在 TTL 内且指纹未变:零次 provider 扫描、零次快照重写,直接返回持久化数据。
        Assert.Equal("A", Assert.Single(runtimes).Name);
        Assert.Equal(0, discovery.ScanCalls);
        Assert.Equal(0, repository.UpsertCalls);
    }

    [Fact]
    public async Task RefreshAsync_ChangedFingerprintFallsBackToFullScan()
    {
        var discovery = new FakeDiscovery();
        var repository = new FakeRuntimeRepository { Persisted = [Runtime("A", "1.0")] };
        var scanState = new FakeScanStateRepository
        {
            States = { ["runtimes"] = new InventoryScanState("runtimes", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 5, "FP-OLD") }
        };
        var service = new EnvironmentInventoryService(discovery, repository, scanState, new FakeFingerprintProvider("FP-NEW"));

        await service.RefreshAsync();

        Assert.Equal(1, discovery.ScanCalls);
        Assert.Equal(1, repository.UpsertCalls);
        // 重扫完成后以新指纹覆盖 scan_state。
        Assert.Equal("FP-NEW", scanState.States["runtimes"].Fingerprint);
    }

    [Fact]
    public async Task RefreshAsync_ExpiredSnapshotFallsBackToFullScan()
    {
        var discovery = new FakeDiscovery();
        var repository = new FakeRuntimeRepository { Persisted = [Runtime("A", "1.0")] };
        var scanState = new FakeScanStateRepository
        {
            States = { ["runtimes"] = new InventoryScanState("runtimes", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - NorniaSettings.PersistedScanTtlSeconds - 1, 5, "FP") }
        };
        var service = new EnvironmentInventoryService(discovery, repository, scanState, new FakeFingerprintProvider("FP"));

        await service.RefreshAsync();

        Assert.Equal(1, discovery.ScanCalls);
    }

    [Fact]
    public async Task RefreshAsync_UnavailableFingerprintFailsOpenToFullScan()
    {
        var discovery = new FakeDiscovery();
        var repository = new FakeRuntimeRepository { Persisted = [Runtime("A", "1.0")] };
        var scanState = new FakeScanStateRepository
        {
            States = { ["runtimes"] = new InventoryScanState("runtimes", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 5, "FP") }
        };
        var service = new EnvironmentInventoryService(discovery, repository, scanState, new FakeFingerprintProvider(null));

        await service.RefreshAsync();

        // 指纹不可用必须重扫(fail-open),绝不把快照当新数据展示。
        Assert.Equal(1, discovery.ScanCalls);
    }

    [Fact]
    public async Task RefreshAsync_EmptyPersistedSnapshotFallsBackToFullScan()
    {
        var discovery = new FakeDiscovery();
        var repository = new FakeRuntimeRepository { Persisted = [] };
        var scanState = new FakeScanStateRepository
        {
            States = { ["runtimes"] = new InventoryScanState("runtimes", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 5, "FP") }
        };
        var service = new EnvironmentInventoryService(discovery, repository, scanState, new FakeFingerprintProvider("FP"));

        await service.RefreshAsync();

        // 首启(库内无在场记录)必须真正扫描。
        Assert.Equal(1, discovery.ScanCalls);
    }

    [Fact]
    public async Task RefreshAsync_PersistedReadFailureFallsBackToFullScan()
    {
        var discovery = new FakeDiscovery();
        var repository = new FakeRuntimeRepository();
        var scanState = new FakeScanStateRepository { GetThrows = true };
        var service = new EnvironmentInventoryService(discovery, repository, scanState, new FakeFingerprintProvider("FP"));

        await service.RefreshAsync();

        // 持久化层暂时不可用(如表未建好):落回全扫,绝不抛给调用方。
        Assert.Equal(1, discovery.ScanCalls);
        Assert.Equal(1, repository.UpsertCalls);
    }

    private sealed class FakeDiscovery : IRuntimeDiscoveryService
    {
        public int ScanCalls { get; private set; }
        public IReadOnlyList<CoreRuntime> Result { get; set; } = [Runtime("A", "1.0")];

        public Task<IReadOnlyList<CoreRuntime>> ScanAsync(CancellationToken cancellationToken = default)
        {
            ScanCalls++;
            return Task.FromResult(Result);
        }

        public Task<RuntimeDetectionResult> ScanDetailedAsync(CancellationToken cancellationToken = default)
        {
            ScanCalls++;
            return Task.FromResult(RuntimeDetectionResult.Create(Result, []));
        }
    }

    private sealed class BlockingDiscovery : IRuntimeDiscoveryService
    {
        public int ScanCalls { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<CoreRuntime>> ScanAsync(CancellationToken cancellationToken = default)
        {
            ScanCalls++;
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return [Runtime("A", "1.0")];
        }

        public Task<RuntimeDetectionResult> ScanDetailedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RuntimeDetectionResult.Create([Runtime("A", "1.0")], []));
    }

    private sealed class FakeRuntimeRepository : IRuntimeRepository
    {
        public int UpsertCalls { get; private set; }
        public IReadOnlyList<CoreRuntime> Persisted { get; set; } = [];

        public Task UpsertSnapshotAsync(IReadOnlyCollection<CoreRuntime> runtimes, long scannedAt, CancellationToken cancellationToken = default)
        {
            UpsertCalls++;
            Persisted = runtimes.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CoreRuntime>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(Persisted);
    }

    private sealed class FakeScanStateRepository : IInventoryScanStateRepository
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

    private sealed class FakeFingerprintProvider(string? fingerprint) : IEnvironmentFingerprintProvider
    {
        public Task<string?> ComputeAsync(CancellationToken cancellationToken = default) => Task.FromResult(fingerprint);
    }

    private static CoreRuntime Runtime(string name, string version) =>
        new(Guid.NewGuid(), name, version, $"C:\\{name}", "X64", "Test", 0, RuntimeStatus.Installed);
}
