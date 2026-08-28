using Nornia.CLI.Localization;
using Nornia.Project.Models;

namespace Nornia.CLI.Commands;

public sealed class EnvCheckCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Environment} {CliCommands.Check}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_EnvCheck");
    public string Usage => $"Nornia {Name} [path]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var path = args.Count > 0 ? args[0] : Environment.CurrentDirectory;
        var (_, _, results) = await services.InspectEnvironmentAsync(path, cancellationToken);

        if (results.Count == 0)
        {
            Console.WriteLine(CliText.Get("Env_NoRequirements"));
            return 0;
        }

        foreach (var result in results)
        {
            Console.WriteLine($"{result.Status.ToString().ToUpperInvariant()}: {result.Message}");
        }

        return results.All(result => result.Status == EnvironmentCheckStatus.Pass) ? 0 : 1;
    }
}

public sealed class EnvPlanCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Environment} {CliCommands.Plan}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_EnvPlan");
    public string Usage => $"Nornia {Name} [path]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var path = args.Count > 0 ? args[0] : Environment.CurrentDirectory;
        var (_, installed, results) = await services.InspectEnvironmentAsync(path, cancellationToken);
        var plan = services.RepairPlanner.CreatePlan(results, installed);
        CliOutput.PrintRepairPlan(plan);
        return plan.HasOutstandingRequirements ? 1 : 0;
    }
}

public sealed class EnvFixCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Environment} {CliCommands.Fix}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_EnvFix");
    public string Usage => $"Nornia {Name} [path] [--apply]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var path = Environment.CurrentDirectory;
        var apply = false;
        foreach (var argument in args)
        {
            if (string.Equals(argument, CliCommands.ApplyOption, StringComparison.OrdinalIgnoreCase))
            {
                apply = true;
            }
            else if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                path = argument;
            }
        }

        var (_, installed, results) = await services.InspectEnvironmentAsync(path, cancellationToken);
        var plan = services.RepairPlanner.CreatePlan(results, installed);
        CliOutput.PrintRepairPlan(plan);
        if (!apply)
        {
            Console.WriteLine(CliText.Get("Env_PlanPreviewOnly"));
            return plan.HasOutstandingRequirements ? 1 : 0;
        }

        if (plan.Operations.Count > 0)
        {
            await services.RepairExecutor.ExecuteAsync(plan.Operations, services.ProcessProgress, cancellationToken);
        }

        // Re-evaluate after applying repairs and return the resulting status.
        var refreshed = await services.InspectEnvironmentAsync(path, cancellationToken);
        foreach (var result in refreshed.Results)
        {
            Console.WriteLine($"{result.Status.ToString().ToUpperInvariant()}: {result.Message}");
        }

        return refreshed.Results.All(result => result.Status == Nornia.Project.Models.EnvironmentCheckStatus.Pass) ? 0 : 1;
    }
}
