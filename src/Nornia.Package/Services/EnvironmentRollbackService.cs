using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Localization;
using Nornia.Package.Providers;

namespace Nornia.Package.Services;

/// <summary>Rollback engine for env fix operations. Each repair step is persisted with its
/// previous/target versions and a rollback strategy; rollback runs them in reverse order,
/// flagging Winget downgrades that must be handled manually.</summary>
public sealed class EnvironmentRollbackService(
    IEnvironmentRepairLogRepository repairLogRepository,
    IPackageProvider packageProvider,
    PackageProviderSelector? providerSelector = null) : IEnvironmentRollbackService
{
    public async Task<EnvironmentRollbackPlan?> BuildPlanAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        var entries = await repairLogRepository.GetByCorrelationAsync(correlationId, cancellationToken);
        if (entries.Count == 0) return null;

        var actions = new List<EnvironmentRollbackAction>(entries.Count);
        // Reverse: undo the most recent operation first.
        foreach (var entry in entries.Where(e => e.Succeeded).OrderByDescending(e => e.Sequence))
        {
            var strategy = DetermineStrategy(entry);
            actions.Add(new EnvironmentRollbackAction(
                entry.Sequence,
                entry.Component,
                entry.TargetVersion,
                entry.PreviousVersion,
                entry.PackageId,
                entry.PackageVersion,
                strategy,
                strategy == RollbackStrategy.Manual
                    ? entry.RollbackHint ?? DefaultManualHint(entry)
                    : null));
        }

        var source = entries.OrderBy(e => e.Timestamp).First();
        return new EnvironmentRollbackPlan(correlationId, source.Timestamp, actions);
    }

    public async Task<EnvironmentRollbackResult> RollbackAsync(EnvironmentRollbackPlan plan, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProcessOutput(WingetText.Format("Rollback_Starting", plan.CorrelationId, plan.Actions.Count), false));

        var failures = new List<EnvironmentRollbackFailure>();
        var undoneCount = 0;
        var skippedCount = 0;

        for (var index = 0; index < plan.Actions.Count; index++)
        {
            var action = plan.Actions[index];
            progress?.Report(new ProcessOutput(
                WingetText.Format("Rollback_Step", index + 1, plan.Actions.Count, action.Component, action.FromVersion, action.ToVersion, action.Strategy), false));

            if (action.Strategy != RollbackStrategy.Automatic)
            {
                skippedCount++;
                progress?.Report(new ProcessOutput(
                    WingetText.Format("Rollback_Skipped", action.Hint), true));
                continue;
            }

            var selected = providerSelector is null
                ? packageProvider
                : await providerSelector.SelectAsync(null, cancellationToken);

            if (action.ToVersion == "0.0.0-uninstalled" || action.ToVersion == "none" || string.IsNullOrEmpty(action.ToVersion))
            {
                progress?.Report(new ProcessOutput(WingetText.Format("Package_UninstallUsing", action.PackageId, selected.Name), false));
                try
                {
                    await selected.UninstallAsync(action.PackageId, packageName: null, progress, cancellationToken);
                    undoneCount++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 卸载失败同样不得中断批次:记入结果,后续步骤照旧尝试。
                    failures.Add(CreateFailure(action, ex));
                    progress?.Report(new ProcessOutput(
                        WingetText.Format("Rollback_UninstallFailed", ex.Message, DefaultManualHint(action)), true));
                }

                continue;
            }

            // Downgrade / reinstall exact previous version.
            var targetVersion = action.PackageVersion ?? action.ToVersion;
            progress?.Report(new ProcessOutput(WingetText.Format("Package_InstallUsing", action.PackageId, targetVersion, selected.Name), false));
            try
            {
                await selected.InstallAsync(action.PackageId, targetVersion, progress, cancellationToken);
                undoneCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(CreateFailure(action, ex));
                progress?.Report(new ProcessOutput(
                    WingetText.Format("Rollback_InstallFailed", ex.Message, DefaultManualHint(action)), true));
            }
        }

        var result = new EnvironmentRollbackResult(
            plan.CorrelationId, plan.Actions.Count, undoneCount, skippedCount, failures);

        progress?.Report(new ProcessOutput(
            WingetText.Format("Rollback_Completed", result.UndoneCount, result.TotalSteps, result.SkippedCount, result.Failures.Count),
            result.RequiresManualIntervention));

        return result;
    }

    /// <summary>把单步异常固化为可呈现的失败项。原因是给用户看的,保留原文但去 NUL 与换行。</summary>
    private static EnvironmentRollbackFailure CreateFailure(EnvironmentRollbackAction action, Exception exception)
    {
        var reason = exception.Message.Replace("\0", string.Empty).Trim();
        if (reason.Length > 4_000)
        {
            reason = reason[..4_000] + "…";
        }

        return new EnvironmentRollbackFailure(
            action.OriginalSequence,
            action.Component,
            action.FromVersion,
            action.ToVersion,
            action.PackageId,
            action.PackageVersion,
            reason,
            DefaultManualHint(action));
    }

    private static RollbackStrategy DetermineStrategy(EnvironmentRepairLogEntry entry)
    {
        if (entry.RollbackStrategy != RollbackStrategy.Unsupported) return entry.RollbackStrategy;

        // If user asked for an upgrade (target > previous) and PackageVersion is explicit,
        // Winget's `--version` can in theory downgrade, but in practice many packages block it
        // unless the MSI has AllowDowngrades. Treat explicit PackageVersion as Manual for safety.
        if (entry.PreviousVersion != "0.0.0-uninstalled"
            && !string.IsNullOrWhiteSpace(entry.PackageVersion)
            && string.Equals(entry.Provider, "winget", StringComparison.OrdinalIgnoreCase))
        {
            return RollbackStrategy.Manual;
        }

        return RollbackStrategy.Automatic;
    }

    private static string DefaultManualHint(EnvironmentRepairLogEntry entry)
    {
        if (entry.PreviousVersion == "0.0.0-uninstalled")
        {
            return $"Winget: winget uninstall --id {entry.PackageId} --exact --silent";
        }
        return $"包管理器不支持直接降级。请先卸载：`winget uninstall --id {entry.PackageId}`，然后重装指定版本：`winget install --id {entry.PackageId} --version {entry.PreviousVersion} --exact --silent`（若仍失败可从厂商归档页面手动下载）。";
    }

    private static string DefaultManualHint(EnvironmentRollbackAction action)
    {
        if (action.ToVersion == "0.0.0-uninstalled" || string.IsNullOrEmpty(action.ToVersion))
        {
            return $"Winget: winget uninstall --id {action.PackageId} --exact --silent";
        }
        return $"包管理器不支持直接降级。请先卸载：`winget uninstall --id {action.PackageId}`，然后重装指定版本：`winget install --id {action.PackageId} --version {action.ToVersion} --exact --silent`（若仍失败可从厂商归档页面手动下载）。";
    }
}

/// <summary>Executes repair steps and appends each step's details to the environment repair
/// log file so <see cref="EnvironmentRollbackService"/> can undo them later.</summary>
public sealed class TrackedEnvironmentRepairExecutor(
    IPackageProvider packageProvider,
    PackageInventoryService packageInventoryService,
    IRuntimeInventoryService runtimeInventoryService,
    IEnvironmentRepairLogRepository repairLogRepository,
    IReadOnlyDictionary<string, string>? packageDowngradeBlacklist = null,
    PackageProviderSelector? providerSelector = null) : IEnvironmentRepairExecutor
{
    private static readonly HashSet<string> DefaultDowngradeBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.VCRedist.2015+.x64",
        "Microsoft.VCRedist.2015+.x86",
        "Microsoft.VCRedist.2015+.arm64",
        "Microsoft.DotNet.SDK.Preview",
        "Microsoft.WindowsAppRuntime"
    };

    private readonly HashSet<string> _downgradeBlacklist = new(
        packageDowngradeBlacklist?.Keys ?? DefaultDowngradeBlacklist,
        StringComparer.OrdinalIgnoreCase);

    public async Task<Guid> ExecuteAsync(
        IReadOnlyCollection<RuntimePackageOperation> operations,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid();

        var sequence = 0;
        var failures = new List<EnvironmentRepairFailure>();

        foreach (var operation in operations)
        {
            sequence++;
            var previousVersion = await DetectCurrentVersionAsync(operation.Component, cancellationToken);
            var providerName = operation.PreferredProvider ?? packageProvider.Name;
            progress?.Report(new ProcessOutput(
                WingetText.Format("Repair_StepProgress", sequence, operations.Count, operation.Component, previousVersion, operation.TargetVersion), false));

            Exception? error = null;
            var diagnosticLines = new List<string>();
            RollbackStrategy strategy = RollbackStrategy.Unsupported;
            try
            {
                var selectedProvider = providerSelector is null
                    ? packageProvider
                    : await providerSelector.SelectAsync(operation.PreferredProvider, cancellationToken);
                providerName = selectedProvider.Name;
                var providerProgress = new InlineProgressHandler(output =>
                {
                    if (output.IsError)
                    {
                        diagnosticLines.Add(output.Text);
                    }
                    else
                    {
                        progress?.Report(output);
                    }
                });
                var installMessage = operation.PackageVersion is null
                    ? WingetText.Format("Package_InstallNoVersion", operation.PackageId, selectedProvider.Name)
                    : WingetText.Format("Package_InstallUsing", operation.PackageId, operation.PackageVersion, selectedProvider.Name);
                progress?.Report(new ProcessOutput(installMessage, false));
                await selectedProvider.InstallAsync(
                    operation.PackageId,
                    operation.PackageVersion,
                    providerProgress,
                    cancellationToken);
                strategy = DetermineRollbackStrategy(operation, selectedProvider.Name, previousVersion);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex;
                strategy = DetermineRollbackStrategy(operation, providerName, previousVersion);
                var failure = CreateFailure(
                    sequence, operations.Count, operation, previousVersion, providerName, strategy, ex, diagnosticLines);
                failures.Add(failure);
                progress?.Report(new ProcessOutput(FormatFailure(failure), true));
            }

            var entry = new EnvironmentRepairLogEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                correlationId,
                sequence,
                operation.Component,
                previousVersion,
                operation.TargetVersion,
                operation.PackageId,
                operation.PackageVersion,
                providerName,
                error is null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                strategy,
                error?.Message,
                strategy == RollbackStrategy.Manual
                    ? "Winget 不支持直接降级；请卸载后手动安装旧版或使用厂商安装程序。"
                    : null);
            await repairLogRepository.AppendAsync(entry, cancellationToken);
        }

        progress?.Report(new ProcessOutput(WingetText.Get("Repair_RefreshSnapshots"), false));
        Exception? snapshotError = null;
        try
        {
            // 回滚改变了环境:两个清单都必须绕过合并缓存与持久化快照 TTL 强制重扫。
            await packageInventoryService.RefreshForcedAsync(progress, cancellationToken);
            await runtimeInventoryService.RefreshForcedAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            snapshotError = ex;
            progress?.Report(new ProcessOutput($"修复后刷新环境快照失败：{ex.Message}", true));
        }

        if (failures.Count > 0)
        {
            throw new EnvironmentRepairException(correlationId, failures);
        }

        if (snapshotError is not null)
        {
            throw snapshotError;
        }

        // 成功返回 correlation_id：它是回滚这一批次的唯一定位键，调用方负责呈现给用户。
        return correlationId;
    }

    private static EnvironmentRepairFailure CreateFailure(
        int sequence,
        int total,
        RuntimePackageOperation operation,
        string previousVersion,
        string provider,
        RollbackStrategy strategy,
        Exception exception,
        IReadOnlyCollection<string> diagnosticLines)
    {
        var diagnosticOutput = CombineDiagnostics(exception, diagnosticLines);
        return new EnvironmentRepairFailure(
            sequence,
            total,
            operation.Component,
            previousVersion,
            operation.TargetVersion,
            operation.PackageId,
            operation.PackageVersion,
            provider,
            exception.GetType().Name,
            Limit(exception.Message),
            diagnosticOutput,
            exception is WingetException winget ? winget.ExitCode : null,
            strategy,
            strategy == RollbackStrategy.Manual
                ? WingetText.Get("Repair_ManualRollbackHint")
                : null);
    }

    private static string FormatFailure(EnvironmentRepairFailure failure)
    {
        var lines = new List<string>
        {
            WingetText.Format("Repair_FailureHeader", failure.Sequence, failure.Total, failure.Component),
            WingetText.Format("Repair_FailureCurrent", failure.PreviousVersion),
            WingetText.Format("Repair_FailureTarget", failure.TargetVersion),
            WingetText.Format("Repair_FailureProvider", failure.Provider),
            WingetText.Format("Repair_FailurePackage", $"{failure.PackageId}{(string.IsNullOrWhiteSpace(failure.PackageVersion) ? string.Empty : $" ({failure.PackageVersion})")}"),
            WingetText.Format("Repair_FailureException", failure.ExceptionType),
            WingetText.Format("Repair_FailureReason", failure.FailureReason),
        };
        if (failure.ExitCode is { } exitCode)
        {
            lines.Add(WingetText.Format("Repair_FailureExitCode", WingetExitCodes.Format(exitCode), exitCode));
        }
        if (!string.IsNullOrWhiteSpace(failure.DiagnosticOutput))
        {
            lines.Add(WingetText.Format("Repair_FailureOutput", failure.DiagnosticOutput));
        }
        lines.Add(WingetText.Format("Repair_FailureRollback", failure.RollbackStrategy));
        if (!string.IsNullOrWhiteSpace(failure.RollbackHint))
        {
            lines.Add(WingetText.Format("Repair_FailureHint", failure.RollbackHint));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string CombineDiagnostics(Exception exception, IReadOnlyCollection<string> diagnosticLines)
    {
        var details = diagnosticLines
            .Append(exception is WingetException winget ? winget.OutputDetail : string.Empty)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .SelectMany(line => line.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Limit(string.Join(" | ", details));
    }

    private static string Limit(string value, int maximum = 4_000)
    {
        var normalized = value.Replace("\0", string.Empty).Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }

    private RollbackStrategy DetermineRollbackStrategy(RuntimePackageOperation operation, string provider, string previousVersion)
    {
        // If we are installing something that previously was not there, uninstall is usually safe.
        if (previousVersion == "0.0.0-uninstalled" || previousVersion == "none")
        {
            return RollbackStrategy.Automatic;
        }

        // If PackageVersion is explicit and the package is a known blocklist entry, mark manual.
        if (operation.PackageVersion is not null && _downgradeBlacklist.Contains(operation.PackageId))
        {
            return RollbackStrategy.Manual;
        }

        // Winget downgrade is unreliable even with --version.
        if (string.Equals(provider, "winget", StringComparison.OrdinalIgnoreCase))
        {
            return RollbackStrategy.Manual;
        }

        return RollbackStrategy.Automatic;
    }

    private async Task<string> DetectCurrentVersionAsync(string component, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await runtimeInventoryService.GetPersistedAsync(cancellationToken);
            // 同一组件可能有多个 side-by-side 实例(.NET SDK feature band / VC++ 多架构):
            // 必须取数值序最大的那个作为回滚基点,否则会记录错误的 previous_version。
            var match = snapshot
                .Where(r =>
                    string.Equals(r.Name, component, StringComparison.OrdinalIgnoreCase)
                    || (EnvironmentComponentCatalog.TryGet(component, out var def)
                        && string.Equals(EnvironmentComponentCatalog.Get(r.Name)?.Id, def.Id, StringComparison.OrdinalIgnoreCase)))
                .MaxByVersion(r => r.Version);
            return match?.Version ?? "0.0.0-uninstalled";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>同步执行回调的进度转发。内部 stderr 捕获必须确定性:
    /// <see cref="Progress{T}"/> 默认把回调 Post 到捕获的 SynchronizationContext/线程池,
    /// provider 同步连续 Report 后立即抛出时部分回调尚未执行,诊断信息会丢行、乱序。</summary>
    private sealed class InlineProgressHandler(Action<ProcessOutput> handle) : IProgress<ProcessOutput>
    {
        public void Report(ProcessOutput value) => handle(value);
    }
}
