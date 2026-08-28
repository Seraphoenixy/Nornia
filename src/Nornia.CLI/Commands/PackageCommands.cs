using Nornia.CLI.Localization;

namespace Nornia.CLI.Commands;

public sealed class PackageSearchCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Package} {CliCommands.Search}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_PackageSearch");
    public string Usage => $"Nornia {Name} <query>";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || string.IsNullOrWhiteSpace(string.Join(' ', args)))
        {
            throw new ArgumentException($"Usage: Nornia {Name} <query>");
        }

        CliOutput.PrintPackages(await services.PackageProvider.SearchAsync(string.Join(' ', args), services.ProcessProgress, cancellationToken));
        return 0;
    }
}

public sealed class PackageListCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Package} {CliCommands.List}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_PackageList");
    public string Usage => $"Nornia {Name}";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        CliOutput.PrintPackages(await services.PackageInventory.RefreshAsync(services.ProcessProgress, cancellationToken));
        return 0;
    }
}

public sealed class PackageInstallCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Package} {CliCommands.Install}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_PackageInstall");
    public string Usage => $"Nornia {Name} <id> [version]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException($"Usage: Nornia {Name} <id> [version]");
        }

        return await services.InstallPackageAsync(args[0], args.Count > 1 ? args[1] : null, cancellationToken);
    }
}

public sealed class PackageUninstallCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Package} {CliCommands.Uninstall}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_PackageUninstall");
    public string Usage => $"Nornia {Name} <id>";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException($"Usage: Nornia {Name} <id>");
        }

        return await services.UninstallPackageAsync(args[0], cancellationToken);
    }
}

public sealed class PackageUpgradeCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Package} {CliCommands.Upgrade}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_PackageUpgrade");
    public string Usage => $"Nornia {Name} <id>";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException($"Usage: Nornia {Name} <id>");
        }

        return await services.UpgradePackageAsync(args[0], cancellationToken);
    }
}

public sealed class PackageCacheScanCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Package} {CliCommands.Cache} {CliCommands.Scan}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_PackageCacheScan");
    public string Usage => $"Nornia {Name}";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        CliOutput.PrintCaches(await services.CacheInventory.ScanForcedAsync(services.CacheProgress, cancellationToken));
        return 0;
    }
}

public sealed class PackageCacheCleanCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Package} {CliCommands.Cache} {CliCommands.Clean}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_PackageCacheClean");
    public string Usage => $"Nornia {Name} --id <id> [--id <id> ...] [--apply]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        var apply = false;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], CliCommands.ApplyOption, StringComparison.OrdinalIgnoreCase))
            {
                apply = true;
                continue;
            }

            if (string.Equals(args[index], CliCommands.IdOption, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                ids.Add(args[++index]);
                continue;
            }

            throw new ArgumentException($"Usage: Nornia {Name}");
        }

        if (ids.Count == 0)
        {
            throw new ArgumentException(CliText.Get("Cache_Clean_RequiresId"));
        }

        var candidates = await services.CacheInventory.ScanAsync(services.CacheProgress, cancellationToken);
        CliOutput.PrintCaches(candidates.Where(candidate => ids.Contains(candidate.Id, StringComparer.OrdinalIgnoreCase)));
        if (!apply)
        {
            Console.WriteLine(CliText.Get("Cache_PreviewOnly"));
            return 0;
        }

        foreach (var result in await services.CacheCleanup.CleanAsync(ids, services.CacheProgress, cancellationToken))
        {
            Console.WriteLine($"{result.Status}\t{result.ReclaimedBytes}\t{result.Path}\t{result.Message}");
        }

        return 0;
    }
}
