using CoreRuntime = Nornia.Core.Models.Runtime;
using Nornia.Core.Models;
using Nornia.Project.Models;

namespace Nornia.Project.Services;

public interface IEnvironmentCheckEngine
{
    IReadOnlyList<EnvironmentCheckResult> Check(
        EnvironmentProfile profile,
        IReadOnlyCollection<Runtime> installedRuntimes);

    Task<EnvironmentCheckRuleReport> CheckWithRulesAsync(
        EnvironmentProfile profile,
        IReadOnlyCollection<Runtime> installedRuntimes,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default);
}

public sealed record EnvironmentCheckRuleReport(
    IReadOnlyList<EnvironmentCheckResult> VersionResults,
    IReadOnlyList<EnvironmentRuleResult> RuleResults)
{
    public bool AllPassed =>
        VersionResults.All(v => v.Status == EnvironmentCheckStatus.Pass)
        && RuleResults.All(r => r.Passed);

    public int TotalFailures =>
        VersionResults.Count(v => v.Status != EnvironmentCheckStatus.Pass)
        + RuleResults.Count(r => !r.Passed);
}

public sealed class EnvironmentCheckEngine : IEnvironmentCheckEngine
{
    public IReadOnlyList<EnvironmentCheckResult> Check(
        EnvironmentProfile profile,
        IReadOnlyCollection<CoreRuntime> installedRuntimes)
    {
        var requirements = profile.Runtime.Concat(profile.Tools);
        return requirements.Select(requirement => CheckRequirementStatic(requirement.Key, requirement.Value, installedRuntimes)).ToArray();
    }

    public async Task<EnvironmentCheckRuleReport> CheckWithRulesAsync(
        EnvironmentProfile profile,
        IReadOnlyCollection<CoreRuntime> installedRuntimes,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        var versionResults = Check(profile, installedRuntimes).ToList();

        var rules = new List<IEnvironmentRule>();
        if (profile.RunBuiltinVersionRangeByDefault || profile.Rules.Count == 0)
        {
            rules.Add(new VersionRangeRule());
        }

        foreach (var declaration in profile.Rules)
        {
            var rule = declaration.CreateInstance();
            if (rule is not null && rule.Enabled)
            {
                rules.Add(rule);
            }
        }

        var ruleResults = new List<EnvironmentRuleResult>(rules.Count);
        foreach (var rule in rules)
        {
            EnvironmentRuleResult result;
            try
            {
                result = await rule.EvaluateAsync(profile, installedRuntimes, services, cancellationToken);
            }
            catch (Exception ex)
            {
                result = EnvironmentRuleResult.Fail(
                    rule.RuleId, EnvironmentRuleSeverity.Warning,
                    $"Rule '{rule.DisplayName}' threw during evaluation: {ex.Message}",
                    "Check the rule configuration.");
            }
            ruleResults.Add(result);
        }

        return new EnvironmentCheckRuleReport(versionResults, ruleResults);
    }

    internal static EnvironmentCheckResult CheckRequirementStatic(
        string key,
        VersionRequirement requirement,
        IReadOnlyCollection<CoreRuntime> installedRuntimes)
    {
        var definition = EnvironmentComponentCatalog.Get(key);
        var component = definition?.DisplayName ?? key;
        if (!VersionConstraintParser.TryParse(requirement.Version, out var constraint))
        {
            return new EnvironmentCheckResult(
                EnvironmentCheckStatus.Fail, component, requirement.Version, null,
                $"{component} version constraint '{requirement.Version}' is invalid", EnvironmentCheckReason.InvalidConstraint);
        }

        var installed = installedRuntimes
            .Where(runtime => definition is not null
                ? string.Equals(EnvironmentComponentCatalog.Get(runtime.Name)?.Id, definition.Id, StringComparison.OrdinalIgnoreCase)
                : string.Equals(runtime.Name, component, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(runtime => runtime.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (installed.Length == 0)
        {
            return new EnvironmentCheckResult(
                EnvironmentCheckStatus.Fail, component, requirement.Version, null,
                $"{component} {requirement.Version} missing", EnvironmentCheckReason.Missing);
        }

        var match = installed.FirstOrDefault(runtime => constraint!.IsSatisfiedBy(runtime.Version));
        if (match is not null)
        {
            return new EnvironmentCheckResult(
                EnvironmentCheckStatus.Pass, component, requirement.Version, match.Version,
                $"{component} {requirement.Version} installed", EnvironmentCheckReason.Satisfied);
        }

        return new EnvironmentCheckResult(
            EnvironmentCheckStatus.Warning, component, requirement.Version, installed[0].Version,
            $"{component} version mismatch: required {requirement.Version}, installed {installed[0].Version}", EnvironmentCheckReason.VersionMismatch);
    }
}