using System.Text;
using System.Text.RegularExpressions;
using Nornia.Core.Models;

namespace Nornia.Package.Services;

/// <summary>Classifies cache candidates from directory names only. It deliberately does not inspect
/// installed packages: labels are user-facing guesses, not ownership claims.</summary>
public sealed partial class CacheClassificationService
{
    private sealed record KnownCategory(string Name, string Type, string[] Signatures);

    private static readonly KnownCategory[] KnownCategories =
    [
        new("pnpm", "pnpm", [".pnpm-store", "pnpm-store"]),
        new("npm", "npm", [".npm", "npm-cache"]),
        new("Yarn", "yarn", [".yarn/cache", "yarn/cache"]),
        new("NuGet", "nuget", [".nuget/packages", "nuget/packages"]),
        new("Gradle", "gradle", [".gradle/caches", "gradle/caches"]),
        new("Maven", "maven", [".m2/repository", "m2/repository"]),
        new("Cargo", "cargo", [".cargo/registry/cache", "cargo/registry/cache"]),
        new("Ivy", "ivy", [".ivy2/cache", "ivy2/cache"]),
        new("Bun", "bun", [".bun/install/cache", "bun/install/cache"]),
        new("Go Modules", "go", ["go/pkg/mod"])
    ];

    private static readonly IReadOnlyDictionary<string, string> KnownApplicationNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["google/chrome"] = "Google Chrome",
            ["microsoft/edge"] = "Microsoft Edge",
            ["code"] = "Code",
            ["code - insiders"] = "Code - Insiders",
            ["trae solo cn"] = "TRAE SOLO CN",
            ["traecn"] = "Trae CN",
            ["trae"] = "Trae"
        };

    private static readonly HashSet<string> StructuralNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "appdata", "local", "locallow", "roaming", "packages", "user data", "default",
        "cache", ".cache", "caches", "code cache", "gpucache", "gpu cache", "shadercache",
        "shader cache", "webcache", "web cache", "localcache", "temp", "tmp", "ac",
        "inetcache", "data", "user", "users", "registry", "repository", "install", "pkg", "mod"
    };

    public IReadOnlyList<CacheCandidate> Classify(IReadOnlyCollection<CacheCandidate> candidates) =>
        candidates.Select(Classify).ToArray();

    public CacheCandidate Classify(CacheCandidate candidate)
    {
        var normalizedPath = NormalizePath(candidate.Path);
        var relativePath = RelativePath(candidate);

        foreach (var known in KnownCategories)
        {
            if (known.Signatures.Any(signature => ContainsPath(normalizedPath, signature)))
            {
                return candidate with
                {
                    CategoryKey = NormalizeKey(known.Name),
                    CategoryName = known.Name,
                    ClassificationReason = $"路径名称匹配已知 {known.Name} 缓存目录。",
                    CacheType = known.Type
                };
            }
        }

        foreach (var application in KnownApplicationNames.OrderByDescending(pair => pair.Key.Length))
        {
            if (ContainsPath(relativePath, application.Key))
            {
                return candidate with
                {
                    CategoryKey = NormalizeKey(application.Value),
                    CategoryName = application.Value,
                    ClassificationReason = $"根据目录层级推测为“{application.Value}”。",
                    CacheType = ResolveCacheType(normalizedPath)
                };
            }
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var inferred = segments
            .Take(Math.Max(segments.Length - 1, 0))
            .FirstOrDefault(IsMeaningfulSegment);
        inferred = CleanDisplayName(inferred);
        if (string.IsNullOrWhiteSpace(inferred))
        {
            inferred = "其他缓存";
        }

        return candidate with
        {
            CategoryKey = NormalizeKey(inferred),
            CategoryName = inferred,
            ClassificationReason = inferred == "其他缓存"
                ? "未能从目录名称推测应用或生态，已归入其他缓存。"
                : $"根据扫描根目录下的应用目录推测为“{inferred}”。",
            CacheType = ResolveCacheType(normalizedPath)
        };
    }

    public IReadOnlyList<CacheCategorySummary> Summarize(IReadOnlyCollection<CacheCandidate> candidates) => candidates
        .GroupBy(candidate => candidate.CategoryKey ?? NormalizeKey(candidate.CategoryDisplayName), StringComparer.OrdinalIgnoreCase)
        .Select(group => new CacheCategorySummary(
            group.Key,
            group.Select(candidate => candidate.CategoryDisplayName).First(),
            group.Sum(candidate => candidate.SizeBytes),
            group.Count(),
            group.All(candidate => candidate.Confidence == CacheConfidence.High) ? CacheConfidence.High : CacheConfidence.Review,
            group.GroupBy(candidate => candidate.CacheTypeDisplay, StringComparer.OrdinalIgnoreCase)
                .Select(type => new CacheTypeCount(type.Key, type.Count(), type.Sum(candidate => candidate.SizeBytes)))
                .OrderByDescending(type => type.SizeBytes)
                .ThenBy(type => type.Type, StringComparer.OrdinalIgnoreCase)
                .ToArray()))
        .OrderByDescending(group => group.SizeBytes)
        .ThenBy(group => group.CategoryName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string ResolveCacheType(string path)
    {
        var normalized = NormalizePath(path);
        var known = KnownCategories.FirstOrDefault(category =>
            category.Signatures.Any(signature => ContainsPath(normalized, signature)));
        if (known is not null)
        {
            return known.Type;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name switch
        {
            var value when value.Contains("gpu", StringComparison.OrdinalIgnoreCase) => "GPU 缓存",
            var value when value.Contains("shader", StringComparison.OrdinalIgnoreCase) => "着色器缓存",
            var value when value.Contains("code cache", StringComparison.OrdinalIgnoreCase) => "代码缓存",
            var value when value.Contains("webcache", StringComparison.OrdinalIgnoreCase) || value.Contains("web cache", StringComparison.OrdinalIgnoreCase) => "网页缓存",
            var value when value.Contains("temp", StringComparison.OrdinalIgnoreCase) || value.Equals("tmp", StringComparison.OrdinalIgnoreCase) => "临时文件",
            _ => "其他"
        };
    }

    private static string RelativePath(CacheCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.UserDirectory))
        {
            try
            {
                var relative = Path.GetRelativePath(candidate.UserDirectory, candidate.Path);
                if (!relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return NormalizePath(relative);
                }
            }
            catch (ArgumentException) { }
        }

        return NormalizePath(candidate.Path);
    }

    private static bool IsMeaningfulSegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment)
        && !StructuralNames.Contains(segment)
        && !ProfileDirectory().IsMatch(segment)
        && !VersionDirectory().IsMatch(segment);

    private static string CleanDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var withoutPublisher = PublisherSuffix().Replace(value, string.Empty);
        return withoutPublisher.Trim('.', '_', '-', ' ');
    }

    private static bool ContainsPath(string normalizedPath, string signature)
    {
        var normalizedSignature = NormalizePath(signature).Trim('/');
        var paddedPath = $"/{normalizedPath.Trim('/')}/";
        return paddedPath.Contains($"/{normalizedSignature}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string NormalizeKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.Length == 0 ? "other" : builder.ToString();
    }

    [GeneratedRegex("^profile(?:[ _-]?\\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileDirectory();

    [GeneratedRegex("^v?\\d+(?:[._-]\\d+)+$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionDirectory();

    [GeneratedRegex("_[a-z0-9]{8,}$", RegexOptions.IgnoreCase)]
    private static partial Regex PublisherSuffix();
}
