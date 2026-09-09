using System.Diagnostics;
using Nornia.Core;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Core.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Runtime.Services;

public sealed class EnvironmentInventoryService(
    IRuntimeDiscoveryService discoveryService,
    IRuntimeRepository runtimeRepository,
    IInventoryScanStateRepository? scanStateRepository = null,
    IEnvironmentFingerprintProvider? fingerprintProvider = null,
    IUiPerformanceMetrics? performanceMetrics = null) : IRuntimeInventoryService
{
    private const string ScanKind = "runtimes";

    private readonly object _gate = new();
    private IReadOnlyList<CoreRuntime>? _lastScan;
    private long _lastScanAt;
    private Task<IReadOnlyList<CoreRuntime>>? _scanInFlight;
    private bool _scanInFlightIsForced;

    /// <summary>Coalesces repeated scans: callers asking for a refresh within the in-memory cooldown
    /// window receive the latest result. Beyond it, a persisted snapshot younger than the configured
    /// TTL (see <see cref="NorniaSettings.PersistedScanTtlSeconds"/>) with an unchanged environment
    /// fingerprint is served directly from the database, so navigating pages or restarting the app
    /// does not re-run a single provider process. Only a genuinely stale or changed environment
    /// falls through to a full scan.</summary>
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
            if (_scanInFlight is not null)
            {
                if (forceRescan && !_scanInFlightIsForced)
                {
                    // A post-mutation forced refresh must not reuse a non-forced scan that started
                    // before the mutation. Chain one fresh discovery pass behind it instead.
                    scanTask = TrackScan(
                        ScanForcedAfterAsync(_scanInFlight, cancellationToken),
                        isForced: true);
                }
                else
                {
                    scanTask = _scanInFlight;
                }
            }
            else if (!forceRescan && _lastScan is not null && now - _lastScanAt < NorniaSettings.InventoryScanCacheSeconds)
            {
                return _lastScan;
            }
            else
            {
                // A cache miss is common when the Runtime and Projects pages activate together.
                // Share one load/scan task so one expiry cannot start several provider scans.
                scanTask = TrackScan(
                    forceRescan
                        ? ScanAndPersistAsync(cancellationToken)
                        : LoadOrScanAsync(cancellationToken),
                    forceRescan);
            }
        }

        return await scanTask.WaitAsync(cancellationToken);
    }

    private Task<IReadOnlyList<CoreRuntime>> TrackScan(
        Task<IReadOnlyList<CoreRuntime>> scanTask,
        bool isForced)
    {
        _scanInFlight = scanTask;
        _scanInFlightIsForced = isForced;
        _ = scanTask.ContinueWith(completed =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(_scanInFlight, completed))
                {
                    _scanInFlight = null;
                    _scanInFlightIsForced = false;
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return scanTask;
    }

    private async Task<IReadOnlyList<CoreRuntime>> ScanForcedAfterAsync(
        Task<IReadOnlyList<CoreRuntime>> previousScan,
        CancellationToken cancellationToken)
    {
        try
        {
            await previousScan.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The forced scan is the authoritative post-mutation attempt. If the earlier shared
            // scan failed, let this fresh attempt decide whether the refresh succeeds.
        }

        return await ScanAndPersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Single-flight body for non-forced refreshes: first try the persisted snapshot gate,
    /// and only run a real scan when the gate misses. Registering no scan-state repository (tests)
    /// disables the gate, preserving the always-scan semantics.</summary>
    private async Task<IReadOnlyList<CoreRuntime>> LoadOrScanAsync(CancellationToken cancellationToken)
    {
        var persisted = await TryLoadFreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (persisted is not null)
        {
            lock (_gate)
            {
                _lastScan = persisted;
                _lastScanAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            return persisted;
        }

        return await ScanAndPersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the persisted snapshot when it is fresh enough and the environment
    /// fingerprint is unchanged. Any persistence hiccup (table not created yet, database busy) or
    /// an empty snapshot returns null so the caller falls back to a full scan: the gate may only
    /// ever skip work, never hide data.</summary>
    private async Task<IReadOnlyList<CoreRuntime>?> TryLoadFreshSnapshotAsync(CancellationToken cancellationToken)
    {
        if (scanStateRepository is null || fingerprintProvider is null)
        {
            return null;
        }

        try
        {
            var state = await scanStateRepository.GetAsync(ScanKind, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - state.ScannedAt >= NorniaSettings.PersistedScanTtlSeconds)
            {
                return null;
            }

            var fingerprint = await fingerprintProvider.ComputeAsync(cancellationToken).ConfigureAwait(false);
            if (fingerprint is null || !string.Equals(fingerprint, state.Fingerprint, StringComparison.Ordinal))
            {
                return null;
            }

            var runtimes = await runtimeRepository.GetAllAsync(cancellationToken).ConfigureAwait(false);
            return runtimes.Count == 0 ? null : runtimes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fail open: persistence is currently unusable, so a full scan is the safe answer.
            return null;
        }
    }

    private async Task<IReadOnlyList<CoreRuntime>> ScanAndPersistAsync(CancellationToken cancellationToken)
    {
        var scannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var performance = performanceMetrics?.Begin("runtime.inventory.scan", phase: "background");
        var stopwatch = Stopwatch.StartNew();
        var runtimes = await discoveryService.ScanAsync(cancellationToken);
        await runtimeRepository.UpsertSnapshotAsync(runtimes, scannedAt, cancellationToken);
        if (scanStateRepository is not null)
        {
            // Write scan_state only after the snapshot committed: a crash in between leaves an old
            // timestamp, and the next refresh simply re-scans (the safe failure direction).
            var fingerprint = fingerprintProvider is null ? string.Empty : await fingerprintProvider.ComputeAsync(cancellationToken).ConfigureAwait(false);
            await scanStateRepository.UpsertAsync(
                new InventoryScanState(ScanKind, scannedAt, stopwatch.ElapsedMilliseconds, fingerprint ?? string.Empty),
                cancellationToken);
        }

        lock (_gate)
        {
            _lastScan = runtimes;
            _lastScanAt = scannedAt;
        }

        return runtimes;
    }
}
