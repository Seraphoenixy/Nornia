using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Localization;

namespace Nornia.Package.Services;

public sealed class PackageInventoryService(IPackageProvider packageProvider, IPackageRepository packageRepository) : IPackageInventoryService
{
    public async Task<IReadOnlyList<PackageInfo>> RefreshAsync(
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packages = await packageProvider.ListInstalledAsync(progress, cancellationToken);
        await packageRepository.ReplaceSnapshotAsync(packages, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cancellationToken);
        return packages;
    }

    public Task<IReadOnlyList<PackageInfo>> GetPersistedAsync(CancellationToken cancellationToken = default) =>
        packageRepository.GetAllAsync(cancellationToken);
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

        await packageInventoryService.RefreshAsync(progress, cancellationToken);
        // Repair changed the environment, so bypass the scan-coalescing cache.
        await runtimeInventoryService.RefreshForcedAsync(cancellationToken);
    }
}
