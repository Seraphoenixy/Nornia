using Nornia.Core.Models;

namespace Nornia.Core.Interfaces;

public interface IRuntimeInventoryService
{
    Task<IReadOnlyList<Runtime>> RefreshAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Runtime>> RefreshForcedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Runtime>> GetPersistedAsync(CancellationToken cancellationToken = default);
}

public interface IPackageInventoryService
{
    Task<IReadOnlyList<PackageInfo>> RefreshAsync(
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Always re-runs the provider's installed list and updates the persisted snapshot.
    /// Used by explicit refreshes and by every post-mutation reload so changes are never masked
    /// by the scan-coalescing cache or the persisted-snapshot TTL gate.</summary>
    Task<IReadOnlyList<PackageInfo>> RefreshForcedAsync(
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageInfo>> GetPersistedAsync(CancellationToken cancellationToken = default);
}

/// <summary>Persisted snapshot-level scan metadata (per inventory kind), read by the TTL gate of the
/// inventory services and by pages that display a "last scanned" freshness hint.</summary>
public interface IInventoryScanStateRepository
{
    Task<InventoryScanState?> GetAsync(string kind, CancellationToken cancellationToken = default);

    Task UpsertAsync(InventoryScanState state, CancellationToken cancellationToken = default);
}

/// <summary>Computes a cheap (no process spawn) fingerprint of the machine environment. Callers
/// compare it against the fingerprint recorded with the last persisted scan; a changed or
/// unavailable (null) fingerprint forces a full re-scan (fail open).</summary>
public interface IEnvironmentFingerprintProvider
{
    Task<string?> ComputeAsync(CancellationToken cancellationToken = default);
}

public interface ICacheInventoryService
{
    /// <summary>Scans the cache inventory, reusing a recent result within the configured cooldown
    /// window (see <see cref="NorniaSettings.CacheScanCacheSeconds"/>).</summary>
    Task<IReadOnlyList<CacheCandidate>> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Always re-walks the file system, bypassing the cached scan result.</summary>
    Task<IReadOnlyList<CacheCandidate>> ScanForcedAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ICacheCleanupService
{
    Task<IReadOnlyList<CacheCleanupResult>> CleanAsync(
        IReadOnlyCollection<string> candidateIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
