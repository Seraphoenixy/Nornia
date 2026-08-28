using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

public sealed class WorkbenchLayoutMetricsTests
{
    [Fact]
    public void UsesVsCodeDesktopDensityBaseline()
    {
        Assert.Equal(35, WorkbenchLayoutMetrics.TitleBarHeight);
        // 主状态栏合并文档状态段后加高(22 → 28),单行布局不变。
        Assert.Equal(28, WorkbenchLayoutMetrics.StatusBarHeight);
        Assert.Equal(48, WorkbenchLayoutMetrics.ActivityBarWidth);
        Assert.Equal(48, WorkbenchLayoutMetrics.ActivityActionHeight);
        Assert.Equal(24, WorkbenchLayoutMetrics.ActivityIconSize);
        Assert.Equal(36, WorkbenchLayoutMetrics.CompactActivityBarWidth);
        Assert.Equal(28, WorkbenchLayoutMetrics.CompactActivityActionHeight);
        Assert.Equal(16, WorkbenchLayoutMetrics.CompactActivityIconSize);
    }

    [Fact]
    public void SidebarMaximumUsesAvailableWorkbenchWidth()
    {
        Assert.Equal(170, WorkbenchLayoutMetrics.SidebarMaximumFor(200));
        Assert.Equal(400, WorkbenchLayoutMetrics.SidebarMaximumFor(1000));
        Assert.Equal(720, WorkbenchLayoutMetrics.SidebarMaximumFor(2400));
    }

    [Fact]
    public void RequiredWidthAccountsForGroupsAndSashes()
    {
        Assert.Equal(872, WorkbenchLayoutMetrics.RequiredWorkbenchWidth(1, true, 300));
        Assert.Equal(876, WorkbenchLayoutMetrics.RequiredWorkbenchWidth(2, true, 300));
        Assert.Equal(572, WorkbenchLayoutMetrics.RequiredWorkbenchWidth(2, false));
        Assert.True(WorkbenchLayoutMetrics.ShouldAutoCollapseSidebar(871, 1, 300));
        Assert.False(WorkbenchLayoutMetrics.ShouldAutoCollapseSidebar(872, 1, 300));
    }

    [Fact]
    public void PanelSizingUsesContentKindAndHostFraction()
    {
        Assert.Equal(96, WorkbenchLayoutMetrics.PanelMinimumHeight(WorkbenchPanel.Output));
        Assert.Equal(96, WorkbenchLayoutMetrics.PanelMinimumHeight(WorkbenchPanel.Problems));
        Assert.Equal(140, WorkbenchLayoutMetrics.PanelMinimumHeight(WorkbenchPanel.Terminal));
        Assert.Equal(400, WorkbenchLayoutMetrics.PanelPreferredHeight(1000, WorkbenchPanel.Output));
        Assert.Equal(700, WorkbenchLayoutMetrics.PanelMaximumHeight(1000, WorkbenchPanel.Output));
        Assert.Equal(140, WorkbenchLayoutMetrics.PanelPreferredHeight(100, WorkbenchPanel.Terminal));
    }
}
