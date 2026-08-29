using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;

namespace Nornia.Desktop.Code;

/// <summary>Semantic token categories consumed by the AvalonEdit colour transformer.  The model is
/// deliberately independent from a particular editor so the same snapshot can drive the minimap,
/// outline and future accessibility surfaces.</summary>
public enum CodeTokenKind
{
    Plain,
    Comment,
    String,
    Number,
    Keyword,
    Type,
    Function,
    Tag,
    Attribute,
    Link,
}

/// <summary>A token range in one-based document lines.  Offset and length are character offsets in
/// that line, which makes applying a snapshot to AvalonEdit deterministic after a document swap.
/// <see cref="Scopes"/> keeps the original TextMate scope stack so the reader can resolve theme
/// styles (color + bold/italic/underline) with vscode-style last-match-wins rules instead of a
/// single coarse category; it is null for tokens produced without a grammar.</summary>
public sealed record CodeTokenSpan(int Line, int Start, int Length, CodeTokenKind Kind, IReadOnlyList<string>? Scopes = null);

public sealed record LanguageProfile(string LanguageId, string ScopeName, string GrammarResource, bool SupportsStructure);

public interface IGrammarCatalog
{
    LanguageProfile GetProfile(CodeFileType fileType);
    IReadOnlyCollection<LanguageProfile> Profiles { get; }
}

/// <summary>Single source of truth for TextMate scopes. GrammarResource is also the stable manifest
/// identity used by packaging/license validation; unknown languages are deliberately plain text.</summary>
public sealed class BuiltInGrammarCatalog : IGrammarCatalog
{
    public static readonly BuiltInGrammarCatalog Instance = new();
    private readonly Dictionary<string, LanguageProfile> _profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = Profile("csharp", "source.cs"),
        ["vb"] = Profile("vb", "source.asp.vb.net"),
        ["xml"] = Profile("xml", "text.xml"),
        ["xaml"] = Profile("xaml", "text.xml"),
        ["json"] = Profile("json", "source.json"),
        ["jsonc"] = Profile("jsonc", "source.json.comments"),
        ["javascript"] = Profile("javascript", "source.js"),
        ["typescript"] = Profile("typescript", "source.ts"),
        ["javascriptreact"] = Profile("javascriptreact", "source.js.jsx"),
        ["typescriptreact"] = Profile("typescriptreact", "source.tsx"),
        ["html"] = Profile("html", "text.html.derivative"),
        ["css"] = Profile("css", "source.css"),
        ["python"] = Profile("python", "source.python"),
        ["yaml"] = Profile("yaml", "source.yaml"),
        ["markdown"] = Profile("markdown", "text.html.markdown"),
        ["powershell"] = Profile("powershell", "source.powershell"),
        ["c"] = Profile("c", "source.c"),
        ["cpp"] = Profile("cpp", "source.cpp"),
        ["java"] = Profile("java", "source.java"),
        ["sql"] = Profile("sql", "source.sql"),
        ["fsharp"] = Profile("fsharp", "source.fsharp"),
        ["scss"] = Profile("scss", "source.css.scss"),
        ["less"] = Profile("less", "source.css.less"),
        ["shellscript"] = Profile("shellscript", "source.shell"),
        ["bat"] = Profile("bat", "source.batchfile"),
        ["ini"] = Profile("ini", "source.ini"),
        ["go"] = Profile("go", "source.go"),
        ["rust"] = Profile("rust", "source.rust"),
        ["ruby"] = Profile("ruby", "source.ruby"),
        ["php"] = Profile("php", "source.php"),
    };

    public IReadOnlyCollection<LanguageProfile> Profiles => _profiles.Values;

    public LanguageProfile GetProfile(CodeFileType fileType) => _profiles.TryGetValue(fileType.LanguageId, out var profile)
        ? profile
        : new LanguageProfile(fileType.LanguageId, "text.plain", string.Empty, false);

    private static LanguageProfile Profile(string languageId, string scope) =>
        new(languageId, scope, $"TextMateSharp.Grammars@2.0.4:{languageId}", true);
}

/// <summary>Immutable result of a background language pass.  Version is owned by the caller and
/// prevents a stale analysis result from being painted over a newer document.  The optional by-line
/// indexes are precomputed on the worker thread (single pass over <see cref="Tokens"/>) so the UI
/// thread only performs O(1) field swaps when the snapshot is published — for large C/C++ files the
/// old path re-grouped hundreds of thousands of tokens on the UI thread at every tab activation.</summary>
public sealed record CodePresentationSnapshot(
    int DocumentVersion,
    IReadOnlyList<CodeTokenSpan> Tokens,
    IReadOnlyList<CodeOutlineEntry> Outline,
    IReadOnlyList<CodeFoldSection> Folds,
    IReadOnlyList<byte> LineDensity,
    IReadOnlyDictionary<int, IReadOnlyList<CodeTokenSpan>>? TokensByLine = null,
    IReadOnlyDictionary<int, CodeTokenKind>? MinimapKindByLine = null)
{
    /// <summary>token 是否覆盖整个文档。`false` 表示增量"首屏快照"(仅前 N 行完成分词):
    /// 编辑器必须保留 AvalonEdit 内置定义兜底(未分词行保持回退色),视图只绘制已有 token 的行;
    /// 完整快照到达后内置定义才关闭。既有快照构造不指定该字段,默认 `true`。</summary>
    public bool IsComplete { get; init; } = true;

    public static CodePresentationSnapshot Empty(int version = 0) => new(version, [], [], [], []);
}

/// <summary>Builds the by-line token index and the first-kind-per-line minimap tint in one pass
/// (worker thread).  Returns (null, null) for empty token lists to keep the snapshot lean.</summary>
internal static class CodePresentationSnapshotIndexer
{
    public static (IReadOnlyDictionary<int, IReadOnlyList<CodeTokenSpan>>? ByLine,
        IReadOnlyDictionary<int, CodeTokenKind>? Minimap) Build(IReadOnlyList<CodeTokenSpan> tokens)
    {
        if (tokens.Count == 0)
        {
            return (null, null);
        }

        var mutable = new Dictionary<int, List<CodeTokenSpan>>();
        var minimap = new Dictionary<int, CodeTokenKind>();
        foreach (var token in tokens)
        {
            if (!mutable.TryGetValue(token.Line, out var list))
            {
                list = new List<CodeTokenSpan>(4);
                mutable[token.Line] = list;
                // 首 token 决定该行 minimap 色带(与旧 UI 线程逻辑一致)。
                minimap[token.Line] = token.Kind;
            }

            list.Add(token);
        }

        // 具体 Dictionary 不实现 IReadOnlyDictionary<int, IReadOnlyList<...>>(值类型不变),
        // 投影一层接口字典;行列表引用原 List(只读使用,无深拷贝)。
        var byLine = new Dictionary<int, IReadOnlyList<CodeTokenSpan>>(mutable.Count);
        foreach (var entry in mutable)
        {
            byLine[entry.Key] = entry.Value;
        }

        return (byLine, minimap);
    }
}

public interface ICodePresentationService
{
    Task<CodePresentationSnapshot> AnalyzeAsync(string text, CodeFileType fileType, int documentVersion, CancellationToken cancellationToken = default);
}

public sealed record CodePresentationCacheIdentity(
    string NormalizedPath,
    long FileLength,
    long LastWriteTimeUtcTicks,
    string EncodingName,
    int WindowStartLine,
    int LanguageConfigurationVersion = 1)
{
    public static CodePresentationCacheIdentity FromFile(string path, string encodingName, int windowStartLine)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        return new CodePresentationCacheIdentity(fullPath, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc.Ticks : 0, encodingName, windowStartLine);
    }
}

/// <summary>Read-only language pipeline.  TextMateSharp supplies portable token scopes while the
/// existing outline/folding services remain the source of structural truth.  Results are bounded
/// and cached by content + language, so tab switches do not repeat expensive parsing.</summary>
public sealed class CodePresentationService : ICodePresentationService
{
    public static readonly CodePresentationService Instance = new();

    /// <summary>首屏增量批:分词推进到该行数时,把当前 token 作为部分快照(IsComplete=false)
    /// 发布,让首屏在 1~2 帧内拿到最终配色,消除"缺省色 → 正确高亮"的闪帧;其余行继续在后台
    /// 分词。256 行覆盖常见字号下的任何首屏。</summary>
    internal const int FirstBatchLines = 256;

    private const long CacheBudgetBytes = 64L * 1024 * 1024;
    private readonly TextMateGrammarStore _grammars;
    private readonly ILanguageTokenizer _tokenizer;
    private readonly ICodeOutlineParser _outline = CodeOutlineParser.Instance;
    private readonly ICodeFoldingStrategy _folding = CodeFoldingStrategy.Instance;
    private readonly WeightedSnapshotCache _cache = new(CacheBudgetBytes);

    /// <summary>生产实例持有独立的进程级 grammar 存储;测试可注入隔离实例断言编译次数。</summary>
    public CodePresentationService(TextMateGrammarStore? grammars = null)
    {
        _grammars = grammars ?? new TextMateGrammarStore();
        _tokenizer = new TextMateLanguageTokenizer(grammars: _grammars);
    }

    public Task<CodePresentationSnapshot> AnalyzeAsync(string text, CodeFileType fileType, int documentVersion, CancellationToken cancellationToken = default)
        => AnalyzeCoreAsync(text, fileType, documentVersion, cacheIdentity: null, includeStructure: true, onFirstBatch: null, cancellationToken);

    public Task<CodePresentationSnapshot> AnalyzeAsync(
        string text,
        CodeFileType fileType,
        int documentVersion,
        CodePresentationCacheIdentity cacheIdentity,
        CancellationToken cancellationToken = default)
        => AnalyzeCoreAsync(text, fileType, documentVersion, cacheIdentity, includeStructure: true, onFirstBatch: null, cancellationToken);

    /// <summary>Display-only language pass. The editor already builds its symbol tree and folding
    /// projection in the shared derived-content worker, so the paint path asks for only tokens
    /// and minimap density. The full <see cref="AnalyzeAsync(string, CodeFileType, int, CancellationToken)"/>
    /// contract remains available to callers that need the complete snapshot.
    /// <paramref name="onFirstBatch"/> 在 worker 线程、分词达到 <see cref="FirstBatchLines"/> 行时
    /// 回调一次,携带覆盖首屏的部分快照(调用方负责切回 UI 线程发布)。</summary>
    public Task<CodePresentationSnapshot> AnalyzeTokensAsync(
        string text,
        CodeFileType fileType,
        int documentVersion,
        CodePresentationCacheIdentity cacheIdentity,
        Action<CodePresentationSnapshot>? onFirstBatch = null,
        CancellationToken cancellationToken = default)
        => AnalyzeCoreAsync(text, fileType, documentVersion, cacheIdentity, includeStructure: false, onFirstBatch, cancellationToken);

    /// <summary>Token-only analysis for transient documents such as diff panes.  These documents
    /// do not have a file-backed cache identity, but must still use the exact presentation pass as
    /// a resource-manager preview.</summary>
    public Task<CodePresentationSnapshot> AnalyzeTokensAsync(
        string text,
        CodeFileType fileType,
        int documentVersion,
        CancellationToken cancellationToken = default)
        => AnalyzeCoreAsync(text, fileType, documentVersion, cacheIdentity: null, includeStructure: false, onFirstBatch: null, cancellationToken);

    private Task<CodePresentationSnapshot> AnalyzeCoreAsync(
        string text,
        CodeFileType fileType,
        int documentVersion,
        CodePresentationCacheIdentity? cacheIdentity,
        bool includeStructure,
        Action<CodePresentationSnapshot>? onFirstBatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(CodePresentationSnapshot.Empty(documentVersion));
        }

        var key = cacheIdentity is null ? null : string.Join('|',
            includeStructure ? "full" : "tokens",
            cacheIdentity.NormalizedPath,
            cacheIdentity.FileLength,
            cacheIdentity.LastWriteTimeUtcTicks,
            cacheIdentity.EncodingName,
            cacheIdentity.WindowStartLine,
            cacheIdentity.LanguageConfigurationVersion,
            fileType.LanguageId);
        // Full and display-only entries share one budget; the extra token-only cache must not
        // double the language model's memory ceiling.
        var cache = _cache;
        if (key is not null && cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult(cached with { DocumentVersion = documentVersion });
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineDensity = BuildLineDensity(text);
            var tokens = _tokenizer.Tokenize(
                text,
                fileType,
                onFirstBatch: onFirstBatch is { } publish
                    ? batchTokens =>
                    {
                        // 首屏部分快照:行索引与完整快照同一趟构建;LineDensity 复用全文计算结果
                        // (不重算);结构投影留空(显示路径不消费,视图只绘制已有 token 的行)。
                        var (batchByLine, batchMinimap) = CodePresentationSnapshotIndexer.Build(batchTokens);
                        publish(new CodePresentationSnapshot(
                            documentVersion,
                            batchTokens,
                            [],
                            [],
                            lineDensity,
                            batchByLine,
                            batchMinimap)
                        { IsComplete = false });
                    }
                    : null,
                firstBatchLines: FirstBatchLines,
                cancellationToken);
            // 行索引在 worker 一次性建好并随快照进缓存:UI 线程发布时只做 O(1) 字段替换。
            var (tokensByLine, minimapKindByLine) = CodePresentationSnapshotIndexer.Build(tokens);
            var snapshot = new CodePresentationSnapshot(
                documentVersion,
                tokens,
                includeStructure ? _outline.Parse(text, fileType.OutlineKind) : [],
                includeStructure ? _folding.FindSections(text, fileType) : [],
                lineDensity,
                tokensByLine,
                minimapKindByLine);
            if (key is not null) cache.Set(key, snapshot with { DocumentVersion = 0 });
            return snapshot;
        }, cancellationToken);
    }

    private static IReadOnlyList<byte> BuildLineDensity(string text)
    {
        var result = new List<byte>();
        var lineLength = 0;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                result.Add((byte)Math.Min(255, lineLength));
                lineLength = 0;
            }
            else if (character != '\r')
            {
                lineLength++;
            }
        }

        if (lineLength > 0 || text.Length > 0 && text[^1] != '\n')
        {
            result.Add((byte)Math.Min(255, lineLength));
        }

        return result;
    }

    private sealed class WeightedSnapshotCache(long budgetBytes)
    {
        private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _lru = new();
        private readonly object _sync = new();
        private long _weight;

        public bool TryGetValue(string key, out CodePresentationSnapshot snapshot)
        {
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out var node))
                {
                    snapshot = null!;
                    return false;
                }

                _lru.Remove(node);
                _lru.AddFirst(node);
                snapshot = node.Value.Snapshot;
                return true;
            }
        }

        public void Set(string key, CodePresentationSnapshot snapshot)
        {
            // LineDensity 每行 1 字节;当快照携带预计算行索引时(每行两个字典条目,约 ~96B),
            // 一并计入预算,避免预索引让实际驻留超出 64MB 上限。
            var weight = Math.Max(256, snapshot.Tokens.Count * 24L + snapshot.Outline.Count * 96L + snapshot.Folds.Count * 48L
                + snapshot.LineDensity.Count
                + (snapshot.TokensByLine is not null ? snapshot.LineDensity.Count * 96L : 0L));
            if (weight > budgetBytes) return;
            lock (_sync)
            {
                if (_entries.ContainsKey(key)) return;
                var node = _lru.AddFirst(new Entry(key, snapshot, weight));
                _entries.Add(key, node);
                _weight += weight;
                while (_weight > budgetBytes && _lru.Last is { } last)
                {
                    _lru.RemoveLast();
                    _entries.Remove(last.Value.Key);
                    _weight -= last.Value.Weight;
                }
            }
        }

        private sealed record Entry(string Key, CodePresentationSnapshot Snapshot, long Weight);
    }
}

/// <summary>进程级 TextMate grammar 存储:单个 Registry,每个 scope 首次使用时编译一次
/// (锁只串行化首次编译,之后是纯字典查找)。打开文件不再逐次支付 grammar 编译成本
/// (闪帧窗口的主要构成之一)。
/// TextMateSharp 的 Grammar 实例内部持有按行结果缓存(lastLineText/lastRuleStack),
/// <c>TokenizeLine</c> 并发性不安全:每个 grammar 附带一个 <see cref="Loaded.Gate"/>,
/// 会话在 gate 内逐行扫描(行粒度交错,吞吐近似并行)。</summary>
public sealed class TextMateGrammarStore
{
    /// <summary>已编译 grammar + 其扫描互斥门。</summary>
    public sealed record Loaded(IGrammar Grammar, object Gate);

    private readonly object _gate = new();
    private Registry? _registry;
    private readonly Dictionary<string, Loaded> _grammars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _loadCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>该 scope 是否已编译并常驻缓存。</summary>
    public bool HasGrammar(string scopeName)
    {
        lock (_gate) return _grammars.ContainsKey(scopeName);
    }

    /// <summary>该 scope 实际编译次数(缓存命中不计);每个 scope 恒为 0 或 1。</summary>
    public int LoadCountFor(string scopeName)
    {
        lock (_gate) return _loadCounts.TryGetValue(scopeName, out var count) ? count : 0;
    }

    public Loaded GetOrLoad(string scopeName)
    {
        lock (_gate)
        {
            // 首个 onig 使用(编译)全进程只发生一次:多 Registry(如测试注入的隔离存储)
            // 并行首启不再竞态。
            TextMateNativeRuntime.EnsureWarmed();
            _registry ??= new Registry(new RegistryOptions(ThemeName.DarkPlus));
            if (!_grammars.TryGetValue(scopeName, out var loaded))
            {
                loaded = new Loaded(_registry.LoadGrammar(scopeName), new object());
                _grammars[scopeName] = loaded;
                _loadCounts[scopeName] = 1;
            }

            return loaded;
        }
    }
}

public interface ILanguageTokenizer
{
    /// <param name="onFirstBatch">分词推进到第 <paramref name="firstBatchLines"/> 行(且其后仍有行)
    /// 时,在 worker 线程回调当前 token 列表;用于首屏部分快照的增量发布。0 表示不启用。</param>
    IReadOnlyList<CodeTokenSpan> Tokenize(
        string text,
        CodeFileType fileType,
        Action<IReadOnlyList<CodeTokenSpan>>? onFirstBatch = null,
        int firstBatchLines = 0,
        CancellationToken cancellationToken = default);
}

/// <summary>Small TextMate grammar used as a safe common denominator for the languages exposed by
/// the workbench.  It intentionally prefers reliable comments/strings/numbers/markup tokenization
/// over pretending that an unavailable grammar understands a language.  Scope classification is
/// still TextMate based and can be expanded by adding embedded grammar resources later.</summary>
public interface ILanguagePresentationSession : IDisposable
{
    IReadOnlyList<CodeTokenSpan> Tokenize(
        ReadOnlyMemory<char> text,
        Action<IReadOnlyList<CodeTokenSpan>>? onFirstBatch = null,
        int firstBatchLines = 0,
        CancellationToken cancellationToken = default);
}

public sealed class TextMateLanguageTokenizer : ILanguageTokenizer
{
    /// <summary>超过该字符数的行跳过 TextMate 分词(对标 vscode
    /// <c>editor.maxTokenizationLineLength</c> 默认 2000:超长行不分词,避免单行吞掉整个
    /// 分词时间预算)。</summary>
    public const int MaxTokenizedLineLength = 2000;

    private readonly IGrammarCatalog _catalog;
    private readonly TextMateGrammarStore _grammars;

    public TextMateLanguageTokenizer(IGrammarCatalog? catalog = null, TextMateGrammarStore? grammars = null)
    {
        _catalog = catalog ?? BuiltInGrammarCatalog.Instance;
        _grammars = grammars ?? new TextMateGrammarStore();
    }

    public IReadOnlyList<CodeTokenSpan> Tokenize(
        string text,
        CodeFileType fileType,
        Action<IReadOnlyList<CodeTokenSpan>>? onFirstBatch = null,
        int firstBatchLines = 0,
        CancellationToken cancellationToken = default)
    {
        // Image and plain-text previews intentionally have no colour noise.
        if (fileType.LanguageId is "plaintext" or "log")
        {
            return [];
        }

        var profile = _catalog.GetProfile(fileType);
        if (profile.ScopeName == "text.plain") return [];
        var tokens = TokenizeWithGrammar(text, profile, onFirstBatch, firstBatchLines, cancellationToken);
        if (fileType.LanguageId.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            // VS Code's Markdown grammar embeds the language grammar selected after a fence
            // (the embeddedLanguages contribution). TextMateSharp exposes the Markdown scopes,
            // but does not compose that embedded grammar automatically, so add the same nested
            // token pass here while preserving the original Markdown fence tokens.
            tokens = AddMarkdownEmbeddedTokens(text, tokens, cancellationToken);
        }

        return tokens;
    }

    private IReadOnlyList<CodeTokenSpan> TokenizeWithGrammar(
        string text,
        LanguageProfile profile,
        Action<IReadOnlyList<CodeTokenSpan>>? onFirstBatch,
        int firstBatchLines,
        CancellationToken cancellationToken)
    {
        try
        {
            using ILanguagePresentationSession session = new TextMatePresentationSession(profile, _grammars);
            return session.Tokenize(text.AsMemory(), onFirstBatch, firstBatchLines, cancellationToken);
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or TypeInitializationException or InvalidOperationException)
        {
            // Native Oniguruma or an individual grammar may be unavailable on this machine. The
            // document alone falls back; startup and other tabs remain unaffected.
            return [];
        }
    }

    private static readonly Regex MarkdownFenceStart = new(
        @"^[ \t]{0,3}(?<fence>`{3,}|~{3,})[ \t]*(?<language>[^\s`~]+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private IReadOnlyList<CodeTokenSpan> AddMarkdownEmbeddedTokens(
        string text, IReadOnlyList<CodeTokenSpan> markdownTokens, CancellationToken cancellationToken)
    {
        var result = new List<CodeTokenSpan>(markdownTokens);
        var lines = text.Split('\n');
        for (var openingLine = 0; openingLine < lines.Length; openingLine++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = MarkdownFenceStart.Match(lines[openingLine].TrimEnd('\r'));
            if (!match.Success) continue;

            var marker = match.Groups["fence"].Value;
            var closingLine = openingLine + 1;
            while (closingLine < lines.Length && !IsMarkdownFenceEnd(lines[closingLine], marker))
            {
                closingLine++;
            }

            var language = ResolveMarkdownFenceLanguage(match.Groups["language"].Value);
            if (language is not null && closingLine > openingLine + 1)
            {
                var body = string.Join('\n', lines[(openingLine + 1)..closingLine]
                    .Select(line => line.TrimEnd('\r')));
                // 嵌套代码围栏走完整分词,不产生首屏批(主 Markdown 通道已负责增量发布)。
                var embeddedTokens = TokenizeWithGrammar(body, _catalog.GetProfile(language), null, 0, cancellationToken);
                foreach (var token in embeddedTokens)
                {
                    // The nested tokenizer starts at line 1; the first body line is directly
                    // below the fence, so shift it back into the Markdown document coordinates.
                    result.Add(token with { Line = token.Line + openingLine + 1 });
                }
            }

            // Continue after the closing fence. An unterminated fence naturally consumes the
            // remainder of the current source window, matching VS Code's stateful grammar.
            openingLine = closingLine < lines.Length ? closingLine : lines.Length;
        }

        return result;
    }

    private static bool IsMarkdownFenceEnd(string line, string marker)
    {
        var trimmed = line.AsSpan().Trim();
        if (trimmed.Length < marker.Length || trimmed[0] != marker[0]) return false;
        var count = 0;
        while (count < trimmed.Length && trimmed[count] == marker[0]) count++;
        return count >= marker.Length && trimmed[count..].Trim().Length == 0;
    }

    private static CodeFileType? ResolveMarkdownFenceLanguage(string language)
    {
        var normalized = language.Trim().TrimStart('{', '.').TrimEnd('}', ',').ToLowerInvariant();
        var extension = normalized switch
        {
            "csharp" or "cs" or "c#" => "cs",
            "cpp" or "c++" or "cc" or "cxx" => "cpp",
            "javascript" or "js" or "node" => "js",
            "typescript" or "ts" => "ts",
            "tsx" => "tsx",
            "jsx" => "jsx",
            "python" or "py" => "py",
            "powershell" or "pwsh" or "ps" => "ps1",
            "shell" or "shellscript" or "bash" or "sh" => "sh",
            "yaml" or "yml" => "yml",
            "json" => "json",
            "jsonc" => "jsonc",
            "xml" => "xml",
            "xaml" => "xaml",
            "html" => "html",
            "css" => "css",
            "scss" => "scss",
            "less" => "less",
            "java" => "java",
            "sql" => "sql",
            "go" => "go",
            "rust" or "rs" => "rs",
            "ruby" or "rb" => "rb",
            "php" => "php",
            "fsharp" or "fs" => "fs",
            "markdown" or "md" => "md",
            _ => null,
        };

        return extension is null ? null : CodeFileTypeRegistry.Instance.FromExtension(extension);
    }

    internal static CodeTokenKind Classify(IEnumerable<string> scopes)
    {
        var scope = string.Join(' ', scopes);
        if (scope.Contains("comment", StringComparison.OrdinalIgnoreCase)) return CodeTokenKind.Comment;
        if (scope.Contains("string", StringComparison.OrdinalIgnoreCase)) return CodeTokenKind.String;
        if (scope.Contains("numeric", StringComparison.OrdinalIgnoreCase) || scope.Contains("number", StringComparison.OrdinalIgnoreCase)) return CodeTokenKind.Number;
        if (scope.Contains("keyword", StringComparison.OrdinalIgnoreCase)) return CodeTokenKind.Keyword;
        if (scope.Contains("function", StringComparison.OrdinalIgnoreCase)) return CodeTokenKind.Function;
        if (scope.Contains("attribute", StringComparison.OrdinalIgnoreCase)) return CodeTokenKind.Attribute;
        if (scope.Contains("tag", StringComparison.OrdinalIgnoreCase)) return CodeTokenKind.Tag;
        if (scope.Contains("type", StringComparison.OrdinalIgnoreCase) || scope.Contains("class", StringComparison.OrdinalIgnoreCase)) return CodeTokenKind.Type;
        return CodeTokenKind.Plain;
    }

    private sealed class TextMatePresentationSession : ILanguagePresentationSession
    {
        private readonly IGrammar _grammar;
        private readonly object _tokenizeGate;
        private readonly LanguageProfile _profile;
        private readonly Dictionary<int, IStateStack> _checkpoints = new();

        public TextMatePresentationSession(LanguageProfile profile, TextMateGrammarStore grammars)
        {
            _profile = profile;
            TextMateNativeRuntime.EnsureRegistered();
            // grammar 每 scope 只编译一次;扫描在 gate 内逐行进行(grammar 实例内部有
            // 按行结果缓存,同一实例的并发 TokenizeLine 不安全)。
            var loaded = grammars.GetOrLoad(profile.ScopeName);
            _grammar = loaded.Grammar;
            _tokenizeGate = loaded.Gate;
        }

        public IReadOnlyList<CodeTokenSpan> Tokenize(
            ReadOnlyMemory<char> text,
            Action<IReadOnlyList<CodeTokenSpan>>? onFirstBatch = null,
            int firstBatchLines = 0,
            CancellationToken cancellationToken = default)
        {
            var tokens = new List<CodeTokenSpan>();
            var lineNumber = 1;
            IStateStack state = StateStack.NULL;
            var repairXmlComments = _profile.LanguageId is "xml" or "xaml";
            var inXmlComment = false;
            var lineStart = 0;
            while (lineStart <= text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = text.Span[lineStart..];
                var newline = remaining.IndexOf('\n');
                var length = newline < 0 ? remaining.Length : newline;
                if (length > 0 && remaining[length - 1] == '\r') length--;
                var line = text.Slice(lineStart, length);
                var lineTokenStart = tokens.Count;
                if ((lineNumber - 1) % 256 == 0) _checkpoints[lineNumber] = state;
                if (line.Length <= MaxTokenizedLineLength)
                {
                    // gate:同一 grammar 实例的内部按行缓存在并发 TokenizeLine 下不安全,
                    // 同语言会话按行交错扫描(无争用时锁开销 < 分词本身 0.1%)。
                    lock (_tokenizeGate)
                    {
                        var result = _grammar.TokenizeLine(new LineText(line.ToString()), state, TimeSpan.FromMilliseconds(40));
                        state = result.RuleStack;
                        AppendTokens(tokens, result.Tokens, _profile, lineNumber, line);
                    }
                }
                if (repairXmlComments)
                {
                    // TextMateSharp.Grammars 2.0.4 的 XML grammar 会标记 <!-- / -->，却把
                    // 二者之间的正文发成 text.xml / Plain。用 grammar 已确认的起始标记维护
                    // 跨行状态，并在最后追加覆盖 token，使 XAML/XML 注释全文使用注释色。
                    AppendXmlCommentRepairTokens(tokens, lineTokenStart, tokens.Count,
                        lineNumber, line.Span, ref inXmlComment);
                }
                // 超长行(> MaxTokenizedLineLength,对标 vscode editor.maxTokenizationLineLength=2000
                // "Lines above this length will not be tokenized for performance reasons")整体
                // 跳过 TextMate 分词:不产出 token,语法状态原样带过(vscode nullTokenize 语义,
                // 不把半成品状态喂给后续行)。旧实现对 2KB–64KB 行仍跑带压缩预算的分词,
                // 压缩后的预算对这类行形同虚设且整行 ToString 分配照样发生。

                // 首屏增量批:第 N 行完成且其后仍有行时交出当前 token(列表拷贝,后续行继续
                // 追加不影响已交出的快照)。整篇不足 N 行时不产生部分快照。
                if (onFirstBatch is not null && firstBatchLines > 0 && lineNumber == firstBatchLines && newline >= 0)
                {
                    onFirstBatch(tokens.ToList());
                }

                lineNumber++;
                if (newline < 0) break;
                lineStart += newline + 1;
            }

            return tokens;
        }

        /// <summary>把一行 TextMate 结果追加为 token(4096 上限 + scope 栈保留)。scope 数组
        /// 拷贝一份——TextMateSharp 可能复用 token 对象。</summary>
        private static void AppendTokens(List<CodeTokenSpan> tokens, IReadOnlyCollection<TextMateSharp.Grammars.IToken> resultTokens,
            LanguageProfile profile, int lineNumber, ReadOnlyMemory<char> line)
        {
            var tokenCount = 0;
            foreach (var token in resultTokens)
            {
                if (++tokenCount > 4096) break;
                var kind = Classify(token.Scopes);
                if (kind == CodeTokenKind.Plain && IsLanguageKeyword(profile.LanguageId,
                        line.Span[token.StartIndex..Math.Min(token.EndIndex, line.Length)]))
                {
                    kind = CodeTokenKind.Keyword;
                }
                var tokenLength = Math.Max(0, token.EndIndex - token.StartIndex);
                if (tokenLength > 0) tokens.Add(new CodeTokenSpan(lineNumber, token.StartIndex, tokenLength, kind, token.Scopes.ToArray()));
            }
        }

        private static void AppendXmlCommentRepairTokens(
            List<CodeTokenSpan> tokens,
            int grammarTokenStart,
            int grammarTokenEnd,
            int lineNumber,
            ReadOnlySpan<char> line,
            ref bool inComment)
        {
            var scan = 0;
            while (scan < line.Length)
            {
                if (!inComment)
                {
                    var opening = -1;
                    for (var index = grammarTokenStart; index < grammarTokenEnd; index++)
                    {
                        var token = tokens[index];
                        if (token.Start < scan || token.Start + 4 > line.Length
                            || !line.Slice(token.Start, 4).SequenceEqual("<!--"))
                        {
                            continue;
                        }

                        if (token.Kind == CodeTokenKind.Comment)
                        {
                            opening = token.Start;
                            break;
                        }
                    }

                    if (opening < 0)
                    {
                        return;
                    }

                    scan = opening;
                    inComment = true;
                }

                var closingOffset = line[scan..].IndexOf("-->");
                if (closingOffset < 0)
                {
                    tokens.Add(new CodeTokenSpan(lineNumber, scan, line.Length - scan,
                        CodeTokenKind.Comment, ["text.xml", "comment.block.xml"]));
                    return;
                }

                var end = scan + closingOffset + 3;
                tokens.Add(new CodeTokenSpan(lineNumber, scan, end - scan,
                    CodeTokenKind.Comment, ["text.xml", "comment.block.xml"]));
                inComment = false;
                scan = end;
            }
        }

        public void Dispose() => _checkpoints.Clear();

    }

    private static bool IsLanguageKeyword(string languageId, ReadOnlySpan<char> token) =>
        languageId == "csharp" && token switch
        {
            "abstract" or "as" or "base" or "bool" or "break" or "byte" or "case" or "catch" or
            "char" or "checked" or "class" or "const" or "continue" or "decimal" or "default" or
            "delegate" or "do" or "double" or "else" or "enum" or "event" or "explicit" or
            "extern" or "false" or "finally" or "fixed" or "float" or "for" or "foreach" or
            "goto" or "if" or "implicit" or "in" or "int" or "interface" or "internal" or
            "is" or "lock" or "long" or "namespace" or "new" or "null" or "object" or "operator" or
            "out" or "override" or "params" or "private" or "protected" or "public" or "readonly" or
            "ref" or "return" or "sbyte" or "sealed" or "short" or "sizeof" or "stackalloc" or
            "static" or "string" or "struct" or "switch" or "this" or "throw" or "true" or "try" or
            "typeof" or "uint" or "ulong" or "unchecked" or "unsafe" or "ushort" or "using" or
            "virtual" or "void" or "volatile" or "while" => true,
            _ => false,
        };

}

internal static class TextMateNativeRuntime
{
    private static int _registered;
    private static readonly object _warmGate = new();
    private static volatile int _warmed;

    /// <summary>Oniguruma 原生库的首次使用(正则编译/扫描)在多线程并行首启下会竞态:
    /// 初始化半途失败表现为 tokenize 抛出未兜底的异常,或 grammar 编译出不可用的正则
    /// (整行退化为无 scope 的 Plain token)。全进程第一个使用者在静态锁内完成一次真实的
    /// 编译+扫描 warmup,其余线程等待其结束——之后原生库已完全初始化,所有 Registry/会话
    /// 的使用完全并行。warmup 自身失败被吞掉,回到既有兜底语义(不可用时逐文档空集合)。</summary>
    public static void EnsureWarmed()
    {
        if (_warmed == 1) return;
        lock (_warmGate)
        {
            if (_warmed == 1) return;
            try
            {
                EnsureRegistered();
                var registry = new Registry(new RegistryOptions(ThemeName.DarkPlus));
                var grammar = registry.LoadGrammar("source.css");
                grammar.TokenizeLine(new LineText("a {}"), StateStack.NULL, TimeSpan.FromMilliseconds(100));
            }
            catch (Exception)
            {
                // 原生桥不可用或 warmup 异常:保持既有兜底(分词失败 → 空集合)。
            }

            _warmed = 1;
        }
    }

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0 || !OperatingSystem.IsWindows()) return;
        try
        {
            var assembly = Assembly.Load("Onigwrap");
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or BadImageFormatException)
        {
            // The tokenizer's document-level fallback handles the unavailable native bridge.
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("onigwrap", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => string.Empty,
        };
        if (rid.Length == 0) return IntPtr.Zero;
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "libonigwrap.dll");
        return File.Exists(path) && NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
    }
}
