namespace Nornia.Core.Models;

public sealed record PackageInfo(
    string Id,
    string Name,
    string Version,
    string? AvailableVersion,
    string Provider,
    bool IsInstalled,
    string Architecture = "Unknown");

/// <summary>A Winget package that represents a requested component version.</summary>
public sealed record RuntimePackage(string PackageId, string? PackageVersion);

public sealed record RuntimePackageOperation(
    string Component,
    string TargetVersion,
    string PackageId,
    string? PackageVersion,
    string? PreferredProvider = null);
