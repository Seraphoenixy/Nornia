using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Core.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Runtime.Services;

public sealed class RuntimeDiscoveryService(IEnumerable<IRuntimeProvider> providers) : IRuntimeDiscoveryService
{
    private readonly IReadOnlyList<IRuntimeProvider> _providers = providers.ToArray();

    public async Task<IReadOnlyList<CoreRuntime>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(_providers.Select(provider => DetectSafelyAsync(provider, cancellationToken)));
        var installed = results.SelectMany(x => x.InstalledRuntimes).ToList();
        var broken = results.SelectMany(x => x.BrokenRuntimes).Select(b => b.Runtime).ToList();
        return installed.Concat(broken).ToArray();
    }

    public async Task<RuntimeDetectionResult> ScanDetailedAsync(CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(_providers.Select(provider => DetectSafelyAsync(provider, cancellationToken)));
        return RuntimeDetectionResult.Create(
            results.SelectMany(x => x.InstalledRuntimes),
            results.SelectMany(x => x.BrokenRuntimes));
    }

    private static async Task<RuntimeDetectionResult> DetectSafelyAsync(
        IRuntimeProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.DetectAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A missing or broken provider must not hide other detected runtimes.
            // Provider threw an unexpected exception; treat as Broken if we have at least a name to report.
            try
            {
                var placeholder = new CoreRuntime(
                    Guid.NewGuid(), provider.Name, "unknown",
                    string.Empty, "Unknown", provider.GetType().Name,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RuntimeStatus.Error,
                    DetectionStatus.Broken, $"Provider exception: {ex.Message}");
                return RuntimeDetectionResult.Create([],
                    [new RuntimeBrokenInfo(placeholder, $"Provider '{provider.Name}' threw during detection: {ex.Message}",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds())]);
            }
            catch
            {
                return RuntimeDetectionResult.None;
            }
        }
    }
}