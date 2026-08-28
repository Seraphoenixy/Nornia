using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Services;

namespace Nornia.Tests;

public sealed class PackageProviderSelectorTests
{
    [Fact]
    public async Task SelectAsync_UsesRequestedAvailableProvider()
    {
        var winget = new Provider("winget", true);
        var scoop = new Provider("scoop", true);
        var selected = await new PackageProviderSelector([winget, scoop]).SelectAsync("scoop");
        Assert.Same(scoop, selected);
    }

    [Fact]
    public async Task SelectAsync_FallsBackToAvailableWinget()
    {
        var unavailableScoop = new Provider("scoop", false);
        var winget = new Provider("winget", true);
        var selected = await new PackageProviderSelector([unavailableScoop, winget]).SelectAsync("scoop");
        Assert.Same(winget, selected);
    }

    private sealed class Provider(string name, bool available) : IPackageProvider, IPackageProviderAvailability
    {
        public string Name => name;
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(available);
        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageInfo>>([]);
        public Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageInfo>>([]);
        public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
