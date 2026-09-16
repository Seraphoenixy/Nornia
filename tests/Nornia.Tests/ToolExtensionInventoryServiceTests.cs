using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Runtime.Extensions;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class ToolExtensionInventoryServiceTests
{
    private static ToolExtension Installed(string name, string version) =>
        new(ToolExtensionEcosystem.Pip, name, version, null);

    private static ToolExtension Outdated(string name, string latest) =>
        new(ToolExtensionEcosystem.Pip, name, string.Empty, latest);

    [Fact]
    public async Task RefreshAsync_MergesInstalledWithOutdated()
    {
        var provider = new FakeToolExtensionProvider(ToolExtensionEcosystem.Pip, "pip")
        {
            Installed = [Installed("requests", "2.32.3"), Installed("flask", "3.0.3")],
            Outdated = [Outdated("flask", "3.1.0")]
        };
        var service = new ToolExtensionInventoryService([provider]);

        var result = await service.RefreshAsync(ToolExtensionEcosystem.Pip, force: false);

        Assert.Equal("3.1.0", result.Single(package => package.Name == "flask").AvailableVersion);
        Assert.Null(result.Single(package => package.Name == "requests").AvailableVersion);
        Assert.Equal(1, provider.ListInstalledCalls);
        Assert.Equal(1, provider.ListOutdatedCalls);
    }

    [Fact]
    public async Task RefreshAsync_WithinCooldown_ReturnsCachedListWithoutRescan()
    {
        var provider = new FakeToolExtensionProvider(ToolExtensionEcosystem.Pip, "pip")
        {
            Installed = [Installed("requests", "2.32.3")]
        };
        var service = new ToolExtensionInventoryService([provider]);

        var first = await service.RefreshAsync(ToolExtensionEcosystem.Pip, force: false);
        var cached = await service.RefreshAsync(ToolExtensionEcosystem.Pip, force: false);

        Assert.Same(first, cached);
        Assert.Equal(1, provider.ListInstalledCalls);
        Assert.Equal(1, provider.ListOutdatedCalls);
    }

    [Fact]
    public async Task RefreshAsync_Force_BypassesCooldownCache()
    {
        var provider = new FakeToolExtensionProvider(ToolExtensionEcosystem.Pip, "pip")
        {
            Installed = [Installed("requests", "2.32.3")]
        };
        var service = new ToolExtensionInventoryService([provider]);

        await service.RefreshAsync(ToolExtensionEcosystem.Pip, force: false);
        await service.RefreshAsync(ToolExtensionEcosystem.Pip, force: true);

        Assert.Equal(2, provider.ListInstalledCalls);
        Assert.Equal(2, provider.ListOutdatedCalls);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCalls_ShareSingleInFlightScan()
    {
        var installedGate = new TaskCompletionSource<IReadOnlyList<ToolExtension>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outdatedGate = new TaskCompletionSource<IReadOnlyList<ToolExtension>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new GatedFakeProvider(installedGate.Task, outdatedGate.Task);
        var service = new ToolExtensionInventoryService([provider]);

        var first = service.RefreshAsync(ToolExtensionEcosystem.Pip, force: false);
        var second = service.RefreshAsync(ToolExtensionEcosystem.Pip, force: false);

        installedGate.SetResult([Installed("requests", "2.32.3")]);
        outdatedGate.SetResult([]);
        await Task.WhenAll(first, second);

        Assert.Equal(1, provider.ListInstalledCalls);
        Assert.Equal(1, provider.ListOutdatedCalls);
    }

    [Fact]
    public void GetEcosystemForComponent_UsesToolEcosystemMap()
    {
        var service = new ToolExtensionInventoryService([]);

        Assert.Equal(ToolExtensionEcosystem.Pip, service.GetEcosystemForComponent("python"));
        Assert.Equal(ToolExtensionEcosystem.Npm, service.GetEcosystemForComponent("node"));
        Assert.Equal(ToolExtensionEcosystem.DotnetTool, service.GetEcosystemForComponent("dotnet"));
        Assert.Null(service.GetEcosystemForComponent("java"));
    }

    [Fact]
    public void GetProvider_UnknownEcosystem_Throws()
    {
        var service = new ToolExtensionInventoryService([]);

        Assert.Throws<InvalidOperationException>(() => service.GetProvider(ToolExtensionEcosystem.Pip));
    }

    [Fact]
    public void Merge_FillsAvailableVersionFromOutdated()
    {
        var installed = new[] { Installed("requests", "2.32.3"), Installed("flask", "3.0.3") };
        var outdated = new[] { Outdated("flask", "3.1.0") };

        var merged = ToolExtensionInventoryService.Merge(ToolExtensionEcosystem.Pip, installed, outdated);

        Assert.Equal("3.1.0", merged.Single(package => package.Name == "flask").AvailableVersion);
        Assert.Null(merged.Single(package => package.Name == "requests").AvailableVersion);
    }

    [Fact]
    public void Merge_ClearsAvailableVersionWhenLatestMatchesInstalled()
    {
        var installed = new[] { Installed("flask", "3.0.3") };
        var outdated = new[] { Outdated("flask", "3.0.3") };

        var merged = ToolExtensionInventoryService.Merge(ToolExtensionEcosystem.Pip, installed, outdated);

        Assert.Null(Assert.Single(merged).AvailableVersion);
    }

    [Fact]
    public void Merge_IgnoresOutdatedPackagesNotInstalled()
    {
        var installed = new[] { Installed("requests", "2.32.3") };
        var outdated = new[] { Outdated("ghost-package", "9.9.9") };

        var merged = ToolExtensionInventoryService.Merge(ToolExtensionEcosystem.Pip, installed, outdated);

        Assert.Single(merged);
        Assert.Equal("requests", Assert.Single(merged).Name);
    }

    /// <summary>可挂起的替身:ListInstalledAsync/ListOutdatedAsync 返回测试控制的未完成任务,
    /// 用于验证并发扫描的 single-flight 合并。</summary>
    private sealed class GatedFakeProvider(
        Task<IReadOnlyList<ToolExtension>> installedTask,
        Task<IReadOnlyList<ToolExtension>> outdatedTask) : IToolExtensionProvider
    {
        public ToolExtensionEcosystem Ecosystem => ToolExtensionEcosystem.Pip;
        public string EcosystemName => "pip";
        public int ListInstalledCalls { get; private set; }
        public int ListOutdatedCalls { get; private set; }

        public Task<IReadOnlyList<ToolExtension>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            ListInstalledCalls++;
            return installedTask;
        }

        public Task<IReadOnlyList<ToolExtension>> ListOutdatedAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            ListOutdatedCalls++;
            return outdatedTask;
        }

        public Task InstallAsync(string name, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UninstallAsync(string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeAsync(string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ToolExtensionDependency>> GetDependenciesAsync(string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
