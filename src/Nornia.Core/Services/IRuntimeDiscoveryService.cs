using Nornia.Core.Models;

namespace Nornia.Core.Services;

public interface IRuntimeDiscoveryService
{
    Task<IReadOnlyList<Runtime>> ScanAsync(CancellationToken cancellationToken = default);

    Task<RuntimeDetectionResult> ScanDetailedAsync(CancellationToken cancellationToken = default);
}