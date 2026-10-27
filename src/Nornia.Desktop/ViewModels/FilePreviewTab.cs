using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Coalescing;
using Nornia.Core.Collections;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.Services;
using Nornia.Project.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Nornia.Desktop.ViewModels;

/// <summary>Read-only content preview for files opened from the explorer. Capped so opening a large
/// repository never fills the managed heap; decoding rides the shared <see cref="TextDocumentDecoder"/>
/// (BOM / strict UTF-8 / GB18030 fallback), so legacy-encoded sources do not garble. The editor
/// may release the payload of an inactive tab and load it again when selected.</summary>
public sealed partial class FilePreviewTab : EditorTabItem
{
    private const int MaxSearchMatches = SearchLimits.MaximumFindMatches;

    /// <summary>大文件分层阈值(设置 <c>nornia.reading.*</c> 由 EditorArea 在创建/设置变化时写入;
    /// 在下一次内容加载/重载时生效,已加载内容不实时重新分层)。</summary>
    public ReadOnlyContentLimits ContentLimits { get; set; } = ReadOnlyContentLimits.Default;

    /// <summary>状态栏编码选择器的覆盖项(标签级会话状态,不持久化):null = 自动检测
    /// (BOM→严格 UTF-8→GB18030)。变更后经重载管线按所选编码重新解码。</summary>
    private Encoding? _forcedEncoding;

    /// <summary>当前生效的强制编码 web 名(编码选择器勾选态);null = 自动。</summary>
    public string? ForcedEncodingWebName => _forcedEncoding?.WebName;

    [RelayCommand]
    public void SetEncoding(string? webName)
    {
        var forced = TextDocumentDecoder.CreateForcedEncoding(webName);
        if ((forced?.WebName ?? string.Empty) == (_forcedEncoding?.WebName ?? string.Empty))
        {
            return;
        }

        _forcedEncoding = forced;
        OnPropertyChanged(nameof(ForcedEncodingWebName));
        // 复用外部变更重载管线(串行化 + 版本防旧覆新);载荷被缓存卸载时由再激活路径按新编码读取。
        if (IsClosed || _resourcesReleased || _payloadUnloaded)
        {
            return;
        }

        IsLoading = true;
        _ = RunContentPipelineAsync(initialLoad: false);
    }

    private readonly ITextDocumentDecoder _decoder;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ITextSearchService _search;
    private readonly ICodeOutlineParser _outlineParser;
    private readonly ICodeFoldingStrategy _folding;
    private readonly ICodeSymbolAnalyzer _symbolAnalyzer;
    private readonly HashSet<string> _collapsedSymbolIds = new(StringComparer.Ordinal);
    private CodeSymbolDocument _symbolDocument = CodeSymbolDocument.Empty;
    private readonly List<CodeSymbolNode> _wiredSymbolNodes = [];
    private Task? _loadTask;
    private bool _payloadUnloaded;
    private bool _resourcesReleased;
    private int _pipelineActive;
    private CancellationTokenSource? _presentationCancellation;
    private CancellationTokenSource? _markdownCancellation;
    private CancellationTokenSource? _derivedCancellation;
    private IReadOnlyDocumentSource? _documentSource;
    private int _presentationVersion;
    private int _derivedVersion;
    private int _searchVersion;
    private CancellationTokenSource? _searchCancellation;
    /// <summary>Authoritative windowed-search list (global line + column). <see cref="SearchMatches"/>
    /// holds the current window's subset remapped to window-local coordinates after each load.</summary>
    private List<DocumentSearchMatch> _windowedMatches = [];
    /// <summary>Number of text lines in the currently loaded window's <see cref="Content"/> (0 otherwise).</summary>
    private int _windowTextLines;
    /// <summary>Character offset where each line of the current window's <see cref="Content"/> starts
    /// (index = local line − 1).</summary>
    private IReadOnlyList<int> _windowLineOffsets = [0];

    /// <summary>E10: 查找栏击键防抖(VS Code 查找 widget 的 150–300ms 输入去抖)。一次击键风暴
    /// 合并为一次窗口化重扫——只有停顿后最后一个键触发扫描;Escape(<see cref="ClearSearch"/>)
    /// 立即 Cancel 挂起排程。每次 Schedule 重置计时(尾部去抖),线程安全。</summary>
    private readonly RunOnceScheduler _searchTextDebounce;

    /// <summary>查找文本防抖时长(毫秒);测试按此值等待收敛。</summary>
    public const int SearchTextDebounceMs = 250;

    // ===== External-change reload =====

    /// <summary>Raised on the UI thread immediately before published content is replaced by an
    /// external reload; the view captures scroll/caret/fold position (and the Markdown reading
    /// spot) so the reading position survives the content swap.</summary>
    public event EventHandler? ContentUpdating;

    /// <summary>Serializes load/reload passes: only the newest request runs the decode/rebuild
    /// pipeline, so rapid saves apply the latest content only and stale reads cannot interleave.</summary>
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    /// <summary>Serializes window publication with source replacement/disposal. Navigation requests
    /// also cancel the previous request and carry a monotonically increasing id, so an older read
    /// can neither overwrite a newer window nor calculate a local line against the wrong start.</summary>
    private readonly SemaphoreSlim _windowLoadGate = new(1, 1);
    private readonly object _windowRequestGate = new();
    private CancellationTokenSource? _windowCancellation;
    private int _windowRequestVersion;

    /// <summary>Monotonic content version shared by the initial load and every reload. Each pass
    /// captures it on entry and only the *current* version may publish, so an old decode (or the
    /// initial load racing a reload) can never overwrite newer content.</summary>
    private int _contentVersion;

    /// <summary>延迟重试调度:文件暂时不可读(写入中/锁定/已删除)时周期性重试,恢复后自动刷新。</summary>
    private static readonly TimeSpan ReloadRetryDelay = TimeSpan.FromMilliseconds(300);
    private CancellationTokenSource? _reloadRetryCancellation;

    private const long DocumentSourceBaseMemoryBytes = 32L * 1024;
    private const long DocumentSourceCheckpointBytes = 32L;
    private const int DocumentSourceCheckpointStride = 256;

    public FilePreviewTab(string path) : this(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance, TextSearchService.Instance, CodeOutlineParser.Instance, CodeFoldingStrategy.Instance, CodeSymbolAnalyzer.Instance)
    {
    }

    public FilePreviewTab(string path, ITextDocumentDecoder decoder, ICodeFileTypeRegistry registry)
        : this(path, decoder, registry, TextSearchService.Instance, CodeOutlineParser.Instance, CodeFoldingStrategy.Instance, CodeSymbolAnalyzer.Instance)
    {
    }

    public FilePreviewTab(string path, ITextDocumentDecoder decoder, ICodeFileTypeRegistry registry, ITextSearchService search)
        : this(path, decoder, registry, search, CodeOutlineParser.Instance, CodeFoldingStrategy.Instance, CodeSymbolAnalyzer.Instance)
    {
    }

    public FilePreviewTab(string path, ITextDocumentDecoder decoder, ICodeFileTypeRegistry registry, ITextSearchService search, ICodeOutlineParser outlineParser, IUiDispatcher? uiDispatcher = null)
        : this(path, decoder, registry, search, outlineParser, CodeFoldingStrategy.Instance, CodeSymbolAnalyzer.Instance, uiDispatcher)
    {
    }

    public FilePreviewTab(string path, ITextDocumentDecoder decoder, ICodeFileTypeRegistry registry, ITextSearchService search, ICodeOutlineParser outlineParser, ICodeFoldingStrategy folding, ICodeSymbolAnalyzer? symbolAnalyzer = null, IUiDispatcher? uiDispatcher = null) : base(path)
    {
        _decoder = decoder;
        _search = search;
        _outlineParser = outlineParser;
        _folding = folding;
        _symbolAnalyzer = symbolAnalyzer ?? CodeSymbolAnalyzer.Instance;
        _uiDispatcher = uiDispatcher ?? new WpfUiDispatcher();
        _fileType = registry.FromPath(path);
        LanguageName = FileType.DisplayName;
        // VS Code preview semantics: 文件预览以斜体预览标签打开,被下一次打开替换,直到固定。
        IsPreview = true;
        ThemeEvents.ThemeChanged += OnAppThemeChanged; // ReleaseResources 中解除

        // RunOnceScheduler 的定时器在池线程到期;回到构造时所在的同步上下文(应用内即 UI
        // 线程)再触发 RefreshSearch,可观察状态只在原线程变更。无同步上下文的测试环境就地执行。
        var debounceContext = SynchronizationContext.Current;
        _searchTextDebounce = new RunOnceScheduler(SearchTextDebounceMs)
        {
            Action = () =>
            {
                if (debounceContext is not null)
                {
                    debounceContext.Post(_ => RefreshSearch(), null);
                }
                else
                {
                    RefreshSearch();
                }
            },
        };
    }

    /// <summary>Language descriptor (highlighting, icon, outline kind) resolved from the extension.</summary>
    public override CodeFileType FileType => _fileType;

    private CodeFileType _fileType;

    /// <summary>切换语言模式(VS Code"更改语言模式"):替换解析/高亮/大纲类型;加载完成后即时重建。</summary>
    public void SetLanguage(CodeFileType type)
    {
        if (type.LanguageId == _fileType.LanguageId)
        {
            return;
        }

        _fileType = type;
        OnPropertyChanged(nameof(FileType));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(IsMarkdown));
        OnPropertyChanged(nameof(IsMarkdownRendered));
        OnPropertyChanged(nameof(ShowSourceSurface));
        LanguageName = type.DisplayName;
        if (IsLoadFinished)
        {
            _ = BuildDerivedContentAsync(_contentVersion);
            if (!(IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered))
            {
                _ = BuildPresentationAsync();
            }
            _ = BuildMarkdownPreviewAsync();
        }
    }

    /// <summary>The tab icon reflects the file language: 语言缩写 Monogram(透明底 + 语言色相文字)。</summary>
    public override string Glyph => FileType.IconMonogram;

    public override string TabKey => $"file:{Path}";

    [ObservableProperty]
    private string content = string.Empty;

    /// <summary>True while the initial payload or an external reload is being published.</summary>
    [ObservableProperty]
    private bool isLoading;

    /// <summary>True while a rendered Markdown document is being built and no previous rendered
    /// document is available to keep visible.</summary>
    [ObservableProperty]
    private bool isMarkdownRendering;

    public bool IsReadingBusy => IsLoading || IsMarkdownRendering;

    public string ReadingBusyText => IsMarkdownRendering ? "正在生成 Markdown 预览…" : "正在加载文件…";

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReadingBusy));
        OnPropertyChanged(nameof(ReadingBusyText));
        OnPropertyChanged(nameof(IsLoadFinished));
    }

    partial void OnIsMarkdownRenderingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReadingBusy));
        OnPropertyChanged(nameof(ReadingBusyText));
    }

    /// <summary>Markdown files open as a rendered read-only document and can switch back to the
    /// existing AvalonEdit source surface.</summary>
    public bool IsMarkdown => string.Equals(FileType.LanguageId, "markdown", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private MarkdownViewMode markdownMode = MarkdownViewMode.Rendered;

    [ObservableProperty]
    private MarkdownRenderResult? markdownRenderResult;

    /// <summary>解析得到的标题列表:标签存活期间常驻(轻量),源码模式下光标→逻辑位置同步、
    /// 大纲跳转与重启恢复都不依赖渲染产物,因此 AST 释放/模式切换后仍可用。</summary>
    [ObservableProperty]
    private IReadOnlyList<MarkdownHeading> markdownHeadings = [];

    [ObservableProperty]
    private string markdownRenderNotice = string.Empty;

    [ObservableProperty]
    private double markdownVerticalOffset;

    /// <summary>当前 Markdown 标题锚点(统一逻辑位置状态的一部分;无标题时为空串)。
    /// 由预览滚动、源码光标和大纲点击统一写入,用于模式切换、标签切换与重启恢复。</summary>
    [ObservableProperty]
    private string markdownAnchor = string.Empty;

    /// <summary>当前 Markdown 标题的 1-based 源码行号(0 = 无);锚点失效时的回退定位目标。</summary>
    [ObservableProperty]
    private int markdownHeadingLine;

    public bool IsMarkdownRendered => IsMarkdown
        && MarkdownMode is MarkdownViewMode.Rendered or MarkdownViewMode.SideBySide
        && MarkdownRenderResult is not null;

    /// <summary>是否显示 AvalonEdit 源码面:纯渲染模式隐藏(不等渲染结果就绪),加载/重载期间显示
    /// 中性空白而非"源码 + 内置语法高亮"的闪帧;解析失败/大型文件切回源码模式、或手动切到源码视图
    /// 时自动恢复显示。并排模式两面同屏,源码面保持可见。</summary>
    public bool ShowSourceSurface => !(IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered);

    partial void OnMarkdownModeChanged(MarkdownViewMode value)
    {
        OnPropertyChanged(nameof(IsMarkdownRendered));
        OnPropertyChanged(nameof(ShowSourceSurface));
        if (!IsMarkdown) return;

        if (value == MarkdownViewMode.Source)
        {
            _markdownCancellation?.Cancel();
            // 源码模式:释放渲染产物——绑定驱动视图清空 FlowDocument 并释放图片引用,
            // Markdig AST 随结果对象一并释放(不再与源码侧重复驻留)。标题列表常驻,
            // 源码模式下的光标→逻辑位置同步不中断;切回预览时重新解析。
            MarkdownRenderResult = null;
            if (IsLoadFinished && Content.Length > 0)
            {
                // 折叠整文扫描移到 worker(UI 线程发布),大 Markdown 切换不再卡顿。
                _ = ComputeFoldsAsync();
                _ = BuildPresentationAsync();
            }
        }
        else
        {
            // 渲染面需要产物(并排与纯渲染同语义):无产物即解析渲染;
            // 阅读位置由视图按 锚点→行号→偏移 优先级恢复。
            if (MarkdownRenderResult is null && IsLoadFinished)
            {
                _ = BuildMarkdownPreviewAsync();
            }

            if (value == MarkdownViewMode.SideBySide && IsLoadFinished && Content.Length > 0)
            {
                // 从纯渲染切入并排:源码面此前跳过了高亮/折叠派生,现在补齐。
                _ = ComputeFoldsAsync();
                _ = BuildPresentationAsync();
            }
        }
    }

    partial void OnMarkdownRenderResultChanged(MarkdownRenderResult? value) => OnPropertyChanged(nameof(IsMarkdownRendered));

    [RelayCommand]
    private void ToggleMarkdownView()
    {
        if (!IsMarkdown) return;
        MarkdownMode = MarkdownMode == MarkdownViewMode.Rendered
            ? MarkdownViewMode.Source
            : MarkdownViewMode.Rendered;
    }

    [RelayCommand]
    private void ShowMarkdownPreview()
    {
        if (IsMarkdown) MarkdownMode = MarkdownViewMode.Rendered;
    }

    [RelayCommand]
    private void ShowMarkdownSource() => MarkdownMode = MarkdownViewMode.Source;

    /// <summary>并排模式(VS Code markdown 并排预览):源码面与渲染面同屏;仅完整加载的
    /// Markdown 可用,大文件由 BuildMarkdownPreviewAsync 的分层门控落回源码模式。</summary>
    [RelayCommand]
    private void ShowMarkdownSideBySide()
    {
        if (IsMarkdown) MarkdownMode = MarkdownViewMode.SideBySide;
    }

    /// <summary>预览视图完成首次渲染后调用:把 Markdig AST 从结果中分离(FlowDocument 已承载
    /// 渲染内容),大 AST 不随预览长期驻留;主题变化/切回预览时从 Content 重新解析。</summary>
    public void DetachMarkdownDocument()
    {
        if (MarkdownRenderResult is { Document: not null } result)
        {
            result.Document = null;
        }
    }

    /// <summary>重新解析并构建预览(视图切回一个已释放/已分离渲染产物的标签时调用)。</summary>
    public Task RebuildMarkdownPreviewAsync() => BuildMarkdownPreviewAsync();

    /// <summary>Latest immutable language snapshot.  The view applies only this value, keeping
    /// TextMate/Roslyn work out of AvalonEdit's UI render path.</summary>
    [ObservableProperty]
    private CodePresentationSnapshot presentation = CodePresentationSnapshot.Empty();

    [ObservableProperty]
    private string notice = string.Empty;

    /// <summary>状态栏提示,独立于 <see cref="Notice"/>:文件外部变更但暂时无法读取/被删除时保留旧
    /// 内容并提示,文件恢复后自动刷新并清除。不用 Notice —— 那会把源码区切换成空态,而旧内容仍可读。</summary>
    [ObservableProperty]
    private string fileChangeNotice = string.Empty;

    /// <summary>Localized language label for the tab/breadcrumb/status bar.</summary>
    [ObservableProperty]
    private string languageName = string.Empty;

    [ObservableProperty]
    private string encodingName = string.Empty;

    [ObservableProperty]
    private string newlineTypeText = string.Empty;

    [ObservableProperty]
    private string fileSizeText = string.Empty;

    [ObservableProperty]
    private int lineCount;

    /// <summary>One-based global line represented by AvalonEdit line 1 in windowed mode.</summary>
    [ObservableProperty]
    private int windowStartLine = 1;

    public string AnalysisModeText => CapacityTier == ReadOnlyContentTier.Full ? "完整分析" : "窗口分析";

    [ObservableProperty]
    private bool isBinary;

    [ObservableProperty]
    private bool isTruncated;

    /// <summary>Capacity tier selected before the AvalonEdit document is created.  Full mode keeps
    /// every reading feature; larger tiers remain explicit rather than silently exhausting memory.</summary>
    [ObservableProperty]
    private ReadOnlyContentTier capacityTier = ReadOnlyContentTier.Full;

    // ===== Reading options (session-scoped; persisted later) =====
    // Defaults match VS Code: no word wrap, current line highlighted.

    [ObservableProperty]
    private bool wordWrap;

    [ObservableProperty]
    private bool showMinimap;

    [ObservableProperty]
    private bool showLineNumbers = true;

    [ObservableProperty]
    private bool showIndentGuides = true;

    [ObservableProperty]
    private bool showFoldingControls = true;

    /// <summary>Code font size (driven by Ctrl+wheel zoom; persisted as a reading preference).</summary>
    [ObservableProperty]
    private double fontSize = 14;

    /// <summary>源码/Diff 阅读器等宽字体(editor.fontFamily;视图渲染时转为 FontFamily)。</summary>
    [ObservableProperty]
    private string fontFamily = FontCatalog.DefaultEditorFamily;

    // ===== Document outline (right-hand symbol list) =====

    /// <summary>All symbols derived from the load state (C# / XML / JSON); empty for plain text.</summary>
    public BulkObservableCollection<CodeOutlineEntry> OutlineEntries { get; } = [];

    /// <summary>Outline entries narrowed by the outline filter box.</summary>
    public BulkObservableCollection<CodeOutlineEntry> FilteredOutlineEntries { get; } = [];

    /// <summary>Shared hierarchical symbols used by the tree view and folding projection.</summary>
    public BulkObservableCollection<CodeSymbolNode> SymbolRoots { get; } = [];

    /// <summary>Filtered symbol roots. Ancestors of matching nodes remain in the tree.</summary>
    public BulkObservableCollection<CodeSymbolNode> FilteredSymbolRoots { get; } = [];

    public CodeSymbolDocument SymbolDocument => _symbolDocument;
    public bool HasOutline => SymbolDocument.All.Any(node => node.ShowInOutline);

    /// <summary>光标行目前所属的符号祖先链(VS Code breadcrumb symbol segment 多级链,
    /// 外层 → 内层,最多 3 级;无符号时为空)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveSymbol))]
    private string activeSymbolText = string.Empty;

    public bool HasActiveSymbol => ActiveSymbolText.Length > 0;

    /// <summary>Active symbol ancestors, outermost to innermost, for breadcrumb navigation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveSymbol))]
    private IReadOnlyList<CodeSymbolNode> activeSymbolPath = [];

    /// <summary>VS Code sticky scroll 开关(会话级;命令 editor.action.toggleStickyScroll 切换)。</summary>
    [ObservableProperty]
    private bool showStickyScroll = true;

    /// <summary>迷你地图渲染模式:true=字符格,false=块(VS Code renderCharacters)。</summary>
    [ObservableProperty]
    private bool minimapRenderCharacters;

    /// <summary>迷你地图宽度(像素,60–320)。</summary>
    [ObservableProperty]
    private double minimapWidth = 130;

    /// <summary>相对行号(VS Code editor.lineNumbers relative:光标行为实号,其余显示相对距离)。</summary>
    [ObservableProperty]
    private bool lineNumbersRelative;

    /// <summary>垂直标尺列(editor.rulers,逗号分隔解析;空为无标尺)。</summary>
    [ObservableProperty]
    private double[]? rulerColumns;

    /// <summary>光标行变化 → 按符号范围更新当前节点、完整祖先链(面包屑),
    /// 并把当前逻辑位置同步到 Markdown 标题状态(源码模式)。</summary>
    public void UpdateCaretLine(int line)
    {
        var active = _symbolDocument.FindDeepestContaining(line);
        if (active is null)
        {
            foreach (var node in _wiredSymbolNodes)
            {
                node.IsActive = false;
            }

            ActiveSymbolPath = [];
            ActiveSymbolText = string.Empty;
            return;
        }

        ActivateSymbol(active);
        if (IsMarkdown)
        {
            SyncMarkdownHeadingFromLine(line);
        }
    }

    /// <summary>标记活动符号节点:大纲高亮、顶部面包屑祖先链;Markdown 标题自动展开所有父级,
    /// 保证活动节点在大纲树中可见。</summary>
    private void ActivateSymbol(CodeSymbolNode active)
    {
        foreach (var node in _wiredSymbolNodes)
        {
            node.IsActive = false;
        }

        active.IsActive = true;
        var chain = _symbolDocument.GetAncestors(active);
        if (IsMarkdown)
        {
            foreach (var ancestor in chain)
            {
                if (!ReferenceEquals(ancestor, active))
                {
                    ancestor.IsExpanded = true;
                }
            }
        }

        ActiveSymbolPath = chain;
        ActiveSymbolText = string.Join(" › ", chain.Select(entry => entry.DisplayName));
    }

    /// <summary>源码光标路径:光标行上方最近的标题 = 当前逻辑位置(无标题时保留上次状态)。</summary>
    private void SyncMarkdownHeadingFromLine(int line)
    {
        var heading = MarkdownHeadings.LastOrDefault(item => item.Line <= line);
        if (heading is null)
        {
            return;
        }

        MarkdownAnchor = heading.Anchor;
        MarkdownHeadingLine = heading.Line;
    }

    /// <summary>预览滚动路径:写入当前标题的逻辑位置,并同步大纲高亮、父级展开和顶部面包屑。
    /// 无标题(anchor 为空/行号非正,例如光标上方尚无任何标题)时不更新:保留上次逻辑位置,
    /// 也不改动大纲活动状态。</summary>
    public void SetMarkdownHeading(string? anchor, int line)
    {
        if (!IsMarkdown || anchor is not { Length: > 0 } || line <= 0)
        {
            return;
        }

        MarkdownAnchor = anchor;
        MarkdownHeadingLine = line;
        UpdateCaretLine(line);
    }

    [ObservableProperty]
    private string outlineFilterText = string.Empty;

    // ===== Folding + view state =====

    /// <summary>Foldable regions derived from the loaded content (braces / XML).</summary>
    private IReadOnlyList<CodeFoldSection> foldSections = [];

    public IReadOnlyList<CodeFoldSection> FoldSections
    {
        get => foldSections;
        private set => SetProperty(ref foldSections, value);
    }

    /// <summary>View state captured when the preview loses the editor (scroll offset, caret and
    /// folded section offsets) so re-activating the reused tab restores the previous reading spot.</summary>
    public EditorViewState? ViewState { get; set; }

    private sealed record DerivedContentSnapshot(
        CodeSymbolDocument Symbols,
        IReadOnlyList<CodeOutlineEntry> Outline,
        IReadOnlyList<CodeFoldSection> Folds,
        IReadOnlyList<TextSearchMatch> SearchMatches,
        string SearchError);

    /// <summary>Builds the expensive source projections away from the UI thread, then publishes
    /// one immutable result set. This mirrors VS Code's model/worker split: a newer content or
    /// language revision invalidates the worker result before it can mutate any bound collection.
    /// </summary>
    private async Task BuildDerivedContentAsync(int contentVersion, bool includeSearch = true)
    {
        _derivedCancellation?.Cancel();
        _derivedCancellation?.Dispose();
        var cancellation = _derivedCancellation = new CancellationTokenSource();
        var derivedVersion = Interlocked.Increment(ref _derivedVersion);

        var text = Content;
        var fileType = FileType;
        var outlineKind = fileType.OutlineKind;
        var query = SearchText;
        var caseSensitive = SearchCaseSensitive;
        var wholeWord = SearchWholeWord;
        var useRegex = SearchUseRegex;
        var searchInSelection = SearchInSelection;
        var selection = searchInSelection ? SelectionRangeProvider?.Invoke() : null;
        var renderedMarkdown = IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered;

        try
        {
            var snapshot = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();

                IReadOnlyList<CodeOutlineEntry> compatibilityEntries = [];
                var symbols = CodeSymbolDocument.Empty;
                if (outlineKind != CodeOutlineKind.None && text.Length > 0)
                {
                    // Keep the injected parser as the compatibility/failure boundary. Both
                    // operations are pure and are now paid by a worker instead of the UI thread.
                    compatibilityEntries = _outlineParser.Parse(text, outlineKind);
                    symbols = _symbolAnalyzer.Analyze(text, outlineKind);
                }

                cancellation.Token.ThrowIfCancellationRequested();
                var outline = symbols.ToOutlineEntries();
                if (outline.Count == 0)
                {
                    outline = compatibilityEntries;
                }

                var folds = renderedMarkdown || text.Length == 0
                    ? Array.Empty<CodeFoldSection>()
                    : _folding.FindSections(text, fileType);

                IReadOnlyList<TextSearchMatch> matches = [];
                var searchError = string.Empty;
                if (includeSearch && !string.IsNullOrEmpty(query))
                {
                    searchError = useRegex
                        ? TextSearchService.GetRegexError(query, caseSensitive) ?? string.Empty
                        : string.Empty;
                    if (searchError.Length == 0)
                    {
                        var searchText = text;
                        var baseOffset = 0;
                        var baseLine = 0;
                        if (searchInSelection && selection is { } range && range.End > range.Start
                            && range.End <= text.Length)
                        {
                            searchText = text[range.Start..range.End];
                            baseOffset = range.Start;
                            for (var i = 0; i < range.Start; i++)
                            {
                                if (text[i] == '\n') baseLine++;
                            }
                        }

                        matches = _search.FindAll(searchText, query,
                                new TextSearchOptions(caseSensitive, wholeWord, useRegex))
                            .Select(match => new TextSearchMatch(
                                match.Length,
                                match.Offset + baseOffset,
                                match.Line + baseLine))
                            .Take(MaxSearchMatches)
                            .ToArray();
                    }
                }

                cancellation.Token.ThrowIfCancellationRequested();
                return new DerivedContentSnapshot(symbols, outline, folds, matches, searchError);
            }, cancellation.Token);

            if (cancellation.IsCancellationRequested
                || contentVersion != _contentVersion
                || derivedVersion != _derivedVersion
                || IsClosed
                || !ReferenceEquals(cancellation, _derivedCancellation))
            {
                return;
            }

            UnwireSymbolNodes();
            _symbolDocument = snapshot.Symbols;
            foreach (var root in _symbolDocument.Roots)
            {
                WireSymbolNode(root);
            }

            OutlineEntries.ReplaceRange(snapshot.Outline);
            SymbolRoots.ReplaceRange(_symbolDocument.Roots);
            RefreshFilteredOutline();
            OnPropertyChanged(nameof(HasOutline));
            OnPropertyChanged(nameof(SymbolDocument));
            UpdateCaretLine(Math.Max(1, LineCount > 0 ? 1 : 0));
            FoldSections = snapshot.Folds;

            // Search settings may change while the worker is running. Do not let a content
            // refresh overwrite a newer interactive search request.
            if (includeSearch
                && query == SearchText
                && caseSensitive == SearchCaseSensitive
                && wholeWord == SearchWholeWord
                && useRegex == SearchUseRegex
                && searchInSelection == SearchInSelection)
            {
                SearchErrorMessage = snapshot.SearchError;
                SearchMatches.ReplaceRange(snapshot.SearchMatches);
                MatchCount = SearchMatches.Count;
                CurrentMatchIndex = 0;
                CurrentMatchViewIndex = MatchCount > 0 ? 0 : -1;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer content/language revision owns the next publication.
        }
    }

    private int _foldsVersion;

    /// <summary>折叠区间在 worker 线程整文计算,完成时经版本/内容双重校验后在 UI 线程发布
    /// (FoldSections 驱动视图重建,必须 UI 线程)。旧实现在渲染→源码切换时于 UI 线程
    /// 同步 FindSections,大文件可见卡顿。</summary>
    private async Task ComputeFoldsAsync()
    {
        var version = ++_foldsVersion;
        var content = Content;
        var fileType = FileType;
        IReadOnlyList<CodeFoldSection> folds;
        try
        {
            folds = content.Length == 0
                ? []
                : await Task.Run(() => _folding.FindSections(content, fileType));
        }
        catch (Exception)
        {
            return; // 折叠失败保留现状(视图自愈路径:下次内容管道重算)
        }

        if (version != _foldsVersion || !ReferenceEquals(Content, content))
        {
            return; // 期间又发起了更新或内容已替换:陈旧结果不得发布
        }

        FoldSections = folds;
    }

    partial void OnOutlineFilterTextChanged(string value) => RefreshFilteredOutline();

    [RelayCommand]
    private void JumpToOutline(CodeOutlineEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        // Keep the public quick-pick command's historical line event contract. The tree and
        // breadcrumb commands use JumpToSymbol for precise selection-range navigation.
        if (IsMarkdown && _symbolDocument.FindById(entry.Id) is { } node)
        {
            ApplyOutlineJumpPosition(node);
        }
        else if (IsMarkdown && MarkdownHeadings.FirstOrDefault(item => item.Line == entry.Line) is { } heading)
        {
            MarkdownAnchor = heading.Anchor;
            MarkdownHeadingLine = heading.Line;
        }

        // Line-only jump through the shared go-to-line command: windowed documents, line
        // clamping and event dispatch stay identical to the search-result jump.
        GoToLineInput = Math.Max(1, entry.Line).ToString();
        GoToLineCommand.Execute(null);
    }

    [RelayCommand]
    private void JumpToSymbol(CodeSymbolNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (IsMarkdown)
        {
            // 大纲点击统一写入当前逻辑位置(锚点 + 行号),再交给视图执行跳转;
            // 预览模式跳锚点、源码模式跳源码行,两种模式的状态收敛一致。
            ApplyOutlineJumpPosition(node);
        }

        // Route through the shared go-to-line command instead of raising the raw position event
        // directly: windowed documents load the target window first and the line is clamped to
        // the document — exactly the path the search-result jump (OpenFileAtAsync) uses, so
        // outline clicks and search clicks behave the same.
        GoToLineInput = $"{Math.Max(1, node.SelectionRange.StartLine)}:{Math.Max(1, node.SelectionRange.StartColumn)}";
        GoToLineCommand.Execute(null);
    }

    /// <summary>大纲跳转的统一位置写入:锚点/行号来自同一标题模型,活动节点与面包屑同步高亮。</summary>
    private void ApplyOutlineJumpPosition(CodeSymbolNode node)
    {
        if (MarkdownHeadings.FirstOrDefault(item => item.Line == node.Range.StartLine) is { } heading)
        {
            MarkdownAnchor = heading.Anchor;
            MarkdownHeadingLine = heading.Line;
        }

        ActivateSymbol(node);
    }

    private void BuildOutline()
    {
        UnwireSymbolNodes();
        _symbolDocument = CodeSymbolDocument.Empty;
        IReadOnlyList<CodeOutlineEntry> entries = [];
        IReadOnlyList<CodeSymbolNode> roots = [];
        if (FileType.OutlineKind != CodeOutlineKind.None && Content.Length > 0)
        {
            // Keep the injected parser as a compatibility/failure boundary, while the shared
            // analyzer supplies ranges and parent/child links for navigation and folding.
            var compatibilityEntries = _outlineParser.Parse(Content, FileType.OutlineKind);
            _symbolDocument = _symbolAnalyzer.Analyze(Content, FileType.OutlineKind);
            entries = _symbolDocument.ToOutlineEntries();
            if (entries.Count == 0)
            {
                entries = compatibilityEntries;
            }

            foreach (var root in _symbolDocument.Roots)
            {
                WireSymbolNode(root);
            }

            roots = _symbolDocument.Roots;
        }

        OutlineEntries.ReplaceRange(entries);
        SymbolRoots.ReplaceRange(roots);
        RefreshFilteredOutline();
        OnPropertyChanged(nameof(HasOutline));
        OnPropertyChanged(nameof(SymbolDocument));
        UpdateCaretLine(Math.Max(1, LineCount > 0 ? 1 : 0));
    }

    private void RefreshFilteredOutline()
    {
        var query = OutlineFilterText?.Trim() ?? string.Empty;
        var filteredEntries = OutlineEntries.Where(entry => query.Length == 0
                || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        FilteredOutlineEntries.ReplaceRange(filteredEntries);
        FilteredSymbolRoots.ReplaceRange(_symbolDocument.Filter(query));
    }

    private void WireSymbolNode(CodeSymbolNode node)
    {
        _wiredSymbolNodes.Add(node);
        node.IsExpanded = !_collapsedSymbolIds.Contains(node.Id);
        node.PropertyChanged += OnSymbolNodePropertyChanged;
        foreach (var child in node.Children)
        {
            WireSymbolNode(child);
        }
    }

    private void UnwireSymbolNodes()
    {
        foreach (var node in _wiredSymbolNodes)
        {
            node.PropertyChanged -= OnSymbolNodePropertyChanged;
            node.IsActive = false;
        }

        _wiredSymbolNodes.Clear();
        ActiveSymbolPath = [];
        ActiveSymbolText = string.Empty;
    }

    private void OnSymbolNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CodeSymbolNode node || e.PropertyName != nameof(CodeSymbolNode.IsExpanded))
        {
            return;
        }

        if (node.IsExpanded)
        {
            _collapsedSymbolIds.Remove(node.Id);
        }
        else
        {
            _collapsedSymbolIds.Add(node.Id);
        }
    }

    // ===== Find state =====

    [ObservableProperty]
    private bool isFindBarOpen;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool searchCaseSensitive;

    [ObservableProperty]
    private bool searchWholeWord;

    [ObservableProperty]
    private bool searchUseRegex;

    [ObservableProperty]
    private int matchCount;

    /// <summary>0-based index of the currently focused match (resets on every search).</summary>
    [ObservableProperty]
    private int currentMatchIndex;

    /// <summary>0-based index of the current match inside <see cref="SearchMatches"/> (the view's
    /// render index): full-text mode mirrors <see cref="CurrentMatchIndex"/>; windowed mode holds the
    /// current match's position in the current window's match subset, or −1 when it is outside.</summary>
    [ObservableProperty]
    private int currentMatchViewIndex = -1;

    /// <summary>Find-bar error text (currently: invalid regular expression).</summary>
    [ObservableProperty]
    private string searchErrorMessage = string.Empty;

    /// <summary>True while a windowed search is waiting for debounce or scanning the source.</summary>
    [ObservableProperty]
    private bool isSearching;

    /// <summary>All capped matches of the active search; the view renders these as background markers.</summary>
    public BulkObservableCollection<TextSearchMatch> SearchMatches { get; } = [];

    /// <summary>"3 / 40" style counter for the find bar (or 无匹配 / 空查找).</summary>
    public string CurrentMatchDisplay => string.IsNullOrEmpty(SearchText)
        ? string.Empty
        : MatchCount == 0 ? "无匹配" : $"{CurrentMatchIndex + 1} / {MatchCount}";

    // ===== Go-to-line state =====

    [ObservableProperty]
    private bool isGoToLineBarOpen;

    [ObservableProperty]
    private string goToLineInput = string.Empty;

    [ObservableProperty]
    private string goToLineErrorMessage = string.Empty;

    partial void OnGoToLineInputChanged(string value)
    {
        if (!string.IsNullOrEmpty(GoToLineErrorMessage))
        {
            GoToLineErrorMessage = string.Empty;
        }
    }

    /// <summary>Raised after a validated go-to-line jump; the view centers + emphasizes the line.</summary>
    public event EventHandler<int>? GoToLineRequested;

    /// <summary>Raised for symbol navigation that has a precise selection range.</summary>
    public event EventHandler<(int Line, int Column)>? GoToPositionRequested;

    /// <summary>折叠请求(由视图映射到 CodeDocumentView;VS Code Ctrl+K Ctrl+0/J/1..9)。</summary>
    public event EventHandler<(EditorFoldRequest Kind, int Level)>? FoldRequested;

    /// <summary>主状态栏"当前文档状态"段的光标位置显示(由视图 code-behind 随 caret/选区变化写入,
    /// 形如 "Ln 12, Col 34 (已选择 8 字符 / 2 行)";原视图内独立底栏的内容已并入窗口主状态栏)。</summary>
    [ObservableProperty]
    private string caretPositionText = string.Empty;

    [RelayCommand]
    private void CollapseAllFold() => FoldRequested?.Invoke(this, (EditorFoldRequest.CollapseAll, 0));

    [RelayCommand]
    private void ExpandAllFold() => FoldRequested?.Invoke(this, (EditorFoldRequest.ExpandAll, 0));

    [RelayCommand]
    private void FoldToLevel(int level) => FoldRequested?.Invoke(this, (EditorFoldRequest.ToLevel, level));

    /// <summary>True once the preview payload is resident and its initial load has completed. A
    /// cache-evicted tab deliberately becomes false again so activation reads the latest file.</summary>
    public bool IsLoadFinished => !_payloadUnloaded && _loadTask is not null && !IsLoading;

    /// <summary>True when the decoded/derived payload is resident and no load or reload pass is
    /// still publishing it. The editor cache uses this as its eviction eligibility gate.</summary>
    internal bool IsPayloadLoaded => !IsClosed
        && !_payloadUnloaded
        && _loadTask?.IsCompleted == true
        && Volatile.Read(ref _pipelineActive) == 0;

    /// <summary>Approximate managed bytes retained by this tab's source payload. It intentionally
    /// overestimates the text plus common derived projections; it is accounting for eviction, not
    /// a profiler reading. Large file checkpoints are included even when the visible window is small.</summary>
    internal long RetainedMemoryBytes
    {
        get
        {
            var estimate = checked((long)Content.Length * 4L); // UTF-16 text + derived projections
            if (_documentSource is not null)
            {
                var checkpoints = Math.Max(1L, ((long)Math.Max(1, LineCount) / DocumentSourceCheckpointStride) + 1);
                estimate = checked(estimate
                    + DocumentSourceBaseMemoryBytes
                    + checkpoints * DocumentSourceCheckpointBytes);
            }

            return estimate;
        }
    }

    /// <summary>One shared load task: the editor activates tabs both eagerly (OpenTabAsync awaits) and
    /// from the selection-changed handler, so a single task guarantees every caller observes the same
    /// finished state instead of racing two file reads.</summary>
    public override Task LoadAsync()
    {
        if (IsClosed || _resourcesReleased)
        {
            return Task.CompletedTask;
        }

        if (_loadTask is { IsCompleted: false } loading)
        {
            return loading;
        }

        // A completed task remains cached after a failed initial read. Only an explicit payload
        // eviction is allowed to replace it and trigger a fresh lazy read.
        if (_loadTask is not null && !_payloadUnloaded)
        {
            return _loadTask;
        }

        _payloadUnloaded = false;
        IsLoading = true;
        _loadTask = RunContentPipelineAsync(initialLoad: true);
        OnPropertyChanged(nameof(IsLoadFinished));
        OnPropertyChanged(nameof(IsPayloadLoaded));
        return _loadTask;
    }

    /// <summary>外部变更重载:文件事件(保存/替换/创建/删除/重命名)后重新读取,所有已打开的预览
    /// (包括非活动标签)都经此串行化重载。只有最新一次请求运行解码管线,旧任务不得覆盖新内容。</summary>
    public Task ReloadAsync()
    {
        if (IsClosed || _resourcesReleased || _payloadUnloaded)
        {
            return Task.CompletedTask;
        }

        IsLoading = true;
        return RunContentPipelineAsync(initialLoad: false);
    }

    internal void UnloadContentForCache()
    {
        if (IsClosed || _resourcesReleased || _payloadUnloaded || !IsLoadFinished
            || Volatile.Read(ref _pipelineActive) != 0 || RetainedMemoryBytes <= 0)
        {
            return;
        }

        // Invalidate all workers before clearing their inputs. ContentUpdating runs while the
        // payload is still considered loaded, allowing the visible view to capture its position.
        _contentVersion++;
        _presentationVersion++;
        _derivedVersion++;
        _markdownVersion++;
        _foldsVersion++;
        _searchVersion++;
        CancelPendingWindowLoad();
        CancelPendingReloadRetry();
        ContentUpdating?.Invoke(this, EventArgs.Empty);

        MarkdownRenderResult = null;
        MarkdownHeadings = [];
        MarkdownRenderNotice = string.Empty;
        _searchTextDebounce.Cancel();
        CancelAndDispose(ref _presentationCancellation);
        CancelAndDispose(ref _derivedCancellation);
        CancelAndDispose(ref _markdownCancellation);
        if (_documentSource is not null)
        {
            var stale = _documentSource;
            _documentSource = null;
            _ = DisposeDocumentSourceAsync(stale);
        }

        Content = string.Empty;
        Presentation = CodePresentationSnapshot.Empty();
        Notice = string.Empty;
        FileChangeNotice = string.Empty;
        ClearFindResults();
        SearchErrorMessage = string.Empty;
        _windowedMatches.Clear();
        _windowTextLines = 0;
        _windowLineOffsets = [0];
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        FilteredOutlineEntries.ReplaceRange([]);
        OutlineEntries.ReplaceRange([]);
        FoldSections = [];
        SymbolRoots.ReplaceRange([]);
        FilteredSymbolRoots.ReplaceRange([]);
        UnwireSymbolNodes();
        _symbolDocument = CodeSymbolDocument.Empty;

        _payloadUnloaded = true;
        _loadTask = null;
        OnPropertyChanged(nameof(IsLoadFinished));
        OnPropertyChanged(nameof(IsPayloadLoaded));
        OnPropertyChanged(nameof(RetainedMemoryBytes));
    }

    private async Task RunContentPipelineAsync(bool initialLoad)
    {
        if (IsClosed)
        {
            return;
        }

        Interlocked.Increment(ref _pipelineActive);
        var version = ++_contentVersion;
        var gateEntered = false;
        try
        {
            await _reloadGate.WaitAsync();
            gateEntered = true;
            if (version != _contentVersion || IsClosed)
            {
                return; // 等待门闩期间已被更新的请求超越
            }

            if (initialLoad)
            {
                await InitialLoadCoreAsync(version);
            }
            else
            {
                await ReloadCoreAsync(version);
            }
        }
        catch (ObjectDisposedException)
        {
            return; // 标签已释放,排队中的重载直接放弃
        }
        catch (Exception ex)
        {
            if (version != _contentVersion || IsClosed || _resourcesReleased)
            {
                return;
            }

            if (initialLoad)
            {
                EnsureMarkdownSourceMode();
                Notice = $"无法预览文件：{SingleLineMessage(ex)}";
            }
            else
            {
                FileChangeNotice = SummarizeReloadFailure(SingleLineMessage(ex), File.Exists(Path));
                ScheduleReloadRetry();
            }
        }
        finally
        {
            if (gateEntered)
            {
                _reloadGate.Release();
            }

            if (Interlocked.Decrement(ref _pipelineActive) == 0)
            {
                IsLoading = false;
            }

            OnPropertyChanged(nameof(IsLoadFinished));
            OnPropertyChanged(nameof(IsPayloadLoaded));
        }
    }

    private async Task InitialLoadCoreAsync(int version)
    {
        IReadOnlyDocumentSource? source = null;
        try
        {
            if (File.Exists(Path) && new FileInfo(Path).Length > ContentLimits.FullSourceBytes)
            {
                source = new FileReadOnlyDocumentSource(Path, ContentLimits, _forcedEncoding);
                _documentSource = source;
                var metadata = await source.GetMetadataAsync();
                if (version != _contentVersion || IsClosed)
                {
                    if (ReferenceEquals(source, _documentSource)) _documentSource = null;
                    await DisposeDocumentSourceAsync(source);
                    return;
                }
                FileSizeText = TextDocumentDecoder.FormatSize(metadata.ByteLength);
                CapacityTier = metadata.CapacityTier;
                OnPropertyChanged(nameof(AnalysisModeText));
                LanguageName = FileType.DisplayName;
                EncodingName = metadata.Encoding.WebName;
                NewlineTypeText = metadata.LineEnding == "\r\n" ? "CRLF" : metadata.LineEnding == "\n" ? "LF" : "CR";
                LineCount = metadata.TotalLines;
                if (metadata.IsBinary)
                {
                    _documentSource = null;
                    await DisposeDocumentSourceAsync(source);
                    IsBinary = true;
                    EnsureMarkdownSourceMode();
                    Notice = "二进制文件，无法预览。";
                    return;
                }

                if (CapacityTier == ReadOnlyContentTier.Summary)
                {
                    IsTruncated = true;
                    EnsureMarkdownSourceMode();
                    Notice = $"文件超过 {ContentLimits.EffectiveWindowedSourceBytes / (1024 * 1024)} MB，已进入摘要模式；可通过跳转行进入窗口阅读。";
                    return;
                }

                // 窗口阅读不产生渲染预览:模式显式落到源码,源码面(含提示)可见;
                // 先于 LoadWindowAsync 切换,窗口派生工作(折叠等)按源码模式计算。
                EnsureMarkdownSourceMode();
                var focusGlobalLine = Math.Clamp(ViewState?.CaretLine ?? 1, 1, Math.Max(1, LineCount));
                if (!await LoadWindowAsync(focusGlobalLine))
                {
                    return;
                }
                if (version != _contentVersion || IsClosed) return;
                Notice = $"文件超过 {ContentLimits.FullSourceBytes / (1024 * 1024)} MB，已进入大型文件窗口阅读：当前显示第 {WindowStartLine}–{Math.Min(LineCount, WindowStartLine + _windowTextLines - 1)} 行，共 {LineCount} 行。";
                return;
            }

            var result = await _decoder.DecodeAsync(Path, ContentLimits.FullSourceBytes, _forcedEncoding);
            if (version != _contentVersion || IsClosed) return;
            FileSizeText = TextDocumentDecoder.FormatSize(result.FileSize);
            CapacityTier = ContentLimits.ForSource(result.FileSize);
            OnPropertyChanged(nameof(AnalysisModeText));
            LanguageName = FileType.DisplayName;

            if (result.IsBinary)
            {
                IsBinary = true;
                EnsureMarkdownSourceMode();
                Notice = "二进制文件，无法预览。";
                return;
            }

            if (!result.Success)
            {
                IsTruncated = result.IsTruncated;
                EnsureMarkdownSourceMode();
                Notice = result.Error ?? "无法读取文件。";
                return;
            }

            await ApplyDecodedTextAsync(result, version);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException
            or System.Text.DecoderFallbackException
            or System.Xml.XmlException
            or System.Text.Json.JsonException)
        {
            if (version != _contentVersion || IsClosed) return;
            if (source is not null)
            {
                if (ReferenceEquals(source, _documentSource)) _documentSource = null;
                await DisposeDocumentSourceAsync(source);
            }
            Content = string.Empty;
            EnsureMarkdownSourceMode();
            Notice = $"无法预览文件：{ex.Message}";
        }
    }

    /// <summary>外部变更重载管线:成功(或确定性新状态)时发布;暂时不可读/被删除时保留旧内容并
    /// 提示(独立于 Notice),稍后自动重试。</summary>
    private async Task ReloadCoreAsync(int version)
    {
        try
        {
            if (File.Exists(Path) && new FileInfo(Path).Length > ContentLimits.FullSourceBytes)
            {
                if (version != _contentVersion || IsClosed) return;
                await ReloadWindowedCoreAsync(version);
                return;
            }

            // 窗口化 → 全文迁移:释放旧文件源,避免活动查找/窗口跳转命中已失效的稀疏索引。
            if (_documentSource is not null)
            {
                var stale = _documentSource;
                _documentSource = null;
                CancelPendingWindowLoad();
                _ = DisposeDocumentSourceAsync(stale);
                _windowedMatches.Clear();
                _windowTextLines = 0;
                _windowLineOffsets = [0];
                _searchCancellation?.Cancel();
            }

            var result = await _decoder.DecodeAsync(Path, ContentLimits.FullSourceBytes, _forcedEncoding);
            if (version != _contentVersion || IsClosed) return;

            if (result.IsBinary)
            {
                // 文件真正变成二进制:按初始加载的二进制空态策略显示新状态。
                CancelDerivedWork();
                ContentUpdating?.Invoke(this, EventArgs.Empty);
                Content = string.Empty;
                IsBinary = true;
                IsTruncated = false;
                EnsureMarkdownSourceMode();
                Notice = "二进制文件，无法预览。";
                FileChangeNotice = string.Empty;
                EncodingName = string.Empty;
                NewlineTypeText = string.Empty;
                LineCount = 0;
                CancelPendingReloadRetry();
                return;
            }

            if (!result.Success)
            {
                // 暂时不可读(写入中/被锁定/已删除):保留旧内容,提示;恢复后自动重试刷新。
                FileChangeNotice = SummarizeReloadFailure(result.Error, File.Exists(Path));
                ScheduleReloadRetry();
                return;
            }

            await ApplyDecodedTextAsync(result, version);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException
            or System.Text.DecoderFallbackException
            or System.Xml.XmlException
            or System.Text.Json.JsonException)
        {
            if (version != _contentVersion || IsClosed) return;
            FileChangeNotice = SummarizeReloadFailure(SingleLineMessage(ex), File.Exists(Path));
            ScheduleReloadRetry();
        }
    }

    /// <summary>成功解码后的统一发布:写新内容、重算查找/大纲/折叠/语义高亮/Markdown 预览。
    /// 发布前触发 <see cref="ContentUpdating"/>,让视图保存滚动/光标/选区/折叠位置以便恢复。</summary>
    private async Task ApplyDecodedTextAsync(TextDecodeResult result, int version)
    {
        ContentUpdating?.Invoke(this, EventArgs.Empty);
        Content = result.Text ?? string.Empty;
        WindowStartLine = 1;
        _windowTextLines = 0;
        _windowLineOffsets = [0];
        EncodingName = result.EncodingName ?? string.Empty;
        NewlineTypeText = result.NewlineType switch
        {
            NewlineType.CrLf => "CRLF",
            NewlineType.Lf => "LF",
            NewlineType.Cr => "CR",
            NewlineType.Mixed => "混合",
            _ => string.Empty,
        };
        LineCount = result.LineCount;
        FileSizeText = TextDocumentDecoder.FormatSize(result.FileSize);
        CapacityTier = ContentLimits.ForSource(result.FileSize);
        OnPropertyChanged(nameof(AnalysisModeText));
        LanguageName = FileType.DisplayName;
        IsBinary = false;
        IsTruncated = false;
        Notice = string.Empty;
        FileChangeNotice = string.Empty;
        CancelPendingReloadRetry();
        // 内容重载后旧 blame 整体错位:开着注解就按新内容重取(工作树行归入 0000 块)。
        if (IsBlameVisible)
        {
            _ = LoadBlameAsync(version);
        }
        if (version != _contentVersion || IsClosed) return;

        if (version != _contentVersion || IsClosed) return;

        // VS Code-style staged publication: source text and metadata become available first;
        // expensive derived work runs off the UI thread and publishes only when this content
        // revision is still current. Starting the passes together avoids making a large syntax
        // pass serialize behind outline/folding work.
        var derivedTask = BuildDerivedContentAsync(version);
        var presentationTask = IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered
            ? Task.CompletedTask
            : BuildPresentationAsync();
        var markdownTask = BuildMarkdownPreviewAsync();
        TryAutoEnableBlame();
        await Task.WhenAll(derivedTask, presentationTask, markdownTask);
    }

    /// <summary>大文件(窗口模式)重载:重建稀疏字节/行索引并保留当前阅读窗口附近的位置;索引
    /// 失效后,活动查找按新文件全文重跑再重挂到当前窗口。</summary>
    private async Task ReloadWindowedCoreAsync(int version)
    {
        var focusGlobalLine = WindowStartLine;
        CancelPendingWindowLoad();
        await _windowLoadGate.WaitAsync().ConfigureAwait(true);
        IReadOnlyDocumentSource? source = null;
        try
        {
            if (version != _contentVersion || IsClosed)
            {
                return;
            }

            if (_documentSource is not null)
            {
                var stale = _documentSource;
                _documentSource = null;
                _windowedMatches.Clear();
                _searchCancellation?.Cancel();
                await stale.DisposeAsync().ConfigureAwait(true);
            }

            source = new FileReadOnlyDocumentSource(Path, ContentLimits, _forcedEncoding);
            _documentSource = source;
            var metadata = await source.GetMetadataAsync().ConfigureAwait(true);
            if (version != _contentVersion || IsClosed)
            {
                _documentSource = null;
                await source.DisposeAsync().ConfigureAwait(true);
                return;
            }

            FileSizeText = TextDocumentDecoder.FormatSize(metadata.ByteLength);
            CapacityTier = metadata.CapacityTier;
            OnPropertyChanged(nameof(AnalysisModeText));
            LanguageName = FileType.DisplayName;
            EncodingName = metadata.Encoding.WebName;
            NewlineTypeText = metadata.LineEnding == "\r\n" ? "CRLF" : metadata.LineEnding == "\n" ? "LF" : "CR";
            LineCount = metadata.TotalLines;
            if (metadata.IsBinary)
            {
                CancelDerivedWork();
                ContentUpdating?.Invoke(this, EventArgs.Empty);
                Content = string.Empty;
                IsBinary = true;
                IsTruncated = false;
                EnsureMarkdownSourceMode();
                Notice = "二进制文件，无法预览。";
                FileChangeNotice = string.Empty;
                CancelPendingReloadRetry();
                return;
            }

            if (metadata.CapacityTier == ReadOnlyContentTier.Summary)
            {
                CancelDerivedWork();
                ContentUpdating?.Invoke(this, EventArgs.Empty);
                Content = string.Empty;
                IsTruncated = true;
                EnsureMarkdownSourceMode();
                Notice = "文件超过 32 MB，已进入摘要模式；可通过跳转行进入窗口阅读。";
                FileChangeNotice = string.Empty;
                CancelPendingReloadRetry();
                return;
            }

            IsBinary = false;
            IsTruncated = false;
            Notice = string.Empty;
            FileChangeNotice = string.Empty;
            ContentUpdating?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            if (source is not null && ReferenceEquals(source, _documentSource))
            {
                _documentSource = null;
                await source.DisposeAsync().ConfigureAwait(true);
            }

            throw;
        }
        finally
        {
            _windowLoadGate.Release();
        }

        if (!await LoadWindowAsync(Math.Max(1, focusGlobalLine)))
        {
            return;
        }

        if (version != _contentVersion || IsClosed) return;
        // 窗口阅读不产生渲染预览:模式显式落到源码(全文→窗口迁移时释放旧渲染产物),
        // 源码面(含提示)保持可见;放在窗口内容发布后,模式切换的派生工作面向新窗口。
        EnsureMarkdownSourceMode();
        CancelPendingReloadRetry();
        // 稀疏索引已重建:活动查找按新文件全文重跑,再把匹配重挂到当前窗口。
        if (!string.IsNullOrEmpty(SearchText))
        {
            await RefreshWindowedSearchAsync(SearchText);
        }
    }

    /// <summary>确定性新状态(二进制/摘要模式)发布时取消在途的派生工作(语义高亮/窗口化查找)。</summary>
    private void CancelDerivedWork()
    {
        _derivedCancellation?.Cancel();
        _presentationCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _markdownCancellation?.Cancel();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellation)
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    private void CancelPendingWindowLoad()
    {
        CancellationTokenSource? cancellation;
        lock (_windowRequestGate)
        {
            _windowRequestVersion++;
            cancellation = _windowCancellation;
        }

        cancellation?.Cancel();
    }

    private (int Version, CancellationTokenSource Cancellation) BeginWindowLoad()
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        int version;
        lock (_windowRequestGate)
        {
            previous = _windowCancellation;
            current = new CancellationTokenSource();
            _windowCancellation = current;
            version = ++_windowRequestVersion;
        }

        previous?.Cancel();
        return (version, current);
    }

    private bool IsCurrentWindowLoad(int version, IReadOnlyDocumentSource source, CancellationToken cancellationToken)
    {
        lock (_windowRequestGate)
        {
            return version == _windowRequestVersion
                && ReferenceEquals(source, _documentSource)
                && !cancellationToken.IsCancellationRequested
                && !_resourcesReleased
                && !IsClosed;
        }
    }

    private void FinishWindowLoad(int version, CancellationTokenSource cancellation)
    {
        lock (_windowRequestGate)
        {
            if (version == _windowRequestVersion && ReferenceEquals(cancellation, _windowCancellation))
            {
                _windowCancellation = null;
            }
        }

        cancellation.Dispose();
    }

    private async Task DisposeDocumentSourceAsync(IReadOnlyDocumentSource source)
    {
        try
        {
            await _windowLoadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _windowLoadGate.Release();
            }
        }
        catch (Exception) when (_resourcesReleased || IsClosed)
        {
            // A closing tab only needs best-effort source cleanup; the source is no longer
            // reachable from the tab after detachment.
        }
        catch (Exception)
        {
            // The source was detached before disposal. Cleanup is best effort even during a
            // full-file/windowed migration; observing the exception here prevents an unobserved
            // fire-and-forget disposal task from surfacing as a process-level UI error.
        }
    }

    private void CancelPendingReloadRetry()
    {
        _reloadRetryCancellation?.Cancel();
        _reloadRetryCancellation?.Dispose();
        _reloadRetryCancellation = null;
    }

    /// <summary>文件暂时不可读的提示文案(独立于 Notice,源码区保留旧内容)。</summary>
    private static string SummarizeReloadFailure(string? detail, bool fileExists) =>
        fileExists
            ? $"文件发生外部变更但暂时无法读取（可能仍在保存中）：{detail ?? "未知错误"}"
            : "文件已被删除或暂时不可读；内容恢复后将自动刷新。";

    /// <summary>短暂延迟后自动重试(写入中/锁定/删除恢复);仅在标签存活时调度,发布成功即取消。</summary>
    private void ScheduleReloadRetry()
    {
        if (IsClosed)
        {
            return;
        }

        _reloadRetryCancellation?.Cancel();
        _reloadRetryCancellation?.Dispose();
        var cancellation = _reloadRetryCancellation = new CancellationTokenSource();
        _ = Task.Delay(ReloadRetryDelay, cancellation.Token).ContinueWith(_ =>
        {
            if (cancellation.IsCancellationRequested || IsClosed)
            {
                return;
            }

            // 回到 UI 线程再触发重载,确保内容发布与 ContentUpdating 都在 UI 线程执行。
            if (_uiDispatcher.CheckAccess())
            {
                _ = ReloadAsync();
            }
            else
            {
                _uiDispatcher.BeginInvoke(() => _ = ReloadAsync());
            }
        }, TaskScheduler.Default);
    }

    private async Task<bool> LoadWindowAsync(int globalLine)
    {
        var source = _documentSource;
        if (source is null)
        {
            return false;
        }

        var (requestVersion, cancellation) = BeginWindowLoad();
        var gateEntered = false;
        try
        {
            await _windowLoadGate.WaitAsync(cancellation.Token).ConfigureAwait(true);
            gateEntered = true;
            if (!IsCurrentWindowLoad(requestVersion, source, cancellation.Token))
            {
                return false;
            }

            var contentVersion = _contentVersion;
            var start = Math.Max(1, globalLine - FileReadOnlyDocumentSource.DefaultWindowLines / 3);
            var window = await source.ReadWindowAsync(
                new DocumentRange(start, FileReadOnlyDocumentSource.DefaultWindowLines), cancellation.Token);
            if (!IsCurrentWindowLoad(requestVersion, source, cancellation.Token)
                || contentVersion != _contentVersion)
            {
                return false;
            }

            WindowStartLine = window.StartLine;
            Content = window.Text;
            // 记录窗口行起点:查找匹配的全局行号→窗口内偏移依赖它。
            _windowLineOffsets = window.LineOffsets;
            _windowTextLines = Math.Max(0, window.LineOffsets.Count - 1);
            // 活动查找下的窗口切换:把全文匹配列表重挂到新窗口坐标(当前匹配落位由视图侧完成)。
            // 必须紧贴窗口坐标发布、在下面那次真实 await 之前完成:窗口起点与匹配子集是同一份
            // 状态的两种投影,分开跨界发布会让观察者(查找计数器、视图装饰、等待窗口的测试)
            // 看到"新窗口起点 + 上一个窗口的局部行号"的半成品,匹配行号短暂错位。
            if (!string.IsNullOrEmpty(SearchText))
            {
                ApplyWindowedMatches();
            }

            var derivedTask = BuildDerivedContentAsync(contentVersion, includeSearch: false);
            var presentationTask = BuildPresentationAsync();
            await Task.WhenAll(derivedTask, presentationTask);
            if (!IsCurrentWindowLoad(requestVersion, source, cancellation.Token)
                || contentVersion != _contentVersion)
            {
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (IsCurrentWindowLoad(requestVersion, source, cancellation.Token))
            {
                Notice = $"无法加载文件窗口：{SingleLineMessage(ex)}";
            }

            return false;
        }
        finally
        {
            if (gateEntered)
            {
                _windowLoadGate.Release();
            }

            FinishWindowLoad(requestVersion, cancellation);
        }
    }

    private async Task BuildPresentationAsync()
    {
        _presentationCancellation?.Cancel();
        _presentationCancellation?.Dispose();
        _presentationCancellation = new CancellationTokenSource();
        var version = ++_presentationVersion;
        try
        {
            var cacheIdentity = CodePresentationCacheIdentity.FromFile(Path, EncodingName, WindowStartLine);
            // 首屏增量发布:部分快照(IsComplete=false)覆盖首屏,让可见文本在 1~2 帧内拿到
            // 最终配色,消除"缺省色 → 正确高亮"闪帧;全文快照在分词完成后发布。
            var syncContext = SynchronizationContext.Current;
            var snapshot = await CodePresentationService.Instance.AnalyzeTokensAsync(
                Content,
                FileType,
                version,
                cacheIdentity,
                onFirstBatch: partial =>
                {
                    if (syncContext is null)
                    {
                        // 无同步上下文的测试环境:内联发布(与其他管线续帖在测试线程外落地的
                        // 既有语义一致)。
                        PublishPresentation(partial, version);
                    }
                    else
                    {
                        syncContext.Post(_ => PublishPresentation(partial, version), null);
                    }
                },
                _presentationCancellation.Token);
            PublishPresentation(snapshot, version);
        }
        catch (OperationCanceledException)
        {
            // Replaced/closed tabs intentionally cancel stale presentation work.
        }
    }

    /// <summary>版本门控发布快照:只有最新一次构建可发布;该版本完整快照已发布后,迟到的
    /// 部分快照丢弃(同步上下文通常已保证先发后至,此为兜底)。</summary>
    private void PublishPresentation(CodePresentationSnapshot snapshot, int version)
    {
        if (version != _presentationVersion || _presentationCancellation is { IsCancellationRequested: true }) return;
        if (!snapshot.IsComplete && Presentation is { DocumentVersion: var current, IsComplete: true } && current == version) return;
        Presentation = snapshot;
    }

    private int _markdownVersion;

    /// <summary>渲染预览不可用的确定性状态(二进制/摘要/窗口阅读/解码失败)显式落到源码模式:
    /// 源码面可见性(ShowSourceSurface)、折叠与语义高亮派生都按模式收敛,旧渲染产物随
    /// OnMarkdownModeChanged 释放。幂等:非 Markdown 或已在源码模式时为空操作。</summary>
    private void EnsureMarkdownSourceMode()
    {
        if (IsMarkdown && MarkdownMode is MarkdownViewMode.Rendered or MarkdownViewMode.SideBySide)
        {
            MarkdownMode = MarkdownViewMode.Source;
        }
    }

    private async Task BuildMarkdownPreviewAsync()
    {
        // 版本化构建:外部重载 / 主题变化 / 切回预览可并发发起;只有最新一次能发布结果,
        // 旧解析(内容已再次变化)不得覆盖新内容。
        var version = ++_markdownVersion;
        _markdownCancellation?.Cancel();
        // 不提前置空渲染结果:重载/主题切换期间旧渲染文档保持显示,新结果就绪后由绑定整体
        // 替换(视图侧 Rebuild 按结果对象身份换文档),消除"渲染视图 → 空白/源码 → 新渲染"
        // 的闪帧。不产生新结果的分支在返回前显式清空,旧 AST 不随标签长期驻留。
        MarkdownRenderNotice = string.Empty;
        IsMarkdownRendering = false;
        OnPropertyChanged(nameof(IsMarkdown));
        if (!IsMarkdown)
        {
            // 语言模式改到非 Markdown:释放渲染产物,视图文档随绑定清空。
            MarkdownRenderResult = null;
            return;
        }
        if (MarkdownMode == MarkdownViewMode.Source)
        {
            // 源码模式不产生渲染产物;切到源码时结果已被 OnMarkdownModeChanged 清空,幂等兜底。
            MarkdownRenderResult = null;
            return;
        }
        if (CapacityTier != ReadOnlyContentTier.Full)
        {
            // 切到源码模式:OnMarkdownModeChanged 负责清空结果。
            MarkdownMode = MarkdownViewMode.Source;
            MarkdownRenderNotice = "大型 Markdown 文件使用源码视图；渲染预览仅对完整加载文件启用。";
            return;
        }

        // Keep a previously rendered document visible while refreshing it. Only a first render
        // (or a render after switching back from source mode) needs a blocking progress surface.
        IsMarkdownRendering = MarkdownRenderResult is null;
        try
        {
            MarkdownRenderResult result;
            var cancellation = _markdownCancellation = new CancellationTokenSource();
            try
            {
                result = await MarkdownPreviewService.Instance.ParseAsync(Content, Path, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // 令牌默认不取消;保留显式分支以防未来接入取消。
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // 仅解析失败才回退源码模式;结果赋值触发的视图侧异常不属于"渲染失败"。
                if (version != _markdownVersion) return;
                MarkdownMode = MarkdownViewMode.Source;
                MarkdownRenderNotice = $"Markdown 渲染失败，已切换到源码视图：{SingleLineMessage(ex)}";
                return;
            }

            if (version != _markdownVersion || cancellation.IsCancellationRequested)
            {
                // This result will never be handed to MarkdownPreviewView, so the view cannot
                // perform its normal ownership handoff/release for the worker-side preheat refs.
                MarkdownPreviewService.ReleasePreheatedPaths(result.PreheatedPaths);
                return;
            }
            MarkdownHeadings = result.Headings;
            // 完成时的模式为准:解析期间用户已切到源码则丢弃 AST(切回预览再解析),
            // 避免后台标签持有解析产物。并排模式与纯渲染一样需要产物。
            if (MarkdownMode is MarkdownViewMode.Rendered or MarkdownViewMode.SideBySide)
            {
                MarkdownRenderResult = result;
            }
            else
            {
                MarkdownPreviewService.ReleasePreheatedPaths(result.PreheatedPaths);
            }
        }
        finally
        {
            if (version == _markdownVersion)
            {
                IsMarkdownRendering = false;
            }
        }
    }

    /// <summary>把异常消息压成单行供状态栏提示:换行符(\r\n/\n/\r)→空格、连续空白折叠、
    /// 超长截断——提示位是单行 TextBlock,多行消息会破坏状态栏布局。</summary>
    internal static string SingleLineMessage(Exception ex)
    {
        var message = ex.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "未知错误";
        }

        var builder = new StringBuilder(message.Length);
        var pendingSpace = false;
        foreach (var ch in message)
        {
            if (ch is '\r' or '\n' or '\t' or ' ')
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(ch);
        }

        var oneLine = builder.ToString().Trim();
        return oneLine.Length <= 120 ? oneLine : oneLine[..120] + "…";
    }

    /// <summary>主题在预览模式运行时变化:FlowDocument 需要整树重渲染,而 AST 已在渲染完成后
    /// 分离,故重新解析(非活动标签不重建——其视图不可见,AST 会失去渲染出口)。</summary>
    private void OnAppThemeChanged(object? sender, AppTheme theme)
    {
        if (IsMarkdown && IsActive && IsLoadFinished
            && MarkdownMode is MarkdownViewMode.Rendered or MarkdownViewMode.SideBySide
            && MarkdownRenderResult is not null)
        {
            _ = BuildMarkdownPreviewAsync();
        }
    }

    // ===== Find state: refresh + navigation commands =====

    /// <summary>E10: 击键防抖——**窗口化(大文件流式)路径**每次输入重置 250ms 尾部去抖,
    /// 停顿后才发起一次流式重扫,不再每键取消旧任务并从头扫描。空文本与非法正则不产生
    /// 扫描,保持立即生效(Escape/清空与“无匹配”错误提示不被去抖拖慢);选项切换
    /// (大小写/全词/正则)是显式用户动作,仍立即刷新。防抖窗口内旧匹配列表保留,
    /// 新结果按差分替换(见 <see cref="ReplaceWindowedMatches"/>)。
    /// 全文(内存 ≤8MB)路径 FindAll 是廉价同步扫描,保持“输入即出匹配”的既有语义,
    /// 不去抖(既有测试对同步重计数有断言)。</summary>
    partial void OnSearchTextChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _searchTextDebounce.Cancel();
            RefreshSearch();
            return;
        }

        if (SearchUseRegex && TextSearchService.GetRegexError(value, SearchCaseSensitive) is not null)
        {
            // 非法正则:立即走错误路径(不扫描、不等待)。
            _searchTextDebounce.Cancel();
            RefreshSearch();
            return;
        }

        if (SearchErrorMessage is not { Length: 0 })
        {
            // 查询重新变合法:先清掉过期的错误提示,结果由(去抖后的)扫描替换。
            SearchErrorMessage = string.Empty;
        }

        if (_documentSource is null)
        {
            RefreshSearch();
            return;
        }

        IsSearching = true;
        _searchTextDebounce.Schedule();
    }

    partial void OnSearchCaseSensitiveChanged(bool value) => RefreshSearch();

    partial void OnSearchWholeWordChanged(bool value) => RefreshSearch();

    partial void OnSearchUseRegexChanged(bool value) => RefreshSearch();

    /// <summary>仅在编辑器当前选区范围内查找(VS Code find-in-selection;窗口化大文件模式忽略)。</summary>
    [ObservableProperty]
    private bool searchInSelection;

    partial void OnSearchInSelectionChanged(bool value) => RefreshSearch();

    /// <summary>编辑器当前是否存在文本选区(驱动"所选范围"复选框显隐;由视图维护)。</summary>
    [ObservableProperty]
    private bool hasTextSelection;

    /// <summary>是否高亮全部匹配(VS Code "全部高亮"开关;关闭时视图隐藏装饰,导航仍可用)。</summary>
    [ObservableProperty]
    private bool showAllHighlights = true;

    /// <summary>由视图注入:返回当前选区在文本中的字符范围(start,end);无选区返回 null。</summary>
    /// <summary>编辑器选中文本 → 查找框播种。由视图在 DataContext 变化时注入
    /// (读 AvalonEdit 选区必须持有编辑器实例,留在视图层),返回 null 表示无可用选区。</summary>
    public Func<(int Start, int End)?>? SelectionRangeProvider { get; set; }

    /// <summary>Blame 装载器(由 EditorArea 注入:解析 Git 根并调 IGitService.GetBlameAsync;
    /// 文件不在仓库内时返回 null)。离开仓库/工作区后由注入方失效。</summary>
    public Func<string, CancellationToken, Task<GitBlame?>>? BlameLoader { get; set; }

    /// <summary>行级 Blame 注解开关(GitLens 式边距)。窗口化/摘要分层不支持——边距按可视行
    /// 对齐,窗口化文档行集随翻页变化,注解会整体错位。</summary>
    [ObservableProperty]
    private bool isBlameVisible;

    /// <summary>Blame 数据装载中(边距显示占位行,避免大仓库几秒静默)。</summary>
    [ObservableProperty]
    private bool isBlameLoading;

    /// <summary>一次性自动启用标记(全局会话偏好由 EditorArea 在创建标签时写入):
    /// 首次内容装载完成后若满足条件(Full 层/非二进制/在仓库内)自动打开 Blame。</summary>
    public bool AutoEnableBlameOnce { get; set; }

    /// <summary>当前 blame 数据(null = 未加载/已清除);视图据此更新边距。</summary>
    [ObservableProperty]
    private GitBlame? blame;

    /// <summary>大文件逃生门可见性(降级到窗口化/摘要且非二进制时才出现)。</summary>
    public bool CanOpenFullAnyway => CapacityTier != ReadOnlyContentTier.Full && !IsBinary;

    partial void OnCapacityTierChanged(ReadOnlyContentTier value) => OnPropertyChanged(nameof(CanOpenFullAnyway));
    partial void OnIsBinaryChanged(bool value) => OnPropertyChanged(nameof(CanOpenFullAnyway));

    /// <summary>大文件逃生门(VS Code "Open anyway" 的只读等价物):把本标签的全文上限临时抬到
    /// 窗口化上限(默认 32MB)并重载——≤32MB 的降级文件恢复全文阅读(含 minimap/折叠),
    /// 更大的文件从摘要进入窗口化阅读。仍有上限:不想一键把任意大文件读进内存。</summary>
    [RelayCommand]
    public void OpenFullAnyway()
    {
        if (IsClosed || IsBinary || CapacityTier == ReadOnlyContentTier.Full)
        {
            return;
        }

        var raised = ContentLimits with { FullSourceBytes = ContentLimits.EffectiveWindowedSourceBytes };
        if (raised.FullSourceBytes <= ContentLimits.FullSourceBytes)
        {
            return;
        }

        ContentLimits = raised;
        Notice = string.Empty;
        IsTruncated = false;
        if (_resourcesReleased || _payloadUnloaded)
        {
            return;
        }

        IsLoading = true;
        _ = RunContentPipelineAsync(initialLoad: false);
    }

    private CancellationTokenSource? _blameCancellation;

    [RelayCommand]
    private async Task ToggleBlameAsync()
    {
        if (IsBlameVisible)
        {
            IsBlameVisible = false;
            ClearBlame();
            return;
        }

        if (IsBinary)
        {
            Notice = "二进制文件没有 Blame 注解。";
            return;
        }

        if (CapacityTier != ReadOnlyContentTier.Full)
        {
            Notice = "窗口化阅读的大文件暂不支持 Blame 注解。";
            return;
        }

        if (BlameLoader is null)
        {
            Notice = "当前文件不在 Git 仓库中。";
            return;
        }

        IsBlameVisible = true;
        await LoadBlameAsync(_contentVersion);
    }

    /// <summary>内容装载完成后的全局偏好自动启用(条件不满足时静默放弃,不产生 Notice 噪声)。</summary>
    private void TryAutoEnableBlame()
    {
        if (!AutoEnableBlameOnce || IsBlameVisible)
        {
            return;
        }

        AutoEnableBlameOnce = false;
        if (IsBinary || CapacityTier != ReadOnlyContentTier.Full || BlameLoader is null)
        {
            return;
        }

        IsBlameVisible = true;
        _ = LoadBlameAsync(_contentVersion);
    }

    /// <summary>按当前内容版本装载 blame;外部变更重载成功后也走这里重取(git blame
    /// 会把工作树未提交行归入 0000 哈希块,内容变化后旧注解会整体错位)。</summary>
    private async Task LoadBlameAsync(int version)
    {
        if (BlameLoader is null || IsClosed)
        {
            return;
        }

        IsBlameLoading = true;
        _blameCancellation?.Cancel();
        var cancellation = _blameCancellation = new CancellationTokenSource();
        try
        {
            var blame = await BlameLoader(Path, cancellation.Token);
            if (version != _contentVersion || IsClosed || cancellation.IsCancellationRequested || !IsBlameVisible)
            {
                return;
            }

            Blame = blame;
            if (blame is { Truncated: true })
            {
                Notice = "Blame 输出超出上限，注解已截断。";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                Notice = $"无法读取 Blame：{ex.Message}";
                ClearBlame();
            }
        }
        finally
        {
            IsBlameLoading = false;
        }
    }

    private void ClearBlame()
    {
        _blameCancellation?.Cancel();
        _blameCancellation = null;
        Blame = null;
        IsBlameLoading = false;
    }

    partial void OnMatchCountChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentMatchDisplay));
        // CanExecute 走方法名(nameof(CanStepMatch)),框架不会自动追踪属性变化;
        // 匹配数变化(含窗口化异步搜索完成)必须显式刷新"上一个/下一个"按钮可用态,
        // 否则按钮停留在初始的禁用外观。
        FindNextCommand.NotifyCanExecuteChanged();
        FindPreviousCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentMatchIndexChanged(int value) => OnPropertyChanged(nameof(CurrentMatchDisplay));

    /// <summary>Re-runs the active search over the loaded content. Capped so huge files never flood
    /// the find overlay; the counter reports the capped count. A malformed regular expression is
    /// surfaced through <see cref="SearchErrorMessage"/> instead of a silent “无匹配”.</summary>
    private void RefreshSearch()
    {
        if (string.IsNullOrEmpty(SearchText))
        {
            CancelWindowedSearch();
            IsSearching = false;
            SearchErrorMessage = string.Empty;
            ClearFindResults();
            return;
        }

        if (SearchUseRegex && TextSearchService.GetRegexError(SearchText, SearchCaseSensitive) is { } error)
        {
            CancelWindowedSearch();
            IsSearching = false;
            ClearFindResults();
            _windowedMatches.Clear();
            SearchErrorMessage = error;
            return;
        }

        SearchErrorMessage = string.Empty;

        if (_documentSource is not null)
        {
            IsSearching = true;
            _ = RefreshWindowedSearchAsync(SearchText);
            return;
        }

        IsSearching = false;
        if (Content.Length == 0)
        {
            ClearFindResults();
            return;
        }

        IReadOnlyList<TextSearchMatch> all;
        if (SearchInSelection && SelectionRangeProvider?.Invoke() is { } range && range.End > range.Start
            && range.End <= Content.Length)
        {
            // 选区查找:在选区窗口内搜索,再把行号重挂回全文坐标。
            var window = Content[range.Start..range.End];
            var baseLine = 0;
            for (var i = 0; i < range.Start && i < Content.Length; i++)
            {
                if (Content[i] == '\n') baseLine++;
            }
            all = _search.FindAll(window, SearchText,
                    new TextSearchOptions(SearchCaseSensitive, SearchWholeWord, SearchUseRegex))
                .Select(match => new TextSearchMatch(match.Length, match.Offset + range.Start, baseLine + match.Line))
                .ToArray();
        }
        else
        {
            all = _search.FindAll(Content, SearchText, new TextSearchOptions(SearchCaseSensitive, SearchWholeWord, SearchUseRegex));
        }

        SearchMatches.ReplaceRange(all.Take(MaxSearchMatches));

        MatchCount = SearchMatches.Count;
        CurrentMatchIndex = 0;
        CurrentMatchViewIndex = MatchCount > 0 ? 0 : -1;
    }

    private void ClearFindResults()
    {
        IsSearching = false;
        SearchMatches.ReplaceRange([]);
        MatchCount = 0;
        CurrentMatchIndex = 0;
        CurrentMatchViewIndex = -1;
    }

    private void CancelWindowedSearch()
    {
        Interlocked.Increment(ref _searchVersion);
        _searchCancellation?.Cancel();
    }

    private async Task RefreshWindowedSearchAsync(string query)
    {
        var source = _documentSource;
        if (source is null)
        {
            IsSearching = false;
            return;
        }
        // 版本令牌必须原子自增:快速连续切换选项会并发派发多个流式搜索,
        // 非原子的 ++ 会丢失更新,让过期的旧搜索"获胜"并写回陈旧结果。
        var version = Interlocked.Increment(ref _searchVersion);
        // 只 Cancel 不 Dispose:旧搜索任务可能仍在引用其令牌(取消传播),Dispose 留给 GC。
        _searchCancellation?.Cancel();
        var cancellation = _searchCancellation = new CancellationTokenSource();
        var matches = new List<DocumentSearchMatch>();
        try
        {
            var options = new TextSearchOptions(SearchCaseSensitive, SearchWholeWord, SearchUseRegex);
            await foreach (var match in source.SearchAsync(query, MaxSearchMatches, options, null, cancellation.Token)) matches.Add(match);
        }
        catch (OperationCanceledException)
        {
            if (version == _searchVersion)
            {
                IsSearching = false;
            }
            return;
        }
        catch (Exception ex)
        {
            if (version == _searchVersion
                && query == SearchText
                && ReferenceEquals(source, _documentSource)
                && !IsClosed
                && !_resourcesReleased)
            {
                SearchErrorMessage = SingleLineMessage(ex);
                IsSearching = false;
            }

            return;
        }

        if (version != _searchVersion || query != SearchText
            || !ReferenceEquals(source, _documentSource)
            || cancellation.IsCancellationRequested) return;
        ReplaceWindowedMatches(matches);
        MatchCount = matches.Count;
        CurrentMatchIndex = 0;
        ApplyWindowedMatches();
        IsSearching = false;
    }

    /// <summary>E10: 权威窗口化匹配列表的增量替换。新结果里存活的匹配保留上一轮的实例
    /// (稳定引用,视图可按引用做段差量、只换变化的段),过期的直接跳过(相当于移除),
    /// 新增的追加——不再 Clear+整表重挂。严格细化查询(字面前缀 + 更长)的结果是上一轮
    /// 按位置的子集,因此只剩"移除"一侧,旧列表在扫描期间一直保留显示。</summary>
    private void ReplaceWindowedMatches(IReadOnlyList<DocumentSearchMatch> incoming)
    {
        var previous = _windowedMatches;
        if (previous.Count == 0)
        {
            _windowedMatches = new List<DocumentSearchMatch>(incoming);
            return;
        }

        if (previous.Count == incoming.Count)
        {
            var unchanged = true;
            for (var i = 0; i < previous.Count; i++)
            {
                if (previous[i] != incoming[i])
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
            {
                return;
            }
        }

        var previousByPosition = new Dictionary<(int Line, int Column), int>(previous.Count);
        for (var i = 0; i < previous.Count; i++)
        {
            previousByPosition[(previous[i].Line, previous[i].Column)] = i;
        }

        var kept = new bool[previous.Count];
        var merged = new List<DocumentSearchMatch>(incoming.Count);
        foreach (var match in incoming)
        {
            // 位置命中且值相等(含长度/预览)才复用旧实例;细化后同一位置长度变化视为
            // "变化的段",用新实例替换。
            if (previousByPosition.TryGetValue((match.Line, match.Column), out var index)
                && !kept[index]
                && previous[index] == match)
            {
                kept[index] = true;
                merged.Add(previous[index]);
            }
            else
            {
                merged.Add(match);
            }
        }

        _windowedMatches = merged;
    }

    /// <summary>Remaps the authoritative windowed matches onto the currently loaded window: only
    /// matches inside the window enter <see cref="SearchMatches"/> (window-local line + offset
    /// resolved from the window text); <see cref="MatchCount"/> keeps the full-file count so the
    /// counter stays global while the view highlights only what is displayed. E10: 子集按差分
    /// 挂回(见 <see cref="ReplaceSearchMatchesIncremental"/>)——存活的匹配保留实例,过期移除、
    /// 新增追加,不再整表 Clear+重挂。</summary>
    private void ApplyWindowedMatches()
    {
        if (_documentSource is null || _windowedMatches.Count == 0)
        {
            ClearFindResults();
            return;
        }

        var windowEnd = WindowStartLine + _windowTextLines; // exclusive
        var subset = new List<TextSearchMatch>(_windowedMatches.Count);
        var viewIndex = -1;
        for (var i = 0; i < _windowedMatches.Count; i++)
        {
            var match = _windowedMatches[i];
            if (match.Line < WindowStartLine || match.Line >= windowEnd) continue;
            var localLine = match.Line - WindowStartLine + 1;
            // LineOffsets[k] 记录的是分隔线 k 与 k+1 的换行符位置,行起点 = 换行符位置 + 1。
            var lineStart = localLine == 1 ? 0 : _windowLineOffsets[localLine - 1] + 1;
            subset.Add(new TextSearchMatch(match.Length, lineStart + (match.Column - 1), localLine));
            if (i == CurrentMatchIndex) viewIndex = subset.Count - 1;
        }

        ReplaceSearchMatchesIncremental(subset);

        MatchCount = _windowedMatches.Count;
        CurrentMatchViewIndex = viewIndex;
    }

    /// <summary>E10: <see cref="SearchMatches"/> 的增量替换(段集合差分语义)。目标子集中与当前
    /// 集合同位置且值相等的匹配复用既有实例——细化/重扫后未变的段保持引用稳定,视图按引用
    /// 差量即可只换变化的段;过期的移除、新增的追加。内容相同则零通知;有变化时一次合并
    /// Reset,视图侧保持单次重挂(避免逐条通知触发多次全量重应用)。</summary>
    private void ReplaceSearchMatchesIncremental(IReadOnlyList<TextSearchMatch> incoming)
    {
        var current = SearchMatches;
        if (current.Count == 0)
        {
            if (incoming.Count > 0)
            {
                current.AddRange(incoming);
            }
            return;
        }

        var unchanged = incoming.Count == current.Count;
        if (unchanged)
        {
            for (var i = 0; i < current.Count; i++)
            {
                if (current[i] != incoming[i])
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
        {
            return;
        }

        var currentByPosition = new Dictionary<(int Line, int Offset), int>(current.Count);
        for (var i = 0; i < current.Count; i++)
        {
            currentByPosition[(current[i].Line, current[i].Offset)] = i;
        }

        var kept = new bool[current.Count];
        var resolved = new List<TextSearchMatch>(incoming.Count);
        foreach (var match in incoming)
        {
            if (currentByPosition.TryGetValue((match.Line, match.Offset), out var index)
                && !kept[index]
                && current[index] == match)
            {
                kept[index] = true;
                resolved.Add(current[index]);
            }
            else
            {
                resolved.Add(match);
            }
        }

        current.ReplaceRange(resolved);
    }

    private bool IsLineInCurrentWindow(int globalLine) =>
        _documentSource is not null && globalLine >= WindowStartLine && globalLine < WindowStartLine + _windowTextLines;

    /// <summary>查找框聚焦请求序号:每次 <see cref="OpenFindBarCommand"/> 递增(含已打开时),
    /// 视图据此重新聚焦输入框——IsFindBarOpen 为 true 时 ObservableProperty 不再通知。</summary>
    [ObservableProperty]
    private int findFocusRequest;

    /// <summary>跳转行输入框聚焦请求序号(语义同上)。</summary>
    [ObservableProperty]
    private int goToLineFocusRequest;

    [RelayCommand]
    private void OpenFindBar()
    {
        // 用编辑器选中文本播种查找框(VS Code 语义):选区坐标由视图注入的
        // SelectionRangeProvider 提供(按当前显示文档的 Content 坐标);无选区时保持现有关键字。
        if (SelectionRangeProvider?.Invoke() is { } range && range.End > range.Start
            && range.Start >= 0 && range.End <= Content.Length)
        {
            SearchText = Content[range.Start..range.End];
        }

        IsFindBarOpen = true;
        FindFocusRequest++;
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _searchTextDebounce.Cancel(); // E10: Escape 立即清空,挂起的去抖扫描不得再跑
        IsSearching = false;
        SearchText = string.Empty;
        IsFindBarOpen = false;
        SearchMatches.ReplaceRange([]);
        MatchCount = 0;
        CurrentMatchIndex = 0;
        SearchErrorMessage = string.Empty;
        CurrentMatchViewIndex = -1;
        _windowedMatches.Clear();
        CancelWindowedSearch(); // 中止进行中的窗口化搜索 I/O
    }

    /// <summary>全部高亮开关(VS Code toggle find highlight):关闭时视图隐藏文档内匹配装饰,
    /// 导航计数器与跳转仍可用。</summary>
    [RelayCommand]
    private void ToggleHighlightAll() => ShowAllHighlights = !ShowAllHighlights;

    [RelayCommand(CanExecute = nameof(CanStepMatch))]
    private void FindNext() => StepMatch(+1);

    [RelayCommand(CanExecute = nameof(CanStepMatch))]
    private void FindPrevious() => StepMatch(-1);

    private bool CanStepMatch() => MatchCount > 0;

    private void StepMatch(int offset)
    {
        if (MatchCount == 0)
        {
            return;
        }

        var next = (CurrentMatchIndex + offset + MatchCount) % MatchCount;
        CurrentMatchIndex = next;
        var previousViewIndex = CurrentMatchViewIndex;

        if (_documentSource is not null && _windowedMatches.Count > 0)
        {
            // 窗口化模式:目标匹配落在当前窗口外时先加载其所在窗口;
            // LoadWindowAsync 载入新窗口后会重建窗口内匹配子集并落位。
            var target = _windowedMatches[next];
            if (IsLineInCurrentWindow(target.Line))
            {
                ApplyWindowedMatches();
            }
            else
            {
                CurrentMatchViewIndex = -1;
                _ = LoadWindowAsync(target.Line);
            }
        }
        else
        {
            CurrentMatchViewIndex = next;
        }
        // Navigation is an action even when the only match keeps the same index.
        // Re-publish it so Enter/F3 can return to a match after the user scrolls away.
        if (CurrentMatchViewIndex >= 0 && CurrentMatchViewIndex == previousViewIndex)
            OnPropertyChanged(nameof(CurrentMatchViewIndex));
    }

    [RelayCommand]
    private void ToggleWordWrap() => WordWrap = !WordWrap;

    [RelayCommand]
    private void ToggleShowMinimap() => ShowMinimap = !ShowMinimap;

    /// <summary>Frees the decoded text, search matches, outline and the Markdown parse artifacts
    /// (Markdig AST / render result / heading list) so a closed tab stops holding the managed heap.
    /// 阅读状态(锚点/行号/偏移)在 <c>MarkTabClosed</c> 中先于本方法持久化。</summary>
    public override void ReleaseResources()
    {
        if (_resourcesReleased)
        {
            return;
        }

        _resourcesReleased = true;
        ThemeEvents.ThemeChanged -= OnAppThemeChanged;
        // 使任何在途的加载/重载管线立刻失效(它们检查 version != _contentVersion 即放弃发布),
        // 关闭后的标签不得再被外部变更或重试写回内容。
        _contentVersion++;
        _presentationVersion++;
        _derivedVersion++;
        _markdownVersion++;
        _foldsVersion++;
        _searchVersion++;
        CancelPendingWindowLoad();
        CancelPendingReloadRetry();
        ClearBlame();
        FileChangeNotice = string.Empty;
        // 渲染产物整体释放:绑定驱动视图清空 FlowDocument 并逐处释放图片引用;
        // Markdig AST 随结果对象释放(若已分离则为空)。
        MarkdownRenderResult = null;
        MarkdownHeadings = [];
        MarkdownAnchor = string.Empty;
        MarkdownHeadingLine = 0;
        MarkdownVerticalOffset = 0;
        MarkdownRenderNotice = string.Empty;

        _searchTextDebounce.Dispose(); // E10: 关闭标签不得再触发挂起的查找扫描
        CancelAndDispose(ref _presentationCancellation);
        CancelAndDispose(ref _derivedCancellation);
        CancelAndDispose(ref _markdownCancellation);
        if (_documentSource is not null)
        {
            var stale = _documentSource;
            _documentSource = null;
            _ = DisposeDocumentSourceAsync(stale);
        }
        Content = string.Empty;
        Presentation = CodePresentationSnapshot.Empty();
        Notice = string.Empty;
        SearchMatches.ReplaceRange([]);
        _windowedMatches.Clear();
        _windowTextLines = 0;
        _windowLineOffsets = [0];
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        FilteredOutlineEntries.ReplaceRange([]);
        OutlineEntries.ReplaceRange([]);
        FoldSections = [];
        SymbolRoots.ReplaceRange([]);
        FilteredSymbolRoots.ReplaceRange([]);
        UnwireSymbolNodes();
        _symbolDocument = CodeSymbolDocument.Empty;
        _collapsedSymbolIds.Clear();
        ViewState = null;
        _payloadUnloaded = true;
        _loadTask = null;
    }

    // ===== Go to line / column =====

    [RelayCommand]
    private void OpenGoToLineBar()
    {
        GoToLineInput = string.Empty;
        GoToLineErrorMessage = string.Empty;
        IsGoToLineBarOpen = true;
        GoToLineFocusRequest++;
    }

    /// <summary>Parses "line" or "line:column", clamps the line to the document and raises the jump.</summary>
    [RelayCommand]
    private void GoToLine()
    {
        var input = GoToLineInput?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            GoToLineErrorMessage = "请输入行号，例如 42 或 42:16。";
            return;
        }

        var parts = input.Split(':', 2);
        if (!int.TryParse(parts[0], out var line) || line < 1)
        {
            GoToLineErrorMessage = "行号必须是大于 0 的整数。";
            return;
        }

        // Search results use the same compact form as the go-to-line bar (line:column).
        // Keep line-only input backward compatible, but preserve a valid column so the
        // view can place the caret at the actual match instead of only selecting the line.
        int? column = null;
        if (parts.Length == 2 && int.TryParse(parts[1], out var parsedColumn) && parsedColumn >= 1)
        {
            column = parsedColumn;
        }
        else if (parts.Length == 2)
        {
            GoToLineErrorMessage = "列号必须是大于 0 的整数。";
            return;
        }

        var effectiveLine = Math.Min(line, Math.Max(LineCount, 1));
        GoToLineErrorMessage = string.Empty;
        IsGoToLineBarOpen = false;
        if (_documentSource is null)
        {
            if (column is int effectiveColumn)
            {
                GoToPositionRequested?.Invoke(this, (effectiveLine, effectiveColumn));
            }
            else
            {
                GoToLineRequested?.Invoke(this, effectiveLine);
            }
        }
        else
        {
            _ = NavigateWindowedAsync(effectiveLine, column);
        }
    }

    private async Task NavigateWindowedAsync(int globalLine, int? column = null)
    {
        if (CapacityTier == ReadOnlyContentTier.Summary) CapacityTier = ReadOnlyContentTier.Windowed;
        if (!await LoadWindowAsync(globalLine))
        {
            return;
        }

        Notice = $"窗口分析 · 全局第 {WindowStartLine}–{Math.Min(LineCount, WindowStartLine + FileReadOnlyDocumentSource.DefaultWindowLines - 1)} 行";
        var localLine = Math.Clamp(globalLine - WindowStartLine + 1, 1, Math.Max(1, Content.Count(character => character == '\n') + 1));
        if (column is int effectiveColumn)
        {
            GoToPositionRequested?.Invoke(this, (localLine, effectiveColumn));
        }
        else
        {
            GoToLineRequested?.Invoke(this, localLine);
        }
    }

    public async Task<bool> LoadAdjacentWindowAsync(bool next)
    {
        if (_documentSource is null || CapacityTier == ReadOnlyContentTier.Full) return false;
        var target = next
            ? WindowStartLine + FileReadOnlyDocumentSource.DefaultWindowLines
            : Math.Max(1, WindowStartLine - FileReadOnlyDocumentSource.DefaultWindowLines);
        if (target == WindowStartLine || target > LineCount) return false;
        if (!await LoadWindowAsync(target))
        {
            return false;
        }

        Notice = $"窗口分析 · 全局第 {WindowStartLine}–{Math.Min(LineCount, WindowStartLine + FileReadOnlyDocumentSource.DefaultWindowLines - 1)} 行";
        return true;
    }
}
