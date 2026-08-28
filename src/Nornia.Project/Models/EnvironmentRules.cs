using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Project.Services;
using System.Text.RegularExpressions;

namespace Nornia.Project.Models;

public interface IEnvironmentRule
{
    string RuleId { get; }
    string DisplayName { get; }
    string? Description { get; }
    bool Enabled { get; }

    Task<EnvironmentRuleResult> EvaluateAsync(
        EnvironmentProfile profile,
        IReadOnlyCollection<Runtime> installedRuntimes,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default);
}

public enum EnvironmentRuleSeverity
{
    Info,
    Warning,
    Error
}

public sealed record EnvironmentRuleResult(
    string RuleId,
    bool Passed,
    EnvironmentRuleSeverity Severity,
    string Message,
    string? Hint = null,
    IReadOnlyDictionary<string, object>? Metadata = null)
{
    public static EnvironmentRuleResult Pass(string ruleId, string message, string? hint = null) =>
        new(ruleId, true, EnvironmentRuleSeverity.Info, message, hint);

    public static EnvironmentRuleResult Fail(string ruleId, EnvironmentRuleSeverity severity, string message, string? hint = null) =>
        new(ruleId, false, severity, message, hint);
}

public abstract class EnvironmentRuleBase : IEnvironmentRule
{
    public abstract string RuleId { get; }
    public abstract string DisplayName { get; }
    public virtual string? Description => null;
    public virtual bool Enabled => true;

    public abstract Task<EnvironmentRuleResult> EvaluateAsync(
        EnvironmentProfile profile,
        IReadOnlyCollection<Runtime> installedRuntimes,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default);
}

public sealed class VersionRangeRule : EnvironmentRuleBase
{
    public override string RuleId => "builtin.version-range";
    public override string DisplayName => "Version Range Check";
    public override string Description => "Validates Runtime/Tools version requirements against the installed set.";

    public override Task<EnvironmentRuleResult> EvaluateAsync(
        EnvironmentProfile profile,
        IReadOnlyCollection<Runtime> installedRuntimes,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        var all = profile.Runtime.Concat(profile.Tools).ToList();
        if (all.Count == 0)
        {
            return Task.FromResult(EnvironmentRuleResult.Pass(RuleId, "No version requirements defined."));
        }

        var failures = new List<string>();
        foreach (var (key, requirement) in all)
        {
            var result = EnvironmentCheckEngine.CheckRequirementStatic(key, requirement, installedRuntimes);
            if (result.Status != EnvironmentCheckStatus.Pass)
            {
                failures.Add(result.Message);
            }
        }

        return Task.FromResult(failures.Count == 0
            ? EnvironmentRuleResult.Pass(RuleId, $"All {all.Count} version requirements satisfied.")
            : EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Error,
                $"{failures.Count} version requirement(s) failed: {string.Join("; ", failures)}",
                "Run 'nornia env plan' to generate a repair plan."));
    }
}

public sealed class RequiredCommandRule : EnvironmentRuleBase
{
    public override string RuleId => "builtin.required-command";
    public override string DisplayName => "Required Command Presence";

    public string Command { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = ["--version"];
    public string? ExpectedOutputPattern { get; init; }
    public EnvironmentRuleSeverity Severity { get; init; } = EnvironmentRuleSeverity.Error;

    public RequiredCommandRule() { }

    public RequiredCommandRule(string command, params string[] arguments)
    {
        Command = command;
        if (arguments.Length > 0) Arguments = arguments;
    }

    public override async Task<EnvironmentRuleResult> EvaluateAsync(
        EnvironmentProfile profile,
        IReadOnlyCollection<Runtime> installedRuntimes,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Command))
        {
            return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Warning,
                "Required-command rule has no 'command' field configured.", "Add command: to the rule.");
        }

        var runner = services?.GetService(typeof(IProcessRunner)) as IProcessRunner;
        if (runner is null)
        {
            return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Warning,
                $"Cannot verify command '{Command}': IProcessRunner not available.",
                "This rule only runs in a fully-composed Nornia host.");
        }

        ProcessResult result;
        try
        {
            result = await runner!.RunAsync(Command, Arguments, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            return EnvironmentRuleResult.Fail(RuleId, Severity,
                $"Command '{Command}' could not be started: {ex.Message}",
                "Ensure the executable is on PATH.");
        }

        if (!result.IsSuccess)
        {
            return EnvironmentRuleResult.Fail(RuleId, Severity,
                $"Command '{Command}' returned exit code {result.ExitCode}.",
                ExpectedOutputPattern is not null
                    ? $"Run `{Command} {string.Join(" ", Arguments)}` and verify output matches /{ExpectedOutputPattern}/"
                    : $"Run `{Command} {string.Join(" ", Arguments)}` manually to diagnose.");
        }

        if (ExpectedOutputPattern is not null)
        {
            var combined = $"{result.StandardOutput}\n{result.StandardError}";
            if (!Regex.IsMatch(combined, ExpectedOutputPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
            {
                return EnvironmentRuleResult.Fail(RuleId, Severity,
                    $"Command '{Command}' ran successfully but output did not match pattern /{ExpectedOutputPattern}/.",
                    "Verify the command output matches the rule's expectedOutputPattern.");
            }
        }

        return EnvironmentRuleResult.Pass(RuleId,
            $"Command '{Command}' is available{(!string.IsNullOrWhiteSpace(ExpectedOutputPattern) ? $" and output matches /{ExpectedOutputPattern}/." : ".")}");
    }
}

public sealed class Wsl2EnabledRule : EnvironmentRuleBase
{
    public override string RuleId => "builtin.wsl2-enabled";
    public override string DisplayName => "WSL2 Enabled";
    public override string Description => "Checks that WSL 2 is enabled and a default distribution is registered.";

    public override async Task<EnvironmentRuleResult> EvaluateAsync(
        EnvironmentProfile profile,
        IReadOnlyCollection<Runtime> installedRuntimes,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        var runner = services?.GetService(typeof(IProcessRunner)) as IProcessRunner;
        if (runner is null)
        {
            return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Warning,
                "Cannot verify WSL2: IProcessRunner not available.", null);
        }

        try
        {
            var status = await runner.RunAsync("wsl.exe", ["--status"], cancellationToken: cancellationToken);
            if (!status.IsSuccess)
            {
                return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Error,
                    "WSL is not installed or not available. Run 'wsl --install' in an elevated terminal.",
                    "Microsoft-Windows-Subsystem-Linux and VirtualMachinePlatform optional features must be enabled.");
            }

            var combined = $"{status.StandardOutput}\n{status.StandardError}";
            var versionMatch = Regex.Match(combined, @"WSL version:\s*([0-9.]+)", RegexOptions.IgnoreCase);
            var defaultMatch = Regex.Match(combined, @"Default Distribution:\s*(\S+)", RegexOptions.IgnoreCase);
            var versionOk = versionMatch.Success && versionMatch.Groups[1].Value.StartsWith("2.");

            if (!defaultMatch.Success)
            {
                return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Warning,
                    "WSL is installed but no default distribution is registered.",
                    "Run 'wsl --install -d Ubuntu' or 'wsl --set-default <Distro>'.");
            }

            if (!versionOk)
            {
                return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Error,
                    "WSL is installed but the kernel is not version 2.",
                    "Run 'wsl --update' and 'wsl --set-default-version 2'.");
            }

            return EnvironmentRuleResult.Pass(RuleId,
                $"WSL2 is enabled. Default distribution: {defaultMatch.Groups[1].Value}. Kernel: {versionMatch.Groups[1].Value}");
        }
        catch (Exception ex)
        {
            return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Error,
                $"WSL2 check failed with an exception: {ex.Message}",
                "Enable the Microsoft-Windows-Subsystem-Linux feature and reboot.");
        }
    }
}

public sealed class VsCodeExtensionRule : EnvironmentRuleBase
{
    public override string RuleId => "builtin.vscode-extension";
    public override string DisplayName => "VS Code Extension Installed";

    public required string ExtensionId { get; init; }
    public EnvironmentRuleSeverity Severity { get; init; } = EnvironmentRuleSeverity.Warning;

    public override async Task<EnvironmentRuleResult> EvaluateAsync(
        EnvironmentProfile profile,
        IReadOnlyCollection<Runtime> installedRuntimes,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ExtensionId))
        {
            return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Warning,
                "VS Code extension rule has no 'extensionId' field.",
                "Set extensionId: publisher.name in the rule configuration.");
        }

        var runner = services?.GetService(typeof(IProcessRunner)) as IProcessRunner;
        if (runner is null)
        {
            return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Warning,
                $"Cannot verify VS Code extension '{ExtensionId}': IProcessRunner not available.", null);
        }

        var candidates = new[] { "code", "code-insiders" };
        foreach (var cmd in candidates)
        {
            ProcessResult result;
            try
            {
                result = await runner.RunAsync(cmd, ["--list-extensions"], cancellationToken: cancellationToken);
            }
            catch
            {
                continue;
            }

            if (!result.IsSuccess) continue;

            var installed = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line));

            if (installed.Any(id => string.Equals(id, ExtensionId, StringComparison.OrdinalIgnoreCase)))
            {
                return EnvironmentRuleResult.Pass(RuleId,
                    $"VS Code extension '{ExtensionId}' is installed (via '{cmd}').",
                    hint: "Extensions are per-user, so other users may need to install it separately.");
            }

            return EnvironmentRuleResult.Fail(RuleId, Severity,
                $"VS Code extension '{ExtensionId}' is not installed.",
                hint: $"Run: {cmd} --install-extension {ExtensionId}");
        }

        return EnvironmentRuleResult.Fail(RuleId, EnvironmentRuleSeverity.Warning,
            "Neither 'code' nor 'code-insiders' could be found on PATH.",
            "Install VS Code and ensure it is on PATH (run 'Shell Command: Install code command in PATH' from the Command Palette).");
    }
}

public static class EnvironmentRuleRegistry
{
    public static IReadOnlyDictionary<string, Type> BuiltinRules { get; } = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["version-range"] = typeof(VersionRangeRule),
        ["required-command"] = typeof(RequiredCommandRule),
        ["wsl2-enabled"] = typeof(Wsl2EnabledRule),
        ["vscode-extension"] = typeof(VsCodeExtensionRule)
    };

    public static IEnvironmentRule? CreateBuiltin(string ruleType, IReadOnlyDictionary<string, object> config)
    {
        if (!BuiltinRules.TryGetValue(ruleType, out var type)) return null;

        try
        {
            var instance = Activator.CreateInstance(type) as IEnvironmentRule;
            if (instance is null) return null;

            foreach (var (key, value) in config)
            {
                var prop = type.GetProperty(key,
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase);
                if (prop is null || !prop.CanWrite) continue;
                try
                {
                    var converted = Convert.ChangeType(value, prop.PropertyType);
                    prop.SetValue(instance, converted);
                }
                catch
                {
                    // Ignore malformed fields; the rule will still run with defaults (and likely report the issue).
                }
            }
            return instance;
        }
        catch
        {
            return null;
        }
    }
}