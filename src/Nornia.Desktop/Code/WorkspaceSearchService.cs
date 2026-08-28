using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Services;

namespace Nornia.Desktop.Code;

public sealed record WorkspaceSearchQuery(
    string RootPath,
    string Pattern,
    TextSearchOptions Options,
    string IncludePattern = "",
    string ExcludePattern = "",
    bool UseIgnoreFiles = true);

public sealed record WorkspaceSearchMatch(int Line, int Column, int Length, string Preview);

public sealed record WorkspaceSearchFileResult(
    string FullPath,
    string RelativePath,
    IReadOnlyList<WorkspaceSearchMatch> Matches);

public sealed record WorkspaceSearchProgress(
    int FilesScanned,
    int FilesMatched,
    int MatchCount,
    bool IsTruncated);

public interface IWorkspaceSearchService
{
    IAsyncEnumerable<WorkspaceSearchFileResult> SearchAsync(
        WorkspaceSearchQuery query,
        IProgress<WorkspaceSearchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Bounded, disk-backed workspace text search. Directory enumeration is a cheap serial
/// metadata walk; the expensive per-file read+decode work runs on a bounded parallel pool (VS Code
/// bounded-parallel ripgrep model) and streams matched files through a bounded channel as they
/// arrive, so the workbench never buffers the whole result set and each file is opened exactly
/// once (the 4KB BOM/binary sniff and the line scan share the same handle).</summary>
public sealed class WorkspaceSearchService(ISettingsService settings, IUiLogService? logService = null) : IWorkspaceSearchService
{
    public const int MaximumMatchedFiles = 2_000;
    public const int MaximumMatches = 10_000;
    public const int MaximumMatchesPerFile = 500;
    public const int MaximumPreviewCharacters = 240;
    private const int ResultChannelCapacity = 512;
    private const int ScanSettlingTimeoutMs = 15_000;

    /// <summary>S5: glob-compile LRU shared across every search this service instance runs
    /// (the app hosts a single instance, so it behaves like the process-wide cache VS Code uses
    /// for glob.ts). <see cref="SearchPathFilter"/> compiles include/exclude/workspace-exclude
    /// patterns through it on its first search; later searches with the same patterns are cache
    /// hits.</summary>
    internal SimpleGlobCache GlobCache { get; } = new(SimpleGlobCache.DefaultCapacity);

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "bin", "obj", "node_modules", ".vs", "packages"
    };

    private static Encoding Gb18030
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(54936);
        }
    }

    public async IAsyncEnumerable<WorkspaceSearchFileResult> SearchAsync(
        WorkspaceSearchQuery query,
        IProgress<WorkspaceSearchProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.RootPath) || !Directory.Exists(query.RootPath) ||
            string.IsNullOrEmpty(query.Pattern))
        {
            yield break;
        }

        if (query.Options.UseRegex && TextSearchService.GetRegexError(query.Pattern, query.Options.CaseSensitive) is not null)
        {
            yield break;
        }

        // Ensure the first directory enumeration never happens on the UI synchronization context.
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(query.RootPath);
        var workspaceExcludes = (await settings.GetSnapshotAsync(new(root), cancellationToken)
                .ConfigureAwait(false))
            .Effective(BuiltInSettingsCatalog.FilesExclude);
        var filter = new SearchPathFilter(query, workspaceExcludes, GlobCache);
        var regex = query.Options.UseRegex
            ? new Regex(query.Pattern, TextSearchService.BuildRegexOptions(query.Options.CaseSensitive))
            : null;
        var comparison = query.Options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Phase 1 — enumerate candidate files (metadata only: no file contents are read here).
        var candidates = new List<(string FullPath, string RelativePath)>();
        EnumerateCandidates(root, query, filter, candidates, logService, cancellationToken);

        // Phase 2 — bounded parallel scan, streamed through a bounded channel (arrival order,
        // same asyncDataTree streaming model as VS Code; the UI builds the tree as files arrive).
        var scanned = 0;
        var matchedFiles = 0;
        var matchedLines = 0;
        var truncated = 0; // 0/1, Volatile
        var channel = Channel.CreateBounded<WorkspaceSearchFileResult>(new BoundedChannelOptions(ResultChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var scanTask = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(candidates, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = cancellationToken,
                }, async (candidate, loopToken) =>
                {
                    if (Volatile.Read(ref truncated) == 1) return;
                    Interlocked.Increment(ref scanned);

                    var matches = await SearchFileAsync(candidate.FullPath, query, regex, comparison, loopToken)
                        .ConfigureAwait(false);
                    if (matches.Count == 0)
                    {
                        if ((Volatile.Read(ref scanned) & 255) == 0)
                        {
                            progress?.Report(new(Volatile.Read(ref scanned), Volatile.Read(ref matchedFiles),
                                Volatile.Read(ref matchedLines), Volatile.Read(ref truncated) == 1));
                        }

                        return;
                    }

                    var fileSlot = Interlocked.Increment(ref matchedFiles);
                    if (fileSlot > MaximumMatchedFiles)
                    {
                        Volatile.Write(ref truncated, 1);
                        return;
                    }

                    var room = MaximumMatches - Volatile.Read(ref matchedLines);
                    if (room <= 0)
                    {
                        Volatile.Write(ref truncated, 1);
                        return;
                    }

                    if (matches.Count > room) matches = matches.Take(room).ToArray();
                    var total = Interlocked.Add(ref matchedLines, matches.Count);
                    if (fileSlot >= MaximumMatchedFiles || total >= MaximumMatches) Volatile.Write(ref truncated, 1);
                    await channel.Writer.WriteAsync(
                        new WorkspaceSearchFileResult(candidate.FullPath, candidate.RelativePath, matches), loopToken)
                        .ConfigureAwait(false);
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation propagates through the reader below; the scan itself just unwinds.
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return result;
            }
        }
        finally
        {
            try
            {
                // Wait for the scan to settle so no worker keeps touching the caller's workspace
                // after the iterator is gone (the cancellation token usually already unwound it).
                await scanTask.WaitAsync(TimeSpan.FromMilliseconds(ScanSettlingTimeoutMs), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The scan is still unwinding; the caller's cancellation lifecycle (new search or
                // ClearResults) takes it down. Never block the UI iterator indefinitely.
            }
            catch
            {
                // Per-file IO failures are swallowed inside the workers; nothing else is expected.
            }
        }

        progress?.Report(new(Volatile.Read(ref scanned), Volatile.Read(ref matchedFiles),
            Volatile.Read(ref matchedLines), Volatile.Read(ref truncated) == 1));
    }

    // S8: one non-recursive walk per directory (files and subdirectories in a single
    // FileSystemEnumerable pass instead of two Enumerate* calls), and inaccessible entries are
    // skipped instead of failing the whole walk. Entries keep the OS insertion order: the
    // parallel scan (phase 2) and the UI both treat arrival order as the presentation order, so
    // no per-directory sorting is materialized here.
    private static readonly EnumerationOptions SingleDirectoryOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
    };

    internal static void EnumerateCandidates(
        string root,
        WorkspaceSearchQuery query,
        SearchPathFilter filter,
        List<(string FullPath, string RelativePath)> candidates,
        IUiLogService? logService,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<(string FullPath, string RelativePath, GitIgnoreScope Scope)>();
        stack.Push((root, string.Empty, GitIgnoreScope.Empty));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, relativeDirectory, inheritedScope) = stack.Pop();
            var rules = query.UseIgnoreFiles
                ? GitIgnoreRule.LoadForDirectory(current, relativeDirectory, inheritedScope, logService)
                : GitIgnoreScope.Empty;

            try
            {
                // FileSystemEntry 是 ref struct,不能作泛型实参:变换为完整路径字符串,
                // 目录判定按属性位查询。
                foreach (var path in new FileSystemEnumerable<string>(
                             current,
                             static (ref FileSystemEntry entry) => entry.ToFullPath(),
                             SingleDirectoryOptions))
                {
                    var isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                    if (isDirectory)
                    {
                        var relative = Relative(root, path);
                        if (IgnoredDirectoryNames.Contains(Path.GetFileName(path)) || filter.IsExcluded(relative, true) ||
                            GitIgnoreRule.IsIgnored(relative, true, rules))
                        {
                            continue;
                        }

                        stack.Push((path, relative, rules));
                    }
                    else
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Relative(root, path);
                        if (filter.IsExcluded(relative, false) || GitIgnoreRule.IsIgnored(relative, false, rules))
                        {
                            continue;
                        }

                        candidates.Add((path, relative));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue; // an unreadable directory is skipped; the walk continues elsewhere
            }
        }
    }

    /// <summary>Reads the file once: sniffs the first 4KB for BOM/binary/encoding, seeks back to
    /// the start, and scans with the detected (or strict-UTF8) decoder. Files whose head is not
    /// valid UTF-8 go straight to GB18030 (the preview decoder's legacy-Chinese fallback) instead
    /// of a full first scan + re-scan; a UTF-8 file that breaks mid-stream still falls back to
    /// GB18030.</summary>
    private static async Task<IReadOnlyList<WorkspaceSearchMatch>> SearchFileAsync(
        string path,
        WorkspaceSearchQuery query,
        Regex? regex,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var head = new byte[Math.Min(4096, stream.Length)];
            var read = await stream.ReadAsync(head.AsMemory(), cancellationToken).ConfigureAwait(false);
            var encoding = DetectHeadEncoding(head.AsSpan(0, read));
            if (encoding is null) return []; // binary
            stream.Position = 0;
            return await ScanStreamAsync(stream, encoding, query, regex, comparison, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Decodes the sniffed head: BOM wins, a NUL byte marks binary, otherwise the head is
    /// validated as UTF-8 (incomplete sequences at the 4KB boundary tolerated) — a head that fails
    /// UTF-8 validation is treated as GB18030 up front, matching the preview decoder fallback.</summary>
    internal static Encoding? DetectHeadEncoding(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(UTF32LittleEndianBom)) return new UTF32Encoding(false, true);
        if (head.StartsWith(UTF32BigEndianBom)) return new UTF32Encoding(true, true);
        if (head.StartsWith(UTF8Bom)) return new UTF8Encoding(true, true);
        if (head.StartsWith(UTF16LittleEndianBom)) return new UnicodeEncoding(false, true, true);
        if (head.StartsWith(UTF16BigEndianBom)) return new UnicodeEncoding(true, true, true);
        if (head.Contains((byte)0)) return null;

        // Validate as UTF-8, tolerating an incomplete multi-byte sequence cut at the 4KB boundary:
        // a UTF-8 sequence is at most 4 bytes, so trim up to 3 trailing bytes and re-validate.
        // A head that is not valid UTF-8 even after trimming is treated as GB18030 up front,
        // matching the preview decoder's legacy-Chinese fallback.
        var strictUtf8 = new UTF8Encoding(false, true);
        for (var trim = 0; trim <= Math.Min(3, head.Length); trim++)
        {
            var length = head.Length - trim;
            if (length == 0 && trim > 0) break; // trimming to empty proves nothing
            try
            {
                strictUtf8.GetString(head.Slice(0, length));
                return strictUtf8;
            }
            catch (DecoderFallbackException)
            {
                // Possibly an incomplete sequence at the boundary; retry with one more byte trimmed.
            }
        }

        return Gb18030;
    }

    private static async Task<IReadOnlyList<WorkspaceSearchMatch>> ScanStreamAsync(
        Stream stream,
        Encoding encoding,
        WorkspaceSearchQuery query,
        Regex? regex,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        try
        {
            // leaveOpen: the stream is owned by SearchFileAsync; the mid-stream GB18030 fallback
            // below must be able to reset Position and re-read the same handle.
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true,
                64 * 1024, leaveOpen: true);
            var result = new List<WorkspaceSearchMatch>();
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                if (regex is not null)
                {
                    foreach (Match match in regex.Matches(line))
                    {
                        if (query.Options.WholeWord && !TextSearchService.IsWholeWord(line, match.Index, match.Length)) continue;
                        result.Add(new(lineNumber, match.Index + 1, match.Length, Preview(line)));
                        if (result.Count >= MaximumMatchesPerFile) return result;
                    }
                }
                else
                {
                    for (var offset = 0; result.Count < MaximumMatchesPerFile;)
                    {
                        var index = line.IndexOf(query.Pattern, offset, comparison);
                        if (index < 0) break;
                        if (!query.Options.WholeWord || TextSearchService.IsWholeWord(line, index, query.Pattern.Length))
                        {
                            result.Add(new(lineNumber, index + 1, query.Pattern.Length, Preview(line)));
                        }

                        offset = index + Math.Max(1, query.Pattern.Length);
                    }
                }
            }

            return result;
        }
        catch (DecoderFallbackException) when (encoding.CodePage == 65001)
        {
            // A valid-UTF8 head with a broken tail (rare): re-read the same single handle's file
            // through the legacy-Chinese fallback, matching the preview decoder.
            stream.Position = 0;
            using var reader = new StreamReader(stream, Gb18030, detectEncodingFromByteOrderMarks: false,
                64 * 1024, leaveOpen: true);
            var result = new List<WorkspaceSearchMatch>();
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                if (regex is not null)
                {
                    foreach (Match match in regex.Matches(line))
                    {
                        if (query.Options.WholeWord && !TextSearchService.IsWholeWord(line, match.Index, match.Length)) continue;
                        result.Add(new(lineNumber, match.Index + 1, match.Length, Preview(line)));
                        if (result.Count >= MaximumMatchesPerFile) return result;
                    }
                }
                else
                {
                    for (var offset = 0; result.Count < MaximumMatchesPerFile;)
                    {
                        var index = line.IndexOf(query.Pattern, offset, comparison);
                        if (index < 0) break;
                        if (!query.Options.WholeWord || TextSearchService.IsWholeWord(line, index, query.Pattern.Length))
                        {
                            result.Add(new(lineNumber, index + 1, query.Pattern.Length, Preview(line)));
                        }

                        offset = index + Math.Max(1, query.Pattern.Length);
                    }
                }
            }

            return result;
        }
    }

    private static readonly byte[] UTF8Bom = Encoding.UTF8.GetPreamble();
    private static readonly byte[] UTF16LittleEndianBom = Encoding.Unicode.GetPreamble();
    private static readonly byte[] UTF16BigEndianBom = Encoding.BigEndianUnicode.GetPreamble();
    private static readonly byte[] UTF32LittleEndianBom = { 0xFF, 0xFE, 0x00, 0x00 };
    private static readonly byte[] UTF32BigEndianBom = { 0x00, 0x00, 0xFE, 0xFF };

    private static string Preview(string line) => line.Length <= MaximumPreviewCharacters
        ? line : line[..MaximumPreviewCharacters];

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>Precompiles the "simple expression" glob dialect used by
/// <c>System.IO.Enumeration.FileSystemName.MatchesSimpleExpression</c> — the framework call
/// re-parses the pattern on every invocation, which is wasteful in per-path hot loops. The
/// matcher reproduces the framework semantics exactly (verified by fuzz tests):
/// <c>*</c> matches any run of characters including <c>/</c>; <c>?</c> matches exactly one
/// character including <c>/</c>; <c>\*</c> is a literal <c>*</c>; <c>\?</c> keeps the <c>?</c>
/// wildcard (the escape is dropped); <c>\</c> before any other character makes that character
/// literal; a trailing <c>\</c> matches zero or one arbitrary character; matching is fully
/// anchored and case-insensitive, and empty input never matches. A leading <c>*</c> whose
/// remainder contains no wildcards is the framework's fast path: a case-insensitive suffix
/// (EndsWith) test on the raw remainder. Compiled matchers are served from
/// <see cref="SimpleGlobCache"/> (S5) so repeated searches reuse them instead of recompiling.</summary>
internal static class SimpleGlob
{
    /// <summary>Process-wide fallback cache for static call sites (.gitignore rules, tests);
    /// <see cref="WorkspaceSearchService"/> uses its own instance for its path filters so the
    /// compile counters stay per-service.</summary>
    public static SimpleGlobCache Default { get; } = new(SimpleGlobCache.DefaultCapacity);

    public static Func<string, bool> Compile(string pattern) => Default.Compile(pattern);
}

/// <summary>LRU cache of compiled glob matchers keyed by raw pattern (S5). The framework's
/// <c>FileSystemName.MatchesSimpleExpression</c> re-parses the pattern on every invocation and
/// even <see cref="SimpleGlob"/> used to recompile per search; this cache makes the compiled
/// matcher — regex or the leading-star suffix fast path — reusable across searches. One
/// instance is shared by every search a <see cref="WorkspaceSearchService"/> runs (the app hosts
/// a single service instance), so the first search pays the compile cost and later searches hit
/// the cache. Thread-safe: the bounded-parallel scan can compile patterns concurrently.</summary>
internal sealed class SimpleGlobCache
{
    public const int DefaultCapacity = 256;

    private sealed class Entry
    {
        public required string Pattern;
        public required Func<string, bool> Matcher;
        public Entry? Previous; // toward head (most recently used)
        public Entry? Next;
    }

    private static readonly Func<string, bool> Never = _ => false;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private Entry? _head; // most recently used
    private Entry? _tail; // least recently used

    public SimpleGlobCache(int capacity)
    {
        Capacity = Math.Max(1, capacity);
    }

    public int Capacity { get; }

    /// <summary>Number of patterns actually compiled (cache misses). Monotonic per instance;
    /// exposed for metrics/tests.</summary>
    public int CompileCount { get; private set; }

    /// <summary>Number of patterns served from the cache without recompiling.</summary>
    public int CacheHitCount { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    public Func<string, bool> Compile(string pattern)
    {
        if (pattern.Length == 0) return Never;
        lock (_gate)
        {
            if (_entries.TryGetValue(pattern, out var hit))
            {
                CacheHitCount++;
                Touch(hit);
                return hit.Matcher;
            }

            CompileCount++;
            var entry = new Entry { Pattern = pattern, Matcher = CompileCore(pattern) };
            _entries[pattern] = entry;
            PushHead(entry);
            if (_entries.Count > Capacity)
            {
                var evicted = _tail!;
                _entries.Remove(evicted.Pattern);
                Unlink(evicted);
            }

            return entry.Matcher;
        }
    }

    private void Touch(Entry entry)
    {
        if (ReferenceEquals(_head, entry)) return;
        Unlink(entry);
        PushHead(entry);
    }

    private void PushHead(Entry entry)
    {
        entry.Previous = null;
        entry.Next = _head;
        _head?.Previous = entry;
        _head = entry;
        _tail ??= entry;
    }

    private void Unlink(Entry entry)
    {
        entry.Previous?.Next = entry.Next;
        entry.Next?.Previous = entry.Previous;
        if (ReferenceEquals(_head, entry)) _head = entry.Next;
        if (ReferenceEquals(_tail, entry)) _tail = entry.Previous;
        entry.Previous = entry.Next = null;
    }

    private static Func<string, bool> CompileCore(string pattern)
    {
        if (pattern[0] == '*' && pattern.Length > 1)
        {
            var rest = pattern.AsSpan(1);
            if (rest.IndexOf('*') < 0 && rest.IndexOf('?') < 0)
            {
                var suffix = pattern[1..];
                return name => name.Length > 0 && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            }
        }

        var regex = new Regex(ToRegexPattern(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return name => name.Length > 0 && regex.IsMatch(name);
    }

    private static string ToRegexPattern(string pattern)
    {
        var builder = new StringBuilder(pattern.Length * 2 + 2);
        builder.Append('^');
        for (var i = 0; i < pattern.Length; i++)
        {
            var character = pattern[i];
            if (character == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    builder.Append(".{0,1}"); // trailing backslash: zero or one arbitrary character
                    continue;
                }

                var next = pattern[++i];
                if (next == '*')
                {
                    builder.Append('\\').Append('*'); // \* → literal '*'
                }
                else if (next == '?')
                {
                    builder.Append('.'); // \? → the '?' keeps its wildcard meaning
                }
                else
                {
                    builder.Append(Regex.Escape(next.ToString())); // \X → literal X
                }
            }
            else if (character == '*')
            {
                builder.Append(".*");
            }
            else if (character == '?')
            {
                builder.Append('.');
            }
            else
            {
                builder.Append(Regex.Escape(character.ToString()));
            }
        }

        builder.Append('$');
        return builder.ToString();
    }
}

/// <summary>Per-search include/exclude matcher. Patterns are precompiled once per search and
/// reused across searches through the shared <see cref="SimpleGlobCache"/> (S5) instead of
/// re-parsed by the framework on every path.</summary>
internal sealed class SearchPathFilter
{
    private readonly SimpleGlobCache _cache;
    private readonly Func<string, bool>[] _includes;
    private readonly Func<string, bool>[] _excludes;
    private readonly Func<string, bool>[] _workspaceExcludes;

    internal SearchPathFilter(
        WorkspaceSearchQuery query,
        IReadOnlyDictionary<string, bool> workspaceExcludes,
        SimpleGlobCache? compileCache = null)
    {
        _cache = compileCache ?? SimpleGlob.Default;
        _includes = CompilePatterns(Split(query.IncludePattern));
        _excludes = CompilePatterns(Split(query.ExcludePattern));
        _workspaceExcludes = CompilePatterns(workspaceExcludes
            .Where(pair => pair.Value)
            .Select(pair => pair.Key.Replace('\\', '/').Trim('/'))
            .Where(pattern => pattern.Length > 0));
    }

    public bool IsExcluded(string relativePath, bool directory)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (MatchesAny(_workspaceExcludes, normalized, directory) || MatchesAny(_excludes, normalized, directory)) return true;
        return !directory && _includes.Length > 0 && !MatchesAny(_includes, normalized, false);
    }

    private static bool MatchesAny(Func<string, bool>[] patterns, string relative, bool directory)
    {
        foreach (var pattern in patterns)
        {
            if (pattern(relative)) return true;
            if (!directory && relative.Contains('/'))
            {
                var fileName = relative[(relative.LastIndexOf('/') + 1)..];
                if (pattern(fileName)) return true;
            }
        }

        return false;
    }

    private Func<string, bool>[] CompilePatterns(IEnumerable<string> patterns) =>
        [.. patterns.Select(_cache.Compile)];

    private static string[] Split(string? value) => (value ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Directory-scoped .gitignore rule segment (S3). Rules are grouped per directory and
/// each scope links to the scope of the directory the walk came from, so a path inside a
/// directory is only ever tested against the scopes on the root→directory chain — rules from
/// sibling branches cannot match it (their BasePath never prefix-matches) and are never scanned.
/// Scopes without local rules reuse the parent scope instance (zero allocation, same as the old
/// flat-list pass-through).</summary>
internal sealed class GitIgnoreScope
{
    private GitIgnoreScope(IReadOnlyList<GitIgnoreRule> localRules, GitIgnoreScope? parent)
    {
        LocalRules = localRules;
        Parent = parent;
    }

    public static GitIgnoreScope Empty { get; } = new(Array.Empty<GitIgnoreRule>(), null);

    /// <summary>Rules defined by the directory's own <c>.gitignore</c> (scoped to it).</summary>
    public IReadOnlyList<GitIgnoreRule> LocalRules { get; }

    /// <summary>Scope of the parent directory in the enumeration walk (null at the root).</summary>
    public GitIgnoreScope? Parent { get; }

    public static GitIgnoreScope For(IReadOnlyList<GitIgnoreRule> localRules, GitIgnoreScope? parent) =>
        localRules.Count == 0 ? parent ?? Empty : new(localRules, parent);
}

/// <summary>Small, allocation-light matcher for common .gitignore rules. Rules are attached to
/// their directory, preserving nested ignore-file scope and last-match-wins negation. Each
/// <c>.gitignore</c> is parsed once per (path, mtime, length) — the parse result is cached across
/// searches (S3) — and each rule's glob is precompiled on first use instead of re-parsed by the
/// framework on every path test. Path matching walks the per-directory rule segments (see
/// <see cref="GitIgnoreScope"/>) instead of scanning a flat list of all inherited rules.</summary>
internal sealed record GitIgnoreRule(string BasePath, string Pattern, bool Negated, bool DirectoryOnly, bool Anchored)
{
    private Func<string, bool>? _compiled;

    public static IReadOnlyList<GitIgnoreRule> None { get; } = Array.Empty<GitIgnoreRule>();

    public static GitIgnoreScope LoadForDirectory(string directory, string relativeDirectory,
        GitIgnoreScope inherited, IUiLogService? logService = null)
    {
        var local = LoadLocalRules(Path.Combine(directory, ".gitignore"), logService);
        if (local.Count == 0) return inherited;
        // Scope the cached (file-level) rules to this directory; the `with` copies share the
        // lazily compiled matcher, so no glob is re-parsed here.
        IReadOnlyList<GitIgnoreRule> scoped = local.Count == 1
            ? [local[0] with { BasePath = relativeDirectory }]
            : local.Select(rule => rule with { BasePath = relativeDirectory }).ToArray();
        return GitIgnoreScope.For(scoped, inherited);
    }

    private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length,
        IReadOnlyList<GitIgnoreRule> Rules)> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCachedIgnoreFiles = 1024;

    private static IReadOnlyList<GitIgnoreRule> LoadLocalRules(string path, IUiLogService? logService)
    {
        DateTime lastWriteUtc;
        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return None;
            lastWriteUtc = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return None;
        }

        if (_fileCache.TryGetValue(path, out var hit) && hit.LastWriteUtc == lastWriteUtc && hit.Length == length)
        {
            return hit.Rules;
        }

        List<GitIgnoreRule> rules = [];
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var negated = line[0] == '!';
                if (negated) line = line[1..];
                if (line.Length == 0) continue;
                var directoryOnly = line.EndsWith('/');
                line = line.TrimEnd('/');
                var anchored = line.StartsWith('/');
                line = line.TrimStart('/');
                if (line.Length == 0) continue;
                // BasePath is filled in per-directory by LoadForDirectory (record `with` copies
                // share the lazily compiled matcher, so scoping never re-compiles).
                rules.Add(new(string.Empty, line.Replace('\\', '/'), negated, directoryOnly, anchored));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or DecoderFallbackException)
        {
            // A broken ignore file must not make the project unsearchable, but leave a diagnostic.
            logService?.Write("WARNING", $"无法解析忽略文件 {path}：{ex.Message}");
        }

        if (rules.Count > 0)
        {
            if (_fileCache.Count >= MaxCachedIgnoreFiles) _fileCache.Clear();
            _fileCache[path] = (lastWriteUtc, length, rules);
        }

        return rules;
    }

    /// <summary>Last-match-wins over the applicable rule segments only (S3): the path lives in
    /// <paramref name="scope"/>'s directory subtree, so only the scopes on the root→scope chain
    /// can apply. Scopes are walked from the path's directory upward; the first scope containing
    /// a matching rule is the deepest (hence last in apply order) one, and the last matching rule
    /// inside it decides the result — ancestor scopes are skipped entirely when a local match
    /// exists, and no rules from sibling branches are ever scanned.</summary>
    public static bool IsIgnored(string relativePath, bool directory, GitIgnoreScope scope)
    {
        for (var current = scope; current is not null; current = current.Parent)
        {
            var last = false;
            var matched = false;
            foreach (var rule in current.LocalRules)
            {
                if (!rule.Matches(relativePath, directory)) continue;
                matched = true;
                last = !rule.Negated;
            }

            if (matched) return last;
        }

        return false;
    }

    internal bool Matches(string path, bool directory)
    {
        if (DirectoryOnly && !directory && !IsUnderDirectory(path)) return false;
        var relative = path.Replace('\\', '/').Trim('/');
        var basePath = BasePath.Trim('/');
        if (basePath.Length > 0)
        {
            if (!relative.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase) &&
                !relative.Equals(basePath, StringComparison.OrdinalIgnoreCase)) return false;
            relative = relative.Length == basePath.Length ? string.Empty : relative[(basePath.Length + 1)..];
        }

        var match = Compiled;
        if (Anchored || Pattern.Contains('/'))
        {
            return match(relative);
        }

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(match);
    }

    private Func<string, bool> Compiled => _compiled ??= SimpleGlob.Compile(Pattern);

    private bool IsUnderDirectory(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        var basePrefix = BasePath.Trim('/');
        if (basePrefix.Length > 0)
        {
            if (!normalized.StartsWith(basePrefix + "/", StringComparison.OrdinalIgnoreCase)) return false;
            normalized = normalized[(basePrefix.Length + 1)..];
        }

        var match = Compiled;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (match(parts[index])) return true;
        }

        return false;
    }
}
