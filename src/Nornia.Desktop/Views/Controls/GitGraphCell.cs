using System.Windows;
using System.Windows.Media;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;

namespace Nornia.Desktop.Views.Controls;

/// <summary>提交图形的一行泳道单元(VS Code 图表列):一条分支一条线、提交为线上的圆点,
/// 仅分支创建/合并才画分叉曲线。贯穿竖线、圆点与曲线按 <see cref="GitGraphRow"/> 绘制:
/// 圆点泳道在圆点处留缺口并绘制圆点(实心压线,无背景光晕),圆点泳道仅在从上方延续时
/// 画上段、仅在存在同泳道父提交时画下段,分支顶部/根部没有悬空线段;泳道按分支语义着色
/// (<see cref="GitGraphRow.LaneColorKeys"/>/圆点与下半段、<see cref="GitGraphRow.LaneIncomingColorKeys"/>/
/// 上半段,分段切换落在圆点上),无分支数据时回退三主题的 GraphLane*Brush 调色板。
/// 轻量 OnRender,无布局开销;全部几何常量与纯函数
/// 集中在 <see cref="GitGraphLayout"/>,可脱离 WPF 呈现做单元测试。</summary>
public sealed class GitGraphCell : FrameworkElement
{
    private static readonly string[] PaletteKeys =
    [
        "GraphLane1Brush",
        "GraphLane2Brush",
        "GraphLane3Brush",
        "GraphLane4Brush",
        "GraphLane5Brush",
        "GraphLane6Brush",
    ];

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row),
        typeof(GitGraphRow),
        typeof(GitGraphCell),
        new FrameworkPropertyMetadata(null, OnRowChanged));

    public GitGraphRow? Row
    {
        get => (GitGraphRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>贯穿模式:展开内容(同步栏展开区/提交行文件清单)占位时,泳道以整行竖线穿过,
    /// 不画圆点与曲线——线在增高的行内保持连续。渲染语义:所有活动泳道(贯穿 + 圆点泳道)
    /// 画全高竖线,颜色取出色(贯穿段永远位于任何圆点之下,不发生颜色切换)。</summary>
    public static readonly DependencyProperty PassThroughProperty = DependencyProperty.Register(
        nameof(PassThrough),
        typeof(bool),
        typeof(GitGraphCell),
        new FrameworkPropertyMetadata(false, OnRowChanged));

    public bool PassThrough
    {
        get => (bool)GetValue(PassThroughProperty);
        set => SetValue(PassThroughProperty, value);
    }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((GitGraphCell)d).InvalidateVisual();

    public GitGraphCell()
    {
        Loaded += (_, _) => ThemeEvents.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeEvents.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        // 非宿主线程的广播归组回宿主 Dispatcher(见 CodeDocumentView.OnThemeChanged)。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(InvalidateVisual);
            return;
        }

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(GitGraphLayout.CellWidth, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Row is not { } row)
        {
            return;
        }

        var height = ActualHeight;
        var centerY = GitGraphLayout.CenterY(height);

        var palette = PaletteKeys
            .Select(key => (TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Brush ?? Brushes.Gray)
            .ToArray();
        Brush BrushForLane(int lane)
        {
            if (row.LaneColorKeys is { } keys && lane >= 0 && lane < keys.Count && keys[lane] is string k && !string.IsNullOrWhiteSpace(k))
            {
                var b = (TryFindResource(k) ?? Application.Current?.TryFindResource(k)) as Brush;
                if (b != null) return b;
            }
            return palette[Math.Abs(lane) % palette.Length];
        }

        // 上半段(从上方接入的线)优先取入色:分段着色的颜色切换发生在圆点上,圆点以上半段
        // 用上一行的出色,相邻两个圆点之间的连线全程单色(线的颜色由端点决定)。
        Brush BrushForLaneIncoming(int lane)
        {
            if (row.LaneIncomingColorKeys is { } keys && lane >= 0 && lane < keys.Count && keys[lane] is string k && !string.IsNullOrWhiteSpace(k))
            {
                var b = (TryFindResource(k) ?? Application.Current?.TryFindResource(k)) as Brush;
                if (b != null) return b;
            }
            return BrushForLane(lane);
        }
        Pen PenFor(Brush brush)
        {
            var pen = new Pen(brush, GitGraphLayout.LaneThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            return pen;
        }
        Pen LanePen(int lane) => PenFor(BrushForLane(lane));
        Pen LanePenIncoming(int lane) => PenFor(BrushForLaneIncoming(lane));

        // 贯穿模式:展开内容占位的行只画整行竖线(贯穿 + 圆点泳道),保持连线不断。
        if (PassThrough)
        {
            foreach (var lane in row.PassLanes.Append(row.DotLane).Distinct())
            {
                var passX = GitGraphLayout.LaneX(lane);
                drawingContext.DrawLine(LanePen(lane), new Point(passX, 0), new Point(passX, height));
            }

            return;
        }

        // 1) 贯穿泳道:整行高度的竖线(泳道中心为整数坐标,线条落位稳定)。
        //    只有非圆点泳道整行贯穿;圆点泳道在圆点处留缺口,上/下两段分别在步骤 2 处理,
        //    这样圆点不打断分支线,分支线与圆点连成一条连续线(一条分支一条线)。
        foreach (var passLane in row.PassLanes)
        {
            var x = GitGraphLayout.LaneX(passLane);
            drawingContext.DrawLine(LanePen(passLane), new Point(x, 0), new Point(x, height));
        }

        // 2) 连续泳道与分叉 / 汇入曲线:
        //    - 圆点泳道从上方延续时画上段(行顶到圆点顶缘),把上一行的下段衔接起来;
        //    - 同泳道连线画下段(圆点底缘到行底),接到下一行的上段;
        //    - 汇入侧线(原占位首父的泳道):竖线画到行中,再折线汇入圆点泳道(git 的 / 折线);
        //    - 异泳道连线(分叉/合并)画横扫行中下半部的平滑贝塞尔,终点落在父泳道行底,
        //      与下一行该泳道从顶部开始的竖线首尾相连。
        if (row.DotLaneContinuesFromAbove)
        {
            var x = GitGraphLayout.LaneX(row.DotLane);
            drawingContext.DrawLine(LanePenIncoming(row.DotLane), new Point(x, 0), new Point(x, GitGraphLayout.LaneGap(height).Top));
        }

        foreach (var mergeFromLane in row.MergeFromLanes ?? [])
        {
            var fromX = GitGraphLayout.LaneX(mergeFromLane);
            var toX = GitGraphLayout.LaneX(row.DotLane);
            // 侧线保持自己的入色直到折入圆点(汇入行其出色已被清空为回退色,不反映该分支)。
            drawingContext.DrawLine(LanePenIncoming(mergeFromLane), new Point(fromX, 0), new Point(fromX, centerY));
            drawingContext.DrawGeometry(null, LanePenIncoming(mergeFromLane), MergeGeometry(mergeFromLane, row.DotLane, height));
        }

        foreach (var link in row.Links)
        {
            var fromX = GitGraphLayout.LaneX(link.FromLane);
            var toX = GitGraphLayout.LaneX(link.ToLane);
            if (GitGraphLayout.IsSameLane(link.FromLane, link.ToLane))
            {
                drawingContext.DrawLine(LanePen(link.ToLane), new Point(toX, GitGraphLayout.LaneGap(height).Bottom), new Point(toX, height));
                continue;
            }

            drawingContext.DrawGeometry(null, LanePen(link.ToLane), MergeGeometry(link.FromLane, link.ToLane, height));
        }

        // 3) 圆点:实心泳道色圆点压在本泳道的缺口上,与泳道线连成一体(无背景光晕);
        //    DotHollow(分支顶端的同步节点,上游 tip 位置)画空心圆,环形描边不填充。
        var dotBrush = BrushForLane(row.DotLane);
        var dotX = GitGraphLayout.LaneX(row.DotLane);
        if (row.DotHollow)
        {
            var dotPen = new Pen(dotBrush, GitGraphLayout.LaneThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
                DashStyle = row.DotDashed ? DashStyles.Dash : DashStyles.Solid,
            };
            dotPen.Freeze();
            drawingContext.DrawEllipse(null, dotPen, new Point(dotX, centerY), GitGraphLayout.DotRadius, GitGraphLayout.DotRadius);
        }
        else
        {
            drawingContext.DrawEllipse(dotBrush, null, new Point(dotX, centerY), GitGraphLayout.DotRadius, GitGraphLayout.DotRadius);
        }
    }

    private static Geometry MergeGeometry(int fromLane, int toLane, double height)
    {
        var (control1, control2) = GitGraphLayout.CurveControlPoints(fromLane, toLane, height);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(GitGraphLayout.LaneX(fromLane), GitGraphLayout.CenterY(height)), false, false);
            context.BezierTo(control1, control2, new Point(GitGraphLayout.LaneX(toLane), height), true, true);
        }

        geometry.Freeze();
        return geometry;
    }
}

/// <summary>提交图形泳道单元格的几何常量与纯函数(不依赖 WPF 呈现,可单元测试)。
/// 布局:泳道 0 中心位于单元格横向中点,泳道按固定间距向右排布;曲线终点落在父泳道
/// 行底,与下一行从顶部开始的竖线首尾相连;圆点泳道在 <see cref="LaneGap"/> 区间内留缺口,
/// 圆点实心压线,上/下两段按需求分别绘制。</summary>
internal static class GitGraphLayout
{
    /// <summary>泳道单元格宽度。与 GitView.xaml 中"最近提交"图形列的列宽保持同一数值,
    /// 两处需同步修改。</summary>
    public const double CellWidth = 36;

    /// <summary>相邻泳道中心间距。</summary>
    public const double LanePitch = 7;

    /// <summary>泳道圆点半径。</summary>
    public const double DotRadius = 3.5;

    /// <summary>泳道线条粗细。</summary>
    public const double LaneThickness = 1.6;

    /// <summary>泳道 0 的横向中心(单元格横向中点)。</summary>
    public static double LaneOrigin => CellWidth / 2;

    /// <summary>第 lane 条泳道的横向中心坐标。</summary>
    public static double LaneX(int lane) => LaneOrigin + lane * LanePitch;

    /// <summary>圆点行中线 y(单元格垂直中点)。</summary>
    public static double CenterY(double height) => height / 2;

    /// <summary>圆点占用的纵向区间 [顶缘, 底缘]:圆点泳道的竖线在圆点处留缺口,
    /// 上下两段分别画到缺口边缘,圆点随后实心压上,分支线由此连续穿过圆点。</summary>
    public static (double Top, double Bottom) LaneGap(double height)
    {
        var centerY = CenterY(height);
        return (centerY - DotRadius, centerY + DotRadius);
    }

    /// <summary>同泳道连线判定(同泳道画直线,异泳道画贝塞尔曲线)。</summary>
    public static bool IsSameLane(int fromLane, int toLane) =>
        Math.Abs(LaneX(fromLane) - LaneX(toLane)) < 0.5;

    /// <summary>异泳道连线的三次贝塞尔控制点:起点为圆点行中线,终点为父泳道行底;
    /// 两个控制点位于行中线下方四分之一行高处,曲线横扫行中下半部且 y 单调不越出单元格。</summary>
    public static (Point Control1, Point Control2) CurveControlPoints(int fromLane, int toLane, double height)
    {
        var mid = CenterY(height) + height * 0.25;
        return (new Point(LaneX(fromLane), mid), new Point(LaneX(toLane), mid));
    }
}
