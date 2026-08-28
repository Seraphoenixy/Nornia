using Nornia.Core.Models;

namespace Nornia.Core.Interfaces;

public interface IPackageProvider
{
    string Name { get; }
    Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Uninstalls a package. <paramref name="packageName"/> is the display name: the winget
    /// provider uses it to retry with <c>--name</c> when the Id matches no installed entry, which is
    /// the norm for locally installed (non-winget/ARP) applications.</summary>
    Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Optional capability implemented by package providers that can detect their local CLI.</summary>
public interface IPackageProviderAvailability
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

public interface IRuntimeProvider
{
    string Name { get; }
    Task<RuntimeDetectionResult> DetectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken cancellationToken = default);
    Task InstallAsync(string version, CancellationToken cancellationToken = default);
    Task RemoveAsync(string version, CancellationToken cancellationToken = default);
    Task UpdateAsync(string version, CancellationToken cancellationToken = default);
}

public interface IRuntimePackageResolver
{
    IReadOnlyList<string> SupportedRuntimeNames { get; }
    IReadOnlyList<string> SupportedToolNames { get; }

    /// <summary>Resolves every package representing the component version. Callers must handle all
    /// returned packages because one component can map to several packages.</summary>
    IReadOnlyList<RuntimePackage> ResolveMany(string runtime, string version);

    /// <summary>Resolves every package as a repair operation. Returns null when the component or
    /// version cannot be mapped, so callers can fall back to a manual repair.</summary>
    IReadOnlyList<RuntimePackageOperation>? TryResolveAll(string component, string targetVersion);
}

public interface IEnvironmentRepairExecutor
{
    Task ExecuteAsync(
        IReadOnlyCollection<RuntimePackageOperation> operations,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default);
}