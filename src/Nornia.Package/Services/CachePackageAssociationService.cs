using System.Text;
using Nornia.Core.Models;

namespace Nornia.Package.Services;

/// <summary>Associates safe cache candidates with installed packages using path and known tool-cache identities.</summary>
public sealed class CachePackageAssociationService
{
    /// <summary>Cache directory keyword → package keywords for that tool ecosystem. This mapping is the
    /// authoritative link between a cache path (e.g. ".npm", "NuGet") and the installed packages that own
    /// it (Node.js, .NET …). Short keys ("go", "m2", "bun") are matched as whole path segments so common
    /// substrings like the "go" inside "google" never trigger an ecosystem match.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> KnownCachePackageTerms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["npm"] = ["nodejs", "node"],
            ["pnpm"] = ["nodejs", "node"],
            ["yarn"] = ["nodejs", "node"],
            ["bun"] = ["bun"],
            ["nuget"] = ["dotnet"],
            ["gradle"] = ["java", "jdk", "openjdk"],
            ["m2"] = ["java", "jdk", "openjdk"],
            ["ivy2"] = ["java", "jdk", "openjdk"],
            ["maven"] = ["java", "jdk", "openjdk"],
            ["cargo"] = ["rust"],
            ["go"] = ["golang"],
            ["trae"] = ["traework", "trae"],
            ["trae-cn"] = ["traework", "trae"]
        };

    /// <summary>Cache directory keyword → tool-ecosystem label. Unlike <see cref="KnownCachePackageTerms"/>
    /// (which maps to package keywords for association scoring), this map drives the "cache type" dimension
    /// shown in per-package statistics and filters. Keys are matched longest-first so ".pnpm-store" resolves
    /// to pnpm instead of npm and "gradle/caches" to gradle instead of a generic cache match.</summary>
        private static readonly IReadOnlyDictionary<string, string> CacheTypeTerms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pnpm"] = "pnpm",
            ["nuget"] = "nuget",
            ["gradle"] = "gradle",
            ["maven"] = "maven",
            ["m2"] = "maven",
            ["cargo"] = "cargo",
            ["ivy2"] = "ivy",
            ["trae-cn"] = "trae",
            ["yarn"] = "yarn",
            ["npm"] = "npm",
            ["bun"] = "bun",
            ["go"] = "go"
        };

    /// <summary>Resolves the tool-ecosystem cache type for a path, falling back to "其他" when no known
    /// cache keyword is present. Public static so the scanner can stamp the type at scan time (the CLI
    /// never runs <see cref="Associate"/>), keeping cache knowledge in this service.</summary>
    public static string ResolveCacheType(string path)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var pair in CacheTypeTerms.OrderByDescending(pair => pair.Key.Length))
        {
            if (normalized.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return "其他";
    }

    public IReadOnlyList<CacheCandidate> Associate(
        IReadOnlyCollection<CacheCandidate> candidates,
        IReadOnlyCollection<PackageInfo> packages)
    {
        var installed = packages.Where(package => package.IsInstalled)
            .Select(package => new PackageIdentity(package, GetTerms(package)))
            .ToArray();
        // How many distinct installed packages share each keyword. A path-segment hit on a word that
        // appears in many packages (microsoft/windows/user …) is too weak to trust on its own.
        var termFrequency = installed
            .SelectMany(identity => identity.Terms)
            .GroupBy(term => term, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return candidates.Select(candidate => Associate(candidate, installed, termFrequency)).ToArray();
    }

    public IReadOnlyList<CachePackageSummary> Summarize(IReadOnlyCollection<CacheCandidate> candidates) => candidates
        .GroupBy(candidate => new { candidate.PackageName, candidate.PackageId, candidate.PackageProvider })
        .Select(group => new CachePackageSummary(
            group.Key.PackageName ?? "未关联的软件缓存",
            group.Key.PackageId,
            group.Key.PackageProvider,
            group.Sum(candidate => candidate.SizeBytes),
            group.Count(),
            group.Any(candidate => candidate.Confidence == CacheConfidence.High) ? CacheConfidence.High : CacheConfidence.Review,
            group.GroupBy(candidate => candidate.CacheTypeDisplay)
                .Select(type => new CacheTypeCount(type.Key, type.Count(), type.Sum(candidate => candidate.SizeBytes)))
                .OrderByDescending(type => type.SizeBytes)
                .ThenBy(type => type.Type, StringComparer.OrdinalIgnoreCase)
                .ToArray()))
        .OrderByDescending(group => group.SizeBytes)
        .ThenBy(group => group.PackageName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static CacheCandidate Associate(
        CacheCandidate candidate,
        IReadOnlyCollection<PackageIdentity> packages,
        IReadOnlyDictionary<string, int> termFrequency)
    {
        var cacheTerms = GetCacheTerms(candidate.Path);
        var best = packages
            .Select(identity => new { Identity = identity, Score = Score(candidate.Path, cacheTerms, identity.Terms, termFrequency) })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            // Prefer canonical winget ids over MSIX\ / ARP\ records when scores tie.
            .ThenBy(match => match.Identity.Package.Id.Contains('\\') ? 1 : 0)
            .ThenBy(match => match.Identity.Package.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (best is null)
        {
            return candidate;
        }

        var package = best.Identity.Package;
        return candidate with
        {
            PackageId = package.Id,
            PackageName = package.Name,
            PackageProvider = package.Provider,
            Reason = $"{candidate.Reason} 已关联已安装软件包“{package.Name}”。"
        };
    }

    /// <summary>Scores a candidate against a package. Two signals, deliberately ordered by strength:
    /// <list type="number">
    /// <item><b>Ecosystem hit (100+):</b> the cache path's tool ecosystem (npm → Node.js, NuGet → .NET,
    /// gradle → JDK …) matches a package keyword. Authoritative — a ".npm" cache is a Node.js cache.</item>
    /// <item><b>Path-segment hit (20-40+):</b> package name fragments appear as real path segments
    /// (e.g. "Everything" under "AppData\Roaming\Everything"). Two matching segments are always trusted;
    /// a single segment is only trusted when the keyword is distinctive (appears in ≤ 2 installed
    /// packages), so shared words like "microsoft"/"windows"/"user" never win on their own.</item>
    /// </list>
    /// The previous heuristic matched package keywords anywhere inside the normalized path, which let
    /// common words ("microsoft") score every Microsoft package equally and tie-broke to the wrong one
    /// (Edge caches were being linked to ".NET Runtime").</summary>
    private static int Score(
        string path,
        IReadOnlyCollection<string> cacheTerms,
        IReadOnlyCollection<string> packageTerms,
        IReadOnlyDictionary<string, int> termFrequency)
    {
        var score = 0;

        foreach (var term in packageTerms)
        {
            if (cacheTerms.Contains(term, StringComparer.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 100 + term.Length);
            }
        }

        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .ToArray();
        var hits = packageTerms
            .Where(term => term.Length >= 4 && segments.Contains(term, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // Two matching segments are trusted only when at least one is distinctive; pairs of shared words
        // ("microsoft" + "windows") would otherwise match the wrong Microsoft package.
        if (hits.Length >= 2 && hits.Any(term => termFrequency.TryGetValue(term, out var frequency) && frequency <= 2))
        {
            score = Math.Max(score, 40 + hits.Sum(term => term.Length));
        }
        else if (hits.Length == 1
            && termFrequency.TryGetValue(hits[0], out var frequency)
            && frequency <= 2)
        {
            score = Math.Max(score, 20 + hits[0].Length);
        }

        return score;
    }

    private static IReadOnlyCollection<string> GetTerms(PackageInfo package)
    {
        var terms = SplitTerms(package.Name).Concat(SplitTerms(package.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in KnownCachePackageTerms)
        {
            // Expand the keyword set when a name/id fragment carries an ecosystem keyword as a substring,
            // so "golang" still matches "go", "dotnet" matches ".NET", and "traework" matches "trae".
            if (terms.Any(term => pair.Value.Any(ecosystem => term.Contains(ecosystem, StringComparison.OrdinalIgnoreCase))))
            {
                terms.UnionWith(pair.Value);
            }
        }
        return terms;
    }

    private static IReadOnlyCollection<string> GetCacheTerms(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);
        return KnownCachePackageTerms
            .Where(pair =>
                pair.Key.Length <= 3
                    ? segments.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
                    : normalized.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitTerms(string value) => value.Split([' ', '.', '-', '_', '\\', '/'], StringSplitOptions.RemoveEmptyEntries)
        .Select(Normalize).Where(term => term.Length >= 3);

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private sealed record PackageIdentity(PackageInfo Package, IReadOnlyCollection<string> Terms);
}
