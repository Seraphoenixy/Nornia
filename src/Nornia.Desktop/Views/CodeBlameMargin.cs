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

/// <summary>行级 Blame 注解边距(GitLens 式):提交切换段的首行显示"作者 · 相对时间 · 短哈希"，
/// 其余行留白。数据由宿主推送(<see cref="SetAnnotations"/>),排版计算在纯逻辑
/// <see cref="BlameAnnotationLayout"/>;本控件只做"逻辑行 → 可视行"的绘制映射。
/// 性能契约:<see cref="FormattedText"/> 在数据推送时一次性构建并缓存,OnRender(滚动帧)零分配;
/// 悬浮详情按行命中缓存;无数据时整列折叠(宽度 0 + 隐藏)。</summary>
public sealed class CodeBlameMargin : AbstractMargin
{
    private const double MaxColumnWidth = 260;
    private const double TextPadding = 8;

    private IReadOnlyList<BlameAnnotation> _annotations = [];
    private FormattedText[] _texts = [];
    private string? _loadingText;
    private bool _truncated;
    private Typeface _typeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private double _emSize = 12;
    private Brush _foreground = Brushes.Gray;
    private Brush _uncommittedForeground = Brushes.Orange;
    private Brush _warningForeground = Brushes.OrangeRed;
    private DateTimeOffset _now = DateTimeOffset.Now;

    public CodeBlameMargin()
    {
        Focusable = false;
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;
        // 悬浮详情:按光标行命中的注解换 ToolTip(与渲染同一份数据,不额外分配)。
        ToolTip = string.Empty;
        MouseMove += OnMouseMoveForToolTip;
        MouseLeave += (_, _) => ClearToolTip();
    }

    /// <summary>推送注解数据并重排。null/空 = 折叠整列;loading = 在数据到达前显示占位行。</summary>
    public void SetAnnotations(
        IReadOnlyList<BlameAnnotation>? annotations,
        bool loading,
        bool truncated,
        Typeface typeface,
        double emSize,
        Brush foreground,
        Brush uncommittedForeground,
        Brush warningForeground)
    {
        _typeface = typeface;
        _emSize = emSize;
        _foreground = foreground;
        _uncommittedForeground = uncommittedForeground;
        _warningForeground = warningForeground;
        _now = DateTimeOffset.Now;
        ClearToolTip();
        _truncated = truncated;

        // 加载占位:单行提示,窄列(数据到达后重新推送会覆盖)。
        if (loading && (annotations is null || annotations.Count == 0))
        {
            _annotations = [];
            _texts = [];
            _loadingText = "读取 Blame…";
            Width = Math.Min(Measure(_loadingText).Width + TextPadding * 2, MaxColumnWidth);
            MinWidth = Width;
            Visibility = Visibility.Visible;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        _loadingText = null;
        if (annotations is null || annotations.Count == 0)
        {
            _annotations = [];
            _texts = [];
            Visibility = Visibility.Collapsed;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        _annotations = annotations;
        // 截断标记作为常驻提示(与 git.statusLimit 的"可见降级"约定一致)。
        var rows = truncated ? annotations.Count + 1 : annotations.Count;
        _texts = new FormattedText[rows];
        var width = TextPadding;
        for (var i = 0; i < annotations.Count; i++)
        {
            var text = BlameAnnotationLayout.Format(annotations[i], _now);
            _texts[i] = CreateText(text, annotations[i].IsUncommitted ? _uncommittedForeground : _foreground);
            width = Math.Max(width, _texts[i].Width + TextPadding * 2);
        }

        if (truncated)
        {
            _texts[rows - 1] = CreateText("⚠ Blame 已截断", _warningForeground);
            width = Math.Max(width, _texts[rows - 1].Width + TextPadding * 2);
        }

        Width = Math.Min(width, MaxColumnWidth);
        MinWidth = Width;
        Visibility = Visibility.Visible;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(Visibility == Visibility.Visible ? Math.Min(Width, MaxColumnWidth) : 0, 0);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (TextView is null || !TextView.VisualLinesValid || Visibility != Visibility.Visible)
        {
            return;
        }

        IReadOnlyList<VisualLine>? visualLines;
        try
        {
            visualLines = TextView.VisualLines;
        }
        catch (VisualLinesInvalidException)
        {
            return;
        }

        if (_loadingText is not null)
        {
            var firstTop = visualLines.Count > 0
                ? visualLines[0].GetTextLineVisualYPosition(visualLines[0].TextLines[0], VisualYPosition.TextTop) - TextView.VerticalOffset
                : 0;
            drawingContext.DrawText(CreateText(_loadingText, _foreground), new Point(TextPadding, firstTop));
            return;
        }

        if (_annotations.Count == 0)
        {
            return;
        }

        var floor = 0;
        foreach (var visualLine in visualLines)
        {
            var startLine = visualLine.FirstDocumentLine.LineNumber;
            var index = FindAnnotation(startLine, ref floor);
            if (index < 0)
            {
                continue;
            }

            DrawRow(drawingContext, index, visualLine);
        }

        // 截断标记固定画在编辑器顶部(独立于行命中,任何滚动位置都可见)。
        if (_truncated && _texts.Length > _annotations.Count && visualLines.Count > 0)
        {
            var first = visualLines[0];
            var markerTop = Math.Max(0,
                first.GetTextLineVisualYPosition(first.TextLines[0], VisualYPosition.TextTop)
                - TextView.VerticalOffset - first.Height);
            drawingContext.DrawText(_texts[_texts.Length - 1], new Point(TextPadding, markerTop));
        }
    }

    private void DrawRow(DrawingContext drawingContext, int index, VisualLine visualLine)
    {
        var y = visualLine.GetTextLineVisualYPosition(
            visualLine.TextLines[0], VisualYPosition.TextTop) - TextView.VerticalOffset;
        // 滚动帧零分配:直接复用推送时缓存的 FormattedText(绘制只读)。
        drawingContext.DrawText(_texts[index], new Point(TextPadding, y + ((visualLine.Height - _emSize) / 2)));
    }

    private void OnMouseMoveForToolTip(object sender, MouseEventArgs e)
    {
        if (TextView is null || !TextView.VisualLinesValid || _annotations.Count == 0)
        {
            ClearToolTip();
            return;
        }

        try
        {
            var visualLine = TextView.GetVisualLineFromVisualTop(e.GetPosition(TextView).Y + TextView.VerticalOffset);
            var floor = 0;
            var index = FindAnnotation(visualLine.FirstDocumentLine.LineNumber, ref floor);
            if (index < 0)
            {
                ClearToolTip();
                return;
            }

            ToolTip = BlameAnnotationLayout.FormatToolTip(_annotations[index]);
        }
        catch (VisualLinesInvalidException)
        {
            ClearToolTip();
        }
    }

    private void ClearToolTip()
    {
        if (ToolTip is string { Length: > 0 })
        {
            ToolTip = string.Empty;
        }
    }

    /// <summary>注解按行升序:光标随可视行前进,线性扫描 + 起点 floor 即可。</summary>
    private int FindAnnotation(int line, ref int floor)
    {
        for (var i = floor; i < _annotations.Count; i++)
        {
            var current = _annotations[i].Line;
            if (current == line)
            {
                floor = i;
                return i;
            }

            if (current > line)
            {
                floor = i;
                return -1;
            }
        }

        floor = _annotations.Count;
        return -1;
    }

    private FormattedText CreateText(string text, Brush foreground) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        _typeface,
        _emSize,
        foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private FormattedText Measure(string text) => CreateText(text, _foreground);
}
