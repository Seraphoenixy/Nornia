using Nornia.Core.Models;
using Nornia.Project.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Nornia.Project.Services;

public interface IEnvironmentProfileService
{
    const string FileName = "Nornia.yaml";

    Task<EnvironmentProfile> LoadAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<string> InitializeAsync(
        string projectPath,
        string? projectName = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    Task SaveAsync(string profilePath, EnvironmentProfile profile, CancellationToken cancellationToken = default);

    Task ExportAsync(string projectPath, string destinationPath, CancellationToken cancellationToken = default);

    Task ImportAsync(string sourcePath, string projectPath, CancellationToken cancellationToken = default);

    string Serialize(EnvironmentProfile profile);
}

public sealed class EnvironmentProfileService : IEnvironmentProfileService
{
    public const string FileName = "Nornia.yaml";

    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<EnvironmentProfile> LoadAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var profilePath = ResolveProfilePath(projectPath);
        if (!File.Exists(profilePath))
        {
            throw new FileNotFoundException($"No {FileName} was found at '{profilePath}'.", profilePath);
        }

        var yaml = await File.ReadAllTextAsync(profilePath, cancellationToken);
        var profile = _deserializer.Deserialize<EnvironmentProfile>(yaml)
            ?? throw new InvalidDataException($"'{profilePath}' does not contain a valid environment profile.");
        Validate(profile, profilePath);
        if (NormalizeComponentCategories(profile))
        {
            await SaveAsync(profilePath, profile, cancellationToken);
        }
        return profile;
    }

    public async Task<string> InitializeAsync(
        string projectPath,
        string? projectName = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(projectPath);
        Directory.CreateDirectory(directory);
        var profilePath = Path.Combine(directory, FileName);
        if (File.Exists(profilePath) && !overwrite)
        {
            throw new IOException($"'{profilePath}' already exists.");
        }

        var profile = new EnvironmentProfile
        {
            Project = new ProjectMetadata
            {
                Name = string.IsNullOrWhiteSpace(projectName)
                    ? new DirectoryInfo(directory).Name
                    : projectName
            }
        };

        await SaveAsync(profilePath, profile, cancellationToken);
        return profilePath;
    }

    public async Task SaveAsync(string profilePath, EnvironmentProfile profile, CancellationToken cancellationToken = default)
    {
        Validate(profile, profilePath);
        var yaml = Serialize(profile);
        await File.WriteAllTextAsync(Path.GetFullPath(profilePath), yaml, cancellationToken);
    }

    public async Task ExportAsync(string projectPath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var profile = await LoadAsync(projectPath, cancellationToken);
        await SaveAsync(destinationPath, profile, cancellationToken);
    }

    public async Task ImportAsync(string sourcePath, string projectPath, CancellationToken cancellationToken = default)
    {
        var yaml = await File.ReadAllTextAsync(Path.GetFullPath(sourcePath), cancellationToken);
        var profile = _deserializer.Deserialize<EnvironmentProfile>(yaml)
            ?? throw new InvalidDataException($"'{sourcePath}' does not contain a valid environment profile.");
        Validate(profile, sourcePath);
        await SaveAsync(Path.Combine(Path.GetFullPath(projectPath), FileName), profile, cancellationToken);
    }

    public string Serialize(EnvironmentProfile profile)
    {
        Validate(profile, FileName);
        return _serializer.Serialize(profile);
    }

    private static string ResolveProfilePath(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        return File.Exists(fullPath) || string.Equals(Path.GetFileName(fullPath), FileName, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.Combine(fullPath, FileName);
    }

    private static void Validate(EnvironmentProfile profile, string source)
    {
        if (string.IsNullOrWhiteSpace(profile.Project?.Name))
        {
            throw new InvalidDataException($"Profile '{source}' must define project.name.");
        }

        profile.Runtime ??= new Dictionary<string, VersionRequirement>(StringComparer.OrdinalIgnoreCase);
        profile.Tools ??= new Dictionary<string, VersionRequirement>(StringComparer.OrdinalIgnoreCase);
        profile.Environment ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        profile.Architectures ??= [];
        profile.OperatingSystems ??= [];
    }

    private static bool NormalizeComponentCategories(EnvironmentProfile profile)
    {
        var changed = false;
        changed |= MoveMisclassifiedComponents(profile.Runtime, profile.Tools, EnvironmentComponentCategory.DevelopmentTool);
        changed |= MoveMisclassifiedComponents(profile.Tools, profile.Runtime, EnvironmentComponentCategory.Runtime);
        return changed;
    }

    private static bool MoveMisclassifiedComponents(
        Dictionary<string, VersionRequirement> source,
        Dictionary<string, VersionRequirement> destination,
        EnvironmentComponentCategory destinationCategory)
    {
        var changed = false;
        foreach (var entry in source.ToArray())
        {
            if (!EnvironmentComponentCatalog.TryGet(entry.Key, out var component) || component.Category != destinationCategory)
            {
                continue;
            }

            var existsInDestination = destination.Keys.Any(key =>
                EnvironmentComponentCatalog.TryGet(key, out var destinationComponent)
                && string.Equals(destinationComponent.Id, component.Id, StringComparison.OrdinalIgnoreCase));
            if (!existsInDestination)
            {
                destination[entry.Key] = entry.Value;
            }

            source.Remove(entry.Key);
            changed = true;
        }

        return changed;
    }
}
