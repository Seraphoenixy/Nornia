using Microsoft.Extensions.DependencyInjection;
using Nornia.CLI.Commands;

namespace Nornia.CLI;

public static class CliServiceCollectionExtensions
{
    /// <summary>Registers the CLI command set and dispatcher on top of the shared composition root.</summary>
    public static IServiceCollection AddNorniaCommands(this IServiceCollection services)
    {
        return services
            .AddSingleton<CliServices>()
            // HelpCommand resolves the command table lazily through a captured provider factory to
            // avoid a registration cycle (the table itself contains the help command).
            .AddSingleton<HelpCommand>(provider => new HelpCommand(
                () => provider.GetServices<ICommand>().Where(command => command is not HelpCommand).ToArray()))
            .AddSingleton<ICommand>(provider => provider.GetRequiredService<HelpCommand>())
            .AddSingleton<ICommand, RuntimeListCommand>()
            .AddSingleton<ICommand, RuntimeRefreshCommand>()
            .AddSingleton<ICommand, RuntimeInstallCommand>()
            .AddSingleton<ICommand, RuntimeRemoveCommand>()
            .AddSingleton<ICommand, ToolListCommand>()
            .AddSingleton<ICommand, ToolRefreshCommand>()
            .AddSingleton<ICommand, ToolInstallCommand>()
            .AddSingleton<ICommand, ToolRemoveCommand>()
            .AddSingleton<ICommand, PackageSearchCommand>()
            .AddSingleton<ICommand, PackageListCommand>()
            .AddSingleton<ICommand, PackageInstallCommand>()
            .AddSingleton<ICommand, PackageUninstallCommand>()
            .AddSingleton<ICommand, PackageUpgradeCommand>()
            .AddSingleton<ICommand, PackageCacheScanCommand>()
            .AddSingleton<ICommand, PackageCacheCleanCommand>()
            .AddSingleton<ICommand, EnvCheckCommand>()
            .AddSingleton<ICommand, EnvPlanCommand>()
            .AddSingleton<ICommand, EnvFixCommand>()
            .AddSingleton<ICommand, ProjectInitCommand>()
            .AddSingleton<ICommand, ProjectExportCommand>()
            .AddSingleton<ICommand, ProjectImportCommand>()
            .AddSingleton<ICommand, ProjectOpenCommand>()
            .AddSingleton<CommandDispatcher>();
    }
}