using Nornia.Runtime.Services;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

public sealed class RuntimeDiscoveryServiceTests
{
    [Fact]
    public async Task ScanAsync_WithNoProviders_ReturnsEmptyCollection()
    {
        var service = new RuntimeDiscoveryService([]);
        var runtimes = await service.ScanAsync();
        Assert.Empty(runtimes);
    }

    [Fact]
    public async Task ScanAsync_WhenProviderFails_ReturnsResultsFromHealthyProviders()
    {
        var expected = new CoreRuntime(Guid.NewGuid(), "Git", "2.50.0", "C:\\Git", "X64", "Test", 0, RuntimeStatus.Installed);
        var service = new RuntimeDiscoveryService([new ThrowingProvider(), new StaticProvider(expected)]);

        var runtimes = await service.ScanAsync();

        // The healthy provider's runtime is surfaced as installed; the throwing provider is
        // captured as a Broken placeholder so it does not hide healthy results.
        var healthy = Assert.Single(runtimes, r => r.Name == expected.Name);
        Assert.Equal(RuntimeStatus.Installed, healthy.Status);
        var broken = Assert.Single(runtimes, r => r.Status == RuntimeStatus.Error);
        Assert.Equal("Broken", broken.Name);
    }

    private sealed class ThrowingProvider : IRuntimeProvider
    {
        public string Name => "Broken";
        public Task<RuntimeDetectionResult> DetectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Broken provider");
        public Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task InstallAsync(string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StaticProvider(CoreRuntime runtime) : IRuntimeProvider
    {
        public string Name => runtime.Name;
        public Task<RuntimeDetectionResult> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RuntimeDetectionResult.Create([runtime], []));
        public Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([runtime.Version]);
        public Task InstallAsync(string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
