using Nornia.Core;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Core.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Runtime.Services;

public sealed class EnvironmentInventoryService(
    IRuntimeDiscoveryService discoveryService,
    IRuntimeRepository runtimeRepository,
    IUiPerformanceMetrics? performanceMetrics = null) : IRuntimeInventoryService
{
    private readonly object _gate = new();
    private IReadOnlyList<CoreRuntime>? _lastScan;
    private long _lastScanAt;
    private Task<IReadOnlyList<CoreRuntime>>? _scanInFlight;

    /// <summary>Coalesces repeated scans: callers asking for a refresh within the cooldown window
    /// receive the latest result without re-scanning every provider or rewriting the database.</summary>
    public Task<IReadOnlyList<CoreRuntime>> RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(forceRescan: false, cancellationToken);

    /// <summary>Always re-runs discovery and updates the persisted snapshot.</summary>
    public Task<IReadOnlyList<CoreRuntime>> RefreshForcedAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(forceRescan: true, cancellationToken);

    public Task<IReadOnlyList<CoreRuntime>> GetPersistedAsync(CancellationToken cancellationToken = default) =>
        runtimeRepository.GetAllAsync(cancellationToken);

    private async Task<IReadOnlyList<CoreRuntime>> RefreshCoreAsync(bool forceRescan, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Task<IReadOnlyList<CoreRuntime>> scanTask;
        lock (_gate)
        {
            if (!forceRescan && _lastScan is not null && now - _lastScanAt < NorniaSettings.InventoryScanCacheSeconds)
            {
                return _lastScan;
            }

            // A cache miss is common when the Runtime and Projects pages activate together. Share
            // the discovery/persistence task so one expiry cannot start several provider scans.
            if (_scanInFlight is null)
            {
                scanTask = _scanInFlight = ScanAndPersistAsync(now, cancellationToken);
                _ = scanTask.ContinueWith(completed =>
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_scanInFlight, completed))
                        {
                            _scanInFlight = null;
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                scanTask = _scanInFlight;
            }
        }

        return await scanTask.WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<CoreRuntime>> ScanAndPersistAsync(long scannedAt, CancellationToken cancellationToken)
    {
        using var performance = performanceMetrics?.Begin("runtime.inventory.scan", phase: "background");
        var runtimes = await discoveryService.ScanAsync(cancellationToken);
        await runtimeRepository.UpsertSnapshotAsync(runtimes, scannedAt, cancellationToken);
        lock (_gate)
        {
            _lastScan = runtimes;
            _lastScanAt = scannedAt;
        }

        return runtimes;
    }
}
