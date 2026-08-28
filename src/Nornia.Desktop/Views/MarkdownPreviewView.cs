using Nornia.Desktop.Markdown;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace Nornia.Desktop.Views;

/// <summary>Markdown 预览的逻辑阅读位置:当前标题锚点 + 标题行号 + 垂直滚动偏移。</summary>
public sealed record MarkdownLogicalPosition(string? Anchor, int Line, double VerticalOffset);

/// <summary>WPF-native Markdown surface for a read-only file preview. Exposes the current heading
/// (throttled from the scroll position), anchor/line jumps with feedback-loop suppression, and the
/// logical reading position consumed by the tab's unified source/preview position state.</summary>
public sealed class MarkdownPreviewView : FlowDocumentScrollViewer
{
    /// <summary>视口顶部附近(0–24px)的标题视为"当前标题"(滚动同步的判定边距)。</summary>
    private const double CurrentHeadingTopMargin = 24;

    public static readonly DependencyProperty RenderResultProperty = DependencyProperty.Register(
        nameof(RenderResult), typeof(MarkdownRenderResult), typeof(MarkdownPreviewView),
        new FrameworkPropertyMetadata(null, OnRenderResultChanged));

    public MarkdownRenderResult? RenderResult
    {
        get => (MarkdownRenderResult?)GetValue(RenderResultProperty);
        set => SetValue(RenderResultProperty, value);
    }

    public event EventHandler<MarkdownLinkRequest>? LinkRequested;

    public event ScrollChangedEventHandler? ScrollChanged;

    /// <summary>当前标题(视口顶部附近最近的标题);无标题或文档未布局时为 null。</summary>
    public MarkdownHeading? CurrentHeading { get; private set; }

    /// <summary>产出当前 FlowDocument 的结果(标签接线时序兜底:绑定先于视图事件完成渲染时
    /// 据此补做 AST 分离;测试断言用)。</summary>
    internal MarkdownRenderResult? RenderedResult => _renderedResult;

    /// <summary>当前标题变化事件(滚动经 Dispatcher 节流;程序化跳转不产生重复同步)。</summary>
    public event EventHandler<MarkdownHeading?>? CurrentHeadingChanged;

    private double _pendingVerticalOffset;
    private ScrollViewer? _scrollHost;
    private MarkdownHeading? _pendingJumpHeading;
    private bool _headingSyncPending;
    private bool _suppressHeadingSync;
    private bool _rebuildQueued;
    private MarkdownWpfRenderer.MarkdownFlowDocumentRenderSession? _renderSession;
    private int _renderGeneration;
    // 标题段落查找缓存(名称 → 段落):ComputeCurrentHeading 每个滚动批次对每个标题各做一次
    // 顶层块遍历(O(标题×块)),缓存后降为 O(1) 查找。增量渲染期只对每批新增块增量合并
    // (M4,UpdateHeadingParagraphs),不再每批全文档重建。
    private Dictionary<string, Paragraph>? _headingParagraphs;
    // M10: 排队等待文档布局后应用的"存储值"(≤1 = 垂直进度,>1 = 旧版像素偏移)。
    private double? _pendingRestoreStored;

    /// <summary>M4 测试断言:本视图实例触发全文档重建标题查找表的次数(增量渲染路径应为 0;
    /// 仅短文档一次性渲染路径保留单次全量构建)。实例级计数,并行测试互不干扰。</summary>
    internal int HeadingMapFullRebuildCount { get; private set; }

    // Keep the first paint small enough for input/layout to get a turn, then use larger batches
    // so long documents finish quickly after the preview is already usable.
    private const int InitialRenderBlockBudget = 32;
    private const int IncrementalRenderBlockBudget = 64;

    public double VerticalOffset
    {
        get => _scrollHost?.VerticalOffset ?? _pendingVerticalOffset;
        set
        {
            _pendingVerticalOffset = Math.Max(0, value);
            _scrollHost?.ScrollToVerticalOffset(_pendingVerticalOffset);
        }
    }

    /// <summary>FlowDocument 首次渲染完成(标签据此分离 Markdig AST,让大 AST 不随预览长期驻留)。</summary>
    public event EventHandler? RenderCompleted;

    /// <summary>当前文档引用的图片路径(与渲染器逐处 Acquire 对称);文档替换/清空时统一释放。</summary>
    private IReadOnlyList<string>? _acquiredImagePaths;

    /// <summary>当前文档对应解析的预热引用路径(M6);渲染器首处嵌入渲染时逐张"消费"
    /// (交接给嵌入引用),文档替换/清空/渲染失败时统一释放保证对称。</summary>
    private IReadOnlyList<string>? _acquiredPreheatPaths;

    /// <summary>产出当前 FlowDocument 的结果对象;共享视图换标签时据此区分"同一结果重绑"
    /// 与"必须换文档",绝不让旧标签的文档残留在新标签下。</summary>
    private MarkdownRenderResult? _renderedResult;

    /// <summary>视图生命周期说明:图片引用只随 <see cref="RenderResult"/> 变化在
    /// <see cref="Rebuild"/> 中释放(标签切换/关闭必然触发绑定更新)。不在 Unloaded 释放——
    /// 视觉树瞬时卸载不得让仍可见的 FlowDocument 失去位图。</summary>
    public MarkdownPreviewView()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // The rendered document is still an editor surface from the user's point of view:
        // Ctrl+F/F3/Ctrl+G/Esc are owned by FilePreviewView and need a reliable focus route
        // when the source editor is collapsed.
        Focusable = true;
        IsTabStop = true;
        PreviewMouseLeftButtonDown += (_, _) => Focus();
        // .NET 10 的 ScrollViewer 滚轮处理是每事件固定 48px(ScrollViewer._mouseWheelDelta
        // 常量,只看 delta 符号、忽略幅值),FlowDocumentScrollViewer 基类内建处理则是
        // 每事件一行——两者都让预览滚轮"体感不灵敏"。这里在隧道路径(先于内层滚动宿主的
        // 冒泡处理)接管:按硬件 delta 像素滚动滚动宿主(标准滚轮 120/tick,高精度鼠标
        // 按细粒度 delta 比例累积),与代码编辑器及常见 Markdown 预览的滚动体感一致。
        PreviewMouseWheel += OnPreviewMouseWheel;
        Loaded += (_, _) => HookScrollHost();
    }

    /// <summary>按锚点跳转;标题不存在或文档尚未就绪时安全忽略。</summary>
    public void ScrollToAnchor(string anchor)
    {
        ScrollToAnchorSafe(anchor);
    }

    /// <summary>按锚点跳转,并报告跳转目标是否存在(false = 锚点失效,调用方回退到行号/偏移)。</summary>
    public bool ScrollToAnchorSafe(string anchor)
    {
        var requested = anchor.TrimStart('#');
        var heading = RenderResult?.Headings.FirstOrDefault(item =>
            item.Anchor.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (heading is null || FindHeadingParagraph(heading.DocumentName) is null)
        {
            return false;
        }

        RequestJump(heading);
        return true;
    }

    /// <summary>按源码行跳转:落到不高于该行、行号最大的标题;无匹配标题时安全忽略。</summary>
    public void ScrollToLine(int line) => ScrollToLineSafe(line);

    /// <summary>按源码行跳转,并报告是否存在可跳转标题(false = 标题在新文档中失效,
    /// 调用方回退到垂直进度/偏移恢复,M10)。</summary>
    public bool ScrollToLineSafe(int line)
    {
        var heading = RenderResult?.Headings
            .Where(item => item.Line <= line)
            .OrderByDescending(item => item.Line)
            .FirstOrDefault();
        if (heading is null || FindHeadingParagraph(heading.DocumentName) is null)
        {
            return false;
        }

        RequestJump(heading);
        return true;
    }

    /// <summary>当前逻辑阅读位置(供标签统一位置状态捕获)。</summary>
    public MarkdownLogicalPosition GetLogicalPosition() =>
        new(CurrentHeading?.Anchor, CurrentHeading?.Line ?? 0, VerticalOffset);

    /// <summary>M10: 排队一个"存储值"等待文档布局就绪后应用。存储值语义:
    /// ≤ 1 = 保存时的垂直进度(offset/extent,新格式);&gt; 1 = 旧版本持久化的原始
    /// 像素偏移。文档未布局时排队,布局完成(<see cref="FinishRenderLifecycle"/>)后按新
    /// extent 映射为像素偏移再滚动。</summary>
    public void QueueVerticalRestore(double stored)
    {
        _pendingRestoreStored = Math.Max(0, stored);
        if (_scrollHost is { ExtentHeight: > 0 })
        {
            ApplyPendingRestore();
        }
    }

    /// <summary>文档 extent(M10 进度映射用;未布局为 0)。</summary>
    internal double ExtentHeight => _scrollHost?.ExtentHeight ?? 0;

    /// <summary>视口高度(M10 进度映射用)。</summary>
    internal double ViewportHeight => _scrollHost?.ViewportHeight ?? 0;

    private void ApplyPendingRestore()
    {
        if (_pendingRestoreStored is not { } stored)
        {
            return;
        }

        _pendingRestoreStored = null;
        var pixels = RestoreVerticalOffset(stored, _scrollHost?.ExtentHeight ?? 0, _scrollHost?.ViewportHeight ?? 0);
        _pendingVerticalOffset = pixels;
        _scrollHost?.ScrollToVerticalOffset(pixels);
    }

    /// <summary>M10 纯函数:把存储的阅读位置映射为新文档的像素偏移。
    /// 存储值 ≤ 1 视为垂直进度(保存时 offset/extent):映射为 progress×新 extent;
    /// &gt; 1 视为旧版像素偏移:直接钳制到新的可滚动范围(保持旧行为)。
    /// 结果恒在 [0, extent−viewport] 内。</summary>
    internal static double RestoreVerticalOffset(double stored, double extent, double viewport)
    {
        var maxScroll = Math.Max(0, extent - viewport);
        if (stored <= 0)
        {
            return 0;
        }

        if (stored > 1.0)
        {
            return Math.Min(stored, maxScroll); // 旧版像素偏移:钳制
        }

        return Math.Min(stored * extent, maxScroll); // 垂直进度:按比例映射
    }

    /// <summary>M10 纯函数:把像素偏移换算为持久化的垂直进度(offset/extent,钳制到 [0,1])。
    /// 顶部附近的亚像素偏移(≤ 1)归零——避免与"进度"语义的 (0,1] 区间混淆,
    /// 文档顶部保持为 0。</summary>
    internal static double ToVerticalProgress(double offset, double extent)
    {
        if (extent <= 0 || offset <= 1.0)
        {
            return 0;
        }

        return Math.Clamp(offset / extent, 0, 1);
    }

    private void RequestJump(MarkdownHeading? heading)
    {
        if (heading is null || FindHeadingParagraph(heading.DocumentName) is null)
        {
            return;
        }

        _pendingJumpHeading = heading;
        // 延迟到 Background 优先级:渲染重建排在前面,未布局时还能拿到一次布局后的机会。
        Dispatcher.BeginInvoke(new Action(ApplyPendingJump), DispatcherPriority.Background);
    }

    private void ApplyPendingJump()
    {
        if (_pendingJumpHeading is not { } heading)
        {
            return;
        }

        if (FindHeadingParagraph(heading.DocumentName) is not { } paragraph)
        {
            // 目标标题已不存在(文档已换成不同内容):放弃跳转,解除对滚动重算的抑制。
            _pendingJumpHeading = null;
            SetCurrentHeading(ComputeCurrentHeading());
            return;
        }

        _pendingJumpHeading = null;
        _suppressHeadingSync = true;
        paragraph.BringIntoView(); // 未布局时为空操作 —— 安全忽略,调用方回退到保存的偏移
        // 跳转产生的滚动事件被抑制;落定后直接写回目标标题,避免重复同步回路。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _suppressHeadingSync = false;
            SetCurrentHeading(heading);
        }), DispatcherPriority.Background);
    }

    private static void OnRenderResultChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownPreviewView)d).QueueRebuild();

    /// <summary>Coalesces rapid result changes (reload, mode switch, theme update) before creating
    /// the expensive FlowDocument tree. The latest binding value is read when the UI reaches the
    /// background slot, so an obsolete intermediate Markdown tree is never materialized.</summary>
    private void QueueRebuild()
    {
        if (_rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        }), DispatcherPriority.Background);
    }

    /// <summary>按结果重建 FlowDocument。规则:结果 null → 清空文档并释放图片引用(标签切到
    /// 源码模式/关闭);当前文档即该结果的渲染 → 幂等不变;该结果 AST 已分离(无法重渲染,
    /// 如共享视图切回一个已分离标签)→ 清空旧文档,由标签重新解析;AST 就绪 → 渲染新文档,
    /// 先释放上一份文档的图片引用再取新引用,渲染失败时回滚本次部分取得的引用。</summary>
    private void Rebuild()
    {
        var result = RenderResult;
        if (result is null)
        {
            CancelIncrementalRender();
            if (Document is not null || _acquiredImagePaths is not null)
            {
                ReleaseImagePaths(ref _acquiredImagePaths);
                ReleaseImagePaths(ref _acquiredPreheatPaths);
                Document = null;
                _headingParagraphs = null;
                _renderedResult = null;
                _pendingRestoreStored = null;
                SetCurrentHeading(null);
            }
            return;
        }

        if (ReferenceEquals(result, _renderedResult))
        {
            return; // 当前文档正是该结果的渲染,无需重建
        }

        if (result.Document is null)
        {
            // 该结果已分离且不是当前文档的出处:必须清空,否则共享视图会把上一个标签的
            // 内容残留在本标签下;标签侧(OnDataContextChanged)会重新解析产生新结果。
            CancelIncrementalRender();
            ReleaseImagePaths(ref _acquiredImagePaths);
            ReleaseImagePaths(ref _acquiredPreheatPaths);
            Document = null;
            _headingParagraphs = null;
            _pendingRestoreStored = null;
            _renderedResult = result;
            SetCurrentHeading(null);
            return;
        }

        // Preserve the simple lifecycle for ordinary documents. Only long Markdown documents
        // need the incremental session; this keeps focus/shortcut and test-host behavior for the
        // common short-preview path identical to the established FlowDocument path.
        // M5: 渲染预算也参与门控——块数少但公式/图片密集的文档同样改走增量路径,
        // 首帧只做首屏量;8MB 字节门控(Windowed vs Full)不受影响。
        if (result.Document.Count <= InitialRenderBlockBudget
            && !MarkdownRenderBudget.IsOverInitialBudget(result.Budget.FormulaCount, result.Budget.ImageCount))
        {
            RebuildShortDocument(result);
            return;
        }

        MarkdownWpfRenderer.MarkdownFlowDocumentRenderSession? session = null;
        try
        {
            session = MarkdownWpfRenderer.BeginRender(result, request => LinkRequested?.Invoke(this, request));
            // M5: 初始批次按渲染预算决定(重元素文档 = 16 块首屏,常规 = 32)。
            session.AppendBatch(MarkdownRenderBudget.DecideInitialBatch(
                result.Document.Count, result.Budget.FormulaCount, result.Budget.ImageCount));
        }
        catch
        {
            // 渲染中途失败:回滚渲染器已按 ImagePaths 部分取得的引用,保留旧文档。
            session?.Dispose();
            ReleaseImagePaths(result.ImagePaths);
            ReleaseImagePaths(result.PreheatedPaths); // M6: 预热引用随渲染失败整体释放
            throw;
        }

        CancelIncrementalRender();
        ReleaseImagePaths(ref _acquiredImagePaths);
        ReleaseImagePaths(ref _acquiredPreheatPaths);
        Document = session.Document;
        // M4: 标题查找表从首批新增块增量构建(渲染器保证标题段落位于文档顶层),
        // 后续批次只合并各自新增块,不再全文档扫描。
        _headingParagraphs = null;
        UpdateHeadingParagraphs(session.LastAddedBlocks);
        _acquiredImagePaths = result.ImagePaths;
        _acquiredPreheatPaths = result.PreheatedPaths;
        _renderedResult = result;
        _renderSession = session;
        var generation = ++_renderGeneration;

        if (session.IsCompleted)
        {
            CompleteRender(session, generation);
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => ContinueIncrementalRender(session, generation)), DispatcherPriority.Background);
    }

    private void RebuildShortDocument(MarkdownRenderResult result)
    {
        FlowDocument rendered;
        try
        {
            rendered = MarkdownWpfRenderer.Render(result, request => LinkRequested?.Invoke(this, request));
        }
        catch
        {
            ReleaseImagePaths(result.ImagePaths);
            ReleaseImagePaths(result.PreheatedPaths); // M6: 预热引用随渲染失败整体释放
            throw;
        }

        CancelIncrementalRender();
        ReleaseImagePaths(ref _acquiredImagePaths);
        ReleaseImagePaths(ref _acquiredPreheatPaths);
        Document = rendered;
        // 短文档一次性渲染:单次全量构建查找表(≤32 块,开销可忽略),计入全量重建计数。
        _headingParagraphs = BuildHeadingParagraphMap(rendered);
        _acquiredImagePaths = result.ImagePaths;
        _acquiredPreheatPaths = result.PreheatedPaths;
        _renderedResult = result;
        FinishRenderLifecycle();
    }

    /// <summary>Appends the next bounded block batch. Rechecking both the generation and the
    /// bound result prevents a delayed dispatcher callback from touching a replaced/closed tab.</summary>
    private void ContinueIncrementalRender(
        MarkdownWpfRenderer.MarkdownFlowDocumentRenderSession session, int generation)
    {
        if (generation != _renderGeneration
            || !ReferenceEquals(session, _renderSession)
            || !ReferenceEquals(RenderResult, _renderedResult))
        {
            session.Dispose();
            return;
        }

        try
        {
            var more = !session.AppendBatch(IncrementalRenderBlockBudget);
            // M4: 文档树随批次增长 —— 标题查找表只对"本批新增块"增量合并
            // (map.Add 每个新增标题),不再每批全文档遍历(O(n²/批) → O(n))。
            UpdateHeadingParagraphs(session.LastAddedBlocks);
            if (more)
            {
                Dispatcher.BeginInvoke(new Action(() => ContinueIncrementalRender(session, generation)), DispatcherPriority.Background);
                return;
            }
        }
        catch
        {
            // Keep the already visible prefix if a later block is malformed. The next result
            // change can retry; the old full-document path also treated rendering errors as a
            // view concern rather than changing the source model.
            _renderSession = null;
            session.Dispose();
            ReleaseImagePaths(ref _acquiredImagePaths);
            ReleaseImagePaths(ref _acquiredPreheatPaths); // M6: 未消费的预热引用随渲染失败释放
            return;
        }

        CompleteRender(session, generation);
    }

    private void CompleteRender(
        MarkdownWpfRenderer.MarkdownFlowDocumentRenderSession session, int generation)
    {
        if (generation != _renderGeneration || !ReferenceEquals(session, _renderSession))
        {
            session.Dispose();
            return;
        }

        _renderSession = null;
        session.Dispose();
        FinishRenderLifecycle();
    }

    private void FinishRenderLifecycle()
    {
        // 收尾(滚动宿主钩子 + 保存偏移 + 标题重算)必须先入队、再触发 RenderCompleted:
        // 后者会让 FilePreviewView 重跑位置恢复并请求跳转;跳转必须排在收尾之后执行,
        // 否则收尾的 ScrollToVerticalOffset 会把跳转滚回保存偏移、标题重算覆盖跳转目标。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            HookScrollHost();
            // M7: 渲染完成后才物化标题位置标记(首屏渲染不为数千标题多付 Visual 布局成本;
            // 必须先于标题重算——重算依赖标记几何)。
            MaterializeHeadingMarkers();
            if (_pendingRestoreStored is not null)
            {
                // M10: 排队的存储值(垂直进度/旧版像素偏移)在文档布局就绪后按新 extent
                // 映射为像素偏移再应用。
                ApplyPendingRestore();
            }
            else
            {
                _scrollHost?.ScrollToVerticalOffset(_pendingVerticalOffset);
            }
            // 有待处理跳转时不重算——新文档顶部的"第一个标题"不得覆盖即将落定的跳转目标
            // (恢复优先级:锚点→行号→偏移,跳转由 ApplyPendingJump 显式写回)。
            if (_pendingJumpHeading is null)
            {
                SetCurrentHeading(ComputeCurrentHeading());
            }
        }), DispatcherPriority.Background);
        RenderCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelIncrementalRender()
    {
        _renderGeneration++;
        _renderSession?.Dispose();
        _renderSession = null;
    }

    /// <summary>按路径列表(含重复,与渲染器逐处 Acquire 对称)释放图片引用;从未加载的路径
    /// (解码失败)在缓存侧是空操作。</summary>
    private static void ReleaseImagePaths(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            MarkdownImageCache.Instance.Release(path);
        }
    }

    private void ReleaseImagePaths(ref IReadOnlyList<string>? paths)
    {
        if (paths is null) return;
        ReleaseImagePaths(paths);
        paths = null;
    }

    private void HookScrollHost()
    {
        if (_scrollHost is not null) return;
        _scrollHost = FindVisualChild<ScrollViewer>(this);
        if (_scrollHost is not null)
        {
            _scrollHost.ScrollChanged += OnHostScrollChanged;
            _scrollHost.ScrollToVerticalOffset(_pendingVerticalOffset);
        }
    }

    /// <summary>滚轮 → 预览内容滚动(隧道路径接管,先于 .NET 10 ScrollViewer 的固定 48px
    /// 步进与基类每行处理):按硬件 delta 做像素滚动,位移与滚轮幅值成比例。Ctrl 组合键
    /// 不接管(保留基类/内层默认行为);宿主未就绪或文档不可垂直滚动时同样不接管。</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            return;
        }

        if (_scrollHost is null)
        {
            HookScrollHost();
        }

        if (_scrollHost is not { } host || host.ScrollableHeight <= 0)
        {
            return;
        }

        host.ScrollToVerticalOffset(host.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>滚动事件经 Dispatcher 节流:同一帧内的多次滚动只触发一次当前标题重算。</summary>
    private void OnHostScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        ScrollChanged?.Invoke(this, e);
        if (_suppressHeadingSync || _pendingJumpHeading is not null || _headingSyncPending)
        {
            return;
        }

        _headingSyncPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _headingSyncPending = false;
            if (_suppressHeadingSync || _pendingJumpHeading is not null)
            {
                return;
            }

            SetCurrentHeading(ComputeCurrentHeading());
        }), DispatcherPriority.Background);
    }

    /// <summary>视口顶部附近最近的标题 = 当前标题。标题位置取自渲染器挂在其段落上的零尺寸
    /// 标记(M7:标记在渲染完成后/首次使用时惰性物化;段落是 ContentElement,自身没有几何
    /// API);没有任何标题时返回 null(不更新活动大纲)。标题 Y 随文档顺序单调递增,
    /// 一旦超出顶部判定边距即可提前退出,无需扫描其余标题。</summary>
    private MarkdownHeading? ComputeCurrentHeading()
    {
        if (RenderResult is null || Document is null)
        {
            return null;
        }

        MarkdownHeading? current = null;
        var bestY = double.NegativeInfinity;
        foreach (var heading in RenderResult.Headings)
        {
            if (FindHeadingParagraph(heading.DocumentName) is not { Tag: FrameworkElement marker } paragraph)
            {
                // 标记尚未物化(增量渲染进行中):跳过本轮同步,与"文档未布局"同语义。
                continue;
            }

            try
            {
                var y = marker.TransformToVisual(this).Transform(new Point(0, 0)).Y;
                if (y <= CurrentHeadingTopMargin)
                {
                    if (y > bestY)
                    {
                        bestY = y;
                        current = heading;
                    }
                }
                else
                {
                    break; // Y 单调递增:此后的标题都在判定边距之外
                }
            }
            catch (InvalidOperationException)
            {
                return null; // 标记尚未进入视觉树(文档未布局):安全忽略
            }
        }

        // 文档顶部兜底:未滚动(或几乎未滚动)时第一个标题即当前标题,
        // 避免首标题落在判定边距之外而"无活动大纲"。
        if (current is null
            && (_scrollHost is null || _scrollHost.VerticalOffset <= 1)
            && RenderResult.Headings.Count > 0)
        {
            current = RenderResult.Headings[0];
        }

        return current;
    }

    private void SetCurrentHeading(MarkdownHeading? heading)
    {
        if (Equals(CurrentHeading, heading))
        {
            return;
        }

        CurrentHeading = heading;
        CurrentHeadingChanged?.Invoke(this, heading);
    }

    /// <summary>按 WPF 安全名称查找标题段落。渲染器以代码方式命名,名称不一定注册进文档
    /// 名称作用域,FindName 不可靠 —— 直接遍历文档块树,确定且零依赖。</summary>
    private Paragraph? FindHeadingParagraph(string documentName)
    {
        if (Document is null)
        {
            return null;
        }

        if (_headingParagraphs is { } map && map.TryGetValue(documentName, out var cached))
        {
            return cached;
        }

        // 兜底:缓存缺失(理论上不应发生)时退回顶层遍历。
        return FindNamedBlock(Document, documentName) as Paragraph;
    }

    /// <summary>一次顶层遍历建立 名称→标题段落 查找表(渲染器保证标题段落均在文档顶层)。
    /// 仅用于短文档一次性渲染路径;增量渲染路径改用 <see cref="UpdateHeadingParagraphs"/>
    /// 逐批增量合并,全程不触发全文档重建。</summary>
    private Dictionary<string, Paragraph>? BuildHeadingParagraphMap(FlowDocument document)
    {
        HeadingMapFullRebuildCount++;
        Dictionary<string, Paragraph>? map = null;
        foreach (var block in document.Blocks)
        {
            if (block is Paragraph { Name: { } name } paragraph)
            {
                (map ??= new Dictionary<string, Paragraph>())[name] = paragraph;
            }
        }

        return map;
    }

    /// <summary>M4: 把本批新增块中的标题段落增量合并进查找表(渲染器保证标题段落均位于
    /// 文档顶层,语义与全量构建逐键一致)。</summary>
    private void UpdateHeadingParagraphs(IEnumerable<Block> addedBlocks)
    {
        foreach (var block in addedBlocks)
        {
            if (block is Paragraph { Name: { } name } paragraph)
            {
                (_headingParagraphs ??= new Dictionary<string, Paragraph>())[name] = paragraph;
            }
        }
    }

    /// <summary>M7: 物化全部标题的位置标记(渲染完成收尾时调用一次;标记创建不在块
    /// 构造期,首屏渲染不为数千标题多付数千个零尺寸 Visual 的布局成本)。</summary>
    private void MaterializeHeadingMarkers()
    {
        if (_headingParagraphs is null)
        {
            return;
        }

        foreach (var paragraph in _headingParagraphs.Values)
        {
            EnsureHeadingMarker(paragraph);
        }
    }

    /// <summary>M7: 惰性创建标题段落的零尺寸位置标记(幂等)。段落是 ContentElement 没有
    /// 几何 API,标记经 TransformToVisual 提供滚动同步所需的视口位置。</summary>
    internal static FrameworkElement EnsureHeadingMarker(Paragraph paragraph)
    {
        if (paragraph.Tag is FrameworkElement existing)
        {
            return existing;
        }

        var marker = new System.Windows.Shapes.Rectangle
        {
            Width = 0,
            Height = 0,
            Opacity = 0,
            IsHitTestVisible = false,
            Focusable = false,
        };
        paragraph.Inlines.Add(new InlineUIContainer(marker));
        paragraph.Tag = marker;
        return marker;
    }

    /// <summary>标题段落查找表(测试断言增量合并结果用)。</summary>
    internal IReadOnlyDictionary<string, Paragraph>? HeadingParagraphsForTest => _headingParagraphs;

    /// <summary>渲染器把引文/列表扁平化为顶层段落,标题段落一律位于文档顶层 —— 直接顶层查找。
    /// (代码方式命名的段落不保证注册进文档名称作用域,FindName 不可靠,故按 Name 遍历匹配。)</summary>
    internal static Block? FindNamedBlock(FlowDocument document, string name)
    {
        foreach (var block in document.Blocks)
        {
            if (block is Paragraph { Name: var n } && n == name)
            {
                return block;
            }
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
