using Nornia.CLI.Localization;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.CLI.Commands;

public sealed class ToolListCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Tool} {CliCommands.List}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_ToolList");
    public string Usage => $"Nornia {Name}";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var components = await services.RuntimeInventory.GetPersistedAsync(cancellationToken);
        if (components.Count == 0)
        {
            components = await services.RuntimeInventory.RefreshForcedAsync(cancellationToken);
        }

        var tools = components.Where(CliServices.IsDevelopmentTool).ToArray();
        if (tools.Length == 0)
        {
            Console.WriteLine(CliText.Get("Tool_None"));
            return 0;
        }

        Console.WriteLine("NAME\tVERSION\tPATH");
        foreach (var tool in tools.OrderBy(tool => tool.Name).ThenBy(tool => tool.Version))
        {
            Console.WriteLine($"{tool.Name}\t{tool.Version}\t{tool.InstallPath}");
        }

        return 0;
    }
}

public sealed class ToolRefreshCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Tool} {CliCommands.Refresh}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_ToolRefresh");
    public string Usage => $"Nornia {Name}";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var components = await services.RuntimeInventory.RefreshForcedAsync(cancellationToken);
        Console.WriteLine(CliText.Format("Tool_Refreshed", components.Count(CliServices.IsDevelopmentTool)));
        return 0;
    }
}

public sealed class ToolInstallCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Tool} {CliCommands.Install}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_ToolInstall");
    public string Usage => $"Nornia {Name} <dotnet|node|python|git|java> <version>";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        RuntimeInstallCommand.RequireNameAndVersion(args, out var tool, out var version);
        services.EnsureCategory(tool, EnvironmentComponentCategory.DevelopmentTool, "development tool");
        var exitCode = 0;
        foreach (var package in services.PackageResolver.ResolveMany(tool, version))
        {
            exitCode = await services.InstallPackageAsync(package.PackageId, package.PackageVersion, cancellationToken);
        }

        await services.RuntimeInventory.RefreshForcedAsync(cancellationToken);
        return exitCode;
    }
}

public sealed class ToolRemoveCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Tool} {CliCommands.Remove}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_ToolRemove");
    public string Usage => $"Nornia {Name} <name> <version>";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        RuntimeInstallCommand.RequireNameAndVersion(args, out var tool, out var version);
        services.EnsureCategory(tool, EnvironmentComponentCategory.DevelopmentTool, "development tool");
        foreach (var package in services.PackageResolver.ResolveMany(tool, version))
        {
            await services.PackageProvider.UninstallAsync(package.PackageId, null, services.ProcessProgress, cancellationToken);
        }

        await services.RuntimeInventory.RefreshForcedAsync(cancellationToken);
        Console.WriteLine(CliText.Format("Tool_Removed", tool, version));
        return 0;
    }
}
