using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nornia.Desktop.Views;

/// <summary>
/// Read-only diff viewer with two layouts: an inline (unified) editor showing old|new numbers,
/// per-kind line backgrounds, a left change bar and an overview ruler; and a side-by-side pair of
/// editors built from the row-aligned <c>GitSideBySideRow</c> model with 1:1 synchronized scrolling.
/// Hunk actions mutate only the selected working-tree/index patch through <see cref="DiffTab"/>;
/// the text editors themselves remain read-only.
/// </summary>
public partial class DiffDocumentView : UserControl
{
    /// <summary>等宽字体家族,统一取自 App.xaml 的 MonoFontFamily 令牌(兜底走 Code/ViewFonts)。</summary>
    internal static string MonoFontFamilyValue =>
        (Application.Current?.TryFindResource("MonoFontFamily") as FontFamily)?.Source
        ?? Nornia.Desktop.Code.ViewFonts.MonoFamily;

    // AvalonEdit invalidates VisualLines while a document is being replaced or remeasured.
    // Custom gutters/renderers are invoked during that window as well, so never read the
    // collection without checking its state. The validity can change between the check and the
    // property access; the exception guard covers that small race too.
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
    private DiffTab? _tab;
    private IReadOnlyList<DiffRenderLine> _inlineLines = [];
    private IReadOnlyList<DiffRenderLine> _oldLines = [];
    private IReadOnlyList<DiffRenderLine> _newLines = [];
    private DiffLineNumberMargin? _inlineNumbers;
    private DiffLineNumberMargin? _oldNumbers;
    private DiffLineNumberMargin? _newNumbers;
    private DiffLineBackgroundRenderer? _inlineBackground;
    private DiffLineBackgroundRenderer? _oldBackground;
    private DiffLineBackgroundRenderer? _newBackground;
    private ScrollViewer? _oldScroll;
    private ScrollViewer? _newScroll;
    private ScrollViewer? _inlineScroll;
    private bool _syncingScroll;
    private bool _initialSideSyncScheduled;
    private bool _hasExpectedSideOffsets;
    private double _expectedOldHorizontal;
    private double _expectedOldVertical;
    private double _expectedNewHorizontal;
    private double _expectedNewVertical;
    private Brush _overviewAddedBrush = Brushes.Green;
    private Brush _overviewRemovedBrush = Brushes.Red;
    private string _highlightingName = string.Empty;
    private IHighlightingDefinition? _highlightingDefinition;
    private readonly CodeTokenColorizer _inlineSemanticTokens;
    private readonly CodeTokenColorizer _oldSemanticTokens;
    private readonly CodeTokenColorizer _newSemanticTokens;
    private CancellationTokenSource? _presentationCancellation;
    private int _presentationVersion;
    private DiffMetaLineTransformer? _inlineMeta;
    private DiffMetaLineTransformer? _oldMeta;
    private DiffMetaLineTransformer? _newMeta;
    private DiffIntralineBackgroundRenderer? _inlineIntraline;
    private DiffIntralineBackgroundRenderer? _oldIntraline;
    private DiffIntralineBackgroundRenderer? _newIntraline;
    // Persistent context-collapse projections (kept once the tab finishes loading so the expanded
    // placeholder state survives rebuilds / mode switches; ExpandAtDisplayIndex mutates them).
    private DiffDisplayMap? _inlineMap;
    private DiffDisplayMap? _sideMap;
    // 原始 hunk 头文本(按 hunk 序):折叠投影会把 @@ 元数据从内联文档隐藏(hideHunkHeader),
    // sticky 行仍显示真实头文本;第 k 个显示 hunk 头与第 k 个原始 hunk 头一一对应
    // (折叠只压缩上下文,不增删 hunk 头)。
    private IReadOnlyList<string> _rawHunkHeaderTexts = [];
    // 并排两侧的行对齐变更掩码(RebuildDocuments 随 _oldLines/_newLines 一次性重建):
    // 变更导航/概览 current 标记按并排行号空间定位。每次重建只分配一次,
    // 滚动路径上的 BlockAtIndex 扫描零分配。
    private IReadOnlyList<DiffRenderLine> _sideChangeMask = [];
    private bool _positionedFirstChange;
    private readonly List<int> _diffFindMatches = [];
    private DiffFindRenderer? _diffFindRenderer;
    private int _hoverHunkIndex = -1;
    private TextView? _hoverTextView;
    private DiffScrollState? _pendingScrollRestore;

    public DiffDocumentView()
    {
        InitializeComponent();
        _inlineSemanticTokens = new CodeTokenColorizer(Brush, () => InlineEditor.FontFamily);
        _oldSemanticTokens = new CodeTokenColorizer(Brush, () => OldEditor.FontFamily);
        _newSemanticTokens = new CodeTokenColorizer(Brush, () => NewEditor.FontFamily);
        InlineEditor.TextArea.TextView.LineTransformers.Add(_inlineSemanticTokens);
        OldEditor.TextArea.TextView.LineTransformers.Add(_oldSemanticTokens);
        NewEditor.TextArea.TextView.LineTransformers.Add(_newSemanticTokens);
        DataContextChanged += OnDataContextChanged;
        OverviewCanvas.SizeChanged += (_, _) => RedrawOverview();
        OverviewCanvas.MouseLeftButtonDown += OnOverviewMouseDown;
        OldOverviewCanvas.SizeChanged += (_, _) => RedrawSideOverviews();
        OldOverviewCanvas.MouseLeftButtonDown += OnSideOverviewMouseDown;
        NewOverviewCanvas.SizeChanged += (_, _) => RedrawSideOverviews();
        NewOverviewCanvas.MouseLeftButtonDown += OnSideOverviewMouseDown;
        // Click-to-expand collapsed context placeholders (VS Code "… N unchanged lines …" bar).
        InlineEditor.TextArea.TextView.PreviewMouseLeftButtonDown += OnEditorPreviewMouseDown;
        OldEditor.TextArea.TextView.PreviewMouseLeftButtonDown += OnEditorPreviewMouseDown;
        NewEditor.TextArea.TextView.PreviewMouseLeftButtonDown += OnEditorPreviewMouseDown;
        InlineEditor.TextArea.TextView.MouseMove += OnDiffTextViewMouseMove;
        OldEditor.TextArea.TextView.MouseMove += OnDiffTextViewMouseMove;
        NewEditor.TextArea.TextView.MouseMove += OnDiffTextViewMouseMove;
        // AvalonEdit's line-number and change-marker margins are outside TextView's normal
        // MouseMove route. Listen at the pane level so the hunk action bar stays active while
        // the pointer crosses the complete review gutter (including the action-button column).
        InlinePane.PreviewMouseMove += OnDiffPaneMouseMove;
        SideBySidePane.PreviewMouseMove += OnDiffPaneMouseMove;
        InlineEditor.ContextMenuOpening += OnDiffContextMenuOpening;
        OldEditor.ContextMenuOpening += OnDiffContextMenuOpening;
        NewEditor.ContextMenuOpening += OnDiffContextMenuOpening;
        InlinePane.MouseLeave += (_, _) => HideHunkActionBars();
        SideBySidePane.MouseLeave += (_, _) => HideHunkActionBars();
        // Active line-number highlight follows each editor's caret.
        InlineEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            _inlineNumbers?.SetActiveLine(InlineEditor.TextArea.Caret.Line);
            UpdateDiffPosition(InlineEditor);
        };
        OldEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            _oldNumbers?.SetActiveLine(OldEditor.TextArea.Caret.Line);
            UpdateDiffPosition(OldEditor);
        };
        NewEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            _newNumbers?.SetActiveLine(NewEditor.TextArea.Caret.Line);
            UpdateDiffPosition(NewEditor);
        };
        foreach (var editor in new[] { InlineEditor, OldEditor, NewEditor })
        {
            editor.PreviewGotKeyboardFocus += (sender, _) => _positionedEditor = (TextEditor)sender!;
        }
        ApplyEditorTheme();
        Loaded += (_, _) =>
        {
            ThemeEvents.ThemeChanged += OnThemeChanged;
            // DataContextChanged can run before AvalonEdit applies its template. Re-hook here
            // so the side-by-side scroll viewers are available and vertical sync is reliable.
            HookScrollSync(this, new RoutedEventArgs());
            UpdateLayoutMode();
            ScheduleInitialSideScrollSync();
            if (_tab is not null)
            {
                _ = ApplyPresentationAsync();
            }
        };
        Unloaded += (_, _) => ThemeEvents.ThemeChanged -= OnThemeChanged;
        Unloaded += (_, _) => CancelPresentation();
        SizeChanged += (_, _) =>
        {
            UpdateLayoutMode();
            ScheduleInitialSideScrollSync();
        };
    }

    private TextEditor _positionedEditor = null!;

    /// <summary>窄窗自动内联(VS Code useInlineViewWhenSpaceIsLimited):窗口过窄时即使请求并排
    /// 也以内联显示(仅显示层,不改 DiffMode)。</summary>
    private const double NarrowInlineWidth = 540;

    private bool EffectiveInline() =>
        _tab is null || _tab.IsInlineDiff || (_tab.UseInlineWhenNarrow && ActualWidth < NarrowInlineWidth);

    private void UpdateLayoutMode()
    {
        var inline = EffectiveInline();
        if (InlinePane is not null)
        {
            InlinePane.Visibility = inline ? Visibility.Visible : Visibility.Collapsed;
        }
        if (SideBySidePane is not null)
        {
            SideBySidePane.Visibility = inline ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateDiffSticky();
    }

    /// <summary>光标位置(最后一个获得键盘焦点的编辑面;CLR 只读浏览)→ 写标签的
    /// <see cref="DiffTab.CaretPositionText"/>;显示位置在窗口主状态栏的"当前文档状态"段
    /// (原视图内底栏已移除)。caret 无效时不覆盖旧值。</summary>
    private void UpdateDiffPosition(TextEditor editor)
    {
        if (_tab is null)
        {
            return;
        }

        if (editor.TextArea.Caret.Position is { } caret && caret.Line > 0)
        {
            _tab.CaretPositionText = $"Ln {caret.Line}, Col {caret.Column}";
        }
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        // 与 CodeDocumentView.OnThemeChanged 相同:非宿主线程的广播归组回宿主 Dispatcher。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(ApplyEditorTheme);
            return;
        }

        ApplyEditorTheme();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        CancelPresentation();
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab.DiffLines.CollectionChanged -= OnDiffLinesChanged;
            _tab.SideBySideRows.CollectionChanged -= OnSideBySideRowsChanged;
            _tab.ChangeNavigationRequested -= OnChangeNavigationRequested;
        }

        _tab = DataContext as DiffTab;
        if (_tab is null)
        {
            return;
        }

        _inlineMap = null;
        _sideMap = null;
        _positionedFirstChange = false;
        _tab.PropertyChanged += OnTabPropertyChanged;
        _tab.DiffLines.CollectionChanged += OnDiffLinesChanged;
        _tab.SideBySideRows.CollectionChanged += OnSideBySideRowsChanged;
        _tab.ChangeNavigationRequested += OnChangeNavigationRequested;
        _positionedEditor = InlineEditor;
        _ = _tab.LoadAsync();
        RebuildDocuments();
        ApplyHighlighting();
        HookScrollSync(this, new RoutedEventArgs());
        UpdateLayoutMode();
        ScheduleInitialSideScrollSync();
        RefreshDiffFind();
    }

    private void OnDiffTextViewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is TextView textView)
        {
            UpdateHunkActionBar(textView);
        }
    }

    private void OnDiffPaneMouseMove(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, InlinePane))
        {
            UpdateHunkActionBar(InlineEditor.TextArea.TextView);
            return;
        }

        if (ReferenceEquals(sender, SideBySidePane))
        {
            UpdateHunkActionBar(GetSideTextViewAtPointer());
        }
    }

    private TextView GetSideTextViewAtPointer()
    {
        if (_hoverTextView is not null && SideHunkActionBar.IsMouseOver)
        {
            return _hoverTextView;
        }

        var pointer = Mouse.GetPosition(SideBySidePane);
        var oldOrigin = OldEditor.TransformToAncestor(SideBySidePane).Transform(new Point(0, 0));
        var newOrigin = NewEditor.TransformToAncestor(SideBySidePane).Transform(new Point(0, 0));
        var oldRight = oldOrigin.X + OldEditor.ActualWidth;
        var newLeft = newOrigin.X;

        if (pointer.X >= newLeft)
        {
            return NewEditor.TextArea.TextView;
        }

        if (pointer.X <= oldRight)
        {
            return OldEditor.TextArea.TextView;
        }

        return _hoverTextView ?? NewEditor.TextArea.TextView;
    }

    private void UpdateHunkActionBar(TextView textView)
    {
        if (!TryGetHunkAtPointer(textView, out var hunkIndex, out var y))
        {
            // Keep the current bar alive while moving through its hit area. The pane-level
            // handler receives these moves, but the button itself is not a document line.
            if (InlineHunkActionBar.IsMouseOver || SideHunkActionBar.IsMouseOver)
            {
                return;
            }

            HideHunkActionBars();
            return;
        }

        var sideBySide = ReferenceEquals(textView, OldEditor.TextArea.TextView)
            || ReferenceEquals(textView, NewEditor.TextArea.TextView);
        var bar = sideBySide ? SideHunkActionBar : InlineHunkActionBar;
        var canvas = sideBySide ? SideHunkActionCanvas : InlineHunkActionCanvas;
        ConfigureHunkActionBar(bar, hunkIndex);
        bar.Visibility = Visibility.Visible;
        canvas.UpdateLayout();
        bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // VS Code renders hunk review actions in a dedicated gutter: compact icons beside the
        // change block, outside the code and overview-ruler hit areas. The toolbar does not
        // take a document row or change the editor's text layout.
        // VS Code renders the hunk toolbar in a dedicated gutter. Inline uses the left gutter;
        // side-by-side uses the modified (new) editor's gutter and keeps one shared toolbar.
        var gutterLeft = sideBySide
            ? Math.Max(0, (canvas.ActualWidth + 5) / 2 + 2.5)
            : 2;
        Canvas.SetLeft(bar, gutterLeft);
        var boundsLines = ReferenceEquals(textView, OldEditor.TextArea.TextView)
            ? _oldLines
            : ReferenceEquals(textView, NewEditor.TextArea.TextView) ? _newLines : _inlineLines;
        var hunkBounds = FindVisibleHunkBounds(textView, boundsLines, hunkIndex);
        var actionTop = hunkBounds is { } bounds
            ? (bounds.Top + bounds.Bottom - bar.DesiredSize.Height) / 2
            : y + 1;
        Canvas.SetTop(bar, Math.Max(2, actionTop));
        _hoverHunkIndex = hunkIndex;
        _hoverTextView = textView;
    }

    private void ConfigureHunkActionBar(Border bar, int hunkIndex)
    {
        if (_tab is null)
        {
            bar.Visibility = Visibility.Collapsed;
            return;
        }

        InlineStageHunkButton.Visibility = Visibility.Collapsed;
        InlineRestoreHunkButton.Visibility = Visibility.Collapsed;
        InlineUnstageHunkButton.Visibility = Visibility.Collapsed;
        SideStageHunkButton.Visibility = Visibility.Collapsed;
        SideRestoreHunkButton.Visibility = Visibility.Collapsed;
        SideUnstageHunkButton.Visibility = Visibility.Collapsed;

        var stage = _tab.CanApplyHunk(hunkIndex, GitHunkOperation.Stage);
        var restore = _tab.CanApplyHunk(hunkIndex, GitHunkOperation.Restore);
        var unstage = _tab.CanApplyHunk(hunkIndex, GitHunkOperation.Unstage);
        bar.Tag = hunkIndex;
        var side = ReferenceEquals(bar, SideHunkActionBar);
        (side ? SideStageHunkButton : InlineStageHunkButton).Visibility = stage ? Visibility.Visible : Visibility.Collapsed;
        (side ? SideRestoreHunkButton : InlineRestoreHunkButton).Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
        (side ? SideUnstageHunkButton : InlineUnstageHunkButton).Visibility = unstage ? Visibility.Visible : Visibility.Collapsed;
        if (!stage && !restore && !unstage)
        {
            bar.Visibility = Visibility.Collapsed;
        }
    }

    private bool TryGetHunkAtPointer(TextView textView, out int hunkIndex, out double y)
    {
        hunkIndex = -1;
        y = 0;

        var lines = ReferenceEquals(textView, InlineEditor.TextArea.TextView)
            ? _inlineLines
            : ReferenceEquals(textView, OldEditor.TextArea.TextView) ? _oldLines : _newLines;
        var displayIndex = GetDisplayLineAtPointer(textView);
        if (displayIndex < 0 || displayIndex >= lines.Count || lines[displayIndex].HunkIndex < 0)
        {
            return false;
        }

        hunkIndex = lines[displayIndex].HunkIndex;
        // Anchor the toolbar to the first changed line of the hunk (rather than the line under
        // the pointer), matching VS Code's diff review widget. For a pure insertion/deletion the
        // corresponding side can be an empty row, so fall back to the first row carrying the hunk.
        var anchorIndex = FindHunkAnchorLine(lines, hunkIndex, displayIndex);
        var visualLines = GetValidVisualLines(textView);
        if (visualLines is null)
        {
            return false;
        }

        var visualLine = visualLines.FirstOrDefault(line =>
            line.FirstDocumentLine.LineNumber <= anchorIndex + 1 && line.LastDocumentLine.LineNumber >= anchorIndex + 1);
        y = visualLine is null ? 0 : visualLine.VisualTop - textView.ScrollOffset.Y;
        return _tab?.CanApplyHunk(hunkIndex, GitHunkOperation.Stage) == true
            || _tab?.CanApplyHunk(hunkIndex, GitHunkOperation.Unstage) == true
            || _tab?.CanApplyHunk(hunkIndex, GitHunkOperation.Restore) == true;
    }

    private static int GetDisplayLineAtPointer(TextView textView)
    {
        var visualLines = GetValidVisualLines(textView);
        if (visualLines is null || visualLines.Count == 0)
        {
            return -1;
        }

        var point = Mouse.GetPosition(textView);
        if (point.Y < 0 || point.Y > textView.ActualHeight)
        {
            return -1;
        }

        var documentY = point.Y + textView.ScrollOffset.Y;
        var visualLine = visualLines.FirstOrDefault(line =>
            documentY >= line.VisualTop && documentY < line.VisualTop + line.Height);
        return visualLine is null ? -1 : visualLine.FirstDocumentLine.LineNumber - 1;
    }

    private static int FindHunkAnchorLine(IReadOnlyList<DiffRenderLine> lines, int hunkIndex, int fallback)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].HunkIndex == hunkIndex && lines[index].IsChange)
            {
                return index;
            }
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].HunkIndex == hunkIndex)
            {
                return index;
            }
        }

        return fallback;
    }

    private static (double Top, double Bottom)? FindVisibleHunkBounds(
        TextView textView, IReadOnlyList<DiffRenderLine> lines, int hunkIndex)
    {
        var start = -1;
        var end = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].HunkIndex != hunkIndex)
            {
                continue;
            }

            if (lines[index].IsChange)
            {
                start = start < 0 ? index : Math.Min(start, index);
                end = index;
            }
        }

        if (start < 0)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                if (lines[index].HunkIndex == hunkIndex)
                {
                    start = start < 0 ? index : start;
                    end = index;
                }
            }
        }

        if (start < 0 || end < 0)
        {
            return null;
        }

        var visualLines = GetValidVisualLines(textView);
        if (visualLines is null)
        {
            return null;
        }

        var visible = visualLines.Where(line =>
            line.LastDocumentLine.LineNumber >= start + 1
            && line.FirstDocumentLine.LineNumber <= end + 1).ToArray();
        return visible.Length == 0
            ? null
            : (visible.Min(line => line.VisualTop - textView.ScrollOffset.Y),
               visible.Max(line => line.VisualTop + line.Height - textView.ScrollOffset.Y));
    }
    private void HideHunkActionBars()
    {
        InlineHunkActionBar.Visibility = Visibility.Collapsed;
        SideHunkActionBar.Visibility = Visibility.Collapsed;
        _hoverHunkIndex = -1;
        _hoverTextView = null;
    }

    private void OnDiffContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextEditor editor || editor.ContextMenu is not { } menu)
        {
            return;
        }

        var textView = editor.TextArea.TextView;

        foreach (var item in menu.Items.OfType<FrameworkElement>().Where(item => Equals(item.Tag, "diff-hunk-action")).ToArray())
        {
            menu.Items.Remove(item);
        }

        var position = textView.GetPositionFloor(Mouse.GetPosition(textView));
        var hunkIndex = position is { Line: >= 1 } hit ? HunkIndexAtDisplayLine(textView, hit.Line - 1) : -1;
        if (_tab is null || hunkIndex < 0)
        {
            return;
        }

        var actions = new List<(string Header, GitHunkOperation Operation)>
        {
            ("还原块", GitHunkOperation.Restore),
            ("暂存块", GitHunkOperation.Stage),
            ("取消暂存块", GitHunkOperation.Unstage),
        };
        var insertAt = 0;
        menu.Items.Insert(insertAt++, new Separator { Tag = "diff-hunk-action" });
        foreach (var (header, operation) in actions)
        {
            if (!_tab.CanApplyHunk(hunkIndex, operation))
            {
                continue;
            }

            var item = new MenuItem { Header = header, Tag = "diff-hunk-action", ToolTip = $"对第 {hunkIndex + 1} 个 Diff 块执行“{header}”" };
            item.Click += async (_, _) => await ExecuteHunkOperationAsync(hunkIndex, operation);
            menu.Items.Insert(insertAt++, item);
        }

        if (insertAt == 1)
        {
            menu.Items.RemoveAt(0);
        }
    }

    private int HunkIndexAtDisplayLine(TextView textView, int displayIndex)
    {
        var lines = ReferenceEquals(textView, InlineEditor.TextArea.TextView)
            ? _inlineLines
            : ReferenceEquals(textView, OldEditor.TextArea.TextView) ? _oldLines : _newLines;
        return displayIndex >= 0 && displayIndex < lines.Count ? lines[displayIndex].HunkIndex : -1;
    }

    private async void InlineStageHunk_Click(object sender, RoutedEventArgs e) => await ExecuteHunkOperationAsync(GetActionHunkIndex(sender), GitHunkOperation.Stage);
    private async void InlineRestoreHunk_Click(object sender, RoutedEventArgs e) => await ExecuteHunkOperationAsync(GetActionHunkIndex(sender), GitHunkOperation.Restore);
    private async void InlineUnstageHunk_Click(object sender, RoutedEventArgs e) => await ExecuteHunkOperationAsync(GetActionHunkIndex(sender), GitHunkOperation.Unstage);
    private async void SideStageHunk_Click(object sender, RoutedEventArgs e) => await ExecuteHunkOperationAsync(GetActionHunkIndex(sender), GitHunkOperation.Stage);
    private async void SideRestoreHunk_Click(object sender, RoutedEventArgs e) => await ExecuteHunkOperationAsync(GetActionHunkIndex(sender), GitHunkOperation.Restore);
    private async void SideUnstageHunk_Click(object sender, RoutedEventArgs e) => await ExecuteHunkOperationAsync(GetActionHunkIndex(sender), GitHunkOperation.Unstage);

    private int GetActionHunkIndex(object sender)
    {
        if (sender is DependencyObject current)
        {
            while (current is not null)
            {
                if (current is FrameworkElement { Tag: int hunkIndex })
                {
                    return hunkIndex;
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }

        return _hoverHunkIndex;
    }
    private async Task ExecuteHunkOperationAsync(int hunkIndex, GitHunkOperation operation)
    {
        if (_tab is null || ! _tab.CanApplyHunk(hunkIndex, operation))
        {
            return;
        }

        var state = CaptureDiffScrollState(hunkIndex);
        HideHunkActionBars();
        await _tab.ApplyHunkAsync(hunkIndex, operation);
        _pendingScrollRestore = state;
        await Dispatcher.InvokeAsync(RestorePendingScrollState, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private DiffScrollState CaptureDiffScrollState(int hunkIndex) => new(
        CapturePaneScrollState(InlineEditor, _inlineLines, hunkIndex, _inlineScroll),
        CapturePaneScrollState(OldEditor, _oldLines, hunkIndex, _oldScroll),
        CapturePaneScrollState(NewEditor, _newLines, hunkIndex, _newScroll));

    private static PaneScrollState CapturePaneScrollState(
        TextEditor editor,
        IReadOnlyList<DiffRenderLine> lines,
        int hunkIndex,
        ScrollViewer? scroll)
    {
        scroll ??= FindScrollViewer(editor);
        if (scroll is null)
        {
            return new(0, 0, 0, hunkIndex);
        }

        var lineHeight = Math.Max(1, editor.TextArea.TextView.DefaultLineHeight);
        var topLine = Math.Max(0, (int)Math.Floor(scroll.VerticalOffset / lineHeight));
        var hunkLine = FindHunkLine(lines, hunkIndex);
        var anchorOffset = hunkLine >= 0 ? hunkLine - topLine : 0;
        return new(
            ScrollProgress(scroll.HorizontalOffset, MaxScroll(scroll.ExtentWidth, scroll.ViewportWidth)),
            ScrollProgress(scroll.VerticalOffset, MaxScroll(scroll.ExtentHeight, scroll.ViewportHeight)),
            anchorOffset,
            hunkIndex);
    }

    private void RestorePendingScrollState()
    {
        var state = _pendingScrollRestore;
        _pendingScrollRestore = null;
        if (state is null || _tab is null)
        {
            return;
        }

        if (EffectiveInline())
        {
            RestorePaneScrollState(InlineEditor, _inlineLines, state.Inline, state.HunkIndex);
            return;
        }

        _syncingScroll = true;
        _hasExpectedSideOffsets = false;
        try
        {
            RestorePaneScrollState(OldEditor, _oldLines, state.Old, state.HunkIndex);
            RestorePaneScrollState(NewEditor, _newLines, state.New, state.HunkIndex);
        }
        finally
        {
            _syncingScroll = false;
            if (_oldScroll is not null && _newScroll is not null)
            {
                _expectedOldHorizontal = _oldScroll.HorizontalOffset;
                _expectedOldVertical = _oldScroll.VerticalOffset;
                _expectedNewHorizontal = _newScroll.HorizontalOffset;
                _expectedNewVertical = _newScroll.VerticalOffset;
                _hasExpectedSideOffsets = true;
            }
        }
    }

    private static void RestorePaneScrollState(
        TextEditor editor,
        IReadOnlyList<DiffRenderLine> lines,
        PaneScrollState state,
        int hunkIndex)
    {
        var scroll = FindScrollViewer(editor);
        if (scroll is null)
        {
            return;
        }

        var lineHeight = Math.Max(1, editor.TextArea.TextView.DefaultLineHeight);
        var hunkLine = FindHunkLine(lines, hunkIndex);
        var vertical = hunkLine >= 0
            ? Math.Max(0, (hunkLine - state.AnchorOffset) * lineHeight)
            : state.VerticalProgress * MaxScroll(scroll.ExtentHeight, scroll.ViewportHeight);
        var horizontal = state.HorizontalProgress * MaxScroll(scroll.ExtentWidth, scroll.ViewportWidth);
        scroll.ScrollToVerticalOffset(vertical);
        scroll.ScrollToHorizontalOffset(horizontal);
    }

    private static int FindHunkLine(IReadOnlyList<DiffRenderLine> lines, int hunkIndex)
    {
        if (hunkIndex < 0)
        {
            return -1;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].HunkIndex == hunkIndex)
            {
                return index;
            }
        }

        return -1;
    }

    private sealed record DiffScrollState(
        PaneScrollState Inline,
        PaneScrollState Old,
        PaneScrollState New)
    {
        public int HunkIndex => Inline.HunkIndex >= 0 ? Inline.HunkIndex : Old.HunkIndex >= 0 ? Old.HunkIndex : New.HunkIndex;
    }

    private sealed record PaneScrollState(double HorizontalProgress, double VerticalProgress, int AnchorOffset, int HunkIndex = -1);

    private void HookScrollSync(object sender, RoutedEventArgs e)
    {
        _hasExpectedSideOffsets = false;
        _oldScroll?.ScrollChanged -= OnSideEditorScrollChanged;
        _newScroll?.ScrollChanged -= OnSideEditorScrollChanged;
        _oldScroll = FindScrollViewer(OldEditor);
        _newScroll = FindScrollViewer(NewEditor);
        _inlineScroll = FindScrollViewer(InlineEditor);
        if (_oldScroll is not null)
        {
            _oldScroll.ScrollChanged += OnSideEditorScrollChanged;
        }

        if (_newScroll is not null)
        {
            _newScroll.ScrollChanged += OnSideEditorScrollChanged;
        }

        if (_inlineScroll is not null)
        {
            _inlineScroll.ScrollChanged -= OnInlineScrollChanged;
            _inlineScroll.ScrollChanged += OnInlineScrollChanged;
        }
    }

    private void OnSideEditorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        RedrawSideOverviews();
        if (sender is not ScrollViewer source)
        {
            return;
        }

        // ScrollOffsetChanged covers both wheel/scrollbar axes. ScrollTo* can raise the target
        // event after the initiating event returns, so remember the offsets actually accepted by
        // both editors and consume those delayed echoes. Do not push a clamped target offset back
        // into the source: that feedback loop is what made short one-hunk diffs oscillate.
        if (IsExpectedSideOffset(source))
        {
            return;
        }

        if (_syncingScroll || _tab is null || _tab.IsInlineDiff || !_tab.SynchronizeScrolling)
        {
            return;
        }

        SynchronizeSideEditors(source);
    }

    /// <summary>在两侧文档和 AvalonEdit 模板完成布局后执行一次首屏对齐。DataContextChanged
    /// 经常早于内部 ScrollViewer 创建，单纯接事件会漏掉首次 JumpToChange 的位置，导致
    /// 第一次打开未同步、再次打开才正常。</summary>
    private void ScheduleInitialSideScrollSync()
    {
        if (_initialSideSyncScheduled || !IsLoaded || _tab is null || !_tab.IsSideBySideDiff || EffectiveInline())
        {
            return;
        }

        _initialSideSyncScheduled = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _initialSideSyncScheduled = false;
                if (_tab is null || !_tab.IsSideBySideDiff || EffectiveInline())
                {
                    return;
                }

                // The Loaded callback normally has already found these controls; retrying the
                // hook here also covers a template that was applied during the same layout pass.
                if (_oldScroll is null || _newScroll is null)
                {
                    HookScrollSync(this, new RoutedEventArgs());
                }

                if (_oldScroll is not null && _newScroll is not null)
                {
                    SynchronizeSideEditors(_oldScroll);
                }
            },
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void SynchronizeSideEditors(ScrollViewer source)
    {
        if (_syncingScroll)
        {
            return;
        }

        var target = ReferenceEquals(source, _oldScroll) ? _newScroll : _oldScroll;
        if (target is null)
        {
            return;
        }

        _syncingScroll = true;
        try
        {
            var verticalProgress = ScrollProgress(source.VerticalOffset,
                source.ExtentHeight - source.ViewportHeight);
            var horizontalProgress = ScrollProgress(source.HorizontalOffset,
                source.ExtentWidth - source.ViewportWidth);
            // Sync the logical scrollbar position, rather than copying pixels. The two sides can
            // have different extents because one side has longer lines or an auto scrollbar;
            // mapping 0..1 keeps both thumbs at the same relative position and makes bottom-up
            // wheel scrolling stable even when one side clamps to a different pixel maximum.
            target.ScrollToHorizontalOffset(horizontalProgress * MaxScroll(target.ExtentWidth, target.ViewportWidth));
            target.ScrollToVerticalOffset(verticalProgress * MaxScroll(target.ExtentHeight, target.ViewportHeight));
        }
        finally
        {
            _syncingScroll = false;
            if (_oldScroll is not null && _newScroll is not null)
            {
                _expectedOldHorizontal = _oldScroll.HorizontalOffset;
                _expectedOldVertical = _oldScroll.VerticalOffset;
                _expectedNewHorizontal = _newScroll.HorizontalOffset;
                _expectedNewVertical = _newScroll.VerticalOffset;
                _hasExpectedSideOffsets = true;
            }
        }
    }

    private static double MaxScroll(double extent, double viewport) =>
        double.IsFinite(extent) && double.IsFinite(viewport)
            ? Math.Max(0, extent - viewport)
            : 0;

    private static double ScrollProgress(double offset, double maximum)
    {
        if (!double.IsFinite(offset) || !double.IsFinite(maximum) || maximum <= 0.01)
        {
            return 0;
        }

        return Math.Clamp(offset / maximum, 0, 1);
    }

    private bool IsExpectedSideOffset(ScrollViewer source)
    {
        if (!_hasExpectedSideOffsets)
        {
            return false;
        }

        var expectedHorizontal = ReferenceEquals(source, _oldScroll)
            ? _expectedOldHorizontal
            : _expectedNewHorizontal;
        var expectedVertical = ReferenceEquals(source, _oldScroll)
            ? _expectedOldVertical
            : _expectedNewVertical;
        return Math.Abs(source.HorizontalOffset - expectedHorizontal) <= 0.01
            && Math.Abs(source.VerticalOffset - expectedVertical) <= 0.01;
    }

    private void OnInlineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        RedrawOverview();
        UpdateDiffSticky();
    }

    private static ScrollViewer? FindScrollViewer(TextEditor editor) =>
        editor.Template.FindName("PART_ScrollViewer", editor) as ScrollViewer;

    // The tab fills its collections line by line while loading; rebuilding the editors per
    // notification would be quadratic, so consecutive notifications coalesce into one rebuild.
    private bool _rebuildScheduled;

    private void ScheduleRebuild()
    {
        if (_rebuildScheduled)
        {
            return;
        }

        _rebuildScheduled = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _rebuildScheduled = false;
                RebuildDocuments();
            },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnDiffLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // A hunk operation reloads the source collections. Persistent collapse maps point at
            // the previous snapshot and can index past the new, shorter side-by-side document.
            _inlineMap = null;
            _sideMap = null;
        }

        ScheduleRebuild();
    }

    private void OnSideBySideRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _inlineMap = null;
            _sideMap = null;
        }

        ScheduleRebuild();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiffTab.IsLoaded) or nameof(DiffTab.DiffMode) or nameof(DiffTab.IsContextCollapsed))
        {
            if (e.PropertyName == nameof(DiffTab.IsLoaded))
            {
                if (!_tab!.IsLoaded && _tab.HasDiff)
                {
                    // Automatic repository refreshes replace the document collections. Capture a
                    // relative scroll position before the reset so a long diff does not jump to
                    // the top; hunk actions may replace this with their more precise anchor.
                    _pendingScrollRestore = CaptureDiffScrollState(-1);
                }
                else if (_tab.IsLoaded && _pendingScrollRestore is not null)
                {
                    _ = Dispatcher.InvokeAsync(RestorePendingScrollState, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            }

            ScheduleRebuild();
            UpdateLayoutMode();
        }
        else if (e.PropertyName == nameof(DiffTab.ShowIntralineChanges))
        {
            InlineEditor.TextArea.TextView.Redraw();
            OldEditor.TextArea.TextView.Redraw();
            NewEditor.TextArea.TextView.Redraw();
        }
        else if (e.PropertyName == nameof(DiffTab.IsFindBarOpen))
        {
            if (_tab!.IsFindBarOpen)
            {
                DiffFindBox.Focus();
                DiffFindBox.SelectAll();
            }
            else
            {
                ClearDiffFind();
            }
        }
        else if (e.PropertyName == nameof(DiffTab.FindText))
        {
            RefreshDiffFind();
        }
        else if (e.PropertyName == nameof(DiffTab.ShowOverviewRuler))
        {
            RedrawOverview();
            RedrawSideOverviews();
            UpdateDiffSticky();
        }
        else if (e.PropertyName == nameof(DiffTab.EditorFontSize))
        {
            // The line-number margin draws at the editor font size; re-measure and redraw only.
            _inlineNumbers?.InvalidateMeasure();
            _inlineNumbers?.InvalidateVisual();
            _oldNumbers?.InvalidateMeasure();
            _oldNumbers?.InvalidateVisual();
            _newNumbers?.InvalidateMeasure();
            _newNumbers?.InvalidateVisual();
        }
    }

    // ===== Diff 内查找(统一文本) =====

    private void ToggleFindBar_Click(object sender, RoutedEventArgs e)
    {
        if (_tab is null)
        {
            return;
        }

        _tab.IsFindBarOpen = !_tab.IsFindBarOpen;
    }

    private void DiffFindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DiffFindJump(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_tab is not null)
            {
                _tab.IsFindBarOpen = false;
            }
            e.Handled = true;
        }
    }

    private void DiffFindPrevious_Click(object sender, RoutedEventArgs e) => DiffFindJump(-1);

    private void DiffFindNext_Click(object sender, RoutedEventArgs e) => DiffFindJump(1);

    private void ClearDiffFind()
    {
        _diffFindMatches.Clear();
        if (_tab is not null)
        {
            _tab.FindCount = 0;
            _tab.FindIndex = 0;
        }
        _diffFindRenderer?.Clear();
    }

    private void RefreshDiffFind()
    {
        _diffFindMatches.Clear();
        var query = _tab?.FindText ?? string.Empty;
        if (_tab is null || string.IsNullOrEmpty(query) || _inlineLines.Count == 0)
        {
            _diffFindMatches.Clear();
            ClearDiffFind();
            return;
        }

        for (var i = 0; i < _inlineLines.Count; i++)
        {
            if (_inlineLines[i].Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _diffFindMatches.Add(i);
            }
        }

        _tab.FindCount = _diffFindMatches.Count;
        _tab.FindIndex = -1;
        var segments = new List<(int Start, int Length)>();
        var document = InlineEditor.Document;
        foreach (var index in _diffFindMatches)
        {
            var text = _inlineLines[index].Text;
            var column = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (column < 0)
            {
                continue;
            }

            var offset = document.GetLineByNumber(index + 1).Offset + column;
            segments.Add((offset, Math.Min(query.Length, Math.Max(0, text.Length - column))));
        }

        var renderer = _diffFindRenderer ??= new DiffFindRenderer(this);
        renderer.SetSegments(segments, -1);
    }

    private void DiffFindJump(int step)
    {
        if (_tab is null || _diffFindMatches.Count == 0)
        {
            return;
        }

        var index = (_tab.FindIndex + step + _diffFindMatches.Count) % _diffFindMatches.Count;
        _tab.FindIndex = index;
        var matchIndex = _diffFindMatches[index];
        var text = _inlineLines[matchIndex].Text;
        var column = text.IndexOf(_tab.FindText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        if (column < 0)
        {
            column = 0;
        }

        var lineNumber = matchIndex + 1;
        InlineEditor.ScrollTo(lineNumber, 0);
        InlineEditor.CaretOffset = InlineEditor.Document.GetLineByNumber(lineNumber).Offset + column;
        InlineEditor.SelectionStart = InlineEditor.CaretOffset;
        InlineEditor.SelectionLength = Math.Min(_tab.FindText?.Length ?? 0, Math.Max(0, text.Length - column));

        // 并排模式:同步把对应旧/新行号带进两侧视图(旧/新列号由统一行长行号映射)。
        var line = _inlineLines[matchIndex];
        if (line.OldLineNumber is { } oldLine && OldEditor.Document is { } oldDoc && oldLine <= oldDoc.LineCount)
        {
            OldEditor.ScrollTo(oldLine, 0);
        }
        if (line.NewLineNumber is { } newLine && NewEditor.Document is { } newDoc && newLine <= newDoc.LineCount)
        {
            NewEditor.ScrollTo(newLine, 0);
        }

        var segments = new List<(int Start, int Length)>();
        var document = InlineEditor.Document;
        var query = _tab.FindText ?? string.Empty;
        foreach (var matchLine in _diffFindMatches)
        {
            var matchText = _inlineLines[matchLine].Text;
            var matchColumn = matchText.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (matchColumn < 0)
            {
                continue;
            }

            var offset = document.GetLineByNumber(matchLine + 1).Offset + matchColumn;
            segments.Add((offset, Math.Min(query.Length, Math.Max(0, matchText.Length - matchColumn))));
        }

        _diffFindRenderer?.SetSegments(segments, segments.Count > index ? _diffFindMatches[index] : -1);
    }

    private void OnChangeNavigationRequested(object? sender, int blockIndex) => JumpToChange(blockIndex);

    private void RebuildDocuments()
    {
        if (_tab is null)
        {
            return;
        }

        var rawInline = DiffDocumentBuilders.BuildInline(_tab.DiffLines);
        _rawHunkHeaderTexts = rawInline
            .Where(line => line.Kind == GitDiffLineKind.HunkHeader)
            .Select(line => line.Text)
            .ToArray();
        var sideBySide = DiffDocumentBuilders.BuildSideBySide(_tab.SideBySideRows);
        var sideCollapseSource = DiffDocumentBuilders.BuildSideBySideCollapseSource(sideBySide.Old, sideBySide.New);
        if (_tab.IsContextCollapsed)
        {
            // Once the tab is loaded, keep a persistent projection so click-to-expand state
            // survives rebuilds / mode switches; during loading use throwaway maps (the lines
            // arrive incrementally and a persistent map would churn on every batch).
            if (_tab.IsLoaded && _inlineMap is null)
            {
                _inlineMap = new DiffDisplayMap(rawInline);
                _sideMap = new DiffDisplayMap(sideCollapseSource);
            }

            var inlineMap = _inlineMap ?? new DiffDisplayMap(rawInline);
            var sideMap = _sideMap ?? new DiffDisplayMap(sideCollapseSource);
            // Keep hunk headers in the projection as hard folding boundaries, but do not expose
            // the raw unified-diff "@@ ... @@" metadata in the single-column reading surface.
            _inlineLines = inlineMap.Lines.Select(line => ToDisplayLine(line, hideHunkHeader: true)).ToArray();
            _oldLines = sideMap.Lines.Select(line => ToDisplayLine(line, sideBySide.Old)).ToArray();
            _newLines = sideMap.Lines.Select(line => ToDisplayLine(line, sideBySide.New)).ToArray();
        }
        else
        {
            _inlineLines = rawInline;
            _oldLines = sideBySide.Old;
            _newLines = sideBySide.New;
        }

        _sideChangeMask = DiffDocumentBuilders.BuildSideBySideChangeMask(_oldLines, _newLines);

        SetEditorDocument(InlineEditor, _inlineLines);
        SetEditorDocument(OldEditor, _oldLines);
        SetEditorDocument(NewEditor, _newLines);
        _ = ApplyPresentationAsync();

        InstallMargins();
        InstallBackgrounds();
        InstallIntralineTransformers();
        ApplyEditorTheme();

        // B4: once loaded, land on the first change block instead of the document top.
        if (_tab.IsLoaded && _tab.ChangeCount > 0 && !_positionedFirstChange)
        {
            _positionedFirstChange = true;
            JumpToChange(0);
        }

        ScheduleInitialSideScrollSync();
    }

    /// <summary>Expands the collapsed-context placeholder under the pointer (VS Code click-to-expand)
    /// and rebuilds the editors from the persistent projection.</summary>
    private void OnEditorPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not TextView textView)
        {
            return;
        }

        var position = textView.GetPositionFloor(e.GetPosition(textView));
        if (position is not { Line: >= 1 } hit)
        {
            return;
        }

        var displayIndex = hit.Line - 1;
        var map = ReferenceEquals(textView, InlineEditor.TextArea.TextView) ? _inlineMap : _sideMap;
        if (map is null || displayIndex >= map.Lines.Count || !map.Lines[displayIndex].IsCollapsedContext)
        {
            return;
        }

        if (map.ExpandAtDisplayIndex(displayIndex))
        {
            RebuildDocuments();
            e.Handled = true;
        }
    }

    private static DiffRenderLine ToDisplayLine(
        DiffDisplayLine line,
        IReadOnlyList<DiffRenderLine>? source = null,
        bool hideHunkHeader = false)
    {
        if (line.IsCollapsedContext)
        {
            return new DiffRenderLine(line.Text, GitDiffLineKind.None, null, null);
        }

        var result = source is null ? line.Source! : source[line.OriginalIndex];
        return hideHunkHeader && result.Kind == GitDiffLineKind.HunkHeader
            ? result with { Text = string.Empty }
            : result;
    }

    private static void SetEditorDocument(TextEditor editor, IReadOnlyList<DiffRenderLine> lines)
    {
        var text = string.Join("\n", lines.Select(line => line.Text));
        if (editor.Document is null || editor.Document.Text != text)
        {
            editor.Document = new TextDocument(text);
            // Deliberately do not reset to the top here: the view lands on the first change block
            // once loaded (B4), and rebuilds after a click-to-expand keep the current scroll position.
        }
    }

    private void InstallMargins()
    {
        if (_inlineNumbers is null)
        {
            _inlineNumbers = new DiffLineNumberMargin(_inlineLines, DiffNumberMode.Both, this);
            InlineEditor.TextArea.LeftMargins.Add(_inlineNumbers);
        }
        else
        {
            _inlineNumbers.UpdateLines(_inlineLines);
        }

        if (_oldNumbers is null)
        {
            _oldNumbers = new DiffLineNumberMargin(_oldLines, DiffNumberMode.OldOnly, this);
            OldEditor.TextArea.LeftMargins.Add(_oldNumbers);
        }
        else
        {
            _oldNumbers.UpdateLines(_oldLines);
        }

        if (_newNumbers is null)
        {
            _newNumbers = new DiffLineNumberMargin(_newLines, DiffNumberMode.NewOnly, this);
            NewEditor.TextArea.LeftMargins.Add(_newNumbers);
        }
        else
        {
            _newNumbers.UpdateLines(_newLines);
        }

        RedrawOverview();
        RedrawSideOverviews();
        UpdateDiffSticky();
        RefreshDiffFind();
    }

    private void InstallBackgrounds()
    {
        if (_inlineBackground is null)
        {
            _inlineBackground = new DiffLineBackgroundRenderer(_inlineLines, this, InlineEditor.TextArea.TextView);
            InlineEditor.TextArea.TextView.BackgroundRenderers.Add(_inlineBackground);
        }
        else
        {
            _inlineBackground.UpdateLines(_inlineLines);
        }

        if (_oldBackground is null)
        {
            _oldBackground = new DiffLineBackgroundRenderer(_oldLines, this, OldEditor.TextArea.TextView);
            OldEditor.TextArea.TextView.BackgroundRenderers.Add(_oldBackground);
        }
        else
        {
            _oldBackground.UpdateLines(_oldLines);
        }

        if (_newBackground is null)
        {
            _newBackground = new DiffLineBackgroundRenderer(_newLines, this, NewEditor.TextArea.TextView);
            NewEditor.TextArea.TextView.BackgroundRenderers.Add(_newBackground);
        }
        else
        {
            _newBackground.UpdateLines(_newLines);
        }
    }

    private void JumpToChange(int blockIndex)
    {
        if (_tab?.IsSideBySideDiff == true)
        {
            // Side-by-side rows are 1:1 between the old/new editors but NOT with the inline list:
            // a modified pair is two inline rows but one side row, so the inline block start would
            // land the panes below the real block. Resolve against the row-aligned side mask
            // (ChangeNavigationRequested docs: "centers the target block in its current layout").
            var (sideStart, _) = DiffDocumentBuilders.BlockAtIndex(_sideChangeMask, blockIndex);
            if (sideStart < 0)
            {
                return;
            }

            var sideLine = sideStart + 1;
            CenterLine(OldEditor, sideLine);
            CenterLine(NewEditor, sideLine);
            return;
        }

        var (start, _) = DiffDocumentBuilders.BlockAtIndex(_inlineLines, blockIndex);
        if (start < 0)
        {
            return;
        }

        CenterLine(InlineEditor, start + 1);
    }

    private static void CenterLine(TextEditor editor, int line)
    {
        if (editor.Document is not { } document || document.LineCount == 0)
        {
            return;
        }

        var textView = editor.TextArea.TextView;
        var visible = Math.Max(1, (int)Math.Ceiling(textView.ActualHeight / Math.Max(1, textView.DefaultLineHeight)));
        editor.ScrollTo(Math.Max(1, line - visible / 2), 0);
    }

    private void ApplyEditorTheme()
    {
        foreach (var editor in new[] { InlineEditor, OldEditor, NewEditor })
        {
            editor.Background = Brush("EditorBrush") ?? Brushes.Transparent;
            editor.Foreground = Brush("TextBrush") ?? Brushes.Black;
            editor.TextArea.SelectionBrush = Brush("CodeSelectionBrush") ?? editor.TextArea.SelectionBrush;
            // Match the Explorer/code-editor selection treatment: the selected range is a flat
            // fill only. AvalonEdit's default selection pen/rounded geometry can visually bleed
            // into adjacent diff decorations, especially for short ranges.
            editor.TextArea.SelectionBorder = null;
            editor.TextArea.SelectionCornerRadius = 0;
            editor.TextArea.Caret.CaretBrush = Brush("CaretBrush") ?? editor.TextArea.Caret.CaretBrush;
            // 与源代码阅读器一致的基础编辑外观(非比较相关):当前行淡 tint、无边框、当前行高亮、
            // 关闭只读下的自动缩进策略。
            editor.TextArea.TextView.CurrentLineBackground = Brush("CodeCurrentLineBrush") ?? Brushes.Transparent;
            editor.TextArea.TextView.CurrentLineBorder = null;
            editor.Options.HighlightCurrentLine = true;
            editor.TextArea.IndentationStrategy = null;
        }

        _overviewAddedBrush = Brush("EditorGutterAddedBrush") ?? Brushes.Green;
        _overviewRemovedBrush = Brush("EditorGutterDeletedBrush") ?? Brushes.Red;
        _inlineNumbers?.RefreshTheme();
        _oldNumbers?.RefreshTheme();
        _newNumbers?.RefreshTheme();
        _inlineBackground?.RefreshTheme();
        _oldBackground?.RefreshTheme();
        _newBackground?.RefreshTheme();
        _diffFindRenderer?.RefreshBrushes();
        RedrawOverview();
        RedrawSideOverviews();
        UpdateDiffSticky();
        ApplyHighlightingTheme();
    }

    /// <summary>按 diff 文件的扩展名准备与资源管理器相同的 AvalonEdit 回退定义；正常渲染由
    /// ApplyPresentationAsync 提供的 TextMate token 快照负责。纯文本/未知扩展为 null 高亮。</summary>
    private void ApplyHighlighting()
    {
        _highlightingName = DiffDocumentBuilders.ResolveHighlightingName(_tab?.Path);
        _highlightingDefinition = string.IsNullOrEmpty(_highlightingName)
            ? null
            : HighlightingManager.Instance.GetDefinition(_highlightingName);
        foreach (var editor in new[] { InlineEditor, OldEditor, NewEditor })
        {
            editor.SyntaxHighlighting = _highlightingDefinition;
        }

        InstallMetaTransformers();
        ApplyHighlightingTheme();
    }

    private void CancelPresentation()
    {
        _presentationVersion++;
        _presentationCancellation?.Cancel();
        _presentationCancellation?.Dispose();
        _presentationCancellation = null;
    }

    /// <summary>Uses the same TextMate presentation service as the resource-manager code reader.
    /// Each diff pane gets a snapshot for its own text because removed and added lines are not the
    /// same document.  The version gate prevents an incremental diff rebuild from painting stale
    /// tokens over a newer projection.</summary>
    private async Task ApplyPresentationAsync()
    {
        CancelPresentation();
        var cancellation = _presentationCancellation = new CancellationTokenSource();
        var version = _presentationVersion;
        var tab = _tab;
        if (tab is null)
        {
            return;
        }

        _inlineSemanticTokens.SetSnapshot(null);
        _oldSemanticTokens.SetSnapshot(null);
        _newSemanticTokens.SetSnapshot(null);
        SetSyntaxFallback();

        var fileType = CodeFileTypeRegistry.Instance.FromPath(tab.Path);
        var service = CodePresentationService.Instance;
        try
        {
            var inlineTask = service.AnalyzeTokensAsync(InlineEditor.Document?.Text ?? string.Empty,
                fileType, version, cancellation.Token);
            var oldTask = service.AnalyzeTokensAsync(OldEditor.Document?.Text ?? string.Empty,
                fileType, version, cancellation.Token);
            var newTask = service.AnalyzeTokensAsync(NewEditor.Document?.Text ?? string.Empty,
                fileType, version, cancellation.Token);
            await Task.WhenAll(inlineTask, oldTask, newTask);
            if (cancellation.IsCancellationRequested || version != _presentationVersion ||
                !ReferenceEquals(tab, _tab))
            {
                return;
            }

            _inlineSemanticTokens.SetSnapshot(inlineTask.Result);
            _oldSemanticTokens.SetSnapshot(oldTask.Result);
            _newSemanticTokens.SetSnapshot(newTask.Result);
            SetSyntaxFallback();
            InlineEditor.TextArea.TextView.Redraw();
            OldEditor.TextArea.TextView.Redraw();
            NewEditor.TextArea.TextView.Redraw();
        }
        catch (OperationCanceledException)
        {
            // A newer diff projection or tab switch intentionally invalidated this pass.
        }
        catch (Exception ex)
        {
            // Keep the same safe fallback as the resource-manager preview if a grammar is not
            // available for a particular file. The diff itself remains fully readable.
            LogPresentationFailure(ex);
        }
    }

    private void SetSyntaxFallback()
    {
        InlineEditor.SyntaxHighlighting = _inlineSemanticTokens.HasTokens ? null : _highlightingDefinition;
        OldEditor.SyntaxHighlighting = _oldSemanticTokens.HasTokens ? null : _highlightingDefinition;
        NewEditor.SyntaxHighlighting = _newSemanticTokens.HasTokens ? null : _highlightingDefinition;
    }

    private static void LogPresentationFailure(Exception exception)
    {
        System.Diagnostics.Debug.WriteLine($"Diff syntax presentation failed: {exception.Message}");
    }

    /// <summary>把当前主题的回退高亮定义应用到主题并重绘。正常情况下 diff 使用上面的
    /// TextMate token colorizer；仅在语法分析没有 token 时沿用资源管理器的 AvalonEdit 回退。</summary>
    private void ApplyHighlightingTheme()
    {
        if (_highlightingDefinition is { } definition)
        {
            ThemeHighlightingColorizer.ApplyDefinitionTheme(definition);
        }

        InlineEditor.TextArea.TextView.Redraw();
        OldEditor.TextArea.TextView.Redraw();
        NewEditor.TextArea.TextView.Redraw();
    }

    /// <summary>meta 行(hunk header @@ 与 \ No newline 通知)不是源码,由转换器统一弱化为
    /// MutedTextBrush,避免被语言高亮误着色;行集合经访问器绑定字段,文档重建自动跟随。</summary>
    private void InstallMetaTransformers()
    {
        if (_inlineMeta is null)
        {
            _inlineMeta = new DiffMetaLineTransformer(() => _inlineLines, this);
            InlineEditor.TextArea.TextView.LineTransformers.Add(_inlineMeta);
        }

        if (_oldMeta is null)
        {
            _oldMeta = new DiffMetaLineTransformer(() => _oldLines, this);
            OldEditor.TextArea.TextView.LineTransformers.Add(_oldMeta);
        }

        if (_newMeta is null)
        {
            _newMeta = new DiffMetaLineTransformer(() => _newLines, this);
            NewEditor.TextArea.TextView.LineTransformers.Add(_newMeta);
        }
    }

    private void InstallIntralineTransformers()
    {
        if (_inlineIntraline is null)
        {
            _inlineIntraline = new DiffIntralineBackgroundRenderer(() => _inlineLines, this);
            InlineEditor.TextArea.TextView.BackgroundRenderers.Add(_inlineIntraline);
        }

        if (_oldIntraline is null)
        {
            _oldIntraline = new DiffIntralineBackgroundRenderer(() => _oldLines, this);
            OldEditor.TextArea.TextView.BackgroundRenderers.Add(_oldIntraline);
        }

        if (_newIntraline is null)
        {
            _newIntraline = new DiffIntralineBackgroundRenderer(() => _newLines, this);
            NewEditor.TextArea.TextView.BackgroundRenderers.Add(_newIntraline);
        }
    }

    /// <summary>Right-edge overview ruler in full-document coordinates.  The renderer draws a fixed
    /// number of vertical buckets, so huge diffs remain O(canvas height), not O(document lines).</summary>
    private void RedrawOverview() =>
        RedrawOverviewInto(
            OverviewCanvas,
            _inlineScroll,
            _inlineLines,
            _tab is null ? -1 : DiffDocumentBuilders.BlockAtIndex(_inlineLines, _tab.CurrentChangeIndex).Item1);

    /// <summary>@@ hunk 头吸顶(VS Code diff sticky scroll):滚动经过 hunk 头后把当前 hunk 头钉在
    /// 顶部,点击跳转;仅内联模式(并排双编辑器暂不吸顶)。</summary>
    private void UpdateDiffSticky()
    {
        DiffStickyRows.Children.Clear();
        if (_tab is null || !_tab.IsInlineDiff || _inlineLines.Count == 0 || _inlineScroll is null)
        {
            DiffStickyHost.Visibility = Visibility.Collapsed;
            return;
        }

        var lineHeight = Math.Max(1, InlineEditor.TextArea.TextView.DefaultLineHeight);
        var topLine = Math.Max(1, 1 + (int)(_inlineScroll.VerticalOffset / lineHeight));
        var headers = new List<int>();
        for (var i = 0; i < _inlineLines.Count; i++)
        {
            if (_inlineLines[i].Kind == GitDiffLineKind.HunkHeader)
            {
                headers.Add(i);
            }
        }
        if (headers.Count == 0)
        {
            DiffStickyHost.Visibility = Visibility.Collapsed;
            return;
        }

        // 最后一个段首行 < topLine 且 topLine 仍在段内;段首未滚出时不显示。
        // 用 hunk 序号(ordinal)而非显示行号寻址:折叠投影会移动 hunk 头的显示位置,
        // 序号在显示列表与原始 hunk 头之间稳定对应。
        int? activeOrdinal = null;
        for (var k = 0; k < headers.Count; k++)
        {
            if (headers[k] + 1 < topLine)
            {
                activeOrdinal = k;
            }
            else
            {
                break;
            }
        }
        if (activeOrdinal is not { } ordinal)
        {
            DiffStickyHost.Visibility = Visibility.Collapsed;
            return;
        }

        var headerIndex = headers[ordinal];
        var headerStart = headerIndex + 1;
        var nextHeader = ordinal + 1 < headers.Count ? headers[ordinal + 1] : -1;
        var headerEnd = nextHeader < 0 ? int.MaxValue : nextHeader + 1;
        if (topLine >= headerEnd)
        {
            DiffStickyHost.Visibility = Visibility.Collapsed;
            return;
        }

        DiffStickyHost.Visibility = Visibility.Visible;
        var bright = Brush("BrightTextBrush") ?? Brushes.White;
        var muted = Brush("MutedTextBrush") ?? Brushes.Gray;
        var hover = Brush("HoverBrush") ?? Brushes.Transparent;
        var row = new Border { Cursor = Cursors.Hand, Background = Brushes.Transparent, Padding = new Thickness(8, 2, 8, 2) };
        row.MouseLeftButtonUp += (_, _) => InlineEditor.ScrollTo(headerStart, 0);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            // 折叠投影隐藏了 @@ 元数据(行文本为空),sticky 行回退到原始 hunk 头文本。
            Text = ordinal < _rawHunkHeaderTexts.Count ? _rawHunkHeaderTexts[ordinal] : _inlineLines[headerIndex].Text,
            FontFamily = InlineEditor.FontFamily,
            FontSize = InlineEditor.FontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = bright,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"第 {headerStart} 行",
            FontFamily = InlineEditor.FontFamily,
            FontSize = InlineEditor.FontSize,
            Foreground = muted,
            Margin = new Thickness(8, 0, 0, 0),
        });
        row.Child = panel;
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        DiffStickyRows.Children.Add(row);
    }

    private void RedrawSideOverviews()
    {
        // 并排两侧的行号空间与内联列表不同(修改对=两侧各 1 行 vs 内联 2 行),
        // current 标记必须按行对齐的并排掩码定位,否则会点亮错误的色带桶。
        var sideStart = _tab is null
            ? -1
            : DiffDocumentBuilders.BlockAtIndex(_sideChangeMask, _tab.CurrentChangeIndex).Item1;
        RedrawOverviewInto(OldOverviewCanvas, _oldScroll, _oldLines, sideStart);
        RedrawOverviewInto(NewOverviewCanvas, _newScroll, _newLines, sideStart);
    }

    private void RedrawOverviewInto(Canvas canvas, ScrollViewer? scroll, IReadOnlyList<DiffRenderLine> lines, int currentBlockStart)
    {
        canvas.Children.Clear();
        if (_tab?.ShowOverviewRuler != true || lines.Count == 0 || scroll is null || canvas.ActualHeight <= 0)
        {
            return;
        }

        var lineHeight = Math.Max(1, InlineEditor.TextArea.TextView.DefaultLineHeight);
        var bucketCount = Math.Max(1, (int)Math.Ceiling(canvas.ActualHeight));
        var bucketHeight = canvas.ActualHeight / bucketCount;
        foreach (var bucket in DiffOverviewLayout.Build(lines, bucketCount, currentBlockStart))
        {
            if (!bucket.HasAdded && !bucket.HasRemoved && !bucket.HasCurrent && !bucket.HasCollapsed) continue;
            var brush = bucket.HasCurrent
                ? Brush("AccentBrush")
                : bucket.HasAdded ? _overviewAddedBrush
                : bucket.HasRemoved ? _overviewRemovedBrush
                : Brush("HoverBrush"); // 折叠占位色带
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = bucket.HasCurrent ? 6 : 4,
                Height = Math.Max(1, bucketHeight),
                Fill = brush,
            };
            Canvas.SetLeft(rect, bucket.HasCurrent ? 1 : 2);
            Canvas.SetTop(rect, bucket.Index * bucketHeight);
            canvas.Children.Add(rect);
        }

        var documentHeight = Math.Max(lineHeight, lines.Count * lineHeight);
        var viewportTop = Math.Clamp(scroll.VerticalOffset / documentHeight * canvas.ActualHeight, 0, canvas.ActualHeight);
        var viewportHeight = Math.Clamp(scroll.ViewportHeight / documentHeight * canvas.ActualHeight, 4, canvas.ActualHeight);
        var viewport = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(4, canvas.ActualWidth - 1),
            Height = viewportHeight,
            Stroke = Brush("DiffOverviewViewportBrush") ?? Brush("AccentBrush"),
            StrokeThickness = 1,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(viewport, 0);
        Canvas.SetTop(viewport, Math.Min(viewportTop, Math.Max(0, canvas.ActualHeight - viewportHeight)));
        canvas.Children.Add(viewport);
    }

    private void OnOverviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_tab?.ShowOverviewRuler != true || _inlineScroll is null || _inlineLines.Count == 0) return;
        var line = DiffOverviewLayout.DocumentLineFromY(e.GetPosition(OverviewCanvas).Y, OverviewCanvas.ActualHeight, _inlineLines.Count);
        CenterLine(InlineEditor, line + 1);
        e.Handled = true;
    }

    private void OnSideOverviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || _tab?.ShowOverviewRuler != true) return;
        var lines = ReferenceEquals(canvas, OldOverviewCanvas) ? _oldLines : _newLines;
        var editor = ReferenceEquals(canvas, OldOverviewCanvas) ? OldEditor : NewEditor;
        if (lines.Count == 0 || canvas.ActualHeight <= 0) return;
        var line = DiffOverviewLayout.DocumentLineFromY(e.GetPosition(canvas).Y, canvas.ActualHeight, lines.Count);
        CenterLine(editor, line + 1);
        e.Handled = true;
    }

    private Brush? Brush(string key) => TryFindResource(key) as Brush ?? Application.Current?.TryFindResource(key) as Brush;

    // ===== Margin: old | new line numbers =====

    public enum DiffNumberMode
    {
        Both,
        OldOnly,
        NewOnly,
    }

    private sealed class DiffLineNumberMargin : AbstractMargin
    {
        private const double HunkActionGutterWidth = 28;
        private IReadOnlyList<DiffRenderLine> _lines;
        private readonly DiffNumberMode _mode;
        private readonly DiffDocumentView _owner;
        private Brush _numberBrush = Brushes.Gray;
        private Brush _addedNumberBrush = Brushes.LimeGreen;
        private Brush _removedNumberBrush = Brushes.Red;
        private Brush _headerBrush = Brushes.SkyBlue;
        private Brush _activeNumberBrush = Brushes.White;
        private Brush _addedGutterBrush = Brushes.Green;
        private Brush _removedGutterBrush = Brushes.Red;
        private int _activeLine = -1;
        // D10: 行号位数缓存。旧实现每次 MeasureOverride 对整份行列表做两次 LINQ 全量扫描;
        // 位数只随行列表实例变化(RebuildDocuments 每次产出新数组,UpdateLines 是唯一换文档
        // 入口)才可能变化,故按文档实例缓存,measure 命中缓存零扫描。
        private IReadOnlyList<DiffRenderLine>? _digitCacheLines;
        private int _digitCacheOld;
        private int _digitCacheNew;

        public DiffLineNumberMargin(IReadOnlyList<DiffRenderLine> lines, DiffNumberMode mode, DiffDocumentView owner)
        {
            _lines = lines;
            _mode = mode;
            _owner = owner;
            IsHitTestVisible = false;
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
                // Scrolling changes line Y positions without replacing the visual-line collection;
                // redraw the margin explicitly so numbers track the scrolled text.
                newTextView.ScrollOffsetChanged += OnScrollOffsetChanged;
            }
            base.OnTextViewChanged(oldTextView, newTextView);
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        private void OnScrollOffsetChanged(object? sender, EventArgs e) => InvalidateVisual();

        public void UpdateLines(IReadOnlyList<DiffRenderLine> lines)
        {
            _lines = lines;
            _digitCacheLines = null; // 文档换入:位数缓存失效,下次 measure 重算一次
            InvalidateMeasure();
            InvalidateVisual();
        }

        public void SetActiveLine(int line)
        {
            if (line != _activeLine)
            {
                _activeLine = line;
                InvalidateVisual();
            }
        }

        public void RefreshTheme()
        {
            _numberBrush = _owner.Brush("DiffLineNumberBrush") ?? _owner.Brush("MutedTextBrush") ?? Brushes.Gray;
            _addedNumberBrush = _owner.Brush("SuccessBrush") ?? Brushes.LimeGreen;
            _removedNumberBrush = _owner.Brush("ScmDeletedBrush") ?? Brushes.Red;
            _headerBrush = _owner.Brush("InfoAccentBrush") ?? Brushes.SkyBlue;
            _activeNumberBrush = _owner.Brush("CodeLineNumberActiveBrush") ?? _numberBrush;
            _addedGutterBrush = _owner.Brush("EditorGutterAddedBrush") ?? Brushes.Green;
            _removedGutterBrush = _owner.Brush("EditorGutterDeletedBrush") ?? Brushes.Red;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // D10: 同一文档实例只全量扫一次;后续 measure(滚动/字号外的重排)直接取缓存。
            // 单遍同时取两侧最大位数,替代原先两次 LINQ Where/Select/Max 全量扫描。
            if (!ReferenceEquals(_digitCacheLines, _lines))
            {
                var maxOld = 0;
                var maxNew = 0;
                foreach (var line in _lines)
                {
                    if (line.OldLineNumber is { } oldNumber)
                    {
                        var digits = oldNumber.ToString().Length;
                        if (digits > maxOld)
                        {
                            maxOld = digits;
                        }
                    }

                    if (line.NewLineNumber is { } newNumber)
                    {
                        var digits = newNumber.ToString().Length;
                        if (digits > maxNew)
                        {
                            maxNew = digits;
                        }
                    }
                }

                _digitCacheOld = Math.Max(2, maxOld);
                _digitCacheNew = Math.Max(2, maxNew);
                _digitCacheLines = _lines;
            }

            var oldDigits = _digitCacheOld;
            var newDigits = _digitCacheNew;
            var numberWidth = Math.Max(7, (_owner._tab?.EditorFontSize ?? 14) * 0.62);
            // Reserve a real marker column before the line number. The previous width only
            // accounted for a small inset, so short numbers (for example +1 / -1) touched or
            // overlapped the marker glyph. This extra scaled space is the marker-to-number gap.
            var signWidth = numberWidth + 18;
            return _mode == DiffNumberMode.Both
                ? new(HunkActionGutterWidth + oldDigits * numberWidth + newDigits * numberWidth + signWidth + 16, 0)
                : new(HunkActionGutterWidth + (_mode == DiffNumberMode.OldOnly ? oldDigits : newDigits) * numberWidth + signWidth, 0);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (TextView is not { } view)
            {
                return;
            }

            var fontSize = _owner._tab?.EditorFontSize ?? 14;
            var typeface = new Typeface(_owner.InlineEditor.FontFamily.Source);
            var visualLines = GetValidVisualLines(view);
            if (visualLines is null)
            {
                return;
            }

            foreach (var visualLine in visualLines)
            {
                var index = visualLine.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= _lines.Count)
                {
                    continue;
                }

                var line = _lines[index];
                // VisualTop is document-space; the margin is fixed to the viewport, so convert
                // to viewport coordinates the same way AvalonEdit's built-in margin does.
                var y = visualLine.VisualTop - view.ScrollOffset.Y;
                var brush = index + 1 == _activeLine
                    ? _activeNumberBrush
                    : line.Kind switch
                    {
                        GitDiffLineKind.HunkHeader => _headerBrush,
                        GitDiffLineKind.Added => _addedNumberBrush,
                        GitDiffLineKind.Removed => _removedNumberBrush,
                        _ => _numberBrush,
                    };

                var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var oldText = line.OldLineNumber?.ToString() ?? string.Empty;
                var newText = line.NewLineNumber?.ToString() ?? string.Empty;
                if (_mode == DiffNumberMode.Both)
                {
                    var split = HunkActionGutterWidth + (ActualWidth - HunkActionGutterWidth) / 2;
                    DrawNumber(drawingContext, oldText, split - 8, y, brush, typeface, fontSize, dip);
                    drawingContext.DrawRectangle(
                        _owner.Brush("DiffEditorBorderBrush") ?? Brushes.Transparent,
                        null,
                        new Rect(split, y + 2, 1, Math.Max(1, visualLine.Height - 4)));
                    DrawNumber(drawingContext, newText, ActualWidth - 6, y, brush, typeface, fontSize, dip);
                }
                else
                {
                    DrawNumber(drawingContext, _mode == DiffNumberMode.OldOnly ? oldText : newText,
                        ActualWidth - 6, y, brush, typeface, fontSize, dip);
                }

                // ± change symbol column (VS Code diff gutter): + for added, − for removed.
                if (line.Kind is GitDiffLineKind.Added or GitDiffLineKind.Removed)
                {
                    var glyph = line.Kind == GitDiffLineKind.Added ? "+" : "−";
                    var glyphFormatted = new FormattedText(
                        glyph,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        line.Kind == GitDiffLineKind.Added ? _addedGutterBrush : _removedGutterBrush,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    drawingContext.DrawText(glyphFormatted, new Point(HunkActionGutterWidth + 4, y));
                }
            }
        }

        private static void DrawNumber(DrawingContext drawingContext, string text, double right,
            double y, Brush brush, Typeface typeface, double fontSize, double pixelsPerDip)
        {
            if (string.IsNullOrEmpty(text)) return;
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                brush,
                pixelsPerDip);
            drawingContext.DrawText(formatted, new Point(Math.Max(0, right - formatted.Width), y));
        }
    }

    // ===== Background renderer: per-kind line color(行号右侧的变更加粗实线已按需求移除,
    // 变更识别由整行着色 + 行号区 ± 符号列承担) =====

    private sealed class DiffLineBackgroundRenderer : IBackgroundRenderer
    {
        private IReadOnlyList<DiffRenderLine> _lines;
        private readonly DiffDocumentView _owner;
        private readonly TextView _textView;
        private Brush _addedBrush = Brushes.Transparent;
        private Brush _removedBrush = Brushes.Transparent;
        private Brush _headerBrush = Brushes.Transparent;
        private Brush _modifiedBrush = Brushes.Transparent;
        private Brush _hoverBrush = Brushes.Transparent;
        private Brush _placeholderBrush = Brushes.Transparent;

        public DiffLineBackgroundRenderer(IReadOnlyList<DiffRenderLine> lines, DiffDocumentView owner, TextView textView)
        {
            _lines = lines;
            _owner = owner;
            _textView = textView;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void UpdateLines(IReadOnlyList<DiffRenderLine> lines)
        {
            _lines = lines;
            _textView.InvalidateLayer(Layer);
        }

        public void RefreshTheme()
        {
            _addedBrush = OwnerBrush("DiffAddedBrush", _addedBrush);
            _removedBrush = OwnerBrush("DiffRemovedBrush", _removedBrush);
            _headerBrush = OwnerBrush("DiffHeaderBrush", _headerBrush);
            _modifiedBrush = OwnerBrush("DiffModifiedBrush", _modifiedBrush);
            _hoverBrush = OwnerBrush("HoverBrush", _hoverBrush);
            _placeholderBrush = OwnerBrush("DiffUnchangedRegionBrush", _placeholderBrush);
            _textView.InvalidateLayer(Layer);
        }

        private Brush OwnerBrush(string key, Brush fallback) =>
            _owner.TryFindResource(key) as Brush ?? Application.Current?.TryFindResource(key) as Brush ?? fallback;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var visualLines = GetValidVisualLines(textView);
            if (_lines.Count == 0 || visualLines is null || visualLines.Count == 0)
            {
                return;
            }

            foreach (var visualLine in visualLines)
            {
                var index = visualLine.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= _lines.Count)
                {
                    continue;
                }

                var line = _lines[index];
                var kind = line.Kind;
                // VisualTop is document-space; the background layer draws in viewport space.
                var y = visualLine.VisualTop - textView.ScrollOffset.Y;
                if (line.IsModified)
                {
                    // 改行(old 删 + new 增的配对):修正着色覆盖纯增删色。
                    var modifiedRect = new Rect(0, y, textView.ActualWidth, visualLine.Height);
                    drawingContext.DrawRectangle(_modifiedBrush, null, modifiedRect);
                    continue;
                }

                var background = kind switch
                {
                    GitDiffLineKind.Added => _addedBrush,
                    GitDiffLineKind.Removed => _removedBrush,
                    GitDiffLineKind.HunkHeader => _headerBrush,
                    _ => null,
                };

                if (background is null)
                {
                    // VS Code-style expandable placeholder bar ("… 展开 N 行未更改内容 …").
                    if (kind == GitDiffLineKind.None && _lines[index].Text.Contains("…"))
                    {
                        drawingContext.DrawRectangle(
                            _placeholderBrush,
                            null,
                            new Rect(0, y, textView.ActualWidth, visualLine.Height));
                    }

                    continue;
                }

                var lineRect = new Rect(0, y, textView.ActualWidth, visualLine.Height);
                drawingContext.DrawRectangle(background, null, lineRect);
            }
        }
    }

    // ===== Meta line transformer: hunk header / notice 行不参与语言高亮 =====

    /// <summary>Diff 内查找高亮(统一文本):全部匹配用搜索匹配色,当前行匹配用当前匹配色。
    /// 段集按查找文本在统一文档中的偏移定位。</summary>
    private sealed class DiffFindRenderer : IBackgroundRenderer
    {
        private readonly DiffDocumentView _owner;
        private TextSegmentCollection<TextSegment>? _segments;
        private int _currentLine = -1;
        private Brush _allBrush = Brushes.Transparent;
        private Brush _currentBrush = Brushes.Transparent;

        public DiffFindRenderer(DiffDocumentView owner)
        {
            _owner = owner;
            owner.InlineEditor.TextArea.TextView.BackgroundRenderers.Add(this);
            RefreshBrushes();
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void RefreshBrushes()
        {
            _allBrush = OwnerBrush("CodeSearchMatchBrush");
            _currentBrush = OwnerBrush("CodeSearchCurrentMatchBrush");
            Invalidate();
        }

        private Brush OwnerBrush(string key) =>
            _owner.TryFindResource(key) as Brush ?? Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

        private void Invalidate() => _owner.InlineEditor.TextArea.TextView.InvalidateLayer(Layer);

        public void SetSegments(IReadOnlyList<(int Start, int Length)> segments, int currentLine)
        {
            _currentLine = currentLine;
            _segments ??= new TextSegmentCollection<TextSegment>(_owner.InlineEditor.Document);
            _segments.Clear();
            foreach (var (start, length) in segments)
            {
                if (start >= 0 && start + length <= _owner.InlineEditor.Document.TextLength)
                {
                    _segments.Add(new TextSegment { StartOffset = start, Length = length });
                }
            }

            Invalidate();
        }

        public void Clear()
        {
            _segments?.Clear();
            _currentLine = -1;
            Invalidate();
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
                var isCurrent = _currentLine >= 0
                    && segment.StartOffset >= _owner.InlineEditor.Document.GetLineByNumber(_currentLine + 1).Offset
                    && segment.StartOffset < _owner.InlineEditor.Document.GetLineByNumber(Math.Min(_currentLine + 1, _owner.InlineEditor.Document.LineCount)).EndOffset;
                (isCurrent ? current : all).AddSegment(textView, segment);
            }

            if (all.CreateGeometry() is { } allGeometry)
            {
                drawingContext.DrawGeometry(_allBrush, null, allGeometry);
            }

            if (current.CreateGeometry() is { } currentGeometry)
            {
                drawingContext.DrawGeometry(_currentBrush, null, currentGeometry);
            }
        }
    }

    /// <summary>hunk header(@@)与 notice(\ No newline)是 diff 的 meta 行而非源码,语言高亮会
    /// 把 "@@"、"No" 等误判为符号;本转换器把这些行整行弱化为 MutedTextBrush。画刷在着色时
    /// 现取,主题切换自动跟随。</summary>
    private sealed class DiffMetaLineTransformer : DocumentColorizingTransformer
    {
        private readonly Func<IReadOnlyList<DiffRenderLine>> _lines;
        private readonly DiffDocumentView _owner;

        public DiffMetaLineTransformer(Func<IReadOnlyList<DiffRenderLine>> lines, DiffDocumentView owner)
        {
            _lines = lines;
            _owner = owner;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var lines = _lines();
            var index = line.LineNumber - 1;
            if (index < 0 || index >= lines.Count)
            {
                return;
            }

            var kind = lines[index].Kind;
            if (kind is GitDiffLineKind.HunkHeader or GitDiffLineKind.Notice)
            {
                if (_owner._tab?.ShowIntralineChanges != true) return;
                if (_owner.Brush("MutedTextBrush") is not { } metaBrush) return;
                ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(metaBrush));
                return;
            }

            // VS Code-style clickable collapsed-context placeholder ("… 展开 N 行未更改内容 …").
            if (kind == GitDiffLineKind.None && lines[index].Text.Contains("…"))
            {
                var expandBrush = _owner.Brush("DiffPlaceholderForegroundBrush")
                    ?? _owner.Brush("DiffExpandPlaceholderBrush");
                if (expandBrush is null) return;
                ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(expandBrush));
            }
        }
    }

    /// <summary>VS Code-style local diff decoration. Intraline ranges are token-sized spans from
    /// the parsed Git hunk; drawing them as geometry (instead of a text-run background) keeps short
    /// punctuation and one-token changes confined to their exact range without an outline.</summary>
    private sealed class DiffIntralineBackgroundRenderer : IBackgroundRenderer
    {
        private readonly Func<IReadOnlyList<DiffRenderLine>> _lines;
        private readonly DiffDocumentView _owner;

        public DiffIntralineBackgroundRenderer(Func<IReadOnlyList<DiffRenderLine>> lines, DiffDocumentView owner)
        {
            _lines = lines;
            _owner = owner;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var visualLines = GetValidVisualLines(textView);
            if (_owner._tab?.ShowIntralineChanges != true || visualLines is null || visualLines.Count == 0)
            {
                return;
            }

            var lines = _lines();
            if (lines.Count == 0)
            {
                return;
            }

            // A wrapped AvalonEdit line has several VisualLine instances with the same
            // FirstDocumentLine.  Build its decoration once, otherwise the translucent fill
            // would be composited repeatedly on long lines.
            var drawnDocumentLines = new HashSet<int>();
            foreach (var visualLine in visualLines)
            {
                var index = visualLine.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= lines.Count || lines[index].IntralineChanges is not { Count: > 0 } ranges)
                {
                    continue;
                }

                var documentLine = visualLine.FirstDocumentLine;
                if (!drawnDocumentLines.Add(documentLine.Offset))
                {
                    continue;
                }

                var isRemoved = lines[index].Kind == GitDiffLineKind.Removed;
                var fill = _owner.Brush(isRemoved ? "DiffRemovedIntralineBrush" : "DiffAddedIntralineBrush");
                if (fill is null)
                {
                    continue;
                }

                var geometryBuilder = new BackgroundGeometryBuilder
                {
                    AlignToWholePixels = true,
                };

                foreach (var range in ranges)
                {
                    var start = Math.Clamp(documentLine.Offset + range.Start, documentLine.Offset, documentLine.EndOffset);
                    var end = Math.Clamp(start + range.Length, start, documentLine.EndOffset);
                    if (end <= start)
                    {
                        continue;
                    }

                    var segment = new TextSegment { StartOffset = start, Length = end - start };
                    // Keep the decoration limited to the actual changed range. Unlike the
                    // browser implementation's optional border, Nornia follows the Explorer
                    // selection treatment and draws a fill-only decoration with no outline.
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, false))
                    {
                        geometryBuilder.AddRectangle(textView, rect);
                    }
                }

                if (geometryBuilder.CreateGeometry() is { } geometry)
                {
                    drawingContext.DrawGeometry(fill, null, geometry);
                }
            }
        }
    }
}
