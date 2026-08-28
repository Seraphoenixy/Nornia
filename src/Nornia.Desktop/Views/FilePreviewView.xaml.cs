using Nornia.Desktop.ViewModels;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Nornia.Desktop.Markdown;

namespace Nornia.Desktop.Views;

/// <summary>
/// Read-only file preview page: breadcrumb + actions, find / go-to-line bars, the AvalonEdit code
/// renderer and a read-only status bar. View-only: maps <see cref="FilePreviewTab"/> state onto the
/// <see cref="CodeDocumentView"/> and routes editor shortcuts that the global window handler leaves
/// to the code area (Ctrl+F / F3 / Ctrl+G / Esc).
/// </summary>
public partial class FilePreviewView : UserControl
{
    private FilePreviewTab? _tab;
    private EditorViewState? _pendingRestore;
    private double _outlineWidth = 260;
    private bool _outlinePanelOpen = true;
    // M9: 滚动偏移写入 50ms 尾沿节流(滚动事件风暴只落一次标签写入)。
    private MarkdownScrollThrottle? _scrollOffsetThrottle;

    /// <summary>M9: 滚动偏移节流器(测试断言写入次数用)。</summary>
    internal MarkdownScrollThrottle? ScrollOffsetThrottle => _scrollOffsetThrottle;

    /// <summary>预览控件句柄(集成测试断言跳转结果用;XAML x:Name 字段为私有)。</summary>
    internal MarkdownPreviewView MarkdownViewControl => MarkdownView;

    public FilePreviewView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        // M9: 卸载/键盘焦点离开时把排队中的偏移立即落盘,最终位置不丢。
        Unloaded += (_, _) => _scrollOffsetThrottle?.Flush();
        LostFocus += (_, _) => _scrollOffsetThrottle?.Flush();
        _scrollOffsetThrottle = new MarkdownScrollThrottle(offset =>
        {
            if (_tab is { } tab)
            {
                SaveMarkdownVerticalOffset(tab, offset);
            }
        });
        // FlowDocument content can become the focused element rather than bubbling through the
        // UserControl in some WPF input paths. Keep a direct preview route on the rendered surface
        // so the shortcuts advertised by the find/navigation UI work in Markdown preview too.
        MarkdownView.PreviewKeyDown += OnMarkdownPreviewKeyDown;
        FindBox.PreviewKeyDown += OnFindBoxKeyDown;
        GoToLineBox.PreviewKeyDown += OnGoToLineBoxKeyDown;
        CodeView.DocumentChanged += OnCodeDocumentChanged;
        CodeView.Editor.TextArea.Caret.PositionChanged += (_, _) => UpdatePositionText();
        // 每次预览渲染完成:分离 Markdig AST(大 AST 不随 FlowDocument 长期驻留),
        // 并在文档就绪后重跑位置恢复(结果赋值时的即时恢复可能仍面对旧/空文档)。
        MarkdownView.RenderCompleted += OnMarkdownRenderCompleted;
        UpdateOutlineLayout();
    }

    private void OnMarkdownRenderCompleted(object? sender, EventArgs e)
    {
        _tab?.DetachMarkdownDocument();
        if (_tab is { IsMarkdownRendered: true })
        {
            RestoreMarkdownPosition();
        }
    }

    /// <summary>惰性恢复:会话恢复出的标签不自动加载内容,首次点击页内任意位置(编辑区/面包屑/
    /// 状态栏)才触发首次解码 —— Preview(隧道)路由保证子控件上的点击同样生效。
    /// <see cref="FilePreviewTab.LoadAsync"/> 幂等,与选中链路的加载不会重复。</summary>
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_tab is { IsLoadFinished: false } tab)
        {
            _ = tab.LoadAsync();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // M9: 换标签前把排队中的偏移先写到"离开的"标签(闭包读的是当前 _tab)。
        _scrollOffsetThrottle?.Flush();
        CaptureViewState();
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab.ContentUpdating -= OnTabContentUpdating;
            _tab.GoToLineRequested -= OnGoToLineRequested;
            _tab.GoToPositionRequested -= OnGoToPositionRequested;
            _tab.FoldRequested -= OnFoldRequested;
            _tab.SearchMatches.CollectionChanged -= OnSearchMatchesChanged;
            MarkdownView.ScrollChanged -= OnMarkdownScrollChanged;
            MarkdownView.CurrentHeadingChanged -= OnMarkdownCurrentHeadingChanged;
        }

        _tab = DataContext as FilePreviewTab;
        if (_tab is null)
        {
            return;
        }

        _tab.PropertyChanged += OnTabPropertyChanged;
        _tab.ContentUpdating += OnTabContentUpdating;
        _tab.GoToLineRequested += OnGoToLineRequested;
        _tab.GoToPositionRequested += OnGoToPositionRequested;
        _tab.FoldRequested += OnFoldRequested;
        _tab.SearchMatches.CollectionChanged += OnSearchMatchesChanged;
        MarkdownView.ScrollChanged += OnMarkdownScrollChanged;
        MarkdownView.CurrentHeadingChanged += OnMarkdownCurrentHeadingChanged;
        // 选区查找:查找时从编辑器取当前选区范围;选区变化驱动"所选范围"复选框显隐。
        _tab.SelectionRangeProvider = () =>
        {
            var selection = CodeView.Editor.TextArea.Selection;
            var segment = selection.SurroundingSegment;
            return selection.IsEmpty || segment is null
                ? null
                : (segment.Offset, segment.Offset + segment.Length);
        };
        CodeView.Editor.TextArea.SelectionChanged += (_, _) =>
        {
            _tab.HasTextSelection = !CodeView.Editor.TextArea.Selection.IsEmpty;
            UpdatePositionText();
        };
        _pendingRestore = _tab.ViewState;
        RestoreMarkdownPosition();
        // 接线时序兜底:继承的 DataContext 绑定可能先于本处理器完成渲染(RenderCompleted
        // 触发时 _tab 尚未就绪)——此时补做 AST 分离。
        if (_tab.MarkdownRenderResult is { Document: not null } renderedResult
            && ReferenceEquals(MarkdownView.RenderedResult, renderedResult))
        {
            _tab.DetachMarkdownDocument();
        }

        // 共享视图切回一个渲染产物已释放/AST 已分离的渲染模式标签:重新解析渲染
        // (阅读位置由 RestoreMarkdownPosition 在新文档就绪后按优先级落位)。
        if (_tab.IsMarkdown && _tab.MarkdownMode == MarkdownViewMode.Rendered
            && _tab.IsLoadFinished && _tab.MarkdownRenderResult?.Document is null)
        {
            _ = _tab.RebuildMarkdownPreviewAsync();
        }

        CodeView.WordWrap = _tab.WordWrap;
        CodeView.ShowMinimap = _tab.ShowMinimap;
        CodeView.SetSearchMatches(_tab.SearchMatches, _tab.CurrentMatchViewIndex);
        CodeView.SetFoldingSections(_tab.FoldSections, _tab.ViewState?.FoldedOffsets, _tab.ViewState?.FoldedSymbolIds);
        UpdatePositionText();
        UpdateOutlineLayout();
    }

    /// <summary>大纲列使用可拖动的固定起始宽度；没有符号时完全收起，避免空侧栏占位。</summary>
    private void UpdateOutlineLayout()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(UpdateOutlineLayout);
            return;
        }

        var hasOutline = _tab?.HasOutline == true;
        if (hasOutline && _outlinePanelOpen)
        {
            OutlineSplitterColumn.Width = new GridLength(5, GridUnitType.Pixel);
            OutlineColumn.Width = new GridLength(Math.Clamp(_outlineWidth, 190, 320), GridUnitType.Pixel);
        }
        else
        {
            // Capture a user-adjusted width before collapsing so it is reused when the next
            // document with an outline is displayed in this view instance.
            if (OutlineColumn.ActualWidth is >= 190 and <= 320)
            {
                _outlineWidth = OutlineColumn.ActualWidth;
            }

            OutlineSplitterColumn.Width = new GridLength(0);
            OutlineColumn.Width = new GridLength(0);
        }
    }

    private void ToggleOutline_Click(object sender, RoutedEventArgs e)
    {
        if (_tab?.HasOutline != true)
        {
            return;
        }

        _outlinePanelOpen = !_outlinePanelOpen;
        OutlineToggleButton.ToolTip = _outlinePanelOpen ? "折叠大纲" : "展开大纲";
        UpdateOutlineLayout();
        e.Handled = true;
    }

    private void OutlineSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (OutlineColumn.ActualWidth is >= 190 and <= 320)
        {
            _outlineWidth = OutlineColumn.ActualWidth;
        }
    }

    private void OutlineSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!double.IsFinite(e.HorizontalChange) || OutlineColumn.ActualWidth <= 0)
        {
            return;
        }

        // The outline is on the right: moving the sash right makes the outline narrower,
        // moving it left makes the outline wider.
        var width = Math.Clamp(OutlineColumn.ActualWidth - e.HorizontalChange, 190, 320);
        OutlineColumn.Width = new GridLength(width, GridUnitType.Pixel);
        _outlineWidth = width;
    }

    /// <summary>Documents are replaced asynchronously after DataContext changes; apply folding and
    /// the saved reading position once the new text is actually in the editor.</summary>
    private void OnCodeDocumentChanged(object? sender, EventArgs e)
    {
        if (_tab is null)
        {
            return;
        }

                CodeView.SetFoldingSections(_tab.FoldSections, _tab.ViewState?.FoldedOffsets, _tab.ViewState?.FoldedSymbolIds);
        // Search results may have been applied while the replacement document was still empty;
        // reapply them after the new document is installed so valid matches are highlighted.
        CodeView.SetSearchMatches(_tab.SearchMatches, _tab.CurrentMatchViewIndex);
        if (_pendingRestore is not null)
        {
            CodeView.RestoreViewState(_pendingRestore);
            _pendingRestore = null;
        }
    }

    /// <summary>外部文件变更重载:内容替换前捕获当前阅读位置(源码滚动/光标/选区/折叠 + Markdown
    /// 标题锚点/偏移),替换后由 <see cref="OnCodeDocumentChanged"/> 的 _pendingRestore 恢复
    /// (RestoreViewState 对缩短文件做边界修正);Markdown 渲染位置由 RestoreMarkdownPosition
    /// 按锚点 → 行号 → 偏移 优先级落位,标题失效时自动回退。</summary>
    private void OnTabContentUpdating(object? sender, EventArgs e)
    {
        if (_tab is null || !IsLoaded)
        {
            return;
        }

        // 首次加载发布时编辑器里还没有可保存的内容(空文档):此时不得覆盖 _pendingRestore ——
        // 会话恢复出的标签依赖 DataContextChanged 设置的 ViewState 完成惰性恢复。
        if (CodeView.Document.TextLength == 0 && !_tab.IsLoadFinished)
        {
            return;
        }

        _pendingRestore = CodeView.CaptureViewState();
        if (_tab.IsMarkdownRendered
            && MarkdownView.Document is not null
            && ReferenceEquals(MarkdownView.RenderResult, _tab.MarkdownRenderResult))
        {
            SaveMarkdownVerticalOffset(_tab, MarkdownView.VerticalOffset);
            if (MarkdownView.CurrentHeading is { } heading)
            {
                _tab.SetMarkdownHeading(heading.Anchor, heading.Line);
            }
        }
    }

    /// <summary>Saves the current scroll/caret/fold position back to the tab whenever this view is
    /// replaced (switching tabs, closing, shutdown) so the reused tab restores the reading spot.
    /// 源码(光标/滚动)与预览(偏移/当前标题)分别保存,互不覆盖。</summary>
    private void CaptureViewState()
    {
        if (_tab is not null && IsLoaded)
        {
            _tab.ViewState = CodeView.CaptureViewState();
            // 仅当预览模式且预览文档仍属当前标签时捕获——文档已被释放(切源码模式/换标签)时
            // 偏移读数是无意义的 0,不得回退已保存的阅读位置。
            if (_tab.IsMarkdownRendered
                && MarkdownView.Document is not null
                && ReferenceEquals(MarkdownView.RenderResult, _tab.MarkdownRenderResult))
            {
                _scrollOffsetThrottle?.Flush();
                SaveMarkdownVerticalOffset(_tab, MarkdownView.VerticalOffset);
                // 源码模式下光标已维护逻辑位置,预览的旧标题不得回退它。
                if (MarkdownView.CurrentHeading is { } heading)
                {
                    _tab.SetMarkdownHeading(heading.Anchor, heading.Line);
                }
            }
        }
    }

    private void OnMarkdownScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // M9: 滚动事件逐条不再写标签(每次写都触发属性通知链)——50ms 尾沿节流,
        // 滚动风暴只落一次"最新值"写入;卸载/失焦/换标签时 Flush 保证最终值落地。
        _scrollOffsetThrottle?.Raise(e.VerticalOffset);
    }

    /// <summary>M10: 把预览垂直偏移换算为垂直进度(offset/extent)后写入标签持久化字段
    /// (旧格式为像素偏移;读取/恢复侧按 ≤1=进度、>1=旧像素偏移 归一。extent 未布局为 0
    /// → 进度归 0,与"文档顶部"一致)。</summary>
    private void SaveMarkdownVerticalOffset(FilePreviewTab tab, double offset)
    {
        tab.MarkdownVerticalOffset = MarkdownPreviewView.ToVerticalProgress(
            offset, MarkdownView.ExtentHeight);
    }

    /// <summary>预览滚动(经视图内节流)→ 写入标签统一逻辑位置:大纲高亮、父级展开与顶部面包屑
    /// 随当前标题更新。仅预览模式接收——源码模式下逻辑位置由光标维护,不得被预览旧状态回退。</summary>
    private void OnMarkdownCurrentHeadingChanged(object? sender, MarkdownHeading? heading)
    {
        if (_tab is not { IsMarkdownRendered: true })
        {
            return;
        }

        _tab.SetMarkdownHeading(heading?.Anchor, heading?.Line ?? 0);
    }

    /// <summary>Markdown 预览位置恢复优先级:当前文档中存在的标题锚点 → 标题源码行号 →
    /// 垂直进度兜底(M10:存储值 ≤1 = 保存时 offset/extent,文档/字体变化后按比例映射回
    /// 新文档;&gt;1 = 旧版像素偏移,直接钳制)。锚点/行号在新文档中失效时自动落进度兜底。</summary>
    private void RestoreMarkdownPosition()
    {
        if (_tab is null) return;
        if (!string.IsNullOrEmpty(_tab.MarkdownAnchor) && MarkdownView.ScrollToAnchorSafe(_tab.MarkdownAnchor))
        {
            return;
        }

        if (_tab.MarkdownHeadingLine > 0 && MarkdownView.ScrollToLineSafe(_tab.MarkdownHeadingLine))
        {
            return;
        }

        // 进度/偏移兜底:排队到文档布局就绪后由视图按新 extent 映射(旧偏移越界不再失真)。
        MarkdownView.QueueVerticalRestore(_tab.MarkdownVerticalOffset);
    }

    /// <summary>预览 → 源码:跳转到当前标题行;没有标题时恢复源码光标(ViewState)。</summary>
    private void RestoreSourcePositionForMarkdown()
    {
        if (_tab is null) return;
        var line = _tab.MarkdownHeadingLine;
        if (line <= 0)
        {
            line = _tab.ViewState?.CaretLine ?? 0;
        }

        if (line > 0)
        {
            // Markdown 源码视图与普通源码统一使用 CodeDocumentView 的定位实现，
            // 包括光标、行列边界处理和目标行居中。
            var column = _tab.ViewState is { CaretLine: var caretLine, CaretColumn: var caretColumn }
                && caretLine == line
                ? caretColumn
                : 1;
            CodeView.JumpToPosition(line, column, emphasize: false);
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tab is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(FilePreviewTab.FoldSections):
                // Content 赋值先于 ComputeFolds:文档替换触发的 SetFoldingSections 可能读到
                // 空 FoldSections。此处补发——折叠区间就绪/变化时必然重建(首开与预览切换
                // 都依赖这条路径,CodeDocumentView 侧换文档自愈作为第二道保险)。
                CodeView.SetFoldingSections(_tab.FoldSections, _tab.ViewState?.FoldedOffsets, _tab.ViewState?.FoldedSymbolIds);
                break;
            case nameof(FilePreviewTab.HasOutline):
                UpdateOutlineLayout();
                break;
            case nameof(FilePreviewTab.MarkdownRenderResult):
                // 文档就绪后按恢复优先级落位(锚点 → 行号 → 偏移)。
                RestoreMarkdownPosition();
                break;
            case nameof(FilePreviewTab.MarkdownMode):
                // 模式切换:预览 → 源码跳当前标题行(无标题恢复光标),源码 → 预览按优先级落位。
                if (_tab.IsMarkdown)
                {
                    if (_tab.IsMarkdownRendered)
                    {
                        RestoreMarkdownPosition();
                    }
                    else
                    {
                        RestoreSourcePositionForMarkdown();
                    }
                }
                break;
            case nameof(FilePreviewTab.WordWrap):
                CodeView.WordWrap = _tab.WordWrap;
                break;
            case nameof(FilePreviewTab.ShowMinimap):
                CodeView.ShowMinimap = _tab.ShowMinimap;
                break;
            case nameof(FilePreviewTab.ShowStickyScroll):
                CodeView.ShowStickyScroll = _tab.ShowStickyScroll;
                break;
            case nameof(FilePreviewTab.MinimapRenderCharacters):
                CodeView.MinimapRenderCharacters = _tab.MinimapRenderCharacters;
                break;
            case nameof(FilePreviewTab.MinimapWidth):
                CodeView.MinimapWidth = _tab.MinimapWidth;
                break;
            case nameof(FilePreviewTab.LineNumbersRelative):
                CodeView.LineNumbersRelative = _tab.LineNumbersRelative;
                break;
            case nameof(FilePreviewTab.RulerColumns):
                CodeView.RulerColumns = _tab.RulerColumns;
                break;
            case nameof(FilePreviewTab.IsActive) when !_tab.IsActive:
                // Deactivated tab: save the reading position before the editor is swapped away.
                CaptureViewState();
                break;
            case nameof(FilePreviewTab.CurrentMatchIndex):
            case nameof(FilePreviewTab.MatchCount):
                if (_tab.ShowAllHighlights)
                {
                    CodeView.SetSearchMatches(_tab.SearchMatches, _tab.CurrentMatchViewIndex);
                }
                break;
            case nameof(FilePreviewTab.ShowAllHighlights):
                if (_tab.ShowAllHighlights)
                {
                    CodeView.SetSearchMatches(_tab.SearchMatches, _tab.CurrentMatchViewIndex);
                }
                else
                {
                    CodeView.ClearSearchMarkers();
                }
                break;
            case nameof(FilePreviewTab.IsFindBarOpen) when _tab.IsFindBarOpen:
                FindBox.Focus();
                FindBox.SelectAll();
                break;
            case nameof(FilePreviewTab.IsGoToLineBarOpen) when _tab.IsGoToLineBarOpen:
                GoToLineBox.Focus();
                GoToLineBox.SelectAll();
                break;
        }
    }

    private void OnSearchMatchesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_tab is not null && _tab.ShowAllHighlights)
        {
            CodeView.SetSearchMatches(_tab.SearchMatches, _tab.CurrentMatchViewIndex);
        }
    }

    private void OnGoToLineRequested(object? sender, int line)
    {
        // 只有实际存在渲染文档时才走 Markdown 预览；普通文件和 Markdown 源码
        // 都统一走 CodeDocumentView。
        if (_tab?.IsMarkdownRendered == true)
        {
            MarkdownView.ScrollToLine(line);
            return;
        }

        JumpToSourceLine(line);
    }

    private void OnGoToPositionRequested(object? sender, (int Line, int Column) position)
    {
        if (_tab?.IsMarkdownRendered == true)
        {
            // 大纲点击/符号导航:预览模式按标题锚点精确跳转,无匹配标题时回退按行。
            var heading = _tab.MarkdownHeadings.FirstOrDefault(item => item.Line == position.Line);
            if (heading is not null)
            {
                MarkdownView.ScrollToAnchor(heading.Anchor);
                return;
            }

            MarkdownView.ScrollToLine(position.Line);
            return;
        }

        JumpToSourcePosition(position.Line, position.Column);
    }

    /// <summary>Shared source navigation for every source document, including Markdown source mode.</summary>
    private void JumpToSourceLine(int line, bool emphasize = true) =>
        CodeView.JumpToLine(line, emphasize);

    /// <summary>Shared source navigation preserving the 1-based line and column.</summary>
    private void JumpToSourcePosition(int line, int column, bool emphasize = true) =>
        CodeView.JumpToPosition(line, column, emphasize);

    private void MarkdownView_LinkRequested(object? sender, MarkdownLinkRequest request)
    {
        if (_tab is null || request.IsImage) return;
        var url = request.Url.Trim();
        if (url.Length == 0) return;
        if (url.StartsWith('#'))
        {
            MarkdownView.ScrollToAnchor(url[1..]);
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https" or "mailto")
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _tab.MarkdownRenderNotice = $"无法打开链接：{FilePreviewTab.SingleLineMessage(ex)}";
            }
            return;
        }

        try
        {
            var pathPart = url.Split('#', 2)[0];
            if (pathPart.Length == 0) return;
            var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_tab.Path) ?? string.Empty,
                Uri.UnescapeDataString(pathPart)));
            if (File.Exists(path))
            {
                if (FindAncestor<EditorAreaView>(this)?.DataContext is EditorAreaViewModel editor)
                {
                    _ = editor.OpenFileAsync(path);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            _tab.MarkdownRenderNotice = $"无法打开本地链接：{FilePreviewTab.SingleLineMessage(ex)}";
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnFoldRequested(object? sender, (EditorFoldRequest Kind, int Level) request)
    {
        switch (request.Kind)
        {
            case EditorFoldRequest.CollapseAll:
                CodeView.SetAllFoldState(true);
                break;
            case EditorFoldRequest.ExpandAll:
                CodeView.SetAllFoldState(false);
                break;
            case EditorFoldRequest.ToLevel:
                CodeView.FoldToLevel(request.Level);
                break;
        }
    }

    /// <summary>光标动 → 更新文件预览头部的面包屑符号段。</summary>
    public void CodeView_CaretLineChanged(object sender, int line)
    {
        if (_tab is not null)
        {
            _tab.UpdateCaretLine(line);
        }
    }

    /// <summary>光标/选区变化 → 写标签的 <see cref="FilePreviewTab.CaretPositionText"/>;
    /// 显示位置在窗口主状态栏的"当前文档状态"段(原视图内底栏已移除)。</summary>
    private void UpdatePositionText()
    {
        if (_tab is null)
        {
            return;
        }

        var caret = CodeView.Editor.TextArea.Caret.Position;
        var selection = CodeView.Editor.TextArea.Selection;
        var text = caret is { } c ? $"Ln {c.Line}, Col {c.Column}" : string.Empty;
        var document = CodeView.Editor.Document;
        if (!selection.IsEmpty && document.TextLength > 0)
        {
            var segment = selection.SurroundingSegment;
            var start = segment is null ? -1 : Math.Clamp(segment.Offset, 0, document.TextLength);
            var end = segment is null
                ? -1
                : Math.Clamp(segment.Offset + segment.Length, start, document.TextLength);
            var chars = start < 0 || end <= start ? 0 : end - start;
            var lines = start < 0 || end <= start
                ? 0
                : document.GetLineByOffset(start).LineNumber
                  - document.GetLineByOffset(end - 1).LineNumber + 1;
            text += $"  (已选择 {chars} 字符 / {lines} 行)";
        }

        _tab.CaretPositionText = text;
    }

    // ===== Editor shortcuts (the code area owns them; MainWindow leaves them alone) =====

    // ===== 面包屑键盘导航(VS Code:Ctrl+Shift+. 聚焦面包屑,左右键跨段) =====

    private void BreadcrumbBar_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Right)
        {
            ((UIElement)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
        else if (e.Key == Key.Left)
        {
            ((UIElement)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
        }
        else
        {
            e.Handled = false;
        }
    }

    private void FocusBreadcrumb() => BreadcrumbBar.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

    private void OnMarkdownPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!e.Handled)
        {
            HandleEditorShortcut(e);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) => HandleEditorShortcut(e);

    private void HandleEditorShortcut(KeyEventArgs e)
    {
        if (_tab is null)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // VS Code: Ctrl+Shift+. 聚焦面包屑。
        if (ctrl && shift && e.Key == Key.OemPeriod)
        {
            FocusBreadcrumb();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.F)
        {
            // 正文有选定内容时按 Ctrl+F,用选中文本覆盖更新搜索框(无论搜索框是否为空);
            // 选区文本按当前显示文档(全文/窗口)的坐标从 Content 截取。
            if (!_tab.IsMarkdownRendered)
            {
                var selection = CodeView.Editor.TextArea.Selection;
                if (!selection.IsEmpty
                    && selection.SurroundingSegment is { } segment
                    && segment.Offset >= 0 && segment.Offset + segment.Length <= _tab.Content.Length)
                {
                    _tab.SearchText = _tab.Content[segment.Offset..(segment.Offset + segment.Length)];
                }
            }

            // 查找栏已打开时 Ctrl+F 不重开,而是把焦点交回查找输入框。
            var wasOpen = _tab.IsFindBarOpen;
            _tab.OpenFindBarCommand.Execute(null);
            if (wasOpen)
            {
                FindBox.Focus();
                FindBox.SelectAll();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            if (shift)
            {
                _tab.FindPreviousCommand.Execute(null);
            }
            else
            {
                _tab.FindNextCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.G)
        {
            _tab.OpenGoToLineBarCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_tab.IsFindBarOpen || _tab.IsGoToLineBarOpen)
            {
                _tab.IsFindBarOpen = false;
                _tab.IsGoToLineBarOpen = false;
            }
            else
            {
                _tab.ClearSearchCommand.Execute(null);
            }

            FocusReadingSurface();
            e.Handled = true;
        }
    }

    private void FocusReadingSurface()
    {
        if (_tab?.IsMarkdownRendered == true)
        {
            MarkdownView.Focus();
        }
        else
        {
            CodeView.Editor.TextArea.Focus();
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (_tab is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            // Enter 下一个 · Shift+Enter 上一个(与工具提示一致)。
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _tab.FindPreviousCommand.Execute(null);
            }
            else
            {
                _tab.FindNextCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _tab.ClearSearchCommand.Execute(null);
            FocusReadingSurface();
            e.Handled = true;
        }
    }

    private void OnGoToLineBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (_tab is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _tab.GoToLineCommand.Execute(null);
            FocusReadingSurface();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _tab.IsGoToLineBarOpen = false;
            FocusReadingSurface();
            e.Handled = true;
        }
    }
}

/// <summary>M9: 滚动偏移写入的 50ms 尾沿节流。滚动事件高频触发 <see cref="Raise"/>——
/// 每次只更新"待写值"并在首次时启动定时器;50ms 内无新事件才写一次(最新值),
/// 滚动风暴从"每事件一次属性通知"降到"每 50ms 一次"。<see cref="Flush"/> 在
/// 卸载/键盘焦点离开/换标签/手动捕获前立即落盘排队值,最终位置不丢。</summary>
internal sealed class MarkdownScrollThrottle
{
    /// <summary>默认节流窗口(与 VS Code 的 scrollProgress 节流同量级)。</summary>
    internal const double DefaultIntervalMs = 50;

    private readonly Action<double> _write;
    private readonly double _intervalMs;
    private double _pendingOffset;
    private bool _armed;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private int _writeCount;

    public MarkdownScrollThrottle(Action<double> write, double intervalMs = DefaultIntervalMs)
    {
        _write = write;
        _intervalMs = intervalMs;
    }

    /// <summary>已发生的标签写入次数(测试断言"滚动风暴 → ≤2 次写入")。</summary>
    internal int WriteCount => _writeCount;

    /// <summary>记录一次滚动事件的最新偏移;窗口内重复调用不产生写入。</summary>
    public void Raise(double offset)
    {
        _pendingOffset = offset;
        if (_armed)
        {
            return;
        }

        _armed = true;
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_intervalMs),
        };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            _timer = null;
            _armed = false;
            WritePending();
        };
        _timer.Start();
    }

    /// <summary>立即写入排队中的偏移并取消定时器(无排队值为空操作)。</summary>
    public void Flush()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer = null;
        }

        if (_armed)
        {
            _armed = false;
            WritePending();
        }
    }

    private void WritePending()
    {
        _write(_pendingOffset);
        _writeCount++;
    }
}
