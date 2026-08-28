using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Project.Models;

namespace Nornia.Project.Services;

public interface IEnvironmentRepairPlanner
{
    EnvironmentRepairPlan CreatePlan(
        IReadOnlyCollection<EnvironmentCheckResult> checkResults,
        IReadOnlyCollection<Runtime> installedRuntimes);
}

public sealed class EnvironmentRepairPlanner(IRuntimePackageResolver packageResolver) : IEnvironmentRepairPlanner
{
    public EnvironmentRepairPlan CreatePlan(
        IReadOnlyCollection<EnvironmentCheckResult> checkResults,
        IReadOnlyCollection<Runtime> installedRuntimes)
    {
        _ = installedRuntimes;
        var actions = checkResults.Select(CreateAction).ToArray();
        return new EnvironmentRepairPlan(actions);
    }

    private EnvironmentRepairAction CreateAction(EnvironmentCheckResult result)
    {
        if (result.Reason == EnvironmentCheckReason.Satisfied)
        {
            return new EnvironmentRepairAction(result.Component, result.RequiredVersion, EnvironmentRepairDisposition.NoAction, "Requirement satisfied.");
        }

        if (result.Reason == EnvironmentCheckReason.InvalidConstraint)
        {
            return new EnvironmentRepairAction(result.Component, result.RequiredVersion, EnvironmentRepairDisposition.Manual, "Invalid version constraint must be corrected in Nornia.yaml.");
        }

        if (!VersionConstraintParser.TryParse(result.RequiredVersion, out var constraint))
        {
            return new EnvironmentRepairAction(result.Component, result.RequiredVersion, EnvironmentRepairDisposition.Manual, "Version constraint cannot be resolved automatically.");
        }

        var targetVersion = constraint!.GetRepairTargetVersion();
        if (targetVersion is null)
        {
            return new EnvironmentRepairAction(result.Component, result.RequiredVersion, EnvironmentRepairDisposition.Manual, "A bounded range with an inclusive lower version is required for automatic repair.");
        }

        var operations = packageResolver.TryResolveAll(result.Component, targetVersion);
        return operations is null || operations.Count == 0
            ? new EnvironmentRepairAction(result.Component, result.RequiredVersion, EnvironmentRepairDisposition.Manual, "No supported package mapping is available; repair manually.")
            : new EnvironmentRepairAction(result.Component, result.RequiredVersion, EnvironmentRepairDisposition.InstallOrUpgrade, $"Install or upgrade to {targetVersion} using {string.Join(", ", operations.Select(operation => operation.PackageId))}.", operations[0], operations.Skip(1).ToArray());
    }
}
