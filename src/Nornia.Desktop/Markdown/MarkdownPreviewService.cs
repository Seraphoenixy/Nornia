using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WpfMath.Controls;
using XamlMath;
using XamlMath.Exceptions;
using WpfMath.Parsers;
using MarkdigBlock = Markdig.Syntax.Block;
using MarkdigInline = Markdig.Syntax.Inlines.Inline;
using MarkdigTable = Markdig.Extensions.Tables.Table;
using MarkdigTableCell = Markdig.Extensions.Tables.TableCell;
using MarkdigTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTable = System.Windows.Documents.Table;

namespace Nornia.Desktop.Markdown;

public enum MarkdownViewMode
{
    Rendered,
    Source,
}

public sealed record MarkdownDiagnostic(string Message, int Line = 0, bool IsError = false);

public sealed record MarkdownHeading(string Text, string Anchor, int Level, int Line, string? WpfName = null)
{
    public string DocumentName => WpfName ?? "md_heading";
}

public sealed record MarkdownLinkRequest(string Url, string BasePath, bool IsImage, string? AltText = null);

/// <summary>Markdown 解析产物。<see cref="Document"/>(Markdig AST)在预览视图完成首次渲染后
/// 被标签分离置空(<c>FilePreviewTab.DetachMarkdownDocument</c>)——大 AST 不随 FlowDocument 长期
/// 驻留;需要重渲染(主题变化/切回预览)时经 <see cref="MarkdownParseCache"/> 的内容哈希 LRU
/// 复用已解析 AST(未命中才从标签的 <c>Content</c> 重新解析)。标题列表、诊断、图片路径列表与
/// 渲染预算随结果对象存活到标签切回源码模式或关闭。</summary>
public sealed class MarkdownRenderResult
{
    public MarkdownRenderResult(MarkdownDocument document, string sourcePath,
        IReadOnlyList<MarkdownHeading> headings, IReadOnlyList<MarkdownDiagnostic> diagnostics,
        IReadOnlyList<string> imagePaths, MarkdownRenderBudget budget,
        IReadOnlyList<string>? preheatedPaths = null)
    {
        Document = document;
        SourcePath = sourcePath;
        Headings = headings;
        Diagnostics = diagnostics;
        ImagePaths = imagePaths;
        Budget = budget;
        PreheatedPaths = preheatedPaths ?? [];
    }

    /// <summary>Markdig AST;渲染完成后可为 null(已分离)。</summary>
    public MarkdownDocument? Document { get; internal set; }

    public string SourcePath { get; }

    public IReadOnlyList<MarkdownHeading> Headings { get; }

    public IReadOnlyList<MarkdownDiagnostic> Diagnostics { get; }

    /// <summary>文档中实际嵌入的本地图片绝对路径(每处图片引用一条,允许重复)——
    /// 与渲染器逐处 <c>MarkdownImageCache.Acquire</c> 对称,视图替换文档时按此列表释放引用。</summary>
    public IReadOnlyList<string> ImagePaths { get; }

    /// <summary>解析期统计的渲染预算(顶层块数/公式数/图片数)——预览视图据此决定
    /// 初始批次大小与是否走增量渲染(仅调整增量调度,不改变 8MB 字节门控)。</summary>
    public MarkdownRenderBudget Budget { get; }

    /// <summary>本次解析预热时 <c>Acquire</c> 过的图片路径(去重;缓存命中的复用解析为空)——
    /// 渲染器首处嵌入渲染时逐张"消费"(Acquire 嵌入引用 + Release 预热引用),渲染失败时
    /// 由视图按此列表整体释放,保证引用计数对称。</summary>
    internal IReadOnlyList<string> PreheatedPaths { get; }

    public bool HasErrors => Diagnostics.Any(item => item.IsError);
}

/// <summary>M5 渲染预算:解析期统计的复杂度(顶层块数 + 公式数 + 图片数)。公式/图片是
/// 渲染中最重的元素(WpfMath 解析/位图解码),块数多但无重元素的文档首屏成本可控。
/// 预算只调整增量调度的初始批次,不参与 Windowed/Full 容量门控(仍由字节阈值决定)。</summary>
public sealed record MarkdownRenderBudget(int BlockCount, int FormulaCount, int ImageCount)
{
    /// <summary>公式 + 图片达到该数量视为"重预算":初始批次减半,优先首屏。</summary>
    internal const int HeavyElementThreshold = 8;

    /// <summary>常规初始批次(与预览视图的 InitialRenderBlockBudget 一致)。</summary>
    internal const int NormalInitialBatch = 32;

    /// <summary>重预算初始批次:重元素文档首帧只做约一个视口的量。</summary>
    internal const int HeavyInitialBatch = 16;

    /// <summary>从解析 AST 统计预算(解析 worker 线程调用,与渲染器的块遍历同源)。</summary>
    internal static MarkdownRenderBudget Compute(MarkdownDocument document, int imageCount)
    {
        var formulaCount = 0;
        foreach (var _ in document.Descendants<MathBlock>()) formulaCount++;
        foreach (var _ in document.Descendants<MathInline>()) formulaCount++;
        return new MarkdownRenderBudget(document.Count, formulaCount, imageCount);
    }

    /// <summary>纯函数:按预算决定初始批次大小。重元素文档先渲首屏,再按常规节奏填充。</summary>
    public static int DecideInitialBatch(int totalBlocks, int formulaCount, int imageCount)
    {
        _ = totalBlocks; // 初始批次按首屏量估计,与文档总长无关
        return IsHeavyBudget(formulaCount, imageCount) ? HeavyInitialBatch : NormalInitialBatch;
    }

    /// <summary>纯函数:重元素预算(块数少的文档也应改走增量路径,避免首帧一次性渲染满屏公式)。</summary>
    public static bool IsOverInitialBudget(int formulaCount, int imageCount) =>
        IsHeavyBudget(formulaCount, imageCount);

    private static bool IsHeavyBudget(int formulaCount, int imageCount) =>
        formulaCount + imageCount >= HeavyElementThreshold;
}

public interface IMarkdownPreviewService
{
    Task<MarkdownRenderResult> ParseAsync(string source, string sourcePath,
        CancellationToken cancellationToken = default);
}

/// <summary>Markdown parser used by the read-only workbench.  Parsing stays UI-free so the WPF
/// document tree is only created after a current, successfully decoded file is available.</summary>
public sealed class MarkdownPreviewService : IMarkdownPreviewService
{
    public static readonly MarkdownPreviewService Instance = new();

    /// <summary>Shared parse pipeline; the unified heading model (<see cref="MarkdownHeadingModel"/>)
    /// and the preview renderer must agree on it so the outline and the rendered document stay in sync.</summary>
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseSoftlineBreakAsHardlineBreak()
        .UseYamlFrontMatter()
        .UseSmartyPants()
        .Build();

    public Task<MarkdownRenderResult> ParseAsync(string source, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // M2: 内容哈希 LRU —— 主题变化/切回预览等"仅重渲染"请求跳过整篇 Markdig 解析,
            // 复用已解析 AST(结果包装是新的,标签按结果对象身份渲染/分离,互不干扰)。
            if (MarkdownParseCache.Instance.TryGet(source, sourcePath, out var cached))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 缓存命中:不重新预热(条目仍被现有文档引用,或可经渲染路径按需重新解码)。
                return new MarkdownRenderResult(cached.Document, Path.GetFullPath(sourcePath),
                    cached.Headings, [], cached.ImagePaths, cached.Budget);
            }

            var (document, headings) = MarkdownHeadingModel.Parse(source);
            // 解析期预计算嵌入图片路径(每处一条,含重复)——渲染器逐处引用,
            // 视图替换/释放文档时按同一列表对称释放。
            var imagePaths = new List<string>();
            foreach (var link in document.Descendants<LinkInline>())
            {
                if (link.IsImage && TryResolveLocalImage(link.Url, sourcePath, out var imagePath))
                {
                    imagePaths.Add(imagePath);
                }
            }

            // M1: 公式 worker 预解析 —— 每个唯一公式文本只跑一次 TexFormula 解析
            // (失败判定一并缓存,UI 线程不再现场解析/捕获公式异常)。
            foreach (var formulaText in CollectFormulaTexts(document))
            {
                _ = MarkdownFormulaCache.Instance.GetOrParse(formulaText);
            }

            // M5: 渲染预算(块数/公式数/图片数),随结果对象供预览视图调度初始批次。
            var budget = MarkdownRenderBudget.Compute(document, imagePaths.Count);

            // M6: 解析 worker 线程预热图片缓存(去重后逐张)——预热即 Acquire:首帧渲染
            // 期间条目持有引用,LRU(128 条/96MB)不会逐出正在渲染的位图;引用在渲染器
            // 首处嵌入渲染时"消费"交接给嵌入引用,渲染失败/文档替换时由视图按
            // PreheatedPaths 对称释放。
            var preheatedPaths = new List<string>();
            foreach (var uniquePath in imagePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MarkdownImageCache.Instance.GetOrLoad(uniquePath) is not null)
                {
                    MarkdownImageCache.Instance.Acquire(uniquePath);
                    preheatedPaths.Add(uniquePath);
                }
            }

            var fullPath = Path.GetFullPath(sourcePath);
            MarkdownParseCache.Instance.Put(source, fullPath, document, headings, imagePaths, budget);
            return new MarkdownRenderResult(document, fullPath, headings, [], imagePaths, budget, preheatedPaths);
        }, cancellationToken);
    }

    /// <summary>收集文档中全部公式文本(块级 + 行内),与渲染器传给
    /// <c>CreateFormulaElement</c> 的文本逐字节一致(块级 join 后 Trim、行内原样),
    /// 保证 worker 预解析与 UI 侧查缓存命中同一键。</summary>
    internal static IEnumerable<string> CollectFormulaTexts(MarkdownDocument document)
    {
        foreach (var math in document.Descendants<MathBlock>())
        {
            yield return string.Join("\n", math.Lines.Lines.Select(line => line.ToString())).Trim();
        }

        foreach (var math in document.Descendants<MathInline>())
        {
            yield return math.Content.ToString();
        }
    }

    /// <summary>把相对图片 URL 解析为已存在的本地绝对路径(与渲染器同一判定,保证解析期
    /// 预计算与渲染期实际嵌入完全一致)。</summary>
    internal static bool TryResolveLocalImage(string? url, string sourcePath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || Uri.TryCreate(url, UriKind.Absolute, out _)) return false;
        try
        {
            path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, Uri.UnescapeDataString(url.Split('#')[0])));
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>M1 公式预解析缓存:键 = 公式文本(与渲染器传给 <c>CreateFormulaElement</c> 的
/// 文本逐字节一致),值 = 解析产物(<c>TexFormula</c>),null 值 = 解析失败判定(直接落原文
/// 兜底,UI 线程不再重复尝试/捕获异常)。解析在 ParseAsync 的 worker 线程发生
/// (TexFormula 解析每个唯一文本只跑一次);UI 线程只查缓存。<see cref="MaxEntries"/>
/// 条上限,满时清空重建(公式文本重复率低,清空比逐出简单且不阻塞)。</summary>
internal sealed class MarkdownFormulaCache
{
    internal const int MaxEntries = 128;

    internal static readonly MarkdownFormulaCache Instance = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, TexFormula?> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _insertionOrder = [];
    private int _parseCount;

    /// <summary>实际执行的 TexFormula 解析次数(去重后;测试断言"同一文本只解析一次")。</summary>
    internal int ParseCount
    {
        get { lock (_gate) return _parseCount; }
    }

    internal int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>取缓存解析产物;缺失时在调用线程解析并登记(命中时不解析)。</summary>
    internal TexFormula? GetOrParse(string formulaText)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(formulaText, out var cached))
            {
                return cached;
            }

            if (_entries.Count >= MaxEntries)
            {
                // 满员时只逐出最旧的 1 条(其余缓存保持命中),而非整体清空。
                var oldest = _insertionOrder[0];
                _insertionOrder.RemoveAt(0);
                _entries.Remove(oldest);
            }

            TexFormula? parsed = null;
            try
            {
                parsed = WpfTeXFormulaParser.Instance.Parse(formulaText);
            }
            catch (Exception)
            {
                // 任何解析失败都降级为"失败判定"(渲染期原文兜底)——公式错误绝不破坏
                // 整篇文档渲染(与旧版"控件解析失败 → 原文兜底"的容错边界一致)。
                parsed = null;
            }

            _parseCount++;
            _entries[formulaText] = parsed;
            _insertionOrder.Add(formulaText);
            return parsed;
        }
    }

    /// <summary>只查不解析。返回 false = 该文本未预解析(渲染走原始控件解析兜底);
    /// 返回 true 且 <paramref name="formula"/> 为 null = 已判定解析失败。</summary>
    internal bool TryGet(string formulaText, out TexFormula? formula)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(formulaText, out formula);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _parseCount = 0;
        }
    }
}

/// <summary>M2 Markdown 解析 LRU:键 = 源码 + 绝对路径的内容哈希(FNV-1a 64),
/// 值 = 已解析 AST + 标题列表 + 图片路径 + 渲染预算。容量 <see cref="MaxEntries"/> 篇
/// (超出逐出最久未用);主题切换/切回预览的重新解析请求在内容未变时直接命中,
/// 跳过整篇 Markdig 解析。AST 由缓存持有独立引用:标签分离结果对象的 <c>Document</c>
/// 不影响缓存,缓存逐出也不影响仍在使用的结果包装。</summary>
internal sealed class MarkdownParseCache
{
    /// <summary>默认容量(3–5 篇;测试可放大/隔离)。</summary>
    internal static int DefaultMaxEntries = 4;

    /// <summary>进程级共享缓存;internal 可替换便于测试隔离(替换期间其他解析请求
    /// 只是落到新实例上 miss→全量解析,行为始终正确)。</summary>
    internal static MarkdownParseCache Instance { get; set; } = new();

    private readonly int _maxEntries;

    internal MarkdownParseCache(int? maxEntries = null) => _maxEntries = maxEntries ?? DefaultMaxEntries;

    internal sealed class Entry
    {
        public required long Key { get; init; }
        public required string SourcePath { get; init; }
        public required MarkdownDocument Document { get; init; }
        public required IReadOnlyList<MarkdownHeading> Headings { get; init; }
        public required IReadOnlyList<string> ImagePaths { get; init; }
        public required MarkdownRenderBudget Budget { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new(); // 尾部 = 最近使用

    internal int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>内容哈希:FNV-1a 64-bit,覆盖源码与绝对路径(图片相对路径解析依赖路径)。</summary>
    internal static long ComputeKey(string source, string sourcePath)
    {
        unchecked
        {
            var hash = (ulong)1469598103934665603UL;
            foreach (var ch in source)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            // 额外 FNV 步:源码段与路径段之间的固定变换(64-bit 键,碰撞按不可行处理)。
            hash *= 1099511628211UL;
            foreach (var ch in Path.GetFullPath(sourcePath))
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            return (long)hash;
        }
    }

    internal bool TryGet(string source, string sourcePath, out Entry entry)
    {
        lock (_gate)
        {
            var key = ComputeKey(source, sourcePath);
            if (_entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                entry = node.Value;
                return true;
            }

            entry = null!;
            return false;
        }
    }

    internal void Put(string source, string sourcePath, MarkdownDocument document,
        IReadOnlyList<MarkdownHeading> headings, IReadOnlyList<string> imagePaths,
        MarkdownRenderBudget budget)
    {
        var entry = new Entry
        {
            Key = ComputeKey(source, sourcePath),
            SourcePath = sourcePath,
            Document = document,
            Headings = headings,
            ImagePaths = imagePaths,
            Budget = budget,
        };

        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Key, out var existing))
            {
                existing.Value = entry;
                _lru.Remove(existing);
                _lru.AddLast(existing);
                return;
            }

            while (_entries.Count >= _maxEntries)
            {
                var oldest = _lru.First!;
                _entries.Remove(oldest.Value.Key);
                _lru.RemoveFirst();
            }

            _entries[entry.Key] = _lru.AddLast(entry);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }
}

/// <summary>M7 渲染资源包:一次文档渲染所需的全部字体/字号/画刷,在渲染会话创建时
/// <b>一次性</b> 从应用资源解析(替代逐元素 TryFindResource),随后整棵块树只读它——
/// 块树构造与画刷解析彻底分离,主题重渲染复用同一构造逻辑换一份新资源包。
/// 另携带本次渲染的预热引用集合(M6):渲染器首处嵌入渲染时逐张消费。</summary>
internal sealed class MarkdownRenderResources
{
    public required FontFamily BodyFontFamily { get; init; }    // "UiFontFamily"
    public required FontFamily MonoFontFamily { get; init; }    // "MonoFontFamily"
    public required double BodyFontSize { get; init; }          // "TypeBody"(13)
    public required Brush TextBrush { get; init; }              // "TextBrush"
    public required Brush BrightTextBrush { get; init; }        // "BrightTextBrush"
    public required Brush MutedTextBrush { get; init; }         // "MutedTextBrush"
    public required Brush LinkBrush { get; init; }              // "LinkBrush"
    public required Brush ErrorTextBrush { get; init; }         // "ErrorTextBrush"
    public required Brush InputBrush { get; init; }             // "InputBrush"
    public required Brush ToolTipBorderBrush { get; init; }     // "ToolTipBorderBrush"
    public required Brush BorderBrush { get; init; }            // "BorderBrush"

    /// <summary>本次渲染仍待消费(M6)的预热引用路径;空集合 = 缓存命中的复用解析。</summary>
    internal HashSet<string> PreheatReferences { get; }

    internal MarkdownRenderResources(IEnumerable<string>? preheatPaths) =>
        PreheatReferences = new HashSet<string>(preheatPaths ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>首处嵌入渲染时消费一张预热引用:嵌入引用(Acquire)已在调用方完成,
    /// 这里把预热持有的引用交还缓存,保持 预热 1 + 嵌入 N → 嵌入 N 的对称性。</summary>
    internal void ConsumePreheatReference(string path)
    {
        if (PreheatReferences.Remove(path))
        {
            MarkdownImageCache.Instance.Release(path);
        }
    }

    /// <summary>在 UI 线程(渲染会话创建时)一次性解析全部资源;无 Application 时落
    /// 与旧 <c>Resolve</c>/<c>ResolveDouble</c> 相同的缺省值。</summary>
    internal static MarkdownRenderResources Create(IEnumerable<string>? preheatPaths = null)
    {
        var app = Application.Current;
        return new MarkdownRenderResources(preheatPaths)
        {
            BodyFontFamily = app?.TryFindResource("UiFontFamily") as FontFamily ?? new FontFamily("Segoe UI"),
            MonoFontFamily = app?.TryFindResource("MonoFontFamily") as FontFamily ?? new FontFamily("Consolas"),
            BodyFontSize = app?.TryFindResource("TypeBody") is double size ? size : 13,
            TextBrush = app?.TryFindResource("TextBrush") as Brush ?? Brushes.Gray,
            BrightTextBrush = app?.TryFindResource("BrightTextBrush") as Brush ?? Brushes.Gray,
            MutedTextBrush = app?.TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray,
            LinkBrush = app?.TryFindResource("LinkBrush") as Brush ?? Brushes.Gray,
            ErrorTextBrush = app?.TryFindResource("ErrorTextBrush") as Brush ?? Brushes.Gray,
            InputBrush = app?.TryFindResource("InputBrush") as Brush ?? Brushes.Gray,
            ToolTipBorderBrush = app?.TryFindResource("ToolTipBorderBrush") as Brush ?? Brushes.Gray,
            BorderBrush = app?.TryFindResource("BorderBrush") as Brush ?? Brushes.Gray,
        };
    }
}

/// <summary>Converts the Markdig AST into a theme-aware WPF FlowDocument.  Unsupported or unsafe
/// constructs remain visible as escaped text; no HTML, script, media or remote image is executed.</summary>
public static class MarkdownWpfRenderer
{
    /// <summary>共享图片缓存(有界 LRU + 引用计数);每处嵌入在下方 <see cref="AddImage"/> 中
    /// Acquire,由 MarkdownPreviewView 在文档替换/关闭时按结果中的 <c>ImagePaths</c> 对称释放。</summary>
    public static MarkdownImageCache ImageCache { get; } = MarkdownImageCache.Instance;

    public static FlowDocument Render(MarkdownRenderResult result, Action<MarkdownLinkRequest>? linkAction = null)
    {
        using var session = BeginRender(result, linkAction);
        while (!session.AppendBatch(int.MaxValue))
        {
        }

        return session.Document;
    }

    /// <summary>Creates a UI-thread Markdown render session. Blocks are materialized in bounded
    /// batches by the preview view, matching VS Code's incremental viewport work while retaining
    /// the existing FlowDocument surface and heading/link behavior.</summary>
    public static MarkdownFlowDocumentRenderSession BeginRender(
        MarkdownRenderResult result, Action<MarkdownLinkRequest>? linkAction = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Document,
            "MarkdownRenderResult.Document 已分离:重渲染前请先重新解析(见 FilePreviewTab.BuildMarkdownPreviewAsync)。");
        // M7: 资源包每次渲染只解析一次(预热引用集合同步交给渲染器逐张消费)。
        return new MarkdownFlowDocumentRenderSession(result, linkAction,
            MarkdownRenderResources.Create(result.PreheatedPaths));
    }

    /// <summary>Incremental FlowDocument builder. The session owns only the Markdig enumerator;
    /// rendered WPF blocks remain in the document, and each call performs a bounded amount of UI
    /// work so a long preview can yield back to input and paint between batches.</summary>
    public sealed class MarkdownFlowDocumentRenderSession : IDisposable
    {
        private readonly MarkdownRenderResult _result;
        private readonly Action<MarkdownLinkRequest>? _linkAction;
        private readonly IEnumerator<MarkdigBlock> _sourceBlocks;
        private readonly Dictionary<HeadingBlock, string> _nameByBlock;
        private IEnumerator<WpfBlock>? _renderedBlocks;
        private bool _hasSourceBlock;
        private bool _disposed;

        internal MarkdownFlowDocumentRenderSession(MarkdownRenderResult result, Action<MarkdownLinkRequest>? linkAction,
            MarkdownRenderResources resources)
        {
            _result = result;
            _linkAction = linkAction;
            Resources = resources;
            _sourceBlocks = result.Document!.GetEnumerator();
            _nameByBlock = BuildHeadingNames(result);
            Document = CreateDocument(resources);
        }

        public FlowDocument Document { get; }

        /// <summary>M7: 本次渲染的资源包(整棵块树共用;批次间不重复解析)。</summary>
        internal MarkdownRenderResources Resources { get; }

        public bool IsCompleted { get; private set; }

        /// <summary>M4: 最近一次 <see cref="AppendBatch"/> 实际加入文档的块(预览视图据此
        /// 只对新增标题增量更新查找表,不再每批全文档扫描)。</summary>
        public IReadOnlyList<WpfBlock> LastAddedBlocks { get; private set; } = [];

        public bool AppendBatch(int maxBlocks)
        {
            if (_disposed || IsCompleted)
            {
                LastAddedBlocks = [];
                return true;
            }

            var added = 0;
            var addedBlocks = new List<WpfBlock>();
            while (added < Math.Max(1, maxBlocks))
            {
                if (_renderedBlocks is not null && _renderedBlocks.MoveNext())
                {
                    var block = _renderedBlocks.Current;
                    Document.Blocks.Add(block);
                    addedBlocks.Add(block);
                    added++;
                    continue;
                }

                _renderedBlocks?.Dispose();
                _renderedBlocks = null;
                if (!_sourceBlocks.MoveNext())
                {
                    if (!_hasSourceBlock)
                    {
                        var empty = new Paragraph();
                        Document.Blocks.Add(empty);
                        addedBlocks.Add(empty);
                    }

                    IsCompleted = true;
                    LastAddedBlocks = addedBlocks;
                    return true;
                }

                _hasSourceBlock = true;
                _renderedBlocks = RenderBlock(_sourceBlocks.Current, _result.SourcePath, _linkAction, 0, _nameByBlock, Resources).GetEnumerator();
            }

            LastAddedBlocks = addedBlocks;
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderedBlocks?.Dispose();
            _sourceBlocks.Dispose();
        }

        private static FlowDocument CreateDocument(MarkdownRenderResources resources)
        {
            return new FlowDocument
            {
                PagePadding = new Thickness(20, 14, 20, 24),
                FontFamily = resources.BodyFontFamily,
                FontSize = resources.BodyFontSize,
                Foreground = resources.TextBrush,
                LineHeight = resources.BodyFontSize * 1.55,
            };
        }

        private static Dictionary<HeadingBlock, string> BuildHeadingNames(MarkdownRenderResult result)
        {
            var names = new Dictionary<HeadingBlock, string>();
            var headingIndex = 0;
            foreach (var block in result.Document!.Descendants<HeadingBlock>())
            {
                if (MarkdownHeadingModel.HeadingText(block).Trim().Length == 0) continue;
                if (headingIndex < result.Headings.Count)
                {
                    names[block] = result.Headings[headingIndex].DocumentName;
                }

                headingIndex++;
            }

            return names;
        }
    }

    private static IEnumerable<WpfBlock> RenderBlock(MarkdigBlock block, string sourcePath,
        Action<MarkdownLinkRequest>? linkAction, int depth, Dictionary<HeadingBlock, string> nameByBlock,
        MarkdownRenderResources resources)
    {
        switch (block)
        {
            case BlankLineBlock:
                yield break;
            case HeadingBlock heading:
            {
                var paragraph = NewParagraph();
                paragraph.Margin = new Thickness(0, heading.Level == 1 ? 14 : 9, 0, 5);
                paragraph.FontWeight = FontWeights.Bold;
                paragraph.Foreground = resources.BrightTextBrush;
                paragraph.FontSize = resources.BodyFontSize + Math.Max(1, 7 - heading.Level);
                AppendInline(paragraph, heading.Inline, sourcePath, linkAction, resources);
                if (nameByBlock.TryGetValue(heading, out var wpfName))
                {
                    paragraph.Name = wpfName;
                }
                // M7: 零尺寸位置标记(段落是 ContentElement,无几何 API;预览视图经
                // TransformToVisual 计算标题视口位置做滚动同步)不再在块构造期创建——
                // 渲染完成/首次使用时由 MarkdownPreviewView.EnsureHeadingMarker 惰性
                // 创建,首屏渲染不为数千标题多付数千个 Visual 的布局成本。
                yield return paragraph;
                yield break;
            }
            case ParagraphBlock paragraphBlock:
            {
                var paragraph = NewParagraph();
                AppendInline(paragraph, paragraphBlock.Inline, sourcePath, linkAction, resources);
                yield return paragraph;
                yield break;
            }
            case MathBlock math:
                yield return BuildMathBlock(string.Join("\n", math.Lines.Lines.Select(line => line.ToString())), resources);
                yield break;
            case FencedCodeBlock fenced:
                // Markdig 1.x 的解析器不再填充 CodeBlockLines(恒为空列表),代码正文在
                // Lines 中——读旧属性会把代码块渲染成只剩语言名一行。
                yield return BuildCodeBlock(fenced.Lines, fenced.Info ?? string.Empty, resources);
                yield break;
            case ThematicBreakBlock:
            {
                var rule = NewParagraph();
                rule.BorderBrush = resources.ToolTipBorderBrush;
                rule.BorderThickness = new Thickness(0, 0, 0, 1);
                rule.Margin = new Thickness(0, 8, 0, 8);
                yield return rule;
                yield break;
            }
            case QuoteBlock quote:
            {
                foreach (var child in quote)
                {
                    foreach (var rendered in RenderBlock(child, sourcePath, linkAction, depth + 1, nameByBlock, resources))
                    {
                        rendered.Margin = new Thickness(12, 2, 0, 2);
                        rendered.Foreground = resources.MutedTextBrush;
                        yield return rendered;
                    }
                }
                yield break;
            }
            case ListBlock list:
            {
                var itemIndex = 0;
                foreach (ListItemBlock item in list)
                {
                    var first = true;
                    foreach (var child in item)
                    {
                        foreach (var rendered in RenderBlock(child, sourcePath, linkAction, depth + 1, nameByBlock, resources))
                        {
                            rendered.Margin = new Thickness(18, 1, 0, 1);
                            if (first && rendered is Paragraph paragraph)
                            {
                                var marker = list.IsOrdered ? $"{list.OrderedStart ?? (itemIndex + 1).ToString()}. " : "• ";
                                paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline, new Run(marker)
                                {
                                    Foreground = resources.MutedTextBrush,
                                });
                            }
                            yield return rendered;
                            first = false;
                        }
                    }
                    itemIndex++;
                }
                yield break;
            }
            case MarkdigTable table:
                yield return BuildTable(table, sourcePath, linkAction, depth, nameByBlock, resources);
                yield break;
            case HtmlBlock html:
                yield return BuildUnsupportedBlock(string.Join("\n", html.Lines.Lines.Select(line => line.ToString())), resources);
                yield break;
            default:
                yield return BuildUnsupportedBlock(block.ToString() ?? string.Empty, resources);
                yield break;
        }
    }

    private static WpfTable BuildTable(MarkdigTable source, string sourcePath, Action<MarkdownLinkRequest>? linkAction,
        int depth, Dictionary<HeadingBlock, string> nameByBlock, MarkdownRenderResources resources)
    {
        var table = new WpfTable
        {
            CellSpacing = 0,
            BorderBrush = resources.BorderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 8),
        };
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (MarkdigTableRow sourceRow in source)
        {
            var row = new WpfTableRow();
            group.Rows.Add(row);
            foreach (MarkdigTableCell sourceCell in sourceRow)
            {
                var cell = new WpfTableCell { Padding = new Thickness(7, 4, 7, 4) };
                if (sourceRow.IsHeader) cell.FontWeight = FontWeights.Bold;
                foreach (var child in sourceCell)
                {
                    foreach (var rendered in RenderBlock(child, sourcePath, linkAction, depth + 1, nameByBlock, resources))
                    {
                        cell.Blocks.Add(rendered);
                    }
                }
                row.Cells.Add(cell);
            }
        }
        return table;
    }

    /// <summary>M8: 代码块每 <see cref="CodeRunLineBatch"/> 行合并为一个 Run(Run 文本内嵌
    /// '\n',WPF TextBlock 按换行渲染),内联元素从 2×N(逐行 Run+LineBreak)降到
    /// ceil(N/100);拼接文本与逐行版逐字节一致。</summary>
    internal const int CodeRunLineBatch = 100;

    /// <summary>纯函数:把代码行切分为 ≤ batchSize 行的批(批内以 '\n' 连接;除末批外
    /// 批尾补 '\n')。全批拼接 == 原行以 '\n' 连接,视觉结果不变。</summary>
    internal static IReadOnlyList<string> BuildCodeBatches(IReadOnlyList<string> lines, int batchSize = CodeRunLineBatch)
    {
        var size = Math.Max(1, batchSize);
        var batches = new List<string>((lines.Count + size - 1) / size);
        for (var start = 0; start < lines.Count; start += size)
        {
            var end = Math.Min(start + size, lines.Count);
            var builder = new StringBuilder();
            for (var i = start; i < end; i++)
            {
                if (i > start) builder.Append('\n');
                builder.Append(lines[i]);
            }

            if (end < lines.Count)
            {
                builder.Append('\n'); // 批边界保持换行(拼接语义与逐行版逐字节一致)
            }

            batches.Add(builder.ToString());
        }

        return batches;
    }

    private static Paragraph BuildCodeBlock(Markdig.Helpers.StringLineGroup lines, string info, MarkdownRenderResources resources)
    {
        var paragraph = NewParagraph();
        paragraph.Background = resources.InputBrush;
        paragraph.Padding = new Thickness(8, 6, 8, 6);
        paragraph.Margin = new Thickness(0, 6, 0, 8);
        paragraph.FontFamily = resources.MonoFontFamily;
        paragraph.FontSize = resources.BodyFontSize - 1;
        if (!string.IsNullOrWhiteSpace(info))
        {
            paragraph.Inlines.Add(new Run(info.Trim() + "\n") { Foreground = resources.MutedTextBrush });
        }
        // Lines 的底层数组是按容量预留的(尾部填充空条目,空代码块时甚至为 null),
        // 正文行数只认 Count——直接遍历数组会往代码块尾部补出多余空行。
        var lineArray = lines.Lines;
        string[] lineTexts;
        if (lineArray is null)
        {
            lineTexts = [];
        }
        else
        {
            var count = Math.Min(lines.Count, lineArray.Length);
            lineTexts = new string[count];
            for (var i = 0; i < count; i++)
            {
                lineTexts[i] = lineArray[i].ToString() ?? string.Empty;
            }
        }

        foreach (var batch in BuildCodeBatches(lineTexts))
        {
            paragraph.Inlines.Add(new Run(batch));
        }

        return paragraph;
    }

    private static WpfBlock BuildMathBlock(string formula, MarkdownRenderResources resources)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(CreateFormulaElement(formula.Trim(), resources.BodyFontSize + 3, true, resources));
        return new BlockUIContainer(panel);
    }

    private static Paragraph BuildUnsupportedBlock(string text, MarkdownRenderResources resources)
    {
        var paragraph = NewParagraph();
        paragraph.Background = resources.InputBrush;
        paragraph.Padding = new Thickness(7, 4, 7, 4);
        paragraph.Foreground = resources.MutedTextBrush;
        paragraph.Inlines.Add(new Run(text));
        return paragraph;
    }

    private static void AppendInline(Paragraph target, ContainerInline? container, string sourcePath,
        Action<MarkdownLinkRequest>? linkAction, MarkdownRenderResources resources)
    {
        if (container is null) return;
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            AppendOne(target, inline, sourcePath, linkAction, resources);
        }
    }

    private static void AppendOne(Paragraph target, MarkdigInline inline, string sourcePath,
        Action<MarkdownLinkRequest>? linkAction, MarkdownRenderResources resources)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Inlines.Add(new Run(literal.Content.ToString()));
                break;
            case CodeInline code:
                target.Inlines.Add(new Span(new Run(code.Content))
                {
                    Background = resources.InputBrush,
                    FontFamily = resources.MonoFontFamily,
                });
                break;
            case MathInline math:
                target.Inlines.Add(new InlineUIContainer(CreateFormulaElement(
                    math.Content.ToString(), resources.BodyFontSize, false, resources)));
                break;
            case LineBreakInline:
                target.Inlines.Add(new LineBreak());
                break;
            case AutolinkInline auto:
                AddLink(target, auto.Url, auto.Url, sourcePath, linkAction, resources);
                break;
            case LinkInline link when link.IsImage:
                AddImage(target, link.Url, link.Label, sourcePath, linkAction, resources);
                break;
            case LinkInline link:
            {
                var hyperlink = new Hyperlink { Foreground = resources.LinkBrush };
                hyperlink.TextDecorations = TextDecorations.Underline;
                hyperlink.Click += (_, _) => linkAction?.Invoke(new(link.Url ?? string.Empty, sourcePath, false));
                if (link.FirstChild is null)
                {
                    hyperlink.Inlines.Add(new Run(link.Url ?? string.Empty));
                }
                else
                {
                    for (var child = link.FirstChild; child is not null; child = child.NextSibling)
                    {
                        AppendOne(hyperlink, child, sourcePath, linkAction, resources);
                    }
                }
                target.Inlines.Add(hyperlink);
                break;
            }
            case EmphasisInline emphasis:
            {
                var span = new Span
                {
                    FontWeight = emphasis.DelimiterCount >= 2 ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = emphasis.DelimiterCount == 1 ? FontStyles.Italic : FontStyles.Normal,
                };
                if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                for (var child = emphasis.FirstChild; child is not null; child = child.NextSibling)
                {
                    AppendOne(span, child, sourcePath, linkAction, resources);
                }
                target.Inlines.Add(span);
                break;
            }
            case ContainerInline nested:
                AppendInline(target, nested, sourcePath, linkAction, resources);
                break;
            case HtmlInline html:
                if (html.Tag.Trim().Equals("<br>", StringComparison.OrdinalIgnoreCase)
                    || html.Tag.Trim().Equals("<br/>", StringComparison.OrdinalIgnoreCase))
                {
                    target.Inlines.Add(new LineBreak());
                }
                else if (!IsSafeHtmlTag(html.Tag))
                {
                    target.Inlines.Add(new Run(html.Tag));
                }
                break;
            default:
                target.Inlines.Add(new Run(inline.ToString() ?? string.Empty));
                break;
        }
    }

    private static void AppendOne(Span target, MarkdigInline inline, string sourcePath,
        Action<MarkdownLinkRequest>? linkAction, MarkdownRenderResources resources)
    {
        var paragraph = new Paragraph();
        AppendOne(paragraph, inline, sourcePath, linkAction, resources);
        foreach (var child in paragraph.Inlines.ToArray())
        {
            paragraph.Inlines.Remove(child);
            target.Inlines.Add(child);
        }
    }

    private static void AddLink(Paragraph target, string? label, string? url, string sourcePath,
        Action<MarkdownLinkRequest>? linkAction, MarkdownRenderResources resources)
    {
        var hyperlink = new Hyperlink(new Run(label ?? url ?? string.Empty)) { Foreground = resources.LinkBrush };
        hyperlink.TextDecorations = TextDecorations.Underline;
        hyperlink.Click += (_, _) => linkAction?.Invoke(new(url ?? string.Empty, sourcePath, false));
        target.Inlines.Add(hyperlink);
    }

    private static void AddImage(Paragraph target, string? url, string? alt, string sourcePath,
        Action<MarkdownLinkRequest>? linkAction, MarkdownRenderResources resources)
    {
        if (MarkdownPreviewService.TryResolveLocalImage(url, sourcePath, out var imagePath)
            && ImageCache.GetOrLoad(imagePath) is { } image)
        {
            // 本处嵌入引用该位图:文档释放时视图会按结果 ImagePaths 逐处 Release。
            ImageCache.Acquire(imagePath);
            // M6: 首处嵌入渲染时消费预热引用(预热在解析 worker Acquire,此处交还),
            // 保持 预热 1 + 嵌入 N → 嵌入 N 的引用对称,且渲染全程条目不可被 LRU 逐出。
            resources.ConsumePreheatReference(imagePath);
            var imageControl = new Image { Source = image, MaxWidth = 720, MaxHeight = 480, Stretch = Stretch.Uniform };
            target.Inlines.Add(new InlineUIContainer(imageControl));
            return;
        }

        var fallback = new Hyperlink(new Run($"[图片: {alt ?? url ?? "未知"}]") { Foreground = resources.MutedTextBrush });
        fallback.Click += (_, _) => linkAction?.Invoke(new(url ?? string.Empty, sourcePath, true, alt));
        target.Inlines.Add(fallback);
    }

    private static FrameworkElement CreateFormulaElement(string formula, double scale, bool display,
        MarkdownRenderResources resources)
    {
        // M1: worker 已预解析(键 = 公式文本,与 ParseAsync 收集口径一致):
        // 失败判定 → 直接原文兜底(UI 线程不再尝试解析/捕获公式异常);
        // 成功判定 → 复用预解析构建控件;未缓存(渲染路径未走 ParseAsync)→ 保留原控件解析。
        if (MarkdownFormulaCache.Instance.TryGet(formula, out var parsed) && parsed is null)
        {
            return CreateFormulaFallback(formula, resources);
        }

        try
        {
            var control = new FormulaControl
            {
                Formula = formula,
                Scale = scale,
                Foreground = resources.TextBrush,
                HorizontalAlignment = display ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            };
            if (!control.HasError) return control;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Preserve the source below.
        }

        return CreateFormulaFallback(formula, resources);
    }

    private static TextBlock CreateFormulaFallback(string formula, MarkdownRenderResources resources)
    {
        return new TextBlock
        {
            Text = $"${formula}$",
            Foreground = resources.ErrorTextBrush,
            ToolTip = "公式无法解析，已显示原文",
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static bool IsSafeHtmlTag(string tag) => tag.Contains("<br", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("</br", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("<sub", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("<sup", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("<kbd", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("<mark", StringComparison.OrdinalIgnoreCase);

    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0, 3, 0, 3) };
}

internal static class MarkdownDelimiterNormalizer
{
    public static string Normalize(string source)
    {
        var builder = new StringBuilder(source.Length);
        var inFence = false;
        var offset = 0;
        while (true)
        {
            var lineStart = offset;
            while (offset < source.Length && source[offset] is not '\r' and not '\n')
            {
                offset++;
            }

            var line = source.AsSpan(lineStart, offset - lineStart);
            var trimStart = 0;
            while (trimStart < line.Length && char.IsWhiteSpace(line[trimStart]))
            {
                trimStart++;
            }

            if (StartsWithFence(line[trimStart..]))
            {
                inFence = !inFence;
            }

            AppendLine(builder, line, inFence);
            if (offset >= source.Length)
            {
                break;
            }

            offset++;
            if (source[offset - 1] == '\r' && offset < source.Length && source[offset] == '\n')
            {
                offset++;
            }
        }

        return builder.ToString();
    }

    private static bool StartsWithFence(ReadOnlySpan<char> line) =>
        line.Length >= 3 && ((line[0] == '`' && line[1] == '`' && line[2] == '`')
            || (line[0] == '~' && line[1] == '~' && line[2] == '~'));

    private static void AppendLine(StringBuilder builder, ReadOnlySpan<char> line, bool inFence)
    {
        if (inFence)
        {
            builder.Append(line);
        }
        else
        {
            for (var index = 0; index < line.Length; index++)
            {
                if (line[index] == '\\' && index + 1 < line.Length)
                {
                    var replacement = line[index + 1] switch
                    {
                        '[' or ']' => "$$",
                        '(' or ')' => "$",
                        _ => null,
                    };
                    if (replacement is not null)
                    {
                        builder.Append(replacement);
                        index++;
                        continue;
                    }
                }

                builder.Append(line[index]);
            }
        }

        builder.AppendLine();
    }
}
