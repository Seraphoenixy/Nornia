using System.Security.Cryptography;
using System.Text;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.Package.Services;

/// <summary>Finds and clears user-level application caches without following reparse points.</summary>
public sealed class AppDataCacheService : ICacheInventoryService, ICacheCleanupService
{
    private readonly IReadOnlyList<string> _roots;
    private readonly object _scanLock = new();
    private IReadOnlyList<CacheCandidate>? _lastScan;
    private long _lastScanAt;
    private Task<IReadOnlyList<CacheCandidate>>? _scanInFlight;

    private static readonly HashSet<string> ExactCacheNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", ".cache", ".npm", ".pnpm-store", "code cache", "gpucache", "gpu cache",
        "shadercache", "shader cache", "webcache", "web cache", "temp", "tmp"
    };

    private static readonly string[] KnownUserCacheSuffixes =
    [
        ".nuget/packages", ".gradle/caches", ".m2/repository", ".cargo/registry/cache",
        ".ivy2/cache", ".yarn/cache", ".bun/install/cache", "go/pkg/mod"
    ];

    private static readonly HashSet<string> UserContentRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Contacts", "Desktop", "Documents", "Downloads", "Favorites", "Links",
        "Music", "OneDrive", "Pictures", "Saved Games", "Searches", "Videos"
    };

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cookies", "history", "bookmarks", "local storage", "indexeddb", "databases", "database", "config", "configuration", "settings"
    };

    /// <summary>Directory names that contain "cache"/"temp" substrings but are clearly NOT caches
    /// (source tree folders, Python library modules, docs). Without this the scan floods the list
    /// with false positives like "templates", "tempfile" or "cachetools".</summary>
    private static readonly HashSet<string> FalsePositiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cachetools", "cache-reserve", "cachereserve", "tempdir", "tempfile", "temps"
    };

    private static readonly string[] FalsePositiveNamePrefixes = ["template", "templatetag"];

    public AppDataCacheService() : this(GetDefaultRoots()) { }

    public AppDataCacheService(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<IReadOnlyList<CacheCandidate>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        ScanCoreAsync(forceRescan: false, progress, cancellationToken);

    public Task<IReadOnlyList<CacheCandidate>> ScanForcedAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        ScanCoreAsync(forceRescan: true, progress, cancellationToken);

    private async Task<IReadOnlyList<CacheCandidate>> ScanCoreAsync(bool forceRescan, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // Scanning the entire user profile tree is expensive; reuse a recent result so dashboard and
        // cache-page refreshes do not re-walk the filesystem on every activation.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Task<IReadOnlyList<CacheCandidate>> scanTask;
        lock (_scanLock)
        {
            if (!forceRescan && _lastScan is not null && now - _lastScanAt < Nornia.Core.NorniaSettings.CacheScanCacheSeconds)
            {
                return _lastScan;
            }

            // Forced refreshes still share an in-flight walk. This prevents simultaneous page
            // activation and cleanup refreshes from traversing the user profile more than once.
            _scanInFlight ??= ScanAndCacheAsync(progress, cancellationToken);
            scanTask = _scanInFlight;
        }

        try
        {
            return await scanTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_scanLock)
            {
                if (ReferenceEquals(_scanInFlight, scanTask))
                {
                    _scanInFlight = null;
                }
            }
        }
    }

    private async Task<IReadOnlyList<CacheCandidate>> ScanAndCacheAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (_roots.Count == 0)
        {
            return [];
        }

        var rootScans = new List<Task<IReadOnlyList<CacheCandidate>>>(_roots.Count);
        foreach (var root in _roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            progress?.Report($"正在扫描 {root}");
            rootScans.Add(Task.Run<IReadOnlyList<CacheCandidate>>(() =>
            {
                var rootCandidates = new List<CacheCandidate>();
                ScanRoot(root, rootCandidates, cancellationToken);
                return rootCandidates;
            }, cancellationToken));
        }

        var candidates = (await Task.WhenAll(rootScans).ConfigureAwait(false)).SelectMany(items => items).ToList();

        var result = candidates
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.SizeBytes)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_scanLock)
        {
            _lastScan = result;
            _lastScanAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        return result;
    }

    public async Task<IReadOnlyList<CacheCleanupResult>> CleanAsync(
        IReadOnlyCollection<string> candidateIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        var requested = candidateIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            return [];
        }

        // Cleanup targets must be resolved against a fresh scan: cached candidates may be stale or gone.
        var currentCandidates = await ScanCoreAsync(forceRescan: true, progress, cancellationToken);
        var completed = false;
        try
        {
            var byId = currentCandidates.ToDictionary(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase);
            var results = new List<CacheCleanupResult>();
            foreach (var id in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byId.TryGetValue(id, out var candidate))
                {
                    results.Add(new CacheCleanupResult(id, string.Empty, CacheCleanupStatus.Skipped, 0, "候选项不存在、已过期或不再符合安全规则。"));
                    continue;
                }

                try
                {
                    var reclaimed = await Task.Run(() => ClearContents(candidate.Path, cancellationToken), cancellationToken);
                    results.Add(new CacheCleanupResult(id, candidate.Path, CacheCleanupStatus.Cleaned, reclaimed, "已清理缓存内容。"));
                }
                catch (UnauthorizedAccessException ex)
                {
                    results.Add(new CacheCleanupResult(id, candidate.Path, CacheCleanupStatus.Failed, 0, $"访问被拒绝：{ex.Message}"));
                }
                catch (IOException ex)
                {
                    results.Add(new CacheCleanupResult(id, candidate.Path, CacheCleanupStatus.Failed, 0, $"文件正在使用或无法删除：{ex.Message}"));
                }
            }

            var cleanedIds = results
                .Where(result => result.Status == CacheCleanupStatus.Cleaned)
                .Select(result => result.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (cleanedIds.Count > 0)
            {
                var updated = currentCandidates
                    .Select(candidate => cleanedIds.Contains(candidate.Id) ? candidate with { SizeBytes = 0 } : candidate)
                    .ToArray();
                lock (_scanLock)
                {
                    _lastScan = updated;
                    _lastScanAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
            }

            completed = true;
            return results;
        }
        finally
        {
            if (!completed)
            {
                // A cancelled/partially failed cleanup may have changed the file system without a
                // complete snapshot update, so force the next inventory request to re-scan.
                lock (_scanLock)
                {
                    _lastScan = null;
                    _lastScanAt = 0;
                }
            }
        }
    }

    private static void ScanRoot(string root, List<CacheCandidate> candidates, CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(child))
                {
                    continue;
                }

                if (TryCreateCandidate(normalizedRoot, child, out var candidate))
                {
                    candidates.Add(candidate);
                    continue;
                }

                if (ShouldSkipTraversal(normalizedRoot, child))
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static bool TryCreateCandidate(string root, string path, out CacheCandidate candidate)
    {
        candidate = default!;
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => SensitiveNames.Contains(segment)))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        var normalizedRelative = relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        var knownUserCache = KnownUserCacheSuffixes.Any(suffix =>
            normalizedRelative.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || normalizedRelative.EndsWith('/' + suffix, StringComparison.OrdinalIgnoreCase));
        var exactMatch = ExactCacheNames.Contains(name) || knownUserCache;
        var heuristicMatch = !IsFalsePositiveName(name) && (name.Contains("cache", StringComparison.OrdinalIgnoreCase)
            || name.Contains("temp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("tmp", StringComparison.OrdinalIgnoreCase));
        if (!exactMatch && !heuristicMatch)
        {
            return false;
        }

        var confidence = exactMatch ? CacheConfidence.High : CacheConfidence.Review;
        var source = segments.FirstOrDefault() ?? "AppData";
        var reason = knownUserCache
            ? $"路径“{normalizedRelative}”匹配已知开发工具缓存。"
            : exactMatch
            ? $"目录名“{name}”匹配受支持缓存名称。"
            : $"目录名“{name}”包含缓存或临时文件特征，建议人工审查。";
        candidate = new CacheCandidate(
            CreateId(root, relative),
            source,
            path,
            GetDirectorySize(path),
            confidence,
            reason,
            CacheType: CacheClassificationService.ResolveCacheType(normalizedRelative),
            UserDirectory: root);
        return true;
    }

    private static bool IsFalsePositiveName(string name)
    {
        if (FalsePositiveNames.Contains(name))
        {
            return true;
        }

        foreach (var prefix in FalsePositiveNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static long ClearContents(string cacheRoot, CancellationToken cancellationToken)
    {
        if (IsReparsePoint(cacheRoot))
        {
            throw new IOException("缓存根目录是重解析点，已拒绝清理。");
        }

        long reclaimed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(cacheRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reclaimed += DeleteEntry(entry, cancellationToken);
        }

        return reclaimed;
    }

    private static long DeleteEntry(string entry, CancellationToken cancellationToken)
    {
        if (IsReparsePoint(entry))
        {
            return 0;
        }

        if (Directory.Exists(entry))
        {
            long reclaimed = 0;
            foreach (var child in Directory.EnumerateFileSystemEntries(entry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                reclaimed += DeleteEntry(child, cancellationToken);
            }
            Directory.Delete(entry, recursive: false);
            return reclaimed;
        }

        var length = new FileInfo(entry).Length;
        File.Delete(entry);
        return length;
    }

    private static long GetDirectorySize(string root)
    {
        long size = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (IsReparsePoint(current)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try { size += new FileInfo(file).Length; } catch (IOException) { }
                }
                foreach (var child in Directory.EnumerateDirectories(current)) pending.Push(child);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return size;
    }

    private static IEnumerable<string> GetDefaultRoots()
    {
        // Environment.GetFolderPath can return empty in non-standard launch contexts (e.g. a process
        // started without a full interactive user profile). Fall back to the well-known environment
        // variables so the scan still covers the intended user directories.
        yield return FolderPath(Environment.SpecialFolder.LocalApplicationData, "LOCALAPPDATA");
        yield return FolderPath(Environment.SpecialFolder.ApplicationData, "APPDATA");
        yield return FolderPath(Environment.SpecialFolder.UserProfile, "USERPROFILE");
    }

    private static string FolderPath(Environment.SpecialFolder folder, string environmentVariable)
    {
        var path = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty;
    }

    private static bool ShouldSkipTraversal(string scanRoot, string path)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.Equals(scanRoot.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(userProfile).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = Path.GetRelativePath(scanRoot, path);
        return !relative.Contains(Path.DirectorySeparatorChar) && UserContentRoots.Contains(relative);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string CreateId(string root, string relativePath)
    {
        var value = $"{root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant()}|{relativePath.ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }
}
