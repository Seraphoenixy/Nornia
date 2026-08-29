using Microsoft.Extensions.DependencyInjection;
using Nornia.Core.Interfaces;
using Nornia.Core.Services;
using Nornia.Git;
using Nornia.Package.Providers;
using Nornia.Package.Services;
using Nornia.Project.Services;
using Nornia.Runtime.Providers;
using Nornia.Runtime.Services;
using Nornia.Storage;
using Nornia.Storage.Database;
using Nornia.Storage.Repositories;

namespace Nornia.Composition;

/// <summary>The single composition root shared by the CLI and the Desktop shell. Both entry points
/// call <see cref="AddNorniaServices"/> and then register only their own presentation-layer services.
/// This removes the duplicated DI graphs, provider lists and repository wiring they used to maintain.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNorniaServices(this IServiceCollection services)
    {
        return services
            // Process infrastructure
            .AddSingleton<ProcessRunner>()
            .AddSingleton<IProcessRunner>(provider => provider.GetRequiredService<ProcessRunner>())
            .AddSingleton<IProcessRunnerShutdown>(provider => provider.GetRequiredService<ProcessRunner>())

            // Git source control
            .AddSingleton<IGitService, GitService>()

            // Persistence
            .AddSingleton<NorniaDatabase>(_ => new NorniaDatabase(NorniaPaths.DatabasePath))
            .AddSingleton<ISqliteConnectionFactory>(provider => provider.GetRequiredService<NorniaDatabase>())
            .AddSingleton<IRuntimeRepository, RuntimeRepository>()
            .AddSingleton<IPackageRepository, PackageRepository>()
            .AddSingleton<IProjectRepository, ProjectRepository>()
            .AddSingleton<IEnvironmentProfileRepository, EnvironmentProfileRepository>()
            .AddSingleton<IInventoryScanStateRepository, InventoryScanStateRepository>()
            // 环境修复明细存数据目录下的 JSON Lines 文件,不进数据库。
            .AddSingleton<IEnvironmentRepairLogRepository, EnvironmentRepairLogStore>()
            .AddSingleton<IDashboardSummaryReader, SummaryRepository>()

            // Runtime discovery
            .AddSingleton<IRuntimeProvider, DotnetRuntimeProvider>()
            .AddSingleton<IRuntimeProvider, NodeRuntimeProvider>()
            .AddSingleton<IRuntimeProvider, PythonRuntimeProvider>()
            .AddSingleton<IRuntimeProvider, GitRuntimeProvider>()
            .AddSingleton<IRuntimeProvider, JavaRuntimeProvider>()
            .AddSingleton<IRuntimeProvider, DotnetDesktopRuntimeProvider>()
            .AddSingleton<IRuntimeProvider, VisualCppRedistributableProvider>()
            .AddSingleton<IRuntimeProvider, WindowsAppRuntimeProvider>()
            .AddSingleton<IRuntimeDiscoveryService, RuntimeDiscoveryService>()
            // Cheap (no process spawn) environment probe backing the persisted-snapshot TTL gate.
            .AddSingleton<IEnvironmentFingerprintProvider, EnvironmentFingerprintProvider>()
            .AddSingleton<EnvironmentInventoryService>()
            .AddSingleton<IRuntimeInventoryService>(provider => provider.GetRequiredService<EnvironmentInventoryService>())

            // Package management
            .AddSingleton<WingetProvider>()
            .AddSingleton<WingetProbe>()
            .AddSingleton<ScoopProvider>()
            .AddSingleton<ChocolateyProvider>()
            // IEnumerable<IPackageProvider> contains every supported provider for automatic selection.
            // Register Winget last so ordinary single-provider screens and inventory refreshes use the
            // Windows default instead of an optional CLI that may be absent.
            .AddSingleton<IPackageProvider>(provider => provider.GetRequiredService<ScoopProvider>())
            .AddSingleton<IPackageProvider>(provider => provider.GetRequiredService<ChocolateyProvider>())
            .AddSingleton<IPackageProvider>(provider => provider.GetRequiredService<WingetProvider>())
            .AddSingleton<PackageProviderSelector>()
            .AddSingleton<RuntimePackageResolver>()
            .AddSingleton<IRuntimePackageResolver>(provider => provider.GetRequiredService<RuntimePackageResolver>())
            .AddSingleton<PackageInventoryService>()
            .AddSingleton<IPackageInventoryService>(provider => provider.GetRequiredService<PackageInventoryService>())
            .AddSingleton<CachePackageAssociationService>()
            // Resolve AppDataCacheService through an explicit factory: its parameterized constructor
            // (IEnumerable<string> roots) would otherwise be chosen by the DI container's
            // "most parameters" rule, which injects an EMPTY list for IEnumerable<string>, leaving the
            // scan without any roots. The parameterless constructor computes the default user
            // directories via GetDefaultRoots().
            .AddSingleton<AppDataCacheService>(_ => new AppDataCacheService())
            .AddSingleton<ICacheInventoryService>(provider => provider.GetRequiredService<AppDataCacheService>())
            .AddSingleton<ICacheCleanupService>(provider => provider.GetRequiredService<AppDataCacheService>())

            // Project environment
            .AddSingleton<EnvironmentProfileService>()
            .AddSingleton<IEnvironmentProfileService>(provider => provider.GetRequiredService<EnvironmentProfileService>())
            .AddSingleton<EnvironmentCheckEngine>()
            .AddSingleton<IEnvironmentCheckEngine>(provider => provider.GetRequiredService<EnvironmentCheckEngine>())
            .AddSingleton<IEnvironmentRepairPlanner, EnvironmentRepairPlanner>()
            // Tracked executor appends each repair step to the repair log file so
            // EnvironmentRollbackService can reverse the batch later.
            .AddSingleton<IEnvironmentRepairExecutor, TrackedEnvironmentRepairExecutor>()
            .AddSingleton<IEnvironmentRollbackService, EnvironmentRollbackService>()
            .AddSingleton<ProjectLauncher>()
            .AddSingleton<IProjectLauncher>(provider => provider.GetRequiredService<ProjectLauncher>())
            .AddSingleton<ProjectCatalogService>()
            .AddSingleton<IProjectCatalogService>(provider => provider.GetRequiredService<ProjectCatalogService>());
    }
}