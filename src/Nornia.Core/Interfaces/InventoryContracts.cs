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

    Task<IReadOnlyList<PackageInfo>> GetPersistedAsync(CancellationToken cancellationToken = default);
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
