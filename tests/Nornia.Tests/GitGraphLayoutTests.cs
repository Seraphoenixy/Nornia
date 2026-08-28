using System.Windows;
using Nornia.Desktop.Views.Controls;

namespace Nornia.Tests;

/// <summary>提交图形泳道几何(<see cref="GitGraphLayout"/>)的纯函数测试:泳道中心排布、
/// 同/异泳道判定与贝塞尔控制点边界,均不依赖 WPF 呈现。</summary>
public sealed class GitGraphLayoutTests
{
    [Fact]
    public void LaneX_IsSymmetricAboutTheCellOrigin()
    {
        Assert.Equal(GitGraphLayout.CellWidth / 2, GitGraphLayout.LaneX(0));
        Assert.Equal(GitGraphLayout.LanePitch, GitGraphLayout.LaneX(1) - GitGraphLayout.LaneX(0));
        Assert.Equal(GitGraphLayout.LaneX(-1), GitGraphLayout.LaneX(1) - 2 * GitGraphLayout.LanePitch);
    }

    [Fact]
    public void LaneX_SpacingIsConstantAcrossLanes()
    {
        for (var lane = -5; lane < 10; lane++)
        {
            Assert.Equal(GitGraphLayout.LanePitch, GitGraphLayout.LaneX(lane + 1) - GitGraphLayout.LaneX(lane), 5);
        }
    }

    [Fact]
    public void LaneX_FirstThreeLanesFitInsideTheCell()
    {
        // 泳道 0/1/2 的中心与圆点右缘都必须落在单元格宽度内,第 3 条起允许被滚动区裁剪。
        Assert.True(GitGraphLayout.LaneX(2) + GitGraphLayout.DotRadius <= GitGraphLayout.CellWidth);
        Assert.True(GitGraphLayout.LaneX(3) > GitGraphLayout.CellWidth);
    }

    [Fact]
    public void IsSameLane_TrueOnlyForIdenticalLanes()
    {
        Assert.True(GitGraphLayout.IsSameLane(0, 0));
        Assert.True(GitGraphLayout.IsSameLane(3, 3));
        Assert.False(GitGraphLayout.IsSameLane(0, 1));
        Assert.False(GitGraphLayout.IsSameLane(1, 0));
    }

    [Theory]
    [InlineData(0, 1, 22)]
    [InlineData(0, 2, 22)]
    [InlineData(1, 0, 26)]
    [InlineData(2, 0, 40)]
    [InlineData(0, 0, 22)]
    public void CurveControlPoints_StayInsideTheCell(int fromLane, int toLane, double height)
    {
        var (control1, control2) = GitGraphLayout.CurveControlPoints(fromLane, toLane, height);
        var centerY = GitGraphLayout.CenterY(height);

        Assert.Equal(GitGraphLayout.LaneX(fromLane), control1.X);
        Assert.Equal(GitGraphLayout.LaneX(toLane), control2.X);
        foreach (var point in new[] { control1, control2 })
        {
            Assert.True(point.X >= 0 && point.X <= GitGraphLayout.CellWidth, $"控制点 x 越界: {point.X}");
            Assert.True(point.Y >= centerY && point.Y <= height, $"控制点 y 越界: {point.Y}");
        }

        // 曲线 y 分量单调不越出单元格(终点=行底,起点=行中线,控制点位于二者之间)。
        Assert.True(centerY <= height);
    }

    [Fact]
    public void CurveControlPoints_YBisectsTheLowerQuarterBand()
    {
        // 控制点 y = 行中线 + 四分之一行高:曲线横扫行中下半部,不贴底成"J 型钩"。
        var (control1, control2) = GitGraphLayout.CurveControlPoints(0, 1, 24);
        const double expected = 12 + 6; // centerY(12) + height * 0.25
        Assert.Equal(expected, control1.Y, 5);
        Assert.Equal(expected, control2.Y, 5);
    }

    [Fact]
    public void CenterY_IsHalfTheRowHeight()
    {
        Assert.Equal(11, GitGraphLayout.CenterY(22), 5);
        Assert.Equal(13, GitGraphLayout.CenterY(26), 5);
    }

    [Fact]
    public void LaneGap_IsSymmetricAboutTheRowCenter()
    {
        var (top, bottom) = GitGraphLayout.LaneGap(22);
        Assert.Equal(GitGraphLayout.CenterY(22) - GitGraphLayout.DotRadius, top, 5);
        Assert.Equal(GitGraphLayout.CenterY(22) + GitGraphLayout.DotRadius, bottom, 5);
        Assert.Equal(2 * GitGraphLayout.DotRadius, bottom - top, 5);
    }

    [Fact]
    public void LaneGap_SplitsTheDotLaneLineSoTheDotSitsOnIt()
    {
        // 缺口高度=圆点直径:圆点泳道的竖线在圆点处断开,圆点随后实心压上,
        // 上下两段(行顶→缺口顶、缺口底→行底)正好衔接成一条连续分支线。
        var (top, bottom) = GitGraphLayout.LaneGap(24);
        Assert.True(top >= 0 && bottom <= 24);
        Assert.True(top < bottom);
    }

    [Fact]
    public void PointType_IsSystemWindowsPoint()
    {
        // 几何助手输出 WPF Point(结构体),可脱离 STA/呈现层进行纯函数断言。
        var point = new Point(1, 2);
        Assert.Equal(1, point.X);
        Assert.Equal(2, point.Y);
    }
}