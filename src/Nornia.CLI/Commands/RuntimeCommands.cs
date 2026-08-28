using Nornia.CLI.Localization;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.CLI.Commands;

public sealed class RuntimeListCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Runtime} {CliCommands.List}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_RuntimeList");
    public string Usage => $"Nornia {Name}";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var runtimes = await services.RuntimeInventory.GetPersistedAsync(cancellationToken);
        var runtimeComponents = runtimes.Where(CliServices.IsRuntime).ToArray();
        if (runtimeComponents.Length == 0)
        {
            runtimes = await services.RuntimeInventory.RefreshForcedAsync(cancellationToken);
        }

        if (runtimes.Count == 0)
        {
            Console.WriteLine(CliText.Get("Runtime_None"));
            return 0;
        }

        Console.WriteLine("NAME\tVERSION\tPATH");
        foreach (var runtime in runtimeComponents.OrderBy(runtime => runtime.Name).ThenBy(runtime => runtime.Version))
        {
            Console.WriteLine($"{runtime.Name}\t{runtime.Version}\t{runtime.InstallPath}");
        }

        return 0;
    }
}

public sealed class RuntimeRefreshCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Runtime} {CliCommands.Refresh}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_RuntimeRefresh");
    public string Usage => $"Nornia {Name}";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var runtimes = await services.RuntimeInventory.RefreshForcedAsync(cancellationToken);
        Console.WriteLine(CliText.Format("Runtime_Refreshed", runtimes.Count(CliServices.IsRuntime)));
        return 0;
    }
}

public sealed class RuntimeInstallCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Runtime} {CliCommands.Install}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_RuntimeInstall");
    public string Usage => $"Nornia {Name} <node|python|visual-cpp-redistributable|...> <version>";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        RequireNameAndVersion(args, out var runtime, out var version);
        services.WarnIfLegacyToolCommand(runtime, CliCommands.Install, version);
        var exitCode = 0;
        foreach (var package in services.PackageResolver.ResolveMany(runtime, version))
        {
            exitCode = await services.InstallPackageAsync(package.PackageId, package.PackageVersion, cancellationToken);
        }

        await services.RuntimeInventory.RefreshForcedAsync(cancellationToken);
        return exitCode;
    }

    internal static void RequireNameAndVersion(IReadOnlyList<string> args, out string name, out string version)
    {
        if (args.Count < 2)
        {
            throw new ArgumentException($"Usage: Nornia {CliCommands.Runtime} {CliCommands.Install} <name> <version>");
        }

        name = args[0];
        version = args[1];
    }
}

public sealed class RuntimeRemoveCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Runtime} {CliCommands.Remove}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_RuntimeRemove");
    public string Usage => $"Nornia {Name} <name> <version>";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        RuntimeInstallCommand.RequireNameAndVersion(args, out var runtime, out var version);
        services.WarnIfLegacyToolCommand(runtime, CliCommands.Remove, version);
        foreach (var package in services.PackageResolver.ResolveMany(runtime, version))
        {
            await services.PackageProvider.UninstallAsync(package.PackageId, null, services.ProcessProgress, cancellationToken);
        }

        await services.RuntimeInventory.RefreshForcedAsync(cancellationToken);
        Console.WriteLine(CliText.Format("Runtime_Removed", runtime, version));
        return 0;
    }
}
