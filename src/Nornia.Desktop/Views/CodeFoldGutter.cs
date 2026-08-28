using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Nornia.Desktop.Code;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Nornia.Desktop.Views;

/// <summary>
/// VS Code 风格折叠栏(对应 vscode-main 的 foldingDecorations + folding.css:codicon chevron
/// 图标,展开=chevron-down、折叠=chevron-right)。显隐语义逐条对齐 vscode:
/// - 折叠态图标常显;展开态图标默认完全隐藏字形(opacity 0),鼠标悬停折叠栏整列时所有展开
///   图标一起浮现,离开折叠栏立即收起(不残留);
/// - 展开态只隐藏字形,**位置热区始终保留**:折叠栏固定 16px 宽、整列参与命中测试
///   (<see cref="IsHitTestVisible"/>),保证鼠标悬停(即使该行没有可见图标)能触发浮现;
/// - 颜色统一 foldingControlForeground(不随行变强调色),指针为手型;
/// - 点击 chevron 折叠/展开;点击被折叠内容行展开最近外层折叠;
/// - 被折叠区间遮挡的内层区域不渲染 chevron。
/// 数据与动作由宿主注入(<see cref="_regionProvider"/> 回调 + toggle/expand),本控件不依赖
/// AvalonEdit 折叠 API,保持可测试(<see cref="Revealing"/> 供测试注入悬停态)。
/// </summary>
public sealed class FoldGutterMargin : AbstractMargin
{
    private const double GutterWidth = 18;   // 容纳加粗 chevron 的列宽

    private readonly Func<IReadOnlyList<FoldRegion>> _regionProvider;
    private readonly Action<int> _toggleLine;
    private readonly Action<int> _expandLine;
    private IReadOnlyList<FoldRegion> _regions = [];
    private bool _revealing;
    private readonly FoldGutterMouseState _mouseState = new();

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

    public FoldGutterMargin(Func<IReadOnlyList<FoldRegion>> regionProvider, Action<int> toggleLine, Action<int> expandLine)
    {
        _regionProvider = regionProvider;
        _toggleLine = toggleLine;
        _expandLine = expandLine;
        Focusable = false;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        // 固定占位:折叠列宽度与绘制内容无关,显式 Width/MinWidth + MeasureOverride 双保险,
        // 即使整列全透明(未悬停)也不会塌缩为 0 宽。
        Width = GutterWidth;
        MinWidth = GutterWidth;
        // 位置热区:整列始终参与命中测试;鼠标进入时 MouseEnter → Revealing=true 浮现全部图标。
        IsHitTestVisible = true;
        MouseEnter += (_, _) => Revealing = true;
        MouseLeave += (_, _) => Revealing = false;
    }

    /// <summary>悬停揭示态:鼠标在折叠栏整列上时为 true(展开态图标浮现)。字段驱动而非每次
    /// 读 <c>IsMouseOver</c>,语义明确、时序稳定,并允许测试注入。</summary>
    internal bool Revealing
    {
        get => _revealing;
        set
        {
            if (_revealing == value)
            {
                return;
            }

            _revealing = value;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(GutterWidth, availableSize.Height);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnTextViewVisualsChanged;
            oldTextView.ScrollOffsetChanged -= OnTextViewVisualsChanged;
        }

        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnTextViewVisualsChanged;
            newTextView.ScrollOffsetChanged += OnTextViewVisualsChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        InvalidateVisual();
    }

    private void OnTextViewVisualsChanged(object? sender, EventArgs e) => InvalidateVisual();

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        // 透明占位:整列始终参与绘制(位置占用),展开态只是不画字形,悬停热区不因空白而失效。
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        _regions = _regionProvider();
        if (TextView is not { } view)
        {
            return;
        }

        var visualLines = GetValidVisualLines(view);
        if (visualLines is null || visualLines.Count == 0 || _regions.Count == 0)
        {
            return;
        }

        // 显隐对齐 vscode folding.css:折叠态常显;展开态鼠标未悬停时完全透明(opacity 0),
        // 悬停折叠栏整列时所有展开图标完整浮现,离开立即收起。折叠态与悬停浮现共用**同一
        // 主题刷子**,透明度严格一致;仅"展开且未悬停"使用全透明刷子(仍绘制字形)。
        var brush = Resolve("CodeFoldGutterForegroundBrush") ?? Brushes.Gray;
        var revealExpanded = _revealing;
        // 折叠常显与悬停浮现共用**同一刷子**,不透明度略微降低(更柔和),两者严格一致;
        // 仅"展开且未悬停"使用全透明刷子(仍绘制字形)。
        var foldBrush = brush.Clone();
        foldBrush.Opacity = 0.92;
        // 未悬停也**始终绘制**(全透明刷子 + 透明占位矩形):绘制调用永不跳过,折叠列始终
        // 参与渲染与命中;视觉上"展开且未悬停"不可见。
        var invisibleBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        var iconFont = Application.Current?.TryFindResource("IconFontFamily") as FontFamily;
        if (iconFont is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var visualLine in visualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            var region = FoldingRegions.FindStartVisibleAtLine(_regions, lineNumber);
            if (region is null)
            {
                continue;
            }

            // 折叠态 = chevron-right、展开态 = chevron-down(codicon 字形,与 vscode 同源);
            // 折叠态/悬停展开态同一刷子(略微降低不透明度),展开且未悬停 → 全透明刷子(仍绘制)。
            var glyph = region.IsCollapsed ? Codicons.ChevronRight : Codicons.ChevronDown;
            var drawBrush = region.IsCollapsed || revealExpanded ? foldBrush : invisibleBrush;
            var emSize = Math.Clamp(visualLine.Height * 0.85, 12, 26);
            var formatted = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(iconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), emSize, drawBrush, dpi);
            var x = Math.Max(0, (ActualWidth - formatted.Width) / 2);
            var y = visualLine.VisualTop - view.ScrollOffset.Y + Math.Max(0, (visualLine.Height - formatted.Height) / 2);
            drawingContext.DrawText(formatted, new Point(x, y));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var line = LineFromY(e.GetPosition(this).Y);
        if (line < 1)
        {
            return;
        }

        // Match vscode's folding.ts: remember the line on mouse-down and only act on mouse-up
        // when the pointer is still on that same line.
        _mouseState.Press(line);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        var releasedLine = LineFromY(e.GetPosition(this).Y);
        if (!_mouseState.TryRelease(releasedLine, out _))
        {
            e.Handled = true;
            return;
        }

        HandleClick(releasedLine);
        e.Handled = true;
    }

    /// <summary>折叠栏点击:命中 region 起始行 → 切换折叠;命中被折叠内容行 → 展开最近外层。
    /// <paramref name="line"/> 为当前文档行。实时读取折叠几何,不依赖最近一次渲染的缓存。</summary>
    private void HandleClick(int line)
    {
        _regions = _regionProvider();
        if (FoldingRegions.FindStartVisibleAtLine(_regions, line) is not null)
        {
            _toggleLine(line);
        }
        else if (FoldingRegions.FindCollapsedContaining(_regions, line) is not null)
        {
            _expandLine(line);
        }
    }

    private int LineFromY(double y)
    {
        var view = TextView;
        if (view is null)
        {
            return -1;
        }

        var visualLines = GetValidVisualLines(view);
        if (visualLines is null)
        {
            return -1;
        }

        foreach (var visualLine in visualLines)
        {
            var top = visualLine.VisualTop - view.ScrollOffset.Y;
            if (y >= top && y < top + visualLine.Height)
            {
                return visualLine.FirstDocumentLine.LineNumber;
            }
        }

        return -1;
    }

    private Brush? Resolve(string key) =>
        TryFindResource(key) as Brush ?? Application.Current?.TryFindResource(key) as Brush;
}

/// <summary>Mouse-down/mouse-up identity guard matching vscode's folding gutter click handling.
/// Kept separate from WPF coordinates so the same-line contract can be tested without a real mouse.
/// </summary>
internal sealed class FoldGutterMouseState
{
    private int _pressedLine = -1;

    public void Press(int line) => _pressedLine = line >= 1 ? line : -1;

    public bool TryRelease(int line, out int pressedLine)
    {
        pressedLine = _pressedLine;
        _pressedLine = -1;
        return pressedLine >= 1 && line == pressedLine;
    }
}

/// <summary>折叠段背景(vscode <c>editor.foldBackground</c> 语义):已折叠区域的首行整行
/// 画半透明背景,提示该处有折叠内容。绘制时实时解析主题刷子,主题切换只需重绘一帧。
/// <paramref name="isEnabled"/> 供宿主按容量档位降级(窗口化大文件关闭整层背景),
/// 每次绘制只查一次。</summary>
public sealed class FoldBackgroundRenderer : IBackgroundRenderer
{
    private readonly Func<IReadOnlyList<FoldRegion>> _regionProvider;
    private readonly Func<bool>? _isEnabled;

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

    public FoldBackgroundRenderer(Func<IReadOnlyList<FoldRegion>> regionProvider, Func<bool>? isEnabled = null)
    {
        _regionProvider = regionProvider;
        _isEnabled = isEnabled;
    }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_isEnabled is not null && !_isEnabled())
        {
            return;
        }

        var regions = _regionProvider();
        var visualLines = GetValidVisualLines(textView);
        if (regions.Count == 0 || visualLines is null || visualLines.Count == 0)
        {
            return;
        }

        var brush = Application.Current?.TryFindResource("CodeFoldBackgroundBrush") as Brush;
        if (brush is null)
        {
            return;
        }

        // 已折叠段首行集合 O(R+V) 替代旧实现 O(可视行×全部段) 的双重循环。
        var collapsedStartLines = new HashSet<int>();
        foreach (var region in regions)
        {
            if (region.IsCollapsed)
            {
                collapsedStartLines.Add(region.StartLine);
            }
        }

        foreach (var visualLine in visualLines)
        {
            if (collapsedStartLines.Contains(visualLine.FirstDocumentLine.LineNumber))
            {
                drawingContext.DrawRectangle(brush, null,
                    new Rect(0, visualLine.VisualTop - textView.ScrollOffset.Y, textView.ActualWidth, visualLine.Height));
            }
        }
    }
}
