using Nornia.Core.Models;
using Nornia.Project.Models;
using Nornia.Project.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

/// <summary>Boundary coverage for the EnvironmentCheckEngine ↔ VersionConstraintParser seam: alias
/// resolution, missing/mismatch transitions, prerelease policy and multi-version selection.</summary>
public sealed class EnvironmentCheckEngineTests
{
    private static EnvironmentCheckResult Check(string component, string requirement, params CoreRuntime[] installed) =>
        Assert.Single(new EnvironmentCheckEngine().Check(CreateProfile(component, requirement), installed));

    [Fact]
    public void Check_MatchesInstalledRuntimeByCatalogAlias()
    {
        // The profile key is a catalog alias ("vcredist"); the installed runtime uses its display name.
        var result = Check("vcredist", "14.51", Runtime("Visual C++ Redistributable", "14.51.2025"));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(EnvironmentCheckReason.Satisfied, result.Reason);
        Assert.Equal("14.51.2025", result.InstalledVersion);
    }

    [Fact]
    public void Check_ReportsWarningWhenNoSatisfyingVersionIsInstalled()
    {
        var result = Check("node", ">=22 <23", Runtime("Node.js", "23.0.0"));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(EnvironmentCheckReason.VersionMismatch, result.Reason);
        Assert.Equal("23.0.0", result.InstalledVersion);
    }

    [Fact]
    public void Check_RejectsPrereleaseForStableRequirement()
    {
        var result = Check("node", ">=22 <23", Runtime("Node.js", "22.10.0-preview.1"));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(EnvironmentCheckReason.VersionMismatch, result.Reason);
    }

    [Fact]
    public void Check_ReportsFailWhenNothingIsInstalled()
    {
        var result = Check("python", ">=3.13 <3.14");

        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal(EnvironmentCheckReason.Missing, result.Reason);
        Assert.Null(result.InstalledVersion);
    }

    [Fact]
    public void Check_SelectsNewestInstalledVersionThatSatisfies()
    {
        var result = Check("dotnet", "10", Runtime(".NET", "10.0.100"), Runtime(".NET", "8.0.400"), Runtime(".NET", "7.0.200"));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("10.0.100", result.InstalledVersion);
    }

    [Fact]
    public void Check_UnknownComponentMatchesByRawName()
    {
        var result = Check("SomeCustomTool", "1.5", Runtime("SomeCustomTool", "1.5.3"));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
    }

    [Fact]
    public void Check_InvalidConstraintFailsWithInvalidReason()
    {
        var result = Check("git", "^2");

        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal(EnvironmentCheckReason.InvalidConstraint, result.Reason);
    }

    [Fact]
    public void Check_TreatsRuntimeSuffixAsPrereleaseAndWarnsAgainstStableRequirement()
    {
        // "25.0.4-hotspot" is parsed with a prerelease tag; a stable prefix requirement rejects it.
        var result = Check("java", "25", Runtime("Java", "25.0.4-hotspot"));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(EnvironmentCheckReason.VersionMismatch, result.Reason);
    }

    private static CoreRuntime Runtime(string name, string version) =>
        new(Guid.NewGuid(), name, version, $"C:\\{name}", "X64", "Test", 0, RuntimeStatus.Installed);

    private static EnvironmentProfile CreateProfile(string component, string requirement) => new()
    {
        Runtime = new Dictionary<string, VersionRequirement> { [component] = new() { Version = requirement } }
    };
}