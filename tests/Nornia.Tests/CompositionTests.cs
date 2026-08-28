using Microsoft.Extensions.DependencyInjection;
using Nornia.CLI;
using Nornia.Composition;
using Nornia.Core.Interfaces;
using Nornia.Project.Services;

namespace Nornia.Tests;

/// <summary>Verifies the shared composition root wires every service-layer abstraction. Constructing
/// services must not touch disk or start processes, so this is safe in unit tests.</summary>
public sealed class CompositionTests
{
    private static ServiceProvider BuildSharedRoot() =>
        new ServiceCollection().AddNorniaServices().BuildServiceProvider();

    [Fact]
    public void SharedCompositionRoot_ResolvesCoreServices()
    {
        using var provider = BuildSharedRoot();

        Assert.NotNull(provider.GetRequiredService<IProcessRunner>());
        Assert.NotNull(provider.GetRequiredService<IProcessRunnerShutdown>());
        Assert.NotNull(provider.GetRequiredService<IRuntimeInventoryService>());
        Assert.NotNull(provider.GetRequiredService<IRuntimePackageResolver>());
        Assert.NotNull(provider.GetRequiredService<IPackageInventoryService>());
        Assert.NotNull(provider.GetRequiredService<ICacheInventoryService>());
        Assert.NotNull(provider.GetRequiredService<ICacheCleanupService>());
        Assert.NotNull(provider.GetRequiredService<IDashboardSummaryReader>());
        Assert.NotNull(provider.GetRequiredService<IEnvironmentCheckEngine>());
        Assert.NotNull(provider.GetRequiredService<IEnvironmentProfileService>());
        Assert.NotNull(provider.GetRequiredService<IEnvironmentRepairPlanner>());
        Assert.NotNull(provider.GetRequiredService<IEnvironmentRepairExecutor>());
        Assert.NotNull(provider.GetRequiredService<IProjectCatalogService>());
        Assert.NotNull(provider.GetRequiredService<IProjectLauncher>());
        Assert.NotNull(provider.GetRequiredService<IGitService>());
        Assert.NotNull(provider.GetRequiredService<IRuntimeRepository>());
        Assert.NotNull(provider.GetRequiredService<IPackageRepository>());
        Assert.NotNull(provider.GetRequiredService<IProjectRepository>());
        Assert.NotNull(provider.GetRequiredService<IEnvironmentRepairLogRepository>());
    }

    [Fact]
    public void SharedCompositionRoot_RegistersWingetAsDefaultPackageProvider()
    {
        using var provider = BuildSharedRoot();
        var providerCollection = provider.GetServices<IPackageProvider>().ToArray();

        Assert.Equal(3, providerCollection.Length);
        Assert.Equal("winget", providerCollection[^1].Name);
    }

    [Fact]
    public void CliCompositionRoot_ResolvesDispatcherAndServices()
    {
        using var provider = new ServiceCollection()
            .AddNorniaServices()
            .AddNorniaCommands()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<CommandDispatcher>());
        Assert.NotNull(provider.GetRequiredService<CliServices>());
        Assert.Equal(23, provider.GetServices<ICommand>().Count());
    }
}