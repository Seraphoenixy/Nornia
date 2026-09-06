using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Nornia.Desktop.Code;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Serilog;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Nornia.Desktop.Views;

/// <summary>
/// Read-only code renderer: maps ViewModel state (text, highlighting definition, display options,
/// search matches and go-to-line requests) onto an AvalonEdit <see cref="TextEditor"/>. Holds no
/// business logic — the surrounding view model drives everything through the exposed properties and
/// the view-only adapters below (match background renderer + transient line emphasis).
/// </summary>
public partial class CodeDocumentView : System.Windows.Controls.UserControl
{
    /// <summary>等宽字体家族,统一取自 App.xaml 的 MonoFontFamily 令牌(兜底走 Code/ViewFonts)。</summary>
    internal static string MonoFontFamilyValue =>
        (Application.Current?.TryFindResource("MonoFontFamily") as FontFamily)?.Source
        ?? Nornia.Desktop.Code.ViewFonts.MonoFamily;

    // Document refreshes invalidate AvalonEdit visual lines before the next layout pass. All
    // custom renderers must tolerate that transient state instead of reading VisualLines and
    // surfacing VisualLinesInvalidException on the UI thread.
    private static IReadOnlyList<VisualLine>? GetValidVisualLines(TextView textView)
    {
        if (!textView.VisualLinesValid)
        {
            return null;
        }

        try
        {
            return textView.VisualLines;
        }
        catch (VisualLinesInvalidException)
        {
            return null;
        }
    }

    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceTextChanged));

    public static readonly DependencyProperty HighlightingNameProperty = DependencyProperty.Register(
        nameof(HighlightingName), typeof(string), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(string.Empty, OnHighlightingNameChanged));

    public static readonly DependencyProperty WordWrapProperty = DependencyProperty.Register(
        nameof(WordWrap), typeof(bool), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(false, OnWordWrapChanged));

    public static readonly DependencyProperty ShowLineNumbersProperty = DependencyProperty.Register(
        nameof(ShowLineNumbers), typeof(bool), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(true, OnShowLineNumbersChanged));

    public static readonly DependencyProperty ShowIndentGuidesProperty = DependencyProperty.Register(
        nameof(ShowIndentGuides), typeof(bool), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(true, OnShowIndentGuidesChanged));

    public static readonly DependencyProperty ShowFoldingControlsProperty = DependencyProperty.Register(
        nameof(ShowFoldingControls), typeof(bool), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(true, OnShowFoldingControlsChanged));

    public static readonly DependencyProperty EditorFontSizeProperty = DependencyProperty.Register(
        nameof(EditorFontSize), typeof(double), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(14.0, OnEditorFontSizeChanged));

    public static readonly DependencyProperty ShowMinimapProperty = DependencyProperty.Register(
        nameof(ShowMinimap), typeof(bool), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(false, OnShowMinimapChanged));

    public static readonly DependencyProperty ShowStickyScrollProperty = DependencyProperty.Register(
        nameof(ShowStickyScroll), typeof(bool), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(true, OnShowStickyScrollChanged));

    public static readonly DependencyProperty MinimapRenderCharactersProperty = DependencyProperty.Register(
        nameof(MinimapRenderCharacters), typeof(bool), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(false, OnMinimapVisualsChanged));

    public static readonly DependencyProperty MinimapWidthProperty = DependencyProperty.Register(
        nameof(MinimapWidth), typeof(double), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(130d, OnMinimapVisualsChanged));

    public static readonly DependencyProperty LineNumbersRelativeProperty = DependencyProperty.Register(
        nameof(LineNumbersRelative), typeof(bool), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(false, OnLineNumbersModeChanged));

    public static readonly DependencyProperty RulerColumnsProperty = DependencyProperty.Register(
        nameof(RulerColumns), typeof(double[]), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(null, OnRulerColumnsChanged));

    public static readonly DependencyProperty PresentationSnapshotProperty = DependencyProperty.Register(
        nameof(PresentationSnapshot), typeof(CodePresentationSnapshot), typeof(CodeDocumentView),
        new FrameworkPropertyMetadata(null, OnPresentationSnapshotChanged));

    private readonly SearchMatchRenderer _matchRenderer;
    private readonly LineEmphasisRenderer _lineEmphasis;
    private readonly IndentationGuideRenderer _indentGuides;
    private readonly CodeTokenColorizer _semanticTokens;
    private readonly ReadingHighlightRenderer _readingHighlights;
    private readonly RulerRenderer _rulers;
    private readonly CodeLineNumberMargin _lineNumberMargin;
    private readonly DispatcherTimer _minimapThrottle;
    private bool _minimapDirty;
    private ScrollViewer? _editorScroll;
    private MinimapLayout.Map _minimapLayout = default!;
    private bool _isDraggingMinimap;
    private bool _isPagingWindow;
    private Brush _minimapTextBrush = Brushes.Gray;
    private Brush _minimapMatchBrush = Brushes.Transparent;
    private Brush _minimapViewportFill = Brushes.Transparent;
    private Brush _minimapViewportBorder = Brushes.Transparent;
    // Dominant semantic-token kind per source line (1-based) for the minimap tint.
    private IReadOnlyDictionary<int, CodeTokenKind> _minimapTokenKindByLine = new Dictionary<int, CodeTokenKind>();
    // Source lines (1-based) that contain search matches, for the minimap/overview markers.
    private readonly HashSet<int> _matchLines = new();
    // Minimap:token kind → 主题刷子 在 ApplyTheme 一次性解析(原实现每像素行 TryFindResource)。
    private Dictionary<CodeTokenKind, Brush> _minimapTokenBrushCache = [];
    // Minimap 内容版本:文档/色带数据/主题/尺寸/字符模式/匹配行任一变化 +1。
    // 滚动(仅滑块位置)不 bump → 110ms 定时重绘只重画滑块 visual,行内容复用。
    private int _minimapContentVersion;
    private int _minimapContentRenderedVersion = -1;
    private MiniVisualHost? _minimapContentHost;
    private MiniVisualHost? _minimapSliderHost;
    private int _lastOverviewCaretLine = -1;
    // 滚动驱动的辅助重绘(概览标尺/sticky)合帧调度:ScrollChanged 在拖动/惯性滚动时
    // 事件极密,旧实现每个事件都全量重画;现合并为每渲染帧一次(VS Code 帧调度思路)。
    private FrameCoalescer? _scrollFrame;
    // 光标驱动的辅助重绘(概览标尺/sticky/阅读高亮)合帧:一次快速移动会触发多次
    // Caret.PositionChanged,每帧只执行一次四个操作(VS Code ViewModelEventDispatcher
    // 批量消费思路)。置位 + Schedule 复用 _scrollFrame 的 CompositionTarget.Rendering 合帧;
    // 帧执行时重读当前光标行,因此无需在事件里快照。
    private bool _caretFlushScheduled;
    // 文档/折叠状态版本:BuildCurrentFoldRegions 结果按 (内容版本, 折叠版本) 缓存,
    // 渲染路径不再每次全量遍历折叠集合。
    private int _documentContentVersion;
    private int _foldStateVersion;
    private int _foldRegionsContentVersion = -1;
    private int _foldRegionsFoldVersion = -1;
    private IReadOnlyList<FoldRegion> _cachedFoldRegions = [];
    // 当前 sticky 折叠链引用缓存:链只在越过折叠区边界时变化,滚动像素变化不重建。
    private CodeFoldSection[]? _lastStickyChain;
    private FoldingManager? _foldManager;
    private FoldGutterMargin? _foldGutter;
    private readonly FoldBackgroundRenderer _foldBackground;
    private IReadOnlyList<CodeFoldSection> _foldSections = [];
    private IHighlightingDefinition? _highlightingDefinition;
    // 最近发布的语义快照(驱动内置兜底策略;部分快照只覆盖首屏)。
    private CodePresentationSnapshot? _presentationSnapshot;

    /// <summary>Raised after the document text is replaced (the preview view re-applies folding and
    /// restores the saved reading position then).</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>Raised when the caret moves to a different source line (面包屑符号段跟踪)。</summary>
    public event EventHandler<int>? CaretLineChanged;

    public CodeDocumentView()
    {
        InitializeComponent();
        _matchRenderer = new SearchMatchRenderer(this);
        _lineEmphasis = new LineEmphasisRenderer(this);
        _indentGuides = new IndentationGuideRenderer(this);
        _semanticTokens = new CodeTokenColorizer(Brush, () => CodeEditor.FontFamily);
        _readingHighlights = new ReadingHighlightRenderer(this);
        _rulers = new RulerRenderer(this);
        // 窗口化大文件(>8MB Windowed 档)关闭整层折叠背景:每帧 O(R+V) 的投影+绘制
        // 对这类文档是纯开销(vscode largeFileOptimizations 一票降级思路)。
        _foldBackground = new FoldBackgroundRenderer(BuildCurrentFoldRegions, () => !IsWindowedTier);
        _lineNumberMargin = new CodeLineNumberMargin(this);
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_matchRenderer);
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_lineEmphasis);
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_readingHighlights);
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_indentGuides);
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_rulers);
        // 折叠段背景最后注册:同层内后画者在上,整行 tint 盖住缩进参考线(与 vscode 一致)。
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_foldBackground);
        CodeEditor.TextArea.TextView.LineTransformers.Add(_semanticTokens);
        // Overview ruler caret marker follows the caret (redraw only when the line changes).
        // 四个重绘操作(概览标尺/行事件/阅读高亮/sticky)合帧:PositionChanged 在快速移动时
        // 每行触发一次,现在只置位,下一渲染帧统一执行一次(复用 _scrollFrame 的
        // CompositionTarget.Rendering 合帧调度)。_lastOverviewCaretLine 短路保留。
        CodeEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            var line = CodeEditor.TextArea.Caret.Line;
            if (line != _lastOverviewCaretLine)
            {
                _lastOverviewCaretLine = line;
                if (_caretFlushScheduled)
                {
                    return;
                }

                _caretFlushScheduled = true;
                _scrollFrame ??= new FrameCoalescer();
                _scrollFrame.Schedule(FlushCaretRedraw);
            }
        };
        CodeEditor.PreviewMouseWheel += OnPreviewMouseWheel;
        // 画布尺寸决定每像素行映射与最大条宽 → 内容版本失效(不只是滑块)。
        MinimapCanvas.SizeChanged += (_, _) =>
        {
            InvalidateMinimapContent();
            RequestMinimapDraw();
        };
        MinimapCanvas.MouseLeftButtonDown += OnMinimapMouseDown;
        MinimapCanvas.MouseMove += OnMinimapMouseMove;
        MinimapCanvas.MouseLeftButtonUp += OnMinimapMouseUp;
        MinimapCanvas.MouseLeave += (_, _) => _isDraggingMinimap = false;
        _minimapThrottle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _minimapThrottle.Tick += (_, _) =>
        {
            _minimapThrottle.Stop();
            if (_minimapDirty)
            {
                _minimapDirty = false;
                DrawMinimap();
            }
        };
        Loaded += (_, _) =>
        {
            ThemeEvents.ThemeChanged += OnThemeChanged;
            ApplyTheme();
            HookEditorScroll();
        };
        Unloaded += (_, _) =>
        {
            ThemeEvents.ThemeChanged -= OnThemeChanged;
            // 合帧器随卸载销毁:未及执行的置位一并复位,重新加载后从干净状态开始。
            _caretFlushScheduled = false;
            _scrollFrame?.Dispose();
            _scrollFrame = null;
        };
        ApplyEditorRendering();
    }

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    /// <summary>AvalonEdit built-in highlighting definition name; empty = plain text.</summary>
    public string HighlightingName
    {
        get => (string)GetValue(HighlightingNameProperty);
        set => SetValue(HighlightingNameProperty, value);
    }

    public bool WordWrap
    {
        get => (bool)GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public bool ShowLineNumbers
    {
        get => (bool)GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    public bool ShowIndentGuides
    {
        get => (bool)GetValue(ShowIndentGuidesProperty);
        set => SetValue(ShowIndentGuidesProperty, value);
    }

    public bool ShowFoldingControls
    {
        get => (bool)GetValue(ShowFoldingControlsProperty);
        set => SetValue(ShowFoldingControlsProperty, value);
    }

    /// <summary>Code font size (double-buffered with the Zoom state; Ctrl+wheel writes here so the
    /// reading preference can persist it).</summary>
    public double EditorFontSize
    {
        get => (double)GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }

    /// <summary>Shows/hides the right-edge windowed minimap.</summary>
    public bool ShowMinimap
    {
        get => (bool)GetValue(ShowMinimapProperty);
        set => SetValue(ShowMinimapProperty, value);
    }

    /// <summary>VS Code sticky scroll:滚动时把覆盖视口顶端的折叠段头钉在编辑器顶部。</summary>
    public bool ShowStickyScroll
    {
        get => (bool)GetValue(ShowStickyScrollProperty);
        set => SetValue(ShowStickyScrollProperty, value);
    }

    private static void OnShowStickyScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CodeDocumentView)d).UpdateSticky();

    /// <summary>迷你地图字符模式(renderCharacters)。</summary>
    public bool MinimapRenderCharacters
    {
        get => (bool)GetValue(MinimapRenderCharactersProperty);
        set => SetValue(MinimapRenderCharactersProperty, value);
    }

    /// <summary>迷你地图宽度(像素)。</summary>
    public double MinimapWidth
    {
        get => (double)GetValue(MinimapWidthProperty);
        set => SetValue(MinimapWidthProperty, value);
    }

    private static void OnMinimapVisualsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CodeDocumentView)d;
        view.MinimapHost.Width = Math.Clamp(view.MinimapWidth, 60, 320);
        // 字符模式 / 宽度都属于迷你地图内容数据 → 内容版本失效。
        view.InvalidateMinimapContent();
        view.RequestMinimapDraw();
    }

    /// <summary>相对行号(光标行实号,其余显示与光标的距离)。</summary>
    public bool LineNumbersRelative
    {
        get => (bool)GetValue(LineNumbersRelativeProperty);
        set => SetValue(LineNumbersRelativeProperty, value);
    }

    private static void OnLineNumbersModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CodeDocumentView)d;
        view._lineNumberMargin.InvalidateVisual();
        view._lineNumberMargin.InvalidateMeasure();
    }

    /// <summary>垂直标尺列(editor.rulers)。</summary>
    public double[]? RulerColumns
    {
        get => (double[]?)GetValue(RulerColumnsProperty);
        set => SetValue(RulerColumnsProperty, value);
    }

    private static void OnRulerColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CodeDocumentView)d)._rulers.RefreshColumns((double[]?)e.NewValue);

    /// <summary>Immutable background language pass.  A snapshot is accepted only when the
    /// surrounding tab confirms that it belongs to the currently displayed document version.</summary>
    public CodePresentationSnapshot? PresentationSnapshot
    {
        get => (CodePresentationSnapshot?)GetValue(PresentationSnapshotProperty);
        set => SetValue(PresentationSnapshotProperty, value);
    }

    /// <summary>The raw editor for view-only behavior wiring.</summary>
    public TextEditor Editor => CodeEditor;

    public TextDocument Document => CodeEditor.Document;

    /// <summary>当前承载标签是否为窗口化大文件档(非 Full 容量)。窗口化档默认关闭
    /// sticky / 折叠背景 / 整窗词高亮扫描等大文件能力(vscode largeFileOptimizations
    /// 思路);DataContext 未挂接标签时按 Full 处理(功能全开)。</summary>
    internal bool IsWindowedTier =>
        DataContext is FilePreviewTab tab && tab.CapacityTier != ReadOnlyContentTier.Full;

    // ===== View-only adapters (called by the surrounding preview view) =====

    public void SetSearchMatches(IReadOnlyList<TextSearchMatch> matches, int currentIndex)
    {
        _matchRenderer.SetMatches(matches, currentIndex);
        _matchLines.Clear();
        foreach (var match in matches)
        {
            _matchLines.Add(match.Line);
        }
        var current = currentIndex >= 0 && currentIndex < matches.Count ? matches[currentIndex] : null;
        var documentLength = CodeEditor.Document.TextLength;
        if (current is not null && documentLength > 0)
        {
            var line = Math.Clamp(current.Line, 1, Math.Max(1, CodeEditor.Document.LineCount));
            var safeOffset = Math.Clamp(current.Offset, 0, documentLength);
            var safeLength = Math.Clamp(current.Length, 0, documentLength - safeOffset);
            CenterLine(line);
            CodeEditor.CaretOffset = safeOffset;
            CodeEditor.SelectionStart = safeOffset;
            CodeEditor.SelectionLength = safeLength;
        }
        else if (matches.Count == 0)
        {
            // 无匹配:只清选择,不动光标——光标要么已由文档替换置于 0(首次加载),
            // 要么是刚落位的跳转目标(搜索结果);这里重置会把跳转打回文件顶部。
            CodeEditor.SelectionLength = 0;
        }
        // 匹配行是迷你地图内容(右侧标记条)的一部分 → 内容版本失效。
        InvalidateMinimapContent();
        RequestMinimapDraw();
        RedrawCodeOverview();
    }

    public void ClearSearchMarkers()
    {
        _matchRenderer.Clear();
        _matchLines.Clear();
        InvalidateMinimapContent();
        RequestMinimapDraw();
        RedrawCodeOverview();
    }

    /// <summary>Jumps to a 1-based line, centers it in the viewport and — when requested — briefly
    /// emphasizes the target line (go-to-line feedback).</summary>
    public void JumpToLine(int line, bool emphasize = true, bool deferCentering = false)
    {
        var doc = CodeEditor.Document;
        if (doc is null || doc.TextLength == 0)
        {
            // 文档尚未装入(空文档的 LineCount 是 1,钳制会把跳转塌缩到第 1 行后随整体替换丢在文件顶)
            // ——挂起,由 SetDocument 落位;否则搜索结果跳转被静默丢弃。
            _pendingJump = (line, 1, emphasize);
            return;
        }

        var number = Math.Clamp(line, 1, doc.LineCount);
        var documentLine = doc.GetLineByNumber(number);
        CodeEditor.CaretOffset = documentLine.Offset;
        CodeEditor.TextArea.Caret.BringCaretToView();
        CenterLineAfterLayout(number, CodeEditor.CaretOffset, deferCentering);
        CodeEditor.TextArea.Focus();

        if (emphasize)
        {
            _lineEmphasis.EmphasizeFor(number, TimeSpan.FromMilliseconds(700));
        }
    }

    /// <summary>Ctrl+wheel zoom (VS Code parity). Clamped and exclusive to the code area; writes the
    /// EditorFontSize DP so the reading preference can persist it. 显示字号 = 作者字号 × 界面缩放,
    /// 故钳制区间按当前倍率展开,持久化(写回设置)时除以倍率后仍落在 8–28 的作者范围。</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var scale = UiFontService.CurrentScale;
        var delta = e.Delta > 0 ? 1.0 : -1.0;
        EditorFontSize = Math.Clamp(EditorFontSize + delta, 8 * scale, 28 * scale);
        e.Handled = true;
    }

    /// <summary>跳转前挂起的位置(1-based 行/列 + 是否强调);文档未装入时暂存,
    /// 下一次非空 <see cref="SetDocument"/> 装入后落位,避免搜索结果跳转丢失。</summary>
    private (int Line, int Column, bool Emphasize)? _pendingJump;

    private void CenterLine(int lineNumber)
    {
        // 用像素偏移 API 精确居中(与迷你地图同一模式):AvalonEdit 的 ScrollTo(line, col)
        // 是"最小滚动使可见"语义,不保证目标行顶对齐视口顶,居中误差可达十几行。
        HookEditorScroll();
        if (_editorScroll is not { ViewportHeight: > 0 } scroll)
        {
            return; // 视口未就绪:由 CenterLineAfterLayout 的延迟机制兜底。
        }

        var textView = CodeEditor.TextArea.TextView;
        var visualLine = textView.GetVisualLine(lineNumber);
        if (visualLine is null)
        {
            // 视觉行未物化且按需构建失败:用统一行高的算术坐标兜底(等宽字体下精确,
            // 换行开启时近似)。
            var topPx = (lineNumber - 1) * Math.Max(1, textView.DefaultLineHeight);
            var rowH = Math.Max(1, textView.DefaultLineHeight);
            var fallbackTarget = Math.Clamp(topPx - (scroll.ViewportHeight - rowH) / 2, 0, Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight));
            scroll.ScrollToVerticalOffset(fallbackTarget);
            return;
        }

        var target = Math.Clamp(
            visualLine.VisualTop - (scroll.ViewportHeight - visualLine.Height) / 2,
            0,
            Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight));
        scroll.ScrollToVerticalOffset(target);
    }

    /// <summary>布局无关的居中:文本视图已布局且文档非刚装入时直接居中;否则(新打开文件首次
    /// 布局前的跳转,或文档装入瞬间的延迟落位——对刚 Replace 完的旧几何滚动不生效,布局后会
    /// 回到顶部)把居中排队到布局之后的调度槽,且仅当文档与光标都未变时应用——更新的跳转或
    /// 用户操作优先。</summary>
    private void CenterLineAfterLayout(int lineNumber, int caretOffset, bool forceDefer)
    {
        var document = CodeEditor.Document;
        if (forceDefer || CodeEditor.TextArea.TextView.ActualHeight <= 0)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(CodeEditor.Document, document) && CodeEditor.CaretOffset == caretOffset)
                {
                    CenterLine(lineNumber);
                }
            }), DispatcherPriority.Loaded);
            return;
        }

        CenterLine(lineNumber);
    }

    /// <summary>文档装入后落位挂起的跳转(搜索结果/快速打开在内容就绪瞬间发起的跳转)。</summary>
    private void TryApplyPendingJump()
    {
        if (_pendingJump is not { } pending
            || CodeEditor.Document is not { TextLength: > 0 })
        {
            return; // 文档仍为空(占位/重置):继续挂起,等下一次装入。
        }

        _pendingJump = null;
        // 文档刚装入:对旧几何滚动不生效,强制延迟到布局之后居中。
        if (pending.Column > 1)
        {
            JumpToPosition(pending.Line, pending.Column, pending.Emphasize, deferCentering: true);
        }
        else
        {
            JumpToLine(pending.Line, pending.Emphasize, deferCentering: true);
        }
    }

    private static void OnSourceTextChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var view = (CodeDocumentView)source;
        view.SetDocument(args.NewValue as string ?? string.Empty);
    }

    private static void OnHighlightingNameChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var view = (CodeDocumentView)source;
        var name = args.NewValue as string;
        view._highlightingDefinition = string.IsNullOrEmpty(name)
            ? null
            : HighlightingManager.Instance.GetDefinition(name);
        view.UpdateHighlightingFallback();
        view.ApplyHighlightingTheme();
    }

    private static void OnWordWrapChanged(DependencyObject source, DependencyPropertyChangedEventArgs args) =>
        ((CodeDocumentView)source).CodeEditor.WordWrap = (bool)args.NewValue;

    private static void OnShowLineNumbersChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var view = (CodeDocumentView)source;
        // The built-in AvalonEdit margin is single-color; always suppress it (it cannot tint the
        // caret line) and drive the custom margin's visibility from this DP instead.
        view.CodeEditor.ShowLineNumbers = false;
        view._lineNumberMargin.Visibility = (bool)args.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnShowIndentGuidesChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var view = (CodeDocumentView)source;
        view._indentGuides.Enabled = (bool)args.NewValue;
        view.CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private static void OnShowFoldingControlsChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var view = (CodeDocumentView)source;
        var visible = (bool)args.NewValue;
        if (view._foldGutter is not null)
            view._foldGutter.Visibility = visible && view._foldManager is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static void OnEditorFontSizeChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var view = (CodeDocumentView)source;
        var size = Math.Clamp((double)args.NewValue, 8, 28);
        view.CodeEditor.FontSize = size;
        view._lineNumberMargin.InvalidateMeasure();
        view._lineNumberMargin.InvalidateVisual();
    }

    private static void OnShowMinimapChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var view = (CodeDocumentView)source;
        var visible = (bool)args.NewValue;
        view.MinimapHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            view.HookEditorScroll();
            view.RequestMinimapDraw();
        }
    }

    private static void OnPresentationSnapshotChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var view = (CodeDocumentView)source;
        view._presentationSnapshot = args.NewValue as CodePresentationSnapshot;
        view._semanticTokens.SetSnapshot(view._presentationSnapshot);
        // Exactly one colour pipeline owns a document: semantic tokens when available, otherwise
        // AvalonEdit's immutable built-in definition as a safe language/native fallback.
        view.UpdateHighlightingFallback();
        // 每行首 token 类别驱动 minimap 色带:预索引 O(1) 替换;无预索引的快照(测试直建)
        // 退回 O(tokens) 构建,保持行为兼容。
        if (args.NewValue is CodePresentationSnapshot { MinimapKindByLine: not null } snapshot)
        {
            view._minimapTokenKindByLine = snapshot.MinimapKindByLine;
        }
        else
        {
            var fallback = new Dictionary<int, CodeTokenKind>();
            if (args.NewValue is CodePresentationSnapshot plain)
            {
                foreach (var token in plain.Tokens)
                {
                    if (!fallback.ContainsKey(token.Line))
                    {
                        fallback[token.Line] = token.Kind;
                    }
                }
            }

            view._minimapTokenKindByLine = fallback;
        }

        // 行色带数据(每行主导 token 类别)变化 → 迷你地图内容版本失效。
        view.InvalidateMinimapContent();
        view.RequestMinimapDraw();
    }

    private void SetDocument(string text)
    {
        var document = CodeEditor.Document;
        // 等值短路:先比引用,长度不同直接跳过 O(n) 内容比较(字符串相等本身也是先比长度,
        // 这里把"不同 → 必须重建"的分支显式化,避免对 2MB 串做逐字符比较后再重建)。
        var existing = document.Text;
        if (ReferenceEquals(existing, text))
        {
            return;
        }
        if (existing.Length == text.Length && existing == text)
        {
            return;
        }

        // 折叠几何/管理器与文档实例解耦前先清掉:文档内容即将整体替换,旧折叠段的偏移在新
        // 文本下无意义(无 sections 的早期返回路径也靠这里卸载,避免旧折叠残留在新内容上)。
        ResetFoldingManager();
        // 文档实例连续:同一 TextDocument 上 Replace 全区间,保留行树/piece table 与版本
        // 连续性——折叠管理器绑定、TextSegmentCollection 与渲染器缓存跨窗口翻页仍然有效
        // (vscode ITextModel 区间编辑语义),不再整文档 new TextDocument 重建。
        document.Replace(0, existing.Length, text);
        // AvalonEdit can retain the previous TextArea selection when its document is replaced.
        // A newly opened read-only source must start without a highlighted range.
        ClearSelectionAt(0);
        _matchRenderer.DocumentChanged(document);
        _lineEmphasis.DocumentChanged(document);
        // 文档实例连续但内容已换:阅读高亮段集(构造器创建的 TextSegmentCollection 不随文档
        // 编辑自动调整偏移)必须清空,避免旧偏移的段在新文本上闪一帧。
        _readingHighlights.SetSegments([]);
        // 折叠 sections 在新内容上重建(预览标签切换复用同一视图时,不依赖 FilePreviewView
        // 的 SetFoldingSections 时序)。
        ApplyFoldingSections();
        ApplyEditorRendering();
        InvalidateMinimapContent();
        _documentContentVersion++;
        RequestMinimapDraw();
        // 内容就绪后落位挂起的跳转(搜索结果在内容发布瞬间发起、文档未装入时暂存的请求)。
        TryApplyPendingJump();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===== Folding (themed markers) =====

    /// <summary>Applies the tab's fold sections; folded offsets (captured view state) are re-folded
    /// so re-activating a reused tab restores the previous collapse state.</summary>
    /// <summary>折叠/展开全部折叠段(VS Code Ctrl+K Ctrl+0 / Ctrl+K Ctrl+J)。</summary>
    public void SetAllFoldState(bool folded)
    {
        if (_foldManager is null)
        {
            return;
        }

        foreach (var folding in _foldManager.AllFoldings)
        {
            folding.IsFolded = folded;
        }

        AfterFoldStateChanged();
    }

    /// <summary>折叠到指定层级:被 &gt;= level 层段严格包含的段折叠,其余展开(VS Code Ctrl+K Ctrl+1..9)。
    /// 包含计数见 <see cref="ComputeFoldDepths"/>:Fenwick 树单遍 O(n log n)。
    /// 旧实现为每段重扫全部段 O(n²),大 C 文件上万折叠段会卡 UI 数秒。</summary>
    public void FoldToLevel(int level)
    {
        if (_foldManager is null)
        {
            return;
        }

        var foldings = _foldManager.AllFoldings.OrderBy(f => f.StartOffset).ToList();
        var starts = foldings.Select(f => f.StartOffset).ToArray();
        var ends = foldings.Select(f => f.EndOffset).ToArray();
        var depths = ComputeFoldDepths(starts, ends);
        for (var i = 0; i < foldings.Count; i++)
        {
            foldings[i].IsFolded = depths[i] >= level;
        }

        AfterFoldStateChanged();
    }

    /// <summary>折叠层级扫描核心(纯函数,便于单测):<paramref name="starts"/> 必须按升序排列,
    /// 与 <paramref name="ends"/> 平行;返回每个段"被多少段严格包含"的深度
    /// (含者 Start 严格更小 且 End 严格更大)。Fenwick 树单遍 O(n log n):按 start 升序处理,
    /// 同一 start 的段先统一查询(状态只含严格更早 start 的段)再统一入树。</summary>
    internal static int[] ComputeFoldDepths(IReadOnlyList<int> starts, IReadOnlyList<int> ends)
    {
        var count = starts.Count;
        var endRanks = ends.Distinct().OrderBy(end => end).ToArray();
        var tree = new int[endRanks.Length + 1];
        var depths = new int[count];
        var added = 0;
        var index = 0;
        while (index < count)
        {
            var start = starts[index];
            var groupEnd = index + 1;
            while (groupEnd < count && starts[groupEnd] == start)
            {
                groupEnd++;
            }

            // 先查询:此刻树中只有严格更早 start 的段,满足原谓词的 Start 严格条件。
            for (var i = index; i < groupEnd; i++)
            {
                var rank = endRanks.BinarySearch(ends[i]) + 1;
                depths[i] = added - QueryFoldTree(tree, rank); // End 严格大于本段 End 的段数
            }

            for (var i = index; i < groupEnd; i++)
            {
                AddFoldTree(tree, endRanks.BinarySearch(ends[i]) + 1, 1);
                added++;
            }

            index = groupEnd;
        }

        return depths;
    }

    private static int QueryFoldTree(int[] tree, int index)
    {
        var sum = 0;
        for (var i = index; i > 0; i -= i & -i)
        {
            sum += tree[i];
        }

        return sum;
    }

    private static void AddFoldTree(int[] tree, int index, int delta)
    {
        for (var i = index; i < tree.Length; i += i & -i)
        {
            tree[i] += delta;
        }
    }

    public void SetFoldingSections(
        IReadOnlyList<CodeFoldSection> sections,
        IReadOnlySet<int>? foldedOffsets,
        IReadOnlySet<string>? foldedSymbolIds = null)
    {
        _foldSections = sections;
        _foldedOffsets = foldedOffsets;
        _foldedSymbolIds = foldedSymbolIds;
        _lastStickyChain = null; // 折叠结构变化:sticky 链缓存失效
        ApplyFoldingSections();
    }

    private IReadOnlySet<int>? _foldedOffsets;
    private IReadOnlySet<string>? _foldedSymbolIds;

    /// <summary>用“缓存的 sections + 当前文档”重建折叠。文档内容整体替换(SetDocument,
    /// 同一 TextDocument 实例上的 Replace)后旧折叠段偏移失效,SetDocument 会先
    /// ResetFoldingManager 再调用本方法自愈,不依赖 FilePreviewView 的事件时序。
    /// 外部再次 SetFoldingSections 幂等。</summary>
    private void ApplyFoldingSections()
    {
        UpdateSticky();
        if (CodeEditor.Document is not { } document || _foldSections.Count == 0)
        {
            ResetFoldingManager();
            return;
        }

        if (_foldManager is null)
        {
            _foldManager = FoldingManager.Install(CodeEditor.TextArea);
            // FoldingManager.Install 会自动在 LeftMargins 最左侧插入一个默认 FoldingMargin
            // (旧式三角标记栏)。折叠栏已改由 FoldGutterMargin 自绘,必须移除它,否则旧的
            // 折叠栏仍可见。
            foreach (var builtInMargin in CodeEditor.TextArea.LeftMargins.OfType<FoldingMargin>().ToArray())
            {
                CodeEditor.TextArea.LeftMargins.Remove(builtInMargin);
            }
        }

        // 每次应用新预览的折叠区间都创建新的折叠栏实例。折叠栏的 TextView 绑定跟随当前
        // 文档的测量周期，不能复用上一个预览文件的 margin。
        if (_foldGutter is not null)
        {
            CodeEditor.TextArea.LeftMargins.Remove(_foldGutter);
        }

        _foldGutter = new FoldGutterMargin(BuildCurrentFoldRegions, ToggleFoldingAtLine, ExpandCollapsedAtLine);
        var index = CodeEditor.TextArea.LeftMargins.IndexOf(_lineNumberMargin);
        CodeEditor.TextArea.LeftMargins.Insert(index < 0 ? CodeEditor.TextArea.LeftMargins.Count : index + 1, _foldGutter);
        _foldGutter.Visibility = ShowFoldingControls ? Visibility.Visible : Visibility.Collapsed;

        var foldings = new List<NewFolding>(_foldSections.Count);
        foreach (var section in _foldSections)
        {
            var start = document.GetLineByNumber(Math.Clamp(section.StartLine, 1, document.LineCount));
            var end = document.GetLineByNumber(Math.Clamp(section.EndLine, 1, document.LineCount));
            // Keep both the fold header and a real same-level closing/boundary line visible.
            // AvalonEdit folds the text in [startOffset, endOffset): beginning at the header's
            // EndOffset hides its child lines, while ending at the boundary line's Offset keeps
            // that line visible. Markdown/EOF sections have no boundary line and use EndOffset.
            var foldEndOffset = section.PreserveEndLine ? end.Offset : end.EndOffset;
            if (foldEndOffset <= start.EndOffset)
            {
                continue;
            }

            foldings.Add(new NewFolding(start.EndOffset, foldEndOffset)
            {
                Name = "…",
                // Accept the old line-start offset as a compatibility fallback; new captures use
                // the header EndOffset identity.
                DefaultClosed = _foldedSymbolIds?.Contains(section.SymbolId ?? string.Empty) == true
                    || _foldedOffsets?.Contains(start.EndOffset) == true
                    || _foldedOffsets?.Contains(start.Offset) == true,
            });
        }

        // Keep the AvalonEdit boundary safe even if a future folding provider returns
        // sections in discovery order rather than document order.
        foldings.Sort(static (left, right) =>
        {
            var byStart = left.StartOffset.CompareTo(right.StartOffset);
            return byStart != 0 ? byStart : right.EndOffset.CompareTo(left.EndOffset);
        });
        _foldManager.UpdateFoldings(foldings, 0);
        // 立即刷新折叠栏与折叠段背景(新 gutter 首布局渲染 + 显式重绘双保险)。
        AfterFoldStateChanged();
    }

    private void ResetFoldingManager()
    {
        var oldGutter = _foldGutter;
        if (oldGutter is not null)
        {
            CodeEditor.TextArea.LeftMargins.Remove(oldGutter);
            _foldGutter = null;
        }

        if (_foldManager is not null)
        {
            FoldingManager.Uninstall(_foldManager);
            _foldManager = null;
        }

        _foldStateVersion++; // 折叠集合整体移除:几何缓存失效
        RefreshFoldingLayout();
    }

    /// <summary>当前折叠几何(从 AvalonEdit 折叠集合投影):折叠/展开状态即真实状态,
    /// 折叠栏与折叠段背景共用同一份数据。结果按 (文档内容版本, 折叠状态版本) 缓存:
    /// 渲染路径每次调用不再全量遍历折叠集合,只有 SetDocument / SetFoldingSections /
    /// 折叠态变化才触发一次重建。</summary>
    private IReadOnlyList<FoldRegion> BuildCurrentFoldRegions()
    {
        if (_foldManager is null || CodeEditor.Document is not { } document)
        {
            return [];
        }

        if (_foldRegionsContentVersion == _documentContentVersion
            && _foldRegionsFoldVersion == _foldStateVersion)
        {
            return _cachedFoldRegions;
        }

        var items = new List<(int Start, int End, bool Collapsed)>();
        foreach (var folding in _foldManager.AllFoldings)
        {
            var startOffset = Math.Clamp(folding.StartOffset, 0, document.TextLength);
            var endOffset = Math.Clamp(folding.EndOffset, startOffset, document.TextLength);
            var startLine = document.GetLineByOffset(startOffset).LineNumber;
            var endLine = document.GetLineByOffset(endOffset).LineNumber;
            if (endLine > startLine)
            {
                items.Add((startLine, endLine, folding.IsFolded));
            }
        }

        var regions = FoldingRegions.Build(items);
        _cachedFoldRegions = regions;
        _foldRegionsContentVersion = _documentContentVersion;
        _foldRegionsFoldVersion = _foldStateVersion;
        return regions;
    }

    /// <summary>按折叠段首行定位 AvalonEdit 折叠段。起点才是折叠栏点击的稳定身份;
    /// EndOffset 可能因 AvalonEdit 的区间端点语义与投影行号不同而偏移。等起点的段按
    /// EndOffset 降序选择外层,与 <see cref="FoldingRegions.FindStartVisibleAtLine"/> 的
    /// chevron 选择语义一致。</summary>
    private FoldingSection? FindFoldingSection(FoldRegion region)
    {
        if (_foldManager is null || CodeEditor.Document is not { } document)
        {
            return null;
        }

        var startOffset = document.GetLineByNumber(Math.Clamp(region.StartLine, 1, document.LineCount)).EndOffset;
        FoldingSection? outermost = null;
        foreach (var folding in _foldManager.AllFoldings)
        {
            if (folding.StartOffset != startOffset)
            {
                continue;
            }

            if (outermost is null || folding.EndOffset > outermost.EndOffset)
            {
                outermost = folding;
            }
        }

        return outermost;
    }

    /// <summary>折叠栏点击:切换该行最外层起始区域的折叠状态(VS Code chevron 语义)。</summary>
    private void ToggleFoldingAtLine(int line)
    {
        var region = FoldingRegions.FindStartVisibleAtLine(BuildCurrentFoldRegions(), line);
        if (region is not null && FindFoldingSection(region) is { } folding)
        {
            folding.IsFolded = !folding.IsFolded;
            AfterFoldStateChanged();
        }
    }

    /// <summary>折叠栏点击行为对齐 vscode:点击被折叠内容行展开其所在折叠区间。</summary>
    private void ExpandCollapsedAtLine(int line)
    {
        var region = FoldingRegions.FindCollapsedContaining(BuildCurrentFoldRegions(), line);
        if (region is not null && FindFoldingSection(region) is { } folding)
        {
            folding.IsFolded = false;
            AfterFoldStateChanged();
        }
    }

    /// <summary>折叠状态变化后刷新折叠栏、折叠段背景与 sticky 段头。</summary>
    private void AfterFoldStateChanged()
    {
        _foldStateVersion++; // 折叠几何(含折叠态)变化:BuildCurrentFoldRegions 缓存失效
        _foldGutter?.Refresh();
        CodeEditor.TextArea.TextView.InvalidateLayer(_foldBackground.Layer);
        RefreshFoldingLayout();
        UpdateSticky();
    }

    /// <summary>强制 AvalonEdit 在折叠管理器/文档切换后重新测量并绘制左侧边栏。只调用
    /// margin 的 InvalidateVisual 不足以覆盖 LeftMargins 集合在预览切换期间的布局缓存。</summary>
    private void RefreshFoldingLayout()
    {
        var gutter = _foldGutter;
        gutter?.InvalidateMeasure();
        CodeEditor.TextArea.InvalidateMeasure();
        CodeEditor.TextArea.TextView.InvalidateMeasure();
        CodeEditor.TextArea.TextView.InvalidateLayer(_foldBackground.Layer);
        CodeEditor.TextArea.TextView.Redraw(DispatcherPriority.Render);

        // LeftMargins is updated while the preview DataContext is switching. At that point
        // TextView.VisualLines can still be empty, so an immediate InvalidateVisual is lost.
        // Re-run after the new document has completed its layout; ignore callbacks for a gutter
        // that was replaced by another preview in the meantime.
        if (gutter is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (!ReferenceEquals(_foldGutter, gutter) || _foldManager is null)
                {
                    return;
                }

                CodeEditor.TextArea.TextView.EnsureVisualLines();
                gutter.InvalidateMeasure();
                gutter.Refresh();
                CodeEditor.TextArea.TextView.Redraw(DispatcherPriority.Render);
            }));
        }
    }

    /// <summary>Jumps to a symbol selection position and keeps the target line centered.</summary>
    public void JumpToPosition(int line, int column, bool emphasize = true, bool deferCentering = false)
    {
        var doc = CodeEditor.Document;
        if (doc is null || doc.TextLength == 0)
        {
            // 文档尚未装入(空文档的 LineCount 是 1,钳制会把跳转塌缩到第 1 行后随整体替换丢在文件顶)
            // ——挂起,由 SetDocument 落位;否则搜索结果跳转被静默丢弃。
            _pendingJump = (line, column, emphasize);
            return;
        }

        var number = Math.Clamp(line, 1, doc.LineCount);
        var documentLine = doc.GetLineByNumber(number);
        var position = new TextViewPosition(number, Math.Clamp(column, 1, documentLine.Length + 1));
        CodeEditor.TextArea.Caret.Position = position;
        CodeEditor.TextArea.Caret.BringCaretToView();
        CenterLineAfterLayout(number, CodeEditor.CaretOffset, deferCentering);
        CodeEditor.TextArea.Focus();
        if (emphasize)
        {
            _lineEmphasis.EmphasizeFor(number, TimeSpan.FromMilliseconds(700));
        }
    }

    /// <summary>文档偏移的已折叠段集合(写入阅读态以便恢复)。</summary>
    public IReadOnlySet<int> CaptureFoldedOffsets()
    {
        var set = new HashSet<int>();
        if (_foldManager is not null)
        {
            foreach (var folding in _foldManager.AllFoldings)
            {
                if (folding.IsFolded)
                {
                    set.Add(folding.StartOffset);
                }
            }
        }

        return set;
    }

    public IReadOnlySet<string> CaptureFoldedSymbolIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (_foldManager is null || CodeEditor.Document is not { } document)
        {
            return set;
        }

        foreach (var folding in _foldManager.AllFoldings)
        {
            if (!folding.IsFolded)
            {
                continue;
            }

            var section = _foldSections.FirstOrDefault(candidate =>
            {
                var start = document.GetLineByNumber(Math.Clamp(candidate.StartLine, 1, document.LineCount));
                var end = document.GetLineByNumber(Math.Clamp(candidate.EndLine, 1, document.LineCount));
                var endOffset = candidate.PreserveEndLine ? end.Offset : end.EndOffset;
                return start.EndOffset == folding.StartOffset && endOffset == folding.EndOffset;
            });
            if (!string.IsNullOrEmpty(section?.SymbolId))
            {
                set.Add(section.SymbolId);
            }
        }

        return set;
    }

    private void ThemeFoldingMargin()
    {
        // 折叠栏与折叠段背景在绘制时实时解析主题刷子,无需缓存;这里只触发重绘。
        _foldGutter?.Refresh();
        CodeEditor.TextArea.TextView.InvalidateLayer(_foldBackground.Layer);
    }

    // ===== View-state restore / capture (per-tab reading position) =====

    public void RestoreViewState(EditorViewState? state)
    {
        HookEditorScroll(); // 无 Loaded 的离线路径(集成测试)下模板就绪时兜底挂接,幂等。
        if (state is null)
        {
            ClearSelectionAt(0);
            return;
        }

        _editorScroll?.ScrollToVerticalOffset(state.VerticalOffset);
        if (CodeEditor.Document is { } document && document.LineCount > 0)
        {
            var line = Math.Clamp(state.CaretLine, 1, document.LineCount);
            var documentLine = document.GetLineByNumber(line);
            var column = Math.Clamp(state.CaretColumn, 1, documentLine.Length + 1);
            CodeEditor.TextArea.Caret.Position = new TextViewPosition(line, column);
            ClearSelectionAt(CodeEditor.CaretOffset);
            CodeEditor.ScrollTo(line, 0);
            CodeEditor.TextArea.Caret.BringCaretToView();
        }
    }

    public EditorViewState CaptureViewState()
    {
        HookEditorScroll(); // 无 Loaded 的离线路径(集成测试)下模板就绪时兜底挂接,幂等。
        return new(
            _editorScroll?.VerticalOffset ?? 0,
            CodeEditor.TextArea.Caret.Line,
            CodeEditor.TextArea.Caret.Column,
            CaptureFoldedOffsets(),
            CaptureFoldedSymbolIds());
    }

    private void ClearSelectionAt(int offset)
    {
        var safeOffset = Math.Clamp(offset, 0, CodeEditor.Document.TextLength);
        CodeEditor.CaretOffset = safeOffset;
        CodeEditor.TextArea.ClearSelection();
        CodeEditor.SelectionStart = safeOffset;
        CodeEditor.SelectionLength = 0;
    }

    // ===== Windowed minimap (view-only adapter) =====

    private void HookEditorScroll()
    {
        if (_editorScroll is not null)
        {
            return;
        }

        _editorScroll = CodeEditor.Template.FindName("PART_ScrollViewer", CodeEditor) as ScrollViewer;
        if (_editorScroll is not null)
        {
            _editorScroll.ScrollChanged += OnEditorScrollChanged;
        }
    }

    private void OnEditorScrollChanged(object sender, ScrollChangedEventArgs args) =>
        _ = HandleEditorScrollChangedSafelyAsync(sender, args);

    private async Task HandleEditorScrollChangedSafelyAsync(object sender, ScrollChangedEventArgs args)
    {
        try
        {
            await HandleEditorScrollChangedAsync(sender, args);
        }
        catch (OperationCanceledException)
        {
            // The editor can be unloaded while an adjacent window is loading.
        }
        catch (Exception exception)
        {
            Log.Error(exception, "编辑器滚动触发窗口分页失败");
        }
    }

    private async Task HandleEditorScrollChangedAsync(object sender, ScrollChangedEventArgs args)
    {
        RequestMinimapDraw();
        // 概览标尺/sticky 合帧:高频滚动事件合并为下一渲染帧一次更新。
        _scrollFrame ??= new FrameCoalescer();
        _scrollFrame.Schedule(() =>
        {
            RedrawCodeOverview();
            UpdateSticky();
        });
        if (_isPagingWindow || DataContext is not FilePreviewTab tab || tab.CapacityTier == ReadOnlyContentTier.Full) return;
        var scroll = (ScrollViewer)sender;
        var next = args.VerticalChange > 0 && scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 2;
        var previous = args.VerticalChange < 0 && scroll.VerticalOffset <= 2;
        if (!next && !previous) return;
        _isPagingWindow = true;
        try
        {
            if (await tab.LoadAdjacentWindowAsync(next))
            {
                if (next) scroll.ScrollToVerticalOffset(0);
                else scroll.ScrollToEnd();
            }
        }
        finally
        {
            _isPagingWindow = false;
        }
    }

    private void RequestMinimapDraw()
    {
        if (MinimapHost.Visibility != Visibility.Visible)
        {
            return;
        }

        _minimapDirty = true;
        if (!_minimapThrottle.IsEnabled)
        {
            _minimapThrottle.Start();
        }
    }

    /// <summary>光标行变化的每帧合批执行:概览标尺 + 行事件 + 阅读高亮 + sticky 四项各执行一次。
    /// 帧执行时重读当前光标行(可能已被更快的移动推进),与逐事件直接执行等价且每帧只跑一遍。</summary>
    private void FlushCaretRedraw()
    {
        _caretFlushScheduled = false;
        var line = CodeEditor.TextArea.Caret.Line;
        RedrawCodeOverview();
        CaretLineChanged?.Invoke(this, line);
        RefreshReadingHighlights();
        UpdateSticky();
    }

    /// <summary>迷你地图重绘(110ms 定时/滚动/数据变化触发)。内容经 <see cref="MinimapFramebuffer"/>
    /// 双缓冲 + 脏行位图提交到 WriteableBitmap(E1):每行与上次内容比较,未变行零成本跳过,
    /// 只有变化的行被 <c>WritePixels</c> 上屏;滚动只重建滑块 visual,不再整条重画全部行。
    /// 标记(搜索命中)与视口滑块仍分离:滑块独立重画。</summary>
    private void DrawMinimap()
    {
        if (MinimapCanvas.ActualHeight <= 0 || CodeEditor.Document is not { } document || document.LineCount == 0)
        {
            MinimapCanvas.Children.Clear();
            _minimapContentHost = null;
            _minimapSliderHost = null;
            _minimapContentRenderedVersion = -1;
            _minimapFramebuffer = null;
            _minimapBitmap = null;
            return;
        }

        var textView = CodeEditor.TextArea.TextView;
        var lineHeight = Math.Max(1, textView.DefaultLineHeight);
        var scrollOffset = _editorScroll?.VerticalOffset ?? 0;
        var stripHeight = MinimapCanvas.ActualHeight;
        _minimapLayout = MinimapLayout.Compute(
            document.LineCount,
            lineHeight,
            Math.Max(1, textView.ActualHeight),
            stripHeight,
            scrollOffset);

        var map = _minimapLayout;
        RenderMinimapContent(document, map, stripHeight);

        _minimapSliderHost ??= new MiniVisualHost(new DrawingVisual());
        if (!MinimapCanvas.Children.Contains(_minimapSliderHost))
        {
            MinimapCanvas.Children.Add(_minimapSliderHost);
        }

        _minimapSliderHost.Update(BuildMinimapSliderVisual(map, stripHeight));
    }

    /// <summary>E1 残余:WriteableBitmap 双缓冲 + 脏行位图提交。bitmap/帧缓冲按 (宽,高) 重建
    /// (尺寸变化)或内容版本变化整缓冲作废;随后每行重算并与缓冲比较,滚动/重复重绘零脏行。</summary>
    private void RenderMinimapContent(TextDocument document, MinimapLayout.Map map, double stripHeight)
    {
        var width = Math.Max(1, (int)Math.Ceiling(MinimapCanvas.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(stripHeight));

        // 尺寸或数据版本变化 → 重建 bitmap + 帧缓冲(内容整体作废)。
        if (_minimapFramebuffer is null || _minimapBitmap is null ||
            _minimapFramebuffer.Width != width || _minimapFramebuffer.Height != height)
        {
            if (_minimapContentHost is not null)
            {
                MinimapCanvas.Children.Remove(_minimapContentHost);
            }

            _minimapFramebuffer = new MinimapFramebuffer(width, height);
            _minimapBitmap?.Freeze();
            _minimapBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _minimapContentHost = null;
            _minimapContentRenderedVersion = -1;
        }
        else if (_minimapContentRenderedVersion != _minimapContentVersion)
        {
            _minimapFramebuffer.MarkAllDirty();
            _minimapContentRenderedVersion = _minimapContentVersion;
        }

        BufferRowReady();
        RenderMinimapRows(document, map, stripHeight, width);
        CommitMinimapDirtyRuns(width, height);
    }

    /// <summary>行像素行缓冲(尺寸稳定后固定分配)。</summary>
    private int[] _minimapRowBuffer = [];
    private WriteableBitmap? _minimapBitmap;
    private MinimapFramebuffer? _minimapFramebuffer;

    private void BufferRowReady()
    {
        var width = _minimapFramebuffer!.Width;
        if (_minimapRowBuffer.Length >= width)
        {
            return;
        }

        _minimapRowBuffer = new int[width];
    }

    /// <summary>把各条带行的 (色带 + 命中标记) 写入帧缓冲;未变行自动跳过(零脏行)。</summary>
    private void RenderMinimapRows(TextDocument document, MinimapLayout.Map map, double stripHeight, int width)
    {
        EnsureMinimapTokenBrushCache();
        var density = PresentationSnapshot?.LineDensity;
        var maxWidth = Math.Max(4, MinimapCanvas.ActualWidth - 8);
        var framebuffer = _minimapFramebuffer!;
        var rowBuffer = _minimapRowBuffer;
        var characterCells = MinimapRenderCharacters;
        var matchColor = BrushToBgra(_minimapMatchBrush);
        var stripRows = (int)Math.Ceiling(stripHeight);
        // 搜索命中标记的纵向跨度(LineStripHeight 行 × 每行 1px),与旧实现一致。
        var markerExtent = Math.Max(1, (int)Math.Ceiling(map.LineStripHeight * stripHeight));
        var matchRows = new HashSet<int>();
        foreach (var line in _matchLines)
        {
            var markerY = (int)Math.Round(MinimapLayout.MapY(line - 1, map) * stripHeight);
            for (var r = markerY; r < markerY + markerExtent && r < stripRows; r++)
            {
                matchRows.Add(r);
            }
        }

        var lastLineIndex = -1;
        var lastLength = 0;
        for (var y = 0; y < stripRows; y++)
        {
            Array.Clear(rowBuffer, 0, width); // 透明默认
            // 双精度运算:大文档下 y * LineCount 的 int 乘积可溢出为负,
            // 使 lineIndex 越界并让 GetLineByNumber 抛异常。
            var lineIndex = Math.Min(document.LineCount - 1, (int)(y * (double)document.LineCount / stripHeight));
            var lineNumber = lineIndex + 1;
            var color = _minimapTokenKindByLine.TryGetValue(lineNumber, out var kind)
                ? _minimapTokenBrushCache[kind]
                : _minimapTextBrush;
            var barColor = BrushToBgra(color);
            // 连续多像素行常映射到同一文档行:行长度只查一次(无 LineDensity 时避免
            // 每像素行一次行树二分)。
            if (lineIndex != lastLineIndex)
            {
                lastLineIndex = lineIndex;
                lastLength = density is not null && lineIndex < density.Count
                    ? density[lineIndex]
                    : Math.Min(255, document.GetLineByNumber(lineNumber).Length);
            }

            var length = lastLength;
            if (length > 0)
            {
                FillBar(rowBuffer, width, barColor, length, maxWidth, characterCells);
            }

            // 搜索命中标记:右侧 2px 竖条(覆盖标记行的整段纵向跨度)。
            if (matchRows.Contains(y))
            {
                var right = width - 3;
                if (right >= 0 && right < width)
                {
                    var markerPx = Math.Min(2, width - right);
                    for (var m = 0; m < markerPx; m++)
                    {
                        rowBuffer[right + m] = matchColor;
                    }
                }
            }

            framebuffer.SetRow(y, rowBuffer);
        }
    }

    /// <summary>行内色带填充:块模式一条按行长缩放的色条;字符模式逐字符 2px 格。</summary>
    private static void FillBar(int[] rowBuffer, int width, int color, int length, double maxWidth, bool characterCells)
    {
        if (characterCells)
        {
            var cells = Math.Min(length, (int)(maxWidth / 2));
            for (var cell = 0; cell < cells; cell++)
            {
                var x = 4 + cell * 2;
                if (x < width - 1) rowBuffer[x] = color;
                if (x + 1 < width) rowBuffer[x + 1] = color;
            }
        }
        else
        {
            var barWidth = Math.Min(width, Math.Max(1, (int)Math.Ceiling(maxWidth * Math.Min(1, 0.2 + length / 72.0))));
            for (var x = 4; x < 4 + barWidth && x < width; x++)
            {
                rowBuffer[x] = color;
            }
        }
    }

    /// <summary>把脏行区间批量 WritePixels 上屏(仅变化的行;滚动/重复重绘零提交)。</summary>
    private void CommitMinimapDirtyRuns(int width, int height)
    {
        var bitmap = _minimapBitmap!;
        var pixels = _minimapFramebuffer!.Pixels;
        var stride = width * 4;
        foreach (var (start, count) in _minimapFramebuffer.TakeDirtyRowRuns())
        {
            bitmap.WritePixels(
                new Int32Rect(0, start, width, count),
                pixels,
                stride,
                start * width);
        }

        // 内容 host 展示 bitmap(重建时先挂上)。
        if (_minimapContentHost is null)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(bitmap, new Rect(0, 0, width, height));
            }

            _minimapContentHost = new MiniVisualHost(visual);
            MinimapCanvas.Children.Add(_minimapContentHost);
        }
    }

    /// <summary>Brush → BGRA int(WriteableBitmap/PixelFormats.Bgra32 直通);非纯色刷回退文本色。</summary>
    private static int BrushToBgra(Brush? brush)
    {
        var color = brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
        return color.B | (color.G << 8) | (color.R << 16) | (color.A << 24);
    }

    /// <summary>迷你地图滑块 visual:视口指示条,只依赖滚动偏移,滚动时单独重画。</summary>
    private DrawingVisual BuildMinimapSliderVisual(MinimapLayout.Map map, double stripHeight)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Viewport indicator: translucent slider (VS Code minimap slider).
            var viewportTop = map.ViewportTopInMap * stripHeight;
            var viewportHeight = Math.Clamp(map.ViewportHeightInMap * stripHeight, 2, Math.Max(2, stripHeight));
            var top = Math.Clamp(viewportTop, 0, Math.Max(0, stripHeight - viewportHeight));
            context.DrawRectangle(
                _minimapViewportFill,
                new Pen(_minimapViewportBorder, 1),
                new Rect(1, top, Math.Max(4, MinimapCanvas.ActualWidth - 2), viewportHeight));
        }

        return visual;
    }

    /// <summary>下层的 token kind → 刷子 字典:主题变化(或首次)时整体重解析,行绘制走 O(1) 命中。</summary>
    private void EnsureMinimapTokenBrushCache()
    {
        if (_minimapTokenBrushCache.Count > 0)
        {
            return;
        }

        foreach (var kind in Enum.GetValues<CodeTokenKind>())
        {
            _minimapTokenBrushCache[kind] = TokenBrushForKind(kind) ?? _minimapTextBrush;
        }
    }

    /// <summary>主题变化:重解析迷你地图刷子字典并 bump 内容版本(色带颜色变了)。
    /// 由 ApplyTheme 在 _minimapTextBrush 更新后调用。</summary>
    internal void OnMinimapThemeChanged()
    {
        _minimapTokenBrushCache.Clear();
        _minimapTextBrush = Brush("CodeMinimapTextBrush") ?? Brushes.Gray;
        _minimapMatchBrush = Brush("CodeMinimapMatchBrush") ?? Brushes.Transparent;
        _minimapViewportFill = Brush("ScrollBarThumbBrush") ?? new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80));
        _minimapViewportBorder = Brushes.Transparent;
        InvalidateMinimapContent();
    }

    /// <summary>迷你地图下层数据(文档/色带/主题/尺寸/字符模式/匹配行)变化标记:
    /// 下一次 DrawMinimap 重建内容 visual;纯滚动不调用。</summary>
    private void InvalidateMinimapContent()
    {
        _minimapContentVersion++;
    }

    private void OnMinimapMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MinimapCanvas.ActualHeight <= 0 || _minimapLayout.EditorLineCount == 0)
        {
            return;
        }

        _isDraggingMinimap = true;
        MinimapCanvas.CaptureMouse();
        ScrollMinimapTo(e.GetPosition(MinimapCanvas).Y);
        e.Handled = true;
    }

    private void OnMinimapMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingMinimap)
        {
            ScrollMinimapTo(e.GetPosition(MinimapCanvas).Y);
        }
    }

    private void OnMinimapMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingMinimap = false;
        MinimapCanvas.ReleaseMouseCapture();
    }

    /// <summary>Drag: map Y → fractional editor line → editor scroll offset.</summary>
    private void ScrollMinimapTo(double mapY)
    {
        if (MinimapCanvas.ActualHeight <= 0 || _minimapLayout.LineStripHeight <= 0 || _editorScroll is null)
        {
            return;
        }

        var normalizedY = Math.Clamp(mapY / MinimapCanvas.ActualHeight, 0, 1);
        var editorLine = MinimapLayout.EditorLineFromMapY(normalizedY, _minimapLayout);
        var lineHeight = Math.Max(1, CodeEditor.TextArea.TextView.DefaultLineHeight);
        // 点击/拖动跳转:目标行显示在编辑器视口垂直居中位置。
        _editorScroll.ScrollToVerticalOffset(MinimapLayout.ScrollOffsetForLine(editorLine, lineHeight, _editorScroll.ViewportHeight));
        RequestMinimapDraw();
    }

    /// <summary>Redraws the slim right-gutter overview ruler: find-match lines and the caret line,
    /// bucketed one row per pixel (VS Code overview ruler).</summary>
    private void RedrawCodeOverview()
    {
        CodeOverviewCanvas.Children.Clear();
        if (CodeOverviewCanvas.ActualHeight <= 0 || CodeEditor.Document is not { } document || document.LineCount == 0)
        {
            return;
        }

        var height = CodeOverviewCanvas.ActualHeight;
        var width = CodeOverviewCanvas.ActualWidth;
        var matchBrush = Brush("CodeSearchMatchBrush") ?? Brushes.Transparent;
        var caretBrush = Brush("AccentBrush") ?? Brushes.Transparent;
        var caretLine = CodeEditor.TextArea.Caret.Line;
        var lineCount = document.LineCount;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // 正向投影:只画匹配行 + 光标行(旧实现逐像素行循环 + 每像素 HashSet 查询)。
            foreach (var line in _matchLines)
            {
                if (line < 1 || line > lineCount) continue;
                var y = Math.Min(height - 1, (line - 1) * height / lineCount);
                context.DrawRectangle(matchBrush, null, new Rect(0, y, width, 1));
            }

            if (caretLine >= 1 && caretLine <= lineCount)
            {
                var y = Math.Min(height - 1, (caretLine - 1) * height / lineCount);
                context.DrawRectangle(caretBrush, null, new Rect(0, y, width, 1));
            }
        }

        CodeOverviewCanvas.Children.Add(new MiniVisualHost(visual));
    }

    private void ApplyEditorRendering()
    {
        var view = CodeEditor.TextArea.TextView;
        view.CurrentLineBackground = Brush("CodeCurrentLineBrush") ?? Brushes.Transparent;
        // VS Code current-line is a soft tint without a border (HighContrast keeps its solid
        // surface via CodeCurrentLineBrush's opaque value).
        view.CurrentLineBorder = null;
        // VS Code's modern selection is a soft rounded block without a contrasting border.
        // Leave SelectionForeground unset so the syntax-token colors remain visible through it.
        CodeEditor.TextArea.SelectionBrush = Brush("CodeSelectionBrush") ?? CodeEditor.TextArea.SelectionBrush;
        CodeEditor.TextArea.SelectionBorder = null;
        CodeEditor.TextArea.SelectionCornerRadius = 3;
        CodeEditor.TextArea.Caret.CaretBrush = Brush("CaretBrush") ?? CodeEditor.TextArea.Caret.CaretBrush;
        CodeEditor.TextArea.IndentationStrategy = null;
        CodeEditor.Options.HighlightCurrentLine = true;
        // Replace AvalonEdit's single-color line-number margin with the VS Code-style custom
        // margin (active line number follows the caret). The built-in margin is never re-added:
        // ShowLineNumbers is forced false below and the custom margin's visibility is driven by
        // the ShowLineNumbers dependency property (bound in FilePreviewView).
        var builtInMargin = CodeEditor.TextArea.LeftMargins.OfType<LineNumberMargin>().FirstOrDefault();
        if (builtInMargin is not null)
        {
            CodeEditor.TextArea.LeftMargins.Remove(builtInMargin);
        }
        if (!CodeEditor.TextArea.LeftMargins.Contains(_lineNumberMargin))
        {
            CodeEditor.TextArea.LeftMargins.Add(_lineNumberMargin);
        }
        CodeEditor.ShowLineNumbers = false;
        _lineNumberMargin.Visibility = ShowLineNumbers ? Visibility.Visible : Visibility.Collapsed;
        _lineNumberMargin.RefreshTheme();
        _matchRenderer.RefreshBrushes();
        _lineEmphasis.RefreshBrushes();
        _readingHighlights.RefreshBrushes();
        _rulers.RefreshBrushes();
    }

    private void ApplyTheme()
    {
        CodeEditor.Background = Brush("EditorBrush") ?? Brushes.Transparent;
        CodeEditor.Foreground = Brush("TextBrush") ?? Brushes.Black;
        CodeEditor.LineNumbersForeground = Brush("CodeLineNumberBrush") ?? CodeEditor.LineNumbersForeground;
        CodeEditor.TextArea.SelectionBrush = Brush("CodeSelectionBrush") ?? CodeEditor.TextArea.SelectionBrush;
        // 迷你地图刷子(含 token kind 字典)一次性重解析 + 内容版本失效。
        OnMinimapThemeChanged();
        // 语义着色器的冻结调色板(Typeface 缓存同时清空):每 token 查找回到 O(1) 命中。
        _semanticTokens.RefreshTheme();
        _indentGuides.RefreshTheme();
        ThemeFoldingMargin();
        ApplyEditorRendering();
        ApplyHighlightingTheme();
        RequestMinimapDraw();
        RedrawCodeOverview();
        UpdateSticky();
    }

    /// <summary>内置兜底只在"完整快照(IsComplete)且有 token"时才关闭:部分快照窗口内,已分词
    /// 行由 colorizer 覆盖为最终配色,未分词行保留内置/缺省色——否则首屏之外的行会整篇
    /// "缺省色 → 正确高亮"闪一遍。空快照(初始/释放)HasTokens=false,走内置兜底。</summary>
    private void UpdateHighlightingFallback()
    {
        CodeEditor.SyntaxHighlighting =
            _presentationSnapshot is { IsComplete: true } && _semanticTokens.HasTokens
                ? null
                : _highlightingDefinition;
    }

    /// <summary>Recolors the current syntax-highlighting definition from the theme palette and
    /// redraws, so code colors follow live theme switches (not just the built-in light defaults).</summary>
    private void ApplyHighlightingTheme()
    {
        if (_highlightingDefinition is null)
        {
            return;
        }

        ThemeHighlightingColorizer.ApplyDefinitionTheme(_highlightingDefinition);
        CodeEditor.TextArea.TextView.Redraw();
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        // 主题广播在生产路径恒为 UI 线程;测试进程里 xunit 并行测试类可能从工作线程 Raise,
        // 而已加载视图(含未及卸载的残留实例)的订阅会收到回调——非宿主线程只归组回宿主
        // Dispatcher,避免跨线程触碰 DependencyObject(其余视图订阅者同此模式)。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(ApplyTheme);
            return;
        }

        ApplyTheme();
    }

    private Brush? Brush(string key) => TryFindResource(key) as Brush ?? Application.Current?.TryFindResource(key) as Brush;

    private Brush ResolveBrush(string key) => Brush(key) ?? Brushes.Gray;

    /// <summary>Resolves the theme brush for a semantic token kind. Shared by the inline colorizer
    /// and the minimap tint so both stay on the same palette.</summary>
    private Brush? TokenBrushForKind(CodeTokenKind kind) => Brush(kind switch
    {
        CodeTokenKind.Comment => "CodeTokenCommentBrush",
        CodeTokenKind.String => "CodeTokenStringBrush",
        CodeTokenKind.Number => "CodeTokenNumberBrush",
        CodeTokenKind.Keyword => "CodeTokenKeywordBrush",
        CodeTokenKind.Type => "CodeTokenTypeBrush",
        CodeTokenKind.Function => "CodeTokenFunctionBrush",
        CodeTokenKind.Tag => "CodeTokenTagBrush",
        CodeTokenKind.Attribute => "CodeTokenAttributeBrush",
        CodeTokenKind.Link => "CodeTokenLinkBrush",
        _ => "TextBrush",
    });

    private Pen? Pen(string key)
    {
        var brush = Brush(key);
        return brush is null ? null : new Pen(brush, 1);
    }

    private void InvalidateBackground() => CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

    // ===== 括号匹配 + 当前词高亮 + Sticky scroll =====

    // 阅读高亮扫描窗口:可视行 ± 行数(vscode wordHighlighter 只处理视口附近的思路)。
    private const int WordHighlightMarginLines = 100;
    private const int BracketHighlightMarginLines = 200;
    private const int MaxWordHighlightOccurrences = 500;
    // 当前词出现段集缓存:光标停留在同一词(且文档/扫描窗口未变)时直接复用,
    // 词级左移/右移不再重扫(段集是文档绝对偏移,窗口不变即不变)。
    private string? _wordHighlightCacheWord;
    private int _wordHighlightCacheDocumentVersion = -1;
    private int _wordHighlightCacheStartLine = -1;
    private int _wordHighlightCacheEndLine = -1;
    private IReadOnlyList<(int Start, int Length)>? _wordHighlightCacheSegments;

    /// <summary>光标变化后刷新阅读高亮:匹配括号对 + 当前词出现。
    /// 词扫描收敛到可视行 ±100 行(vscode wordHighlighter 只高亮视口附近);括号对扫描
    /// 收敛到可视行 ±200 行,配对超出窗口即视为无(返回 null)。结果总数保持 500 上限。</summary>
    private void RefreshReadingHighlights()
    {
        if (CodeEditor.Document is not { } document || CodeEditor.TextArea.Selection.Length > 0)
        {
            _readingHighlights.SetSegments([]);
            return;
        }

        var segments = new List<(int Start, int Length)>();
        if (document.TextLength == 0)
        {
            _readingHighlights.SetSegments(segments);
            return;
        }

        var caret = Math.Clamp(CodeEditor.CaretOffset, 0, document.TextLength - 1);
        var lineCount = document.LineCount;
        var span = GetVisibleLineSpan();

        // 括号对:±200 行窗口;partner 在窗外 → 停止扫描返回 null。
        var bracketWindow = ExpandScanWindow(
            span?.FirstLine ?? 1, span?.LastLine ?? lineCount, lineCount, BracketHighlightMarginLines);
        var bracketBounds = ScanWindowToOffset(document, bracketWindow.StartLine, bracketWindow.EndLine);
        if (ComputeBracketPair(document, caret, bracketBounds.StartOffset, bracketBounds.EndOffset) is { } bracket)
        {
            segments.Add(bracket);
        }

        // 当前词:±100 行窗口 + 词级缓存。窗口化档下若扫描窗口覆盖整个文档
        // (整窗 = 全量扫描,失去收敛意义)则跳过词高亮。
        var wordWindow = ExpandScanWindow(
            span?.FirstLine ?? 1, span?.LastLine ?? lineCount, lineCount, WordHighlightMarginLines);
        var windowCoversWholeDocument = wordWindow.StartLine <= 1 && wordWindow.EndLine >= lineCount;
        // 单词符词不产生高亮(与原实现一致)。
        if (!(IsWindowedTier && windowCoversWholeDocument)
            && GetCaretWord(document, caret) is { Word.Length: >= 2 } word)
        {
            IReadOnlyList<(int Start, int Length)>? wordSegments = null;
            if (_wordHighlightCacheWord is { } cachedWord
                && cachedWord == word.Word
                && _wordHighlightCacheDocumentVersion == _documentContentVersion
                && _wordHighlightCacheStartLine == wordWindow.StartLine
                && _wordHighlightCacheEndLine == wordWindow.EndLine)
            {
                wordSegments = _wordHighlightCacheSegments;
            }
            else
            {
                var bounds = ScanWindowToOffset(document, wordWindow.StartLine, wordWindow.EndLine);
                wordSegments = ComputeWordOccurrences(document, word.Word, bounds.StartOffset, bounds.EndOffset);
                _wordHighlightCacheWord = word.Word;
                _wordHighlightCacheDocumentVersion = _documentContentVersion;
                _wordHighlightCacheStartLine = wordWindow.StartLine;
                _wordHighlightCacheEndLine = wordWindow.EndLine;
                _wordHighlightCacheSegments = wordSegments;
            }

            if (wordSegments is not null)
            {
                segments.AddRange(wordSegments);
            }
        }

        _readingHighlights.SetSegments(segments);
    }

    /// <summary>当前可视行的 1-based 行跨度;视觉行不可用(文档切换的瞬态)时返回 null,
    /// 调用方退回全文档窗口。</summary>
    private (int FirstLine, int LastLine)? GetVisibleLineSpan()
    {
        var visualLines = GetValidVisualLines(CodeEditor.TextArea.TextView);
        if (visualLines is null || visualLines.Count == 0)
        {
            return null;
        }

        return (visualLines[0].FirstDocumentLine.LineNumber, visualLines[^1].LastDocumentLine.LineNumber);
    }

    /// <summary>可视行窗口外扩 marginLines 并夹取到 [1, lineCount](纯函数,便于单测)。
    /// 输入行号按 1-based 且 &ge; 1;firstVisible &gt; lastVisible 的退化输入按单行处理。</summary>
    internal static (int StartLine, int EndLine) ExpandScanWindow(
        int firstVisibleLine, int lastVisibleLine, int lineCount, int marginLines)
    {
        if (lineCount <= 0)
        {
            return (1, 1);
        }

        var start = Math.Max(1, firstVisibleLine - marginLines);
        var end = Math.Min(lineCount, lastVisibleLine + marginLines);
        if (end < start)
        {
            end = start;
        }

        return (start, end);
    }

    /// <summary>扫描窗口(1-based 行)→ 文档字符偏移区间 [StartOffset, EndOffset)(EndLine 整行包含)。
    /// 覆盖全文档时直接给 (0, TextLength) 省去行树查询。</summary>
    private static (int StartOffset, int EndOffset) ScanWindowToOffset(
        TextDocument document, int startLine, int endLine)
    {
        if (startLine <= 1 && endLine >= document.LineCount)
        {
            return (0, document.TextLength);
        }

        var start = document.GetLineByNumber(Math.Clamp(startLine, 1, document.LineCount)).Offset;
        var end = endLine >= document.LineCount
            ? document.TextLength
            : document.GetLineByNumber(endLine + 1).Offset;
        return (start, end);
    }

    /// <summary>光标所在"词"的原文与文档内范围(词 = 非空白/非标点连续段,与原全文实现一致)。
    /// 逐字符走文档 API,不物化全文。</summary>
    private static (string Word, int Begin, int End)? GetCaretWord(TextDocument document, int caret)
    {
        var textLength = document.TextLength;
        if (textLength == 0)
        {
            return null;
        }

        var start = Math.Clamp(caret, 0, textLength - 1);
        if (char.IsWhiteSpace(document.GetCharAt(start)) || char.IsPunctuation(document.GetCharAt(start)))
        {
            return null;
        }

        var begin = start;
        while (begin > 0
            && !char.IsWhiteSpace(document.GetCharAt(begin - 1))
            && !char.IsPunctuation(document.GetCharAt(begin - 1)))
        {
            begin--;
        }

        var end = start;
        while (end < textLength
            && !char.IsWhiteSpace(document.GetCharAt(end))
            && !char.IsPunctuation(document.GetCharAt(end)))
        {
            end++;
        }

        return (document.GetText(begin, end - begin), begin, end);
    }

    private static readonly HashSet<char> BracketPairs = ['(', ')', '[', ']', '{', '}'];

    /// <summary>光标(或光标前一格)所在括号的对侧 partner,扫描限定在 [windowStart, windowEnd)
    /// 内,超窗即停并返回 null(vscode 括号高亮不做全文扫描)。</summary>
    private static (int Start, int Length)? ComputeBracketPair(
        TextDocument document, int caret, int windowStart, int windowEnd)
    {
        var textLength = document.TextLength;
        if (textLength == 0)
        {
            return null;
        }

        var open = Math.Clamp(caret, 0, textLength - 1);
        // 光标在右括号之后(光标指向右括号下一字符)时回退一格取括号。
        if (!BracketPairs.Contains(document.GetCharAt(open)) && open > 0 && BracketPairs.Contains(document.GetCharAt(open - 1)))
        {
            open--;
        }
        if (!BracketPairs.Contains(document.GetCharAt(open)))
        {
            return null;
        }

        char c = document.GetCharAt(open);
        bool isClosing = c is ')' or ']' or '}';
        char opening = c;
        char closing = c switch { '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', _ => '}' };
        int depth = 0;
        if (!isClosing)
        {
            // 向前扫描:先遇到目标闭括号为匹配;深度归零处即配对;超窗停止。
            var stop = Math.Min(windowEnd, textLength);
            for (var i = open + 1; i < stop; i++)
            {
                var ch = document.GetCharAt(i);
                if (ch == opening)
                {
                    depth++;
                }
                else if (ch == closing)
                {
                    if (depth == 0)
                    {
                        return (open, 1 + i - open);
                    }
                    depth--;
                }
            }
        }
        else
        {
            // 向后扫描:找到配对的左括号;超窗停止。
            var stop = Math.Max(windowStart, 0);
            for (var i = open - 1; i >= stop; i--)
            {
                var ch = document.GetCharAt(i);
                if (ch == closing)
                {
                    depth++;
                }
                else if (ch == opening)
                {
                    if (depth == 0)
                    {
                        return (i, 1 + open - i);
                    }
                    depth--;
                }
            }
        }
        return null;
    }

    /// <summary>当前词在 [windowStart, windowEnd) 内的全部出现(Ordinal,上限
    /// <see cref="MaxWordHighlightOccurrences"/>);窗口外出现不纳入(有界扫描语义)。
    /// 词本身在窗外(光标行在窗口外)时返回空列表而非 null,与"扫过无命中"同义。</summary>
    private static IReadOnlyList<(int Start, int Length)>? ComputeWordOccurrences(
        TextDocument document, string word, int windowStart, int windowEnd)
    {
        var textLength = document.TextLength;
        if (word.Length == 0 || textLength == 0)
        {
            return null;
        }

        var from = Math.Clamp(windowStart, 0, textLength);
        var to = Math.Clamp(windowEnd, from, textLength);
        if (to <= from)
        {
            return [];
        }

        var windowText = document.GetText(from, to - from);
        var results = new List<(int Start, int Length)>();
        var offset = 0;
        while (results.Count < MaxWordHighlightOccurrences
            && (offset = windowText.IndexOf(word, offset, StringComparison.Ordinal)) >= 0)
        {
            results.Add((from + offset, word.Length));
            offset += word.Length;
        }

        return results;
    }

    /// <summary>VS Code sticky scroll:滚动时把覆盖视口顶端的折叠段头钉在编辑器顶部。
    /// 只钉尚未折叠且段首已滚出视口上缘的段(段首仍可见时不重复显示),最多 <see cref="MaxStickyRows"/>
    /// 级(外层→内层);点击行跳转段首。滚动/折叠/主题变化时重算。</summary>
    private const int MaxStickyRows = 3;

    private void UpdateSticky()
    {
        if (CodeEditor is null || StickyHost is null || StickyRows is null)
        {
            return;
        }

        if (CodeEditor.Document is not { } document || document.LineCount == 0)
        {
            _lastStickyChain = null;
            if (StickyRows.Children.Count > 0) StickyRows.Children.Clear();
            StickyHost.Visibility = Visibility.Collapsed;
            return;
        }

        // 窗口化档默认关 sticky(即使用户偏好为开):段头链缓存 + 每帧滚动重算对大文件窗口
        // 是额外开销,且窗口翻页时折叠链语义不完整。Full 档不受影响。
        var enabled = ShowStickyScroll && _foldSections.Count > 0 && _editorScroll is not null
            && !IsWindowedTier;
        if (!enabled)
        {
            _lastStickyChain = null;
            if (StickyRows.Children.Count > 0) StickyRows.Children.Clear();
            StickyHost.Visibility = Visibility.Collapsed;
            return;
        }

        var lineHeight = Math.Max(1, CodeEditor.TextArea.TextView.DefaultLineHeight);
        var topLine = Math.Max(1, 1 + (int)(_editorScroll!.VerticalOffset / lineHeight));
        var chain = _foldSections
            .Where(section => section.StartLine < topLine && section.EndLine >= topLine)
            .Take(MaxStickyRows)
            .ToArray();
        if (chain.Length == 0)
        {
            _lastStickyChain = null;
            if (StickyRows.Children.Count > 0) StickyRows.Children.Clear();
            StickyHost.Visibility = Visibility.Collapsed;
            return;
        }

        // 链未变化 → 跳过重建:滚动逐像素移动 topLine,但折叠链只在越过折叠区边界时改变。
        // 旧实现每个滚动事件都 Clear + 重建全部 sticky 行(Border/StackPanel/TextBlock 分配)。
        if (SameStickyChain(chain, _lastStickyChain) && StickyHost.Visibility == Visibility.Visible)
        {
            return;
        }

        _lastStickyChain = chain;
        StickyRows.Children.Clear();
        StickyHost.Visibility = Visibility.Visible;
        var bright = ResolveBrush("BrightTextBrush");
        var muted = ResolveBrush("MutedTextBrush");
        var hover = ResolveBrush("HoverBrush");
        for (var i = 0; i < chain.Length; i++)
        {
            var section = chain[i];
            var header = document.GetText(document.GetLineByNumber(Math.Clamp(section.StartLine, 1, document.LineCount))).Trim();
            if (header.Length > 96)
            {
                header = header[..96] + "…";
            }

            var row = new Border
            {
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Padding = new Thickness(8 + i * 12, 2, 8, 2),
            };
            row.MouseLeftButtonUp += (_, _) => JumpToLine(section.StartLine, emphasize: false);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = header,
                FontFamily = CodeEditor.FontFamily,
                FontSize = CodeEditor.FontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = bright,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"第 {section.StartLine} 行",
                FontFamily = CodeEditor.FontFamily,
                FontSize = CodeEditor.FontSize,
                Foreground = muted,
                Margin = new Thickness(8, 0, 0, 0),
            });
            row.Child = panel;
            row.MouseEnter += (_, _) => row.Background = hover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            StickyRows.Children.Add(row);
        }
    }

    private static bool SameStickyChain(CodeFoldSection[] chain, CodeFoldSection[]? cached)
    {
        if (cached is null || cached.Length != chain.Length)
        {
            return false;
        }

        for (var i = 0; i < chain.Length; i++)
        {
            if (!ReferenceEquals(chain[i], cached[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Hosts a pre-rendered DrawingVisual inside the minimap/overview canvas (one visual
    /// per throttled redraw keeps the visible-window redraw cheap). <see cref="Update"/> 支持
    /// 宿主的持久化复用:迷你地图的内容/滑块各自持有一个 host,数据变化时只换 DrawingVisual
    /// 引用(不重建 UIElement,不参与布局测量),滚动只换滑块 host 的 visual。</summary>
    private sealed class MiniVisualHost : FrameworkElement
    {
        private DrawingVisual _visual;

        public MiniVisualHost(DrawingVisual visual) => _visual = visual;

        public void Update(DrawingVisual visual)
        {
            if (ReferenceEquals(_visual, visual))
            {
                return;
            }

            _visual = visual;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.DrawDrawing(_visual.Drawing);
        }
    }

    /// <summary>VS Code-style line-number margin: the caret line's number is tinted with
    /// <c>CodeLineNumberActiveBrush</c> while the rest use <c>CodeLineNumberBrush</c>. Replaces
    /// AvalonEdit's built-in single-color <see cref="LineNumberMargin"/> (removed in
    /// <see cref="ApplyEditorRendering"/>).</summary>
    private sealed class CodeLineNumberMargin : AbstractMargin
    {
        // FormattedText 缓存上限:行号文本基数本就不大(位数 × 10),相对行号模式下
        // 距离值最多样本数 ≈ 可视行数;超限整体清空即可,避免无界增长。
        private const int MaxFormattedTextCache = 4096;

        private readonly CodeDocumentView _owner;
        private readonly Dictionary<(string Text, double FontSize, double Dpi), FormattedText> _formattedTextCache = new();
        private Brush _numberBrush = Brushes.Gray;
        private Brush _activeNumberBrush = Brushes.White;
        // Typeface/DPI 只在字体/字号/DPI 实际变化时重建(原实现每帧每行都 new)。
        private Typeface? _typeface;
        private string? _typefaceFontSource;
        private double _typefaceFontSize;
        private double _typefaceDpi;

        public CodeLineNumberMargin(CodeDocumentView owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            owner.CodeEditor.TextArea.Caret.PositionChanged += (_, _) => InvalidateVisual();
        }

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
            {
                oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
                oldTextView.ScrollOffsetChanged -= OnScrollOffsetChanged;
            }
            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += OnVisualLinesChanged;
                // Scrolling can change line Y positions without replacing the visual-line
                // collection; redraw the margin explicitly in that case.
                newTextView.ScrollOffsetChanged += OnScrollOffsetChanged;
            }
            base.OnTextViewChanged(oldTextView, newTextView);
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        private void OnScrollOffsetChanged(object? sender, EventArgs e) => InvalidateVisual();

        public void RefreshTheme()
        {
            _numberBrush = _owner.Brush("CodeLineNumberBrush") ?? Brushes.Gray;
            _activeNumberBrush = _owner.Brush("CodeLineNumberActiveBrush") ?? Brushes.White;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // Stable width: fit the widest line number in the whole document (VS Code adapts the
            // gutter to the current line count) + 12px padding each side.
            var digits = Math.Max(1, _owner.CodeEditor.Document.LineCount.ToString().Length);
            var digitWidth = _owner.EditorFontSize * 0.6;
            return new Size(12 + digits * digitWidth + 12, availableSize.Height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (TextView is not { } view)
            {
                return;
            }

            var caretLine = _owner.CodeEditor.TextArea.Caret.Line;
            var lastDocumentLine = -1;
            var visualLines = GetValidVisualLines(view);
            if (visualLines is null)
            {
                return;
            }

            var fontSize = _owner.EditorFontSize;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = GetOrCreateTypeface(fontSize, dpi);

            foreach (var visualLine in visualLines)
            {
                var lineNumber = visualLine.FirstDocumentLine.LineNumber;
                if (lineNumber == lastDocumentLine)
                {
                    continue; // wrapped continuation of the same document line
                }
                lastDocumentLine = lineNumber;

                var isActive = lineNumber == caretLine;
                var numberText = lineNumber.ToString();
                if (_owner.LineNumbersRelative && !isActive)
                {
                    // VS Code relative line numbers: 其余行显示与光标的距离。
                    numberText = Math.Abs(lineNumber - caretLine).ToString();
                }

                // 按 (文本, 字号, DPI) 组合键复用 FormattedText:滚动只是位置变化,
                // 行号文本/字体不变时命中缓存,不再每帧每行重建。刷子逐行切换
                // (SetForegroundBrush)保持激活行/普通行配色与原实现一致。
                var formatted = GetOrCreateFormattedText(numberText, fontSize, dpi, typeface);
                formatted.SetForegroundBrush(isActive ? _activeNumberBrush : _numberBrush);
                // GetVisualPosition returns document-space coordinates (relative to the document
                // top). The margin is fixed to the viewport — only the text layer is repositioned
                // on scroll — so subtract the current scroll offset to get the viewport Y.
                var y = view.GetVisualPosition(
                    new TextViewPosition(lineNumber, 1),
                    VisualYPosition.TextTop).Y - view.ScrollOffset.Y;
                drawingContext.DrawText(formatted, new Point(ActualWidth - formatted.Width - 12, y));
            }
        }

        /// <summary>Typeface 缓存:字体源或字号/DPI 变化才重建(原实现每次 OnRender 都 new
        /// Typeface + 每行 new FormattedText + GetDpi)。</summary>
        private Typeface GetOrCreateTypeface(double fontSize, double dpi)
        {
            var fontSource = _owner.CodeEditor.FontFamily.Source;
            if (_typeface is null || _typefaceFontSource != fontSource
                || _typefaceFontSize != fontSize || _typefaceDpi != dpi)
            {
                _typeface = new Typeface(fontSource);
                _typefaceFontSource = fontSource;
                _typefaceFontSize = fontSize;
                _typefaceDpi = dpi;
            }

            return _typeface;
        }

        private FormattedText GetOrCreateFormattedText(
            string numberText, double fontSize, double dpi, Typeface typeface)
        {
            var key = (numberText, fontSize, dpi);
            if (!_formattedTextCache.TryGetValue(key, out var formatted))
            {
                if (_formattedTextCache.Count > MaxFormattedTextCache)
                {
                    _formattedTextCache.Clear();
                }

                formatted = new FormattedText(
                    numberText,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    _numberBrush,
                    dpi);
                _formattedTextCache[key] = formatted;
            }

            return formatted;
        }
    }

    /// <summary>Faint vertical indentation guides (every 4th leading column), themed via
    /// <c>CodeIndentationGuideBrush</c>, that span each contiguous indent block (VS Code
    /// behavior) plus a brighter active guide at the caret line's indent level. Drawn only for
    /// the visible lines.</summary>
    private sealed class IndentationGuideRenderer : IBackgroundRenderer
    {
        private readonly CodeDocumentView _owner;
        private Brush _brush = Brushes.Transparent;
        private Brush _activeBrush = Brushes.Transparent;

        public IndentationGuideRenderer(CodeDocumentView owner)
        {
            _owner = owner;
            // The active guide follows the caret; AvalonEdit does not invalidate the background
            // layer on caret movement, so redraw it explicitly.
            owner.CodeEditor.TextArea.Caret.PositionChanged += (_, _) =>
                owner.CodeEditor.TextArea.TextView.InvalidateLayer(Layer);
        }

        public KnownLayer Layer => KnownLayer.Background;
        public bool Enabled { get; set; } = true;

        public void RefreshTheme()
        {
            _brush = _owner.Brush("CodeIndentationGuideBrush") ?? Brushes.Transparent;
            _activeBrush = _owner.Brush("CodeIndentationGuideActiveBrush") ?? _brush;
            _owner.CodeEditor.TextArea.TextView.InvalidateLayer(Layer);
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var visualLines = GetValidVisualLines(textView);
            if (!Enabled || textView.Document is not { } document || visualLines is null || visualLines.Count == 0)
            {
                return;
            }

            var caretLine = _owner.CodeEditor.TextArea.Caret.Line;

            // First pass: leading indent column per visible line (space 1, tab 4).
            var lines = new List<(VisualLine Visual, int Leading)>(visualLines.Count);
            var maxLevel = 0;
            foreach (var visualLine in visualLines)
            {
                var docLine = visualLine.FirstDocumentLine;
                var text = document.GetText(docLine.Offset, Math.Min(docLine.Length, 512));
                var leading = 0;
                foreach (var ch in text)
                {
                    if (ch == ' ')
                    {
                        leading++;
                    }
                    else if (ch == '\t')
                    {
                        leading += 4;
                    }
                    else
                    {
                        break;
                    }
                }
                lines.Add((visualLine, leading));
                maxLevel = Math.Max(maxLevel, leading / 4);
            }

            if (maxLevel == 0)
            {
                return;
            }

            var pen = new Pen(_brush, 1);
            var activePen = new Pen(_activeBrush, 1);

            // Second pass: for each 4-column level, draw one vertical line spanning every
            // contiguous run of lines indented at least to that level (guides run through the
            // whole indent block, not just the leading segment).
            for (var level = 1; level <= maxLevel; level++)
            {
                var column = level * 4;
                var i = 0;
                while (i < lines.Count)
                {
                    if (lines[i].Leading < column)
                    {
                        i++;
                        continue;
                    }

                    var start = i;
                    var x = textView.GetVisualPosition(
                        new TextViewPosition(lines[start].Visual.FirstDocumentLine.LineNumber, column + 1),
                        VisualYPosition.LineTop).X - textView.ScrollOffset.X;
                    if (x > textView.ActualWidth)
                    {
                        break;
                    }

                    while (i < lines.Count && lines[i].Leading >= column)
                    {
                        i++;
                    }

                    var end = i;
                    // VisualTop is document-space; the background layer draws in viewport space.
                    var y1 = lines[start].Visual.VisualTop - textView.ScrollOffset.Y;
                    var y2 = lines[end - 1].Visual.VisualTop + lines[end - 1].Visual.Height - textView.ScrollOffset.Y;
                    // Half-pixel alignment keeps the one-pixel guide crisp on a 96-DPI
                    // workbench without making it visually heavier than the code glyphs.
                    var guideX = Math.Round(x) + 0.5;
                    drawingContext.DrawLine(pen, new Point(guideX, y1), new Point(guideX, y2));
                }
            }

            // Active guide: the guide column at the caret line's indentation, drawn brighter.
            var caretIndex = lines.FindIndex(entry => entry.Visual.FirstDocumentLine.LineNumber == caretLine);
            if (caretIndex >= 0)
            {
                var activeColumn = (lines[caretIndex].Leading / 4) * 4;
                if (activeColumn >= 4)
                {
                    var caretVisual = lines[caretIndex].Visual;
                    var activeX = textView.GetVisualPosition(
                        new TextViewPosition(caretVisual.FirstDocumentLine.LineNumber, activeColumn + 1),
                        VisualYPosition.LineTop).X - textView.ScrollOffset.X;
                    if (activeX <= textView.ActualWidth)
                    {
                        var activeGuideX = Math.Round(activeX) + 0.5;
                        var activeY = caretVisual.VisualTop - textView.ScrollOffset.Y;
                        drawingContext.DrawLine(
                            activePen,
                            new Point(activeGuideX, activeY),
                            new Point(activeGuideX, activeY + caretVisual.Height));
                    }
                }
            }
        }
    }

    /// <summary>垂直标尺(VS Code editor.rulers):在指定列画覆盖全文的全高细线。</summary>
    private sealed class RulerRenderer : IBackgroundRenderer
    {
        private readonly CodeDocumentView _owner;
        private double[] _columns = [];
        private Pen? _pen;

        public RulerRenderer(CodeDocumentView owner) => _owner = owner;

        public KnownLayer Layer => KnownLayer.Background;

        public void RefreshBrushes()
        {
            _pen = new Pen(_owner.ResolveBrush("BorderBrush"), 1);
            _pen.Freeze();
            _owner.InvalidateBackground();
        }

        public void RefreshColumns(double[]? columns)
        {
            _columns = columns ?? [];
            _owner.InvalidateBackground();
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var visualLines = GetValidVisualLines(textView);
            if (_columns.Length == 0 || _pen is null || visualLines is null || visualLines.Count == 0 || textView.WideSpaceWidth <= 0)
            {
                return;
            }

            var left = _owner.CodeEditor.TextArea.LeftMargins.OfType<FrameworkElement>().Sum(margin => margin.ActualWidth);
            for (var i = 0; i < _columns.Length; i++)
            {
                var x = left + _columns[i] * textView.WideSpaceWidth;
                drawingContext.DrawLine(_pen, new Point(x, 0), new Point(x, textView.ActualHeight));
            }
        }
    }

    /// <summary>阅读高亮(括号匹配对 + 当前词全文出现):与搜索匹配同款分段背景渲染,
    /// 括号对用当前匹配色、词出现用中性选区色。只读文档不变化,段集由光标变化刷新。</summary>
    private sealed class ReadingHighlightRenderer : IBackgroundRenderer
    {
        private readonly CodeDocumentView _owner;
        private TextSegmentCollection<TextSegment>? _segments;
        private Brush _bracketBrush = Brushes.Transparent;
        private Brush _wordBrush = Brushes.Transparent;

        public ReadingHighlightRenderer(CodeDocumentView owner) => _owner = owner;

        public KnownLayer Layer => KnownLayer.Selection;

        public void RefreshBrushes()
        {
            _bracketBrush = _owner.ResolveBrush("CodeSearchCurrentMatchBrush");
            _wordBrush = _owner.ResolveBrush("CodeSearchMatchBrush");
            _owner.InvalidateBackground();
        }

        public void SetSegments(IReadOnlyList<(int Start, int Length)> segments)
        {
            var collection = _segments;
            if (collection is null)
            {
                if (segments.Count == 0) return;
                collection = new TextSegmentCollection<TextSegment>(_owner.CodeEditor.Document);
                _segments = collection;
            }

            collection.Clear();
            foreach (var (start, length) in segments)
            {
                if (start >= 0 && start + length <= _owner.CodeEditor.Document.TextLength)
                {
                    collection.Add(new TextSegment { StartOffset = start, Length = length });
                }
            }

            _owner.InvalidateBackground();
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var visualLines = GetValidVisualLines(textView);
            if (_segments is null || _segments.Count == 0 || visualLines is null || visualLines.Count == 0)
            {
                return;
            }

            var start = visualLines[0].FirstDocumentLine.Offset;
            var end = visualLines[^1].LastDocumentLine.EndOffset;
            var relevant = _segments.FindOverlappingSegments(start, end - start);
            if (relevant.Count == 0)
            {
                return;
            }

            var brackets = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            var words = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            foreach (var segment in relevant)
            {
                // 括号对 = 全选中长度 1-2 的短段;词出现 = 其余。
                if (segment.Length <= 2)
                {
                    brackets.AddSegment(textView, segment);
                }
                else
                {
                    words.AddSegment(textView, segment);
                }
            }

            if (words.CreateGeometry() is { } wordGeometry)
            {
                drawingContext.DrawGeometry(_wordBrush, null, wordGeometry);
            }

            if (brackets.CreateGeometry() is { } bracketGeometry)
            {
                drawingContext.DrawGeometry(_bracketBrush, null, bracketGeometry);
            }
        }
    }

    /// <summary>Draws search matches (all + distinct current match) as background layers above the
    /// text. Read-only documents never change, so plain segments are repositioned only on document
    /// swap — the classic AvalonEdit marker-renderer pattern.</summary>
    private sealed class SearchMatchRenderer : IBackgroundRenderer
    {
        private readonly CodeDocumentView _owner;
        private TextSegmentCollection<TextSegment>? _segments;
        private TextDocument? _document;
        private int _currentOffset = -1;
        private Brush _matchBrush = Brushes.Transparent;
        private Brush _currentBrush = Brushes.Transparent;

        public SearchMatchRenderer(CodeDocumentView owner) => _owner = owner;

        public KnownLayer Layer => KnownLayer.Selection;

        public void RefreshBrushes()
        {
            _matchBrush = _owner.Brush("CodeSearchMatchBrush") ?? Brushes.Transparent;
            _currentBrush = _owner.Brush("CodeSearchCurrentMatchBrush") ?? Brushes.Transparent;
            if (_segments is not null)
            {
                _owner.InvalidateBackground();
            }
        }

        public void DocumentChanged(TextDocument document)
        {
            _document = document;
            _segments = new TextSegmentCollection<TextSegment>(document);
        }

        public void SetMatches(IReadOnlyList<TextSearchMatch> matches, int currentIndex)
        {
            var collection = _segments;
            if (collection is null)
            {
                return;
            }

            collection.Clear();
            _currentOffset = -1;
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                // Search results can briefly belong to the previous tab while AvalonEdit is
                // replacing its document. Never add an offset outside the current document.
                var documentLength = _document?.TextLength ?? 0;
                var safeOffset = Math.Clamp(match.Offset, 0, documentLength);
                var safeLength = Math.Clamp(match.Length, 0, documentLength - safeOffset);
                if (match.Offset < 0 || safeLength <= 0)
                {
                    continue;
                }

                collection.Add(new TextSegment { StartOffset = safeOffset, Length = safeLength });
                if (i == currentIndex)
                {
                    _currentOffset = safeOffset;
                }
            }

            _owner.InvalidateBackground();
        }

        public void Clear()
        {
            _segments?.Clear();
            _currentOffset = -1;
            _owner.InvalidateBackground();
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var visualLines = GetValidVisualLines(textView);
            if (_segments is null || _segments.Count == 0 || visualLines is null || visualLines.Count == 0)
            {
                return;
            }

            var start = visualLines[0].FirstDocumentLine.Offset;
            var end = visualLines[^1].LastDocumentLine.EndOffset;
            var relevant = _segments.FindOverlappingSegments(start, end - start);
            if (relevant.Count == 0)
            {
                return;
            }

            var all = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            var current = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            foreach (var segment in relevant)
            {
                if (segment.StartOffset == _currentOffset)
                {
                    current.AddSegment(textView, segment);
                }
                else
                {
                    all.AddSegment(textView, segment);
                }
            }

            if (all.CreateGeometry() is { } allGeometry)
            {
                drawingContext.DrawGeometry(_matchBrush, null, allGeometry);
            }

            if (current.CreateGeometry() is { } currentGeometry)
            {
                drawingContext.DrawGeometry(_currentBrush, null, currentGeometry);
            }
        }
    }

    /// <summary>Transient whole-line background that briefly emphasizes the go-to-line target.</summary>
    private sealed class LineEmphasisRenderer : IBackgroundRenderer
    {
        private readonly CodeDocumentView _owner;
        private TextSegmentCollection<TextSegment>? _segments;
        private TextDocument? _document;
        private readonly DispatcherTimer _timer;
        private TextSegment? _segment;
        private Brush _brush = Brushes.Transparent;

        public LineEmphasisRenderer(CodeDocumentView owner)
        {
            _owner = owner;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                _segment = null;
                _owner.InvalidateBackground();
            };
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void RefreshBrushes()
        {
            _brush = _owner.Brush("CodeSearchCurrentMatchBrush") ?? Brushes.Transparent;
            _owner.InvalidateBackground();
        }

        public void DocumentChanged(TextDocument document)
        {
            _segments = new TextSegmentCollection<TextSegment>(document);
            _document = document;
            _segment = null;
        }

        public void EmphasizeFor(int lineNumber, TimeSpan duration)
        {
            if (_segments is null || _document is null || _document.LineCount == 0)
            {
                return;
            }

            var number = Math.Clamp(lineNumber, 1, _document.LineCount);
            var documentLine = _document.GetLineByNumber(number);
            _segments.Clear();
            _segment = new TextSegment { StartOffset = documentLine.Offset, Length = Math.Max(1, documentLine.Length) };
            _segments.Add(_segment);
            _timer.Interval = duration;
            _timer.Stop();
            _timer.Start();
            _owner.InvalidateBackground();
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var visualLines = GetValidVisualLines(textView);
            if (_segments is null || _segment is null || visualLines is null || visualLines.Count == 0)
            {
                return;
            }

            var start = visualLines[0].FirstDocumentLine.Offset;
            var end = visualLines[^1].LastDocumentLine.EndOffset;
            var relevant = _segments.FindOverlappingSegments(start, end - start);
            if (relevant.Count == 0)
            {
                return;
            }

            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            foreach (var segment in relevant)
            {
                builder.AddSegment(textView, segment);
            }

            if (builder.CreateGeometry() is { } geometry)
            {
                drawingContext.DrawGeometry(_brush, null, geometry);
            }
        }
    }
}
