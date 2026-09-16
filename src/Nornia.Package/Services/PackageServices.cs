using System.Diagnostics;
using Nornia.Core;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Localization;

namespace Nornia.Package.Services;

/// <summary>Installed-package inventory with the same three-tier freshness strategy as the runtime
/// side: an in-memory cooldown coalesces repeated calls, a persisted snapshot within the configured
/// TTL is served straight from the database (no winget process at all), and only a genuinely stale
/// inventory falls through to a full <c>winget list</c>. Post-mutation reloads must use
/// <see cref="RefreshForcedAsync"/> so changes are never masked by either cache tier.</summary>
public sealed class PackageInventoryService(
    IPackageProvider packageProvider,
    IPackageRepository packageRepository,
    IInventoryScanStateRepository? scanStateRepository = null,
    IUiPerformanceMetrics? performanceMetrics = null) : IPackageInventoryService
{
    private const string ScanKind = "packages";

    private readonly object _gate = new();
    private IReadOnlyList<PackageInfo>? _lastScan;
    private long _lastScanAt;
    private Task<IReadOnlyList<PackageInfo>>? _scanInFlight;
    private bool _scanInFlightIsForced;

    public Task<IReadOnlyList<PackageInfo>> RefreshAsync(
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(forceRescan: false, progress, cancellationToken);

    public Task<IReadOnlyList<PackageInfo>> RefreshForcedAsync(
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(forceRescan: true, progress, cancellationToken);

    public Task<IReadOnlyList<PackageInfo>> GetPersistedAsync(CancellationToken cancellationToken = default) =>
        packageRepository.GetAllAsync(cancellationToken);

    private async Task<IReadOnlyList<PackageInfo>> RefreshCoreAsync(
        bool forceRescan,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Task<IReadOnlyList<PackageInfo>> scanTask;
        lock (_gate)
        {
            if (_scanInFlight is not null)
            {
                if (forceRescan && !_scanInFlightIsForced)
                {
                    // A post-mutation forced refresh must not reuse a non-forced scan that started
                    // before the mutation. Chain one fresh scan behind it instead.
                    scanTask = TrackScan(
                        ScanForcedAfterAsync(_scanInFlight, progress, cancellationToken),
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
                // Share one load/scan task so concurrent page activations cannot start several
                // winget runs.
                scanTask = TrackScan(
                    forceRescan
                        ? ScanAndPersistAsync(progress, cancellationToken)
                        : LoadOrScanAsync(cancellationToken),
                    forceRescan);
            }
        }

        return await scanTask.WaitAsync(cancellationToken);
    }

    private Task<IReadOnlyList<PackageInfo>> TrackScan(
        Task<IReadOnlyList<PackageInfo>> scanTask,
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

    private async Task<IReadOnlyList<PackageInfo>> ScanForcedAfterAsync(
        Task<IReadOnlyList<PackageInfo>> previousScan,
        IProgress<ProcessOutput>? progress,
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

        return await ScanAndPersistAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PackageInfo>> LoadOrScanAsync(CancellationToken cancellationToken)
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

        return await ScanAndPersistAsync(null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Packages have no cheap change probe (that would mean re-running winget anyway), so the
    /// gate is TTL-only. Any persistence hiccup or an empty snapshot returns null and falls back to a
    /// full scan: the gate may only ever skip work, never hide data.</summary>
    private async Task<IReadOnlyList<PackageInfo>?> TryLoadFreshSnapshotAsync(CancellationToken cancellationToken)
    {
        if (scanStateRepository is null)
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

            var packages = await packageRepository.GetAllAsync(cancellationToken).ConfigureAwait(false);
            return packages.Count == 0 ? null : packages;
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

    private async Task<IReadOnlyList<PackageInfo>> ScanAndPersistAsync(
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        var scannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var performance = performanceMetrics?.Begin("package.inventory.scan", phase: "background");
        var stopwatch = Stopwatch.StartNew();
        var packages = await packageProvider.ListInstalledAsync(progress, cancellationToken);
        await packageRepository.ReplaceSnapshotAsync(packages, scannedAt, cancellationToken);
        if (scanStateRepository is not null)
        {
            // Write scan_state only after the snapshot committed: a crash in between leaves an old
            // timestamp, and the next refresh simply re-scans (the safe failure direction).
            await scanStateRepository.UpsertAsync(
                new InventoryScanState(ScanKind, scannedAt, stopwatch.ElapsedMilliseconds, string.Empty),
                cancellationToken);
        }

        lock (_gate)
        {
            _lastScan = packages;
            _lastScanAt = scannedAt;
        }

        return packages;
    }
}

/// <summary>Chooses an installed provider, preferring the operation's requested provider then Winget.</summary>
public sealed class PackageProviderSelector(IEnumerable<IPackageProvider> providers)
{
    private readonly IReadOnlyList<IPackageProvider> _providers = providers.ToArray();

    public async Task<IPackageProvider> SelectAsync(string? preferredProvider = null, CancellationToken cancellationToken = default)
    {
        var ordered = _providers
            .OrderBy(provider => !string.Equals(provider.Name, preferredProvider, StringComparison.OrdinalIgnoreCase))
            .ThenBy(provider => !string.Equals(provider.Name, "winget", StringComparison.OrdinalIgnoreCase));
        foreach (var provider in ordered)
        {
            if (provider is not IPackageProviderAvailability availability || await availability.IsAvailableAsync(cancellationToken))
            {
                return provider;
            }
        }

        throw new InvalidOperationException(WingetText.Get("Package_NoProviderAvailable"));
    }
}

public sealed class EnvironmentRepairExecutor(
    IPackageProvider packageProvider,
    PackageInventoryService packageInventoryService,
    IRuntimeInventoryService runtimeInventoryService,
    PackageProviderSelector? providerSelector = null) : IEnvironmentRepairExecutor
{
    public async Task ExecuteAsync(
        IReadOnlyCollection<RuntimePackageOperation> operations,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var operation in operations)
        {
            var selectedProvider = providerSelector is null
                ? packageProvider
                : await providerSelector.SelectAsync(operation.PreferredProvider, cancellationToken);
            progress?.Report(new ProcessOutput(WingetText.Format("Repair_UsingProvider", operation.Component, selectedProvider.Name), false));
            await selectedProvider.InstallAsync(
                operation.PackageId,
                operation.PackageVersion,
                progress,
                cancellationToken);
        }

        // Mutations must not be masked by the scan-coalescing cache or the persisted-snapshot TTL:
        // both reloads bypass every cache tier so the post-operation list reflects reality.
        await packageInventoryService.RefreshForcedAsync(progress, cancellationToken);
        await runtimeInventoryService.RefreshForcedAsync(cancellationToken);
    }
}
