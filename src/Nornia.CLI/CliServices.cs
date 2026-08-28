using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Services;
using Nornia.Project.Models;
using Nornia.Project.Services;
using Nornia.Runtime.Services;
using Nornia.CLI.Localization;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.CLI;

/// <summary>Shared services and cross-cutting helpers for CLI commands. Commands stay thin by
/// delegating common orchestration (package round-trips, category checks, progress sinks) here.</summary>
public sealed class CliServices(
    IRuntimeInventoryService runtimeInventory,
    IRuntimePackageResolver packageResolver,
    IPackageProvider packageProvider,
    IPackageInventoryService packageInventory,
    ICacheInventoryService cacheInventory,
    ICacheCleanupService cacheCleanup,
    IEnvironmentProfileService profileService,
    IEnvironmentCheckEngine checkEngine,
    IEnvironmentRepairPlanner repairPlanner,
    IEnvironmentRepairExecutor repairExecutor,
    IProjectCatalogService projectCatalog,
    IProjectLauncher projectLauncher)
{
    public IRuntimeInventoryService RuntimeInventory { get; } = runtimeInventory;
    public IRuntimePackageResolver PackageResolver { get; } = packageResolver;
    public IPackageProvider PackageProvider { get; } = packageProvider;
    public IPackageInventoryService PackageInventory { get; } = packageInventory;
    public ICacheInventoryService CacheInventory { get; } = cacheInventory;
    public ICacheCleanupService CacheCleanup { get; } = cacheCleanup;
    public IEnvironmentProfileService ProfileService { get; } = profileService;
    public IEnvironmentCheckEngine CheckEngine { get; } = checkEngine;
    public IEnvironmentRepairPlanner RepairPlanner { get; } = repairPlanner;
    public IEnvironmentRepairExecutor RepairExecutor { get; } = repairExecutor;
    public IProjectCatalogService ProjectCatalog { get; } = projectCatalog;
    public IProjectLauncher ProjectLauncher { get; } = projectLauncher;

    public IProgress<ProcessOutput> ProcessProgress { get; } = new CliProcessProgress();
    public IProgress<string> CacheProgress { get; } = new CliCacheProgress();

    public static bool IsRuntime(CoreRuntime runtime) =>
        EnvironmentComponentCatalog.Get(runtime.Name)?.Category == EnvironmentComponentCategory.Runtime;

    public static bool IsDevelopmentTool(CoreRuntime runtime) =>
        EnvironmentComponentCatalog.Get(runtime.Name)?.Category == EnvironmentComponentCategory.DevelopmentTool;

    public void WarnIfLegacyToolCommand(string component, string operation, string version)
    {
        if (EnvironmentComponentCatalog.Get(component)?.Category == EnvironmentComponentCategory.DevelopmentTool)
        {
            Console.WriteLine(CliText.Format("Warning_LegacyTool", component, operation, version));
        }
    }

    public void EnsureCategory(string component, EnvironmentComponentCategory category, string categoryName)
    {
        if (EnvironmentComponentCatalog.Get(component)?.Category != category)
        {
            throw new ArgumentException(CliText.Format("Error_NotSupportedCategory", component, categoryName));
        }
    }

    public async Task<int> InstallPackageAsync(string id, string? version, CancellationToken cancellationToken)
    {
        await PackageProvider.InstallAsync(id, version, ProcessProgress, cancellationToken);
        await PackageInventory.RefreshAsync(ProcessProgress, cancellationToken);
        Console.WriteLine(CliText.Format("Package_Installed", id, version is null ? string.Empty : $" {version}"));
        return 0;
    }

    public async Task<int> UninstallPackageAsync(string id, CancellationToken cancellationToken)
    {
        await PackageProvider.UninstallAsync(id, null, ProcessProgress, cancellationToken);
        await PackageInventory.RefreshAsync(ProcessProgress, cancellationToken);
        Console.WriteLine(CliText.Format("Package_Uninstalled", id));
        return 0;
    }

    public async Task<int> UpgradePackageAsync(string id, CancellationToken cancellationToken)
    {
        await PackageProvider.UpgradeAsync(id, ProcessProgress, cancellationToken);
        await PackageInventory.RefreshAsync(ProcessProgress, cancellationToken);
        Console.WriteLine(CliText.Format("Package_Upgraded", id));
        return 0;
    }

    public async Task<(EnvironmentProfile Profile, IReadOnlyList<CoreRuntime> Installed, IReadOnlyList<EnvironmentCheckResult> Results)> InspectEnvironmentAsync(string path, CancellationToken cancellationToken)
    {
        var profile = await ProfileService.LoadAsync(path, cancellationToken);
        var installed = await RuntimeInventory.RefreshForcedAsync(cancellationToken);
        var results = CheckEngine.Check(profile, installed);
        await ProjectCatalog.RegisterAsync(path, profile, results, installed, cancellationToken: cancellationToken);
        return (profile, installed, results);
    }
}

/// <summary>Streams process output to the console as it arrives.</summary>
public sealed class CliProcessProgress : IProgress<ProcessOutput>
{
    public void Report(ProcessOutput value)
    {
        if (value.IsError)
        {
            Console.Error.WriteLine(value.Text);
        }
        else
        {
            Console.WriteLine(value.Text);
        }
    }
}

/// <summary>Streams cache-scan progress to the console.</summary>
public sealed class CliCacheProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}