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
        var second = service.RefreshForcedAsync();
        discovery.Release.TrySetResult(true);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, discovery.ScanCalls);
        Assert.Same(results[0], results[1]);
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

    private static CoreRuntime Runtime(string name, string version) =>
        new(Guid.NewGuid(), name, version, $"C:\\{name}", "X64", "Test", 0, RuntimeStatus.Installed);
}
