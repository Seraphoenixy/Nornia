using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nornia.Package.Services;

public sealed class RuntimePackageResolver : IRuntimePackageResolver
{
    private readonly IReadOnlyDictionary<string, PackageMappingEntry> _mappings;

    public RuntimePackageResolver() : this(PackageMappingLoader.LoadEmbedded())
    {
    }

    public RuntimePackageResolver(IReadOnlyDictionary<string, PackageMappingEntry> mappings)
    {
        _mappings = mappings;
    }

    public IReadOnlyList<string> SupportedRuntimeNames { get; } = EnvironmentComponentCatalog.All
        .Where(component => component.Category == EnvironmentComponentCategory.Runtime && component.CanManageWithWinget)
        .Select(component => component.Id)
        .ToArray();

    public IReadOnlyList<string> SupportedToolNames { get; } = EnvironmentComponentCatalog.All
        .Where(component => component.Category == EnvironmentComponentCategory.DevelopmentTool && component.CanManageWithWinget)
        .Select(component => component.Id)
        .ToArray();

    /// <summary>Resolves every Winget package that represents the requested component version.
    /// Callers must handle all returned packages (a component can map to several, e.g. the Visual C++
    /// Redistributable ships one installer per architecture).</summary>
    public IReadOnlyList<RuntimePackage> ResolveMany(string runtime, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!EnvironmentComponentCatalog.TryGet(runtime, out var component) || !component.CanManageWithWinget)
        {
            throw new ArgumentException($"Unsupported component '{runtime}'.");
        }

        if (!_mappings.TryGetValue(component.Id, out var mapping))
        {
            throw new ArgumentException($"Unsupported component '{runtime}'.");
        }

        var tokens = VersionTokens.Parse(version);
        var currentArchitecture = GetCurrentArchitectureSuffix();

        if (component.Id.Equals("windows-app-runtime", StringComparison.OrdinalIgnoreCase))
        {
            // Windows App Runtime AppX versions use 7000.x for 1.7,
            // 6000.x for 1.6, etc. The package id carries that release
            // number, so a fixed .1.8 mapping cannot uninstall 1.7.
            return [new RuntimePackage(ResolveWindowsAppRuntimePackageId(tokens), null)];
        }

        var packages = new List<RuntimePackage>(mapping.Packages.Count);
        foreach (var spec in mapping.Packages)
        {
            var arch = spec.IncludeForArchitecture == "Current" ? currentArchitecture : null;
            if (spec.IncludeForArchitecture == "Current" && arch is null)
            {
                continue;
            }

            var packageId = Expand(spec.IdTemplate, tokens, arch);
            var packageVersion = spec.VersionTemplate is null ? null : Expand(spec.VersionTemplate, tokens, arch);
            packages.Add(new RuntimePackage(packageId, packageVersion));
        }

        return packages;
    }

    private static string ResolveWindowsAppRuntimePackageId(VersionTokens tokens)
    {
        var release = tokens.Major >= 1000
            ? tokens.Major / 1000
            : tokens.Major == 1 && tokens.Minor is not null
                ? tokens.Minor.Value
                : 8;
        return $"Microsoft.WindowsAppRuntime.1.{release}";
    }

    public IReadOnlyList<RuntimePackageOperation> ResolveAll(string component, string targetVersion) =>
        ResolveMany(component, targetVersion).Select(package => new RuntimePackageOperation(component, targetVersion, package.PackageId, package.PackageVersion)).ToArray();

    public IReadOnlyList<RuntimePackageOperation>? TryResolveAll(string component, string targetVersion)
    {
        try { return ResolveAll(component, targetVersion); }
        catch (ArgumentException) { return null; }
    }

    private static string Expand(string template, VersionTokens tokens, string? architecture)
    {
        if (template.Contains("{minor}", StringComparison.Ordinal) && tokens.Minor is null)
        {
            throw new ArgumentException($"Invalid runtime version '{tokens.Version}': a minor version part is required.");
        }

        if (template.Contains("{patch}", StringComparison.Ordinal) && tokens.Patch is null)
        {
            throw new ArgumentException($"Invalid runtime version '{tokens.Version}': a patch version part is required.");
        }

        return template
            .Replace("{version}", tokens.Version, StringComparison.Ordinal)
            .Replace("{major}", tokens.Major.ToString(), StringComparison.Ordinal)
            .Replace("{minor}", tokens.Minor?.ToString() ?? string.Empty, StringComparison.Ordinal)
            .Replace("{patch}", tokens.Patch?.ToString() ?? string.Empty, StringComparison.Ordinal)
            .Replace("{arch}", architecture ?? string.Empty, StringComparison.Ordinal);
    }

    private static string? GetCurrentArchitectureSuffix() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null
    };

    private readonly record struct VersionTokens(string Version, int Major, int? Minor, int? Patch)
    {
        public static VersionTokens Parse(string version)
        {
            var normalized = version.Trim().TrimStart('v');
            var parts = normalized.Split('.');
            if (parts.Length == 0 || !int.TryParse(parts[0], out var major))
            {
                throw new ArgumentException($"Invalid runtime version '{version}'.");
            }

            int? minor = parts.Length > 1 && int.TryParse(parts[1], out var minorValue) ? minorValue : null;
            int? patch = parts.Length > 2 && int.TryParse(parts[2], out var patchValue) ? patchValue : null;
            return new VersionTokens(normalized, major, minor, patch);
        }
    }
}

/// <summary>Declarative RuntimeId → Winget package mapping. Adding a supported component requires a
/// catalog entry plus a mapping here; no resolver code changes are needed.</summary>
public sealed record PackageMappingDocument([property: JsonPropertyName("mappings")] List<PackageMappingEntry> Mappings);

public sealed record PackageMappingEntry(
    [property: JsonPropertyName("runtimeId")] string RuntimeId,
    [property: JsonPropertyName("packages")] List<PackageMappingSpec> Packages);

/// <param name="IdTemplate">Winget package id, may contain {major}, {minor}, {patch}, {version}, {arch} tokens.</param>
/// <param name="VersionTemplate">Optional explicit package version template, or null to let Winget choose.</param>
/// <param name="IncludeForArchitecture">"Current" expands the {arch} token to the running platform
/// architecture; anything else emits the spec unconditionally.</param>
public sealed record PackageMappingSpec(
    [property: JsonPropertyName("idTemplate")] string IdTemplate,
    [property: JsonPropertyName("versionTemplate")] string? VersionTemplate = null,
    [property: JsonPropertyName("includeForArchitecture")] string? IncludeForArchitecture = null);

/// <summary>Loads the embedded <c>PackageMappings.json</c> shipped with Nornia.Package.</summary>
public static class PackageMappingLoader
{
    public const string EmbeddedResourceName = "Nornia.Package.Configuration.PackageMappings.json";

    public static IReadOnlyDictionary<string, PackageMappingEntry> LoadEmbedded()
    {
        using var stream = typeof(PackageMappingLoader).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        var document = JsonSerializer.Deserialize<PackageMappingDocument>(reader.ReadToEnd())
            ?? throw new InvalidDataException($"Embedded resource '{EmbeddedResourceName}' is not valid package mapping JSON.");
        return document.Mappings.ToDictionary(mapping => mapping.RuntimeId, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, PackageMappingEntry> FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<PackageMappingDocument>(json)
            ?? throw new InvalidDataException("Package mapping JSON is invalid.");
        return document.Mappings.ToDictionary(mapping => mapping.RuntimeId, StringComparer.OrdinalIgnoreCase);
    }
}
