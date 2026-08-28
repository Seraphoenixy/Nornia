using Nornia.Core.Models;

namespace Nornia.Project.Models;

public sealed record NorniaProject(Guid Id, string Name, string Path, EnvironmentProfile EnvironmentProfile);

public sealed class EnvironmentProfile
{
    public ProjectMetadata Project { get; set; } = new();
    public Dictionary<string, VersionRequirement> Runtime { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VersionRequirement> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Architectures { get; set; } = [];
    public List<string> OperatingSystems { get; set; } = [];

    /// <summary>When true, the builtin version-range rule always runs even when Rules list is populated.
    /// When false, populating Rules fully replaces the default version check (use 'type: version-range' to
    /// include it explicitly).</summary>
    public bool RunBuiltinVersionRangeByDefault { get; set; } = true;

    /// <summary>Custom rule declarations parsed from the <c>rules:</c> YAML section.</summary>
    public List<RuleDeclaration> Rules { get; set; } = [];
}

public sealed class ProjectMetadata
{
    public string Name { get; set; } = string.Empty;
    public string? StartCommand { get; set; }
}

public sealed class VersionRequirement
{
    public string Version { get; set; } = string.Empty;
    public string? PackageProvider { get; set; }
}

/// <summary>YAML-serializable rule declaration. <c>Type</c> maps to the builtin registry; unknown
/// types are skipped with a warning instead of failing the whole profile load.</summary>
public sealed class RuleDeclaration
{
    public string Type { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, object> Config { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnvironmentRule? CreateInstance()
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Type)) return null;
        return EnvironmentRuleRegistry.CreateBuiltin(Type, Config);
    }
}

public sealed record EnvironmentCheckResult(
    EnvironmentCheckStatus Status,
    string Component,
    string RequiredVersion,
    string? InstalledVersion,
    string Message,
    EnvironmentCheckReason Reason = EnvironmentCheckReason.Satisfied);

public enum EnvironmentCheckStatus
{
    Pass,
    Fail,
    Warning
}

public enum EnvironmentCheckReason
{
    Satisfied,
    Missing,
    VersionMismatch,
    InvalidConstraint
}

public sealed record EnvironmentRepairPlan(IReadOnlyList<EnvironmentRepairAction> Actions)
{
    public IReadOnlyList<RuntimePackageOperation> Operations => Actions
        .Where(action => action.Disposition == EnvironmentRepairDisposition.InstallOrUpgrade)
        .SelectMany(action => action.AllOperations)
        .ToArray();

    public bool HasOutstandingRequirements => Actions.Any(action => action.Disposition != EnvironmentRepairDisposition.NoAction);
}

public sealed record EnvironmentRepairAction(
    string Component,
    string RequiredVersion,
    EnvironmentRepairDisposition Disposition,
    string Message,
    RuntimePackageOperation? Operation = null,
    IReadOnlyList<RuntimePackageOperation>? AdditionalOperations = null)
{
    public IReadOnlyList<RuntimePackageOperation> AllOperations => Operation is null
        ? []
        : [Operation, .. AdditionalOperations ?? []];
}

public enum EnvironmentRepairDisposition
{
    NoAction,
    InstallOrUpgrade,
    Manual
}