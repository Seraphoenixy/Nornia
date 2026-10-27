using Nornia.CLI.Localization;
using Nornia.Core.Models;
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
        var pathProvided = false;
        var apply = false;
        foreach (var argument in args)
        {
            if (string.Equals(argument, CliCommands.ApplyOption, StringComparison.OrdinalIgnoreCase))
            {
                apply = true;
            }
            else if (!argument.StartsWith("--", StringComparison.Ordinal) && !pathProvided)
            {
                path = argument;
                pathProvided = true;
            }
            else
            {
                // 未知选项与多余的路径参数一律按用法错误处理(退出码 2)。
                // 旧实现把任何非 -- 参数当路径、把 --force 之类静默吞掉,与 package cache clean 不一致。
                throw new CliUsageException($"Usage: Nornia {Name} [path] [--apply]");
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
            // 把批次 id 交还给用户:这是回滚这一批修复的唯一钥匙,不打印等于回滚无处可用。
            var correlationId = await services.RepairExecutor.ExecuteAsync(plan.Operations, services.ProcessProgress, cancellationToken);
            Console.WriteLine(CliText.Format("Env_FixBatchId", correlationId.ToString("N")));
        }

        // Re-evaluate after applying repairs and return the resulting status.
        var refreshed = await services.InspectEnvironmentAsync(path, cancellationToken);
        foreach (var result in refreshed.Results)
        {
            Console.WriteLine($"{result.Status.ToString().ToUpperInvariant()}: {result.Message}");
        }

        return refreshed.Results.All(result => result.Status == EnvironmentCheckStatus.Pass) ? 0 : 1;
    }
}

/// <summary>撤销一次已应用的环境修复批次。批次由 <c>env fix --apply</c> 输出(或修复失败异常中)的
/// <c>correlation_id</c> 定位,回滚按步骤逆序执行:自动步骤直接撤销,需要人工处理的步骤打印命令提示。
/// 与修复一样默认只预览,加 <c>--apply</c> 才真正动机器。</summary>
public sealed class EnvRollbackCommand(CliServices services) : ICommand
{
    public string Name => $"{CliCommands.Environment} {CliCommands.Rollback}";
    public IReadOnlyList<string> Aliases => [];
    public string Summary => CliText.Get("Summary_EnvRollback");
    public string Usage => $"Nornia {Name} <correlation-id> [--apply]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        string? rawId = null;
        var apply = false;
        foreach (var argument in args)
        {
            if (string.Equals(argument, CliCommands.ApplyOption, StringComparison.OrdinalIgnoreCase))
            {
                apply = true;
            }
            else if (!argument.StartsWith("--", StringComparison.Ordinal) && rawId is null)
            {
                rawId = argument;
            }
            else
            {
                // 未知选项与多余的位置参数一律按用法错误处理(退出码 2),而不是静默忽略。
                throw new CliUsageException($"Usage: Nornia {Name} <correlation-id> [--apply]");
            }
        }

        if (rawId is null || !Guid.TryParse(rawId, out var correlationId))
        {
            throw new CliUsageException(CliText.Format("Rollback_InvalidId", rawId ?? string.Empty));
        }

        var plan = await services.RollbackService.BuildPlanAsync(correlationId, cancellationToken);
        if (plan is null)
        {
            Console.Error.WriteLine(CliText.Format("Rollback_NoBatch", correlationId.ToString("N")));
            return 1;
        }

        CliOutput.PrintRollbackPlan(plan);
        if (!apply)
        {
            Console.WriteLine(CliText.Get("Rollback_PreviewOnly"));
            return 0;
        }

        var result = await services.RollbackService.RollbackAsync(plan, services.ProcessProgress, cancellationToken);
        Console.WriteLine(CliText.Format("Rollback_Applied", result.UndoneCount, result.TotalSteps, result.SkippedCount, result.Failures.Count));
        foreach (var failure in result.Failures)
        {
            Console.Error.WriteLine(CliText.Format("Rollback_StepFailed", failure.Component, failure.Reason));
            if (!string.IsNullOrWhiteSpace(failure.Hint))
            {
                Console.Error.WriteLine(failure.Hint);
            }
        }

        return result.Failures.Count == 0 ? 0 : 1;
    }
}
