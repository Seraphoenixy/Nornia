using Nornia.CLI.Localization;

namespace Nornia.CLI.Commands;

public sealed class ProjectInitCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Project} {CliCommands.Init}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_ProjectInit");
    public string Usage => $"Nornia {Name} [path] [--name <name>]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var path = Environment.CurrentDirectory;
        string? name = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], CliCommands.NameOption, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                name = args[++index];
            }
            else if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                path = args[index];
            }
        }

        var profilePath = await services.ProfileService.InitializeAsync(path, name, cancellationToken: cancellationToken);
        var profile = await services.ProfileService.LoadAsync(path, cancellationToken);
        await services.ProjectCatalog.RegisterAsync(path, profile, cancellationToken: cancellationToken);
        Console.WriteLine(CliText.Format("Project_Created", profilePath));
        return 0;
    }
}

public sealed class ProjectExportCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Project} {CliCommands.Export}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_ProjectExport");
    public string Usage => $"Nornia {Name} [path] <destination>";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var (path, destination) = ResolvePathAndDestination(args);
        await services.ProfileService.ExportAsync(path, destination, cancellationToken);
        Console.WriteLine(CliText.Format("Project_Exported", Path.GetFullPath(destination)));
        return 0;
    }

    internal static (string Path, string Destination) ResolvePathAndDestination(IReadOnlyList<string> args)
    {
        if (args.Count == 1)
        {
            return (Environment.CurrentDirectory, args[0]);
        }

        if (args.Count >= 2)
        {
            return (args[0], args[1]);
        }

        throw new ArgumentException($"Usage: Nornia {CliCommands.Project} {CliCommands.Export} [path] <destination>");
    }
}

public sealed class ProjectImportCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Project} {CliCommands.Import}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_ProjectImport");
    public string Usage => $"Nornia {Name} <source> [path]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var (source, path) = ResolveSourceAndPath(args);
        await services.ProfileService.ImportAsync(source, path, cancellationToken);
        var profile = await services.ProfileService.LoadAsync(path, cancellationToken);
        await services.ProjectCatalog.RegisterAsync(path, profile, cancellationToken: cancellationToken);
        Console.WriteLine(CliText.Format("Project_Imported", Path.GetFullPath(path)));
        return 0;
    }

    internal static (string Source, string Path) ResolveSourceAndPath(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException($"Usage: Nornia {CliCommands.Project} {CliCommands.Import} <source> [path]");
        }

        return args.Count >= 2 ? (args[0], args[1]) : (args[0], Environment.CurrentDirectory);
    }
}

public sealed class ProjectOpenCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Project} {CliCommands.Open}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_ProjectOpen");
    public string Usage => $"Nornia {Name} [path]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var path = args.Count > 0 ? args[0] : Environment.CurrentDirectory;
        await services.ProjectLauncher.OpenAsync(path, cancellationToken: cancellationToken);
        await services.ProjectCatalog.RegisterAsync(path, opened: true, cancellationToken: cancellationToken);
        Console.WriteLine(CliText.Format("Project_Opened", Path.GetFullPath(path)));
        return 0;
    }
}
