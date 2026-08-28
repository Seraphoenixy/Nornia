using Nornia.Desktop.ViewModels;

namespace Nornia.Desktop.Services;

/// <summary>
/// Shared workbench geometry rules. Values are WPF device-independent pixels (DIP), while
/// percentage calculations are based on the already-laid-out logical size of the host.
/// Keeping these rules outside the views prevents the shell, editor-group view and tests from
/// drifting apart as the layout evolves.
/// </summary>
public static class WorkbenchLayoutMetrics
{
    public const double TitleBarHeight = 35;
    public const double StatusBarHeight = 28;
    public const double TabBarHeight = 35;

    public const double ActivityBarWidth = 48;
    public const double ActivityActionHeight = 48;
    public const double ActivityIconSize = 24;
    public const double CompactActivityBarWidth = 36;
    public const double CompactActivityActionHeight = 28;
    public const double CompactActivityIconSize = 16;

    public const double SidebarMinimumWidth = 170;
    public const double SidebarDefaultWidth = 300;
    public const double SidebarMaximumWidth = 720;
    public const double SidebarMaximumFraction = 0.40;

    public const double EditorHostMinimumWidth = 520;
    public const double EditorGroupMinimumWidth = 240;
    public const double EditorGroupMinimumHeight = 160;

    public const double SashThickness = 4;
    public const double SashHitArea = 8;

    public const double LogPanelMinimumHeight = 96;
    public const double TerminalPanelMinimumHeight = 140;
    public const double PanelPreferredFraction = 0.40;
    public const double PanelMaximumFraction = 0.70;
    public const double PanelAbsoluteMaximumHeight = 1600;

    public const double WindowMinimumWidth = 960;
    public const double WindowMinimumHeight = 680;

    public static double SidebarMaximumFor(double availableWorkbenchWidth)
    {
        if (!double.IsFinite(availableWorkbenchWidth) || availableWorkbenchWidth <= 0)
        {
            return SidebarMaximumWidth;
        }

        return Math.Clamp(availableWorkbenchWidth * SidebarMaximumFraction,
            SidebarMinimumWidth, SidebarMaximumWidth);
    }

    public static double RequiredWorkbenchWidth(int groupCount, bool sidebarVisible,
        double sidebarWidth = SidebarDefaultWidth)
    {
        var groups = Math.Max(1, groupCount);
        var sidebar = sidebarVisible
            ? Math.Clamp(sidebarWidth, SidebarMinimumWidth, SidebarMaximumWidth)
            : 0;
        var sashes = Math.Max(0, groups - 1) * SashThickness;
        return ActivityBarWidth + (sidebarVisible ? SashThickness : 0) + sidebar
            + sashes + Math.Max(EditorHostMinimumWidth, groups * EditorGroupMinimumWidth);
    }

    public static bool ShouldAutoCollapseSidebar(double windowWidth, int groupCount,
        double sidebarWidth = SidebarDefaultWidth)
    {
        if (!double.IsFinite(windowWidth) || windowWidth <= 0)
        {
            return true;
        }

        var withSidebar = RequiredWorkbenchWidth(groupCount, true, sidebarWidth);
        return windowWidth < withSidebar;
    }

    public static double PanelMinimumHeight(WorkbenchPanel panel) =>
        panel == WorkbenchPanel.Terminal ? TerminalPanelMinimumHeight : LogPanelMinimumHeight;

    public static double PanelMaximumHeight(double hostHeight, WorkbenchPanel panel)
    {
        var minimum = PanelMinimumHeight(panel);
        if (!double.IsFinite(hostHeight) || hostHeight <= 0)
        {
            return Math.Min(PanelAbsoluteMaximumHeight, minimum);
        }

        return Math.Clamp(hostHeight * PanelMaximumFraction, minimum, PanelAbsoluteMaximumHeight);
    }

    public static double PanelPreferredHeight(double hostHeight, WorkbenchPanel panel)
    {
        var minimum = PanelMinimumHeight(panel);
        var maximum = PanelMaximumHeight(hostHeight, panel);
        if (!double.IsFinite(hostHeight) || hostHeight <= 0)
        {
            return minimum;
        }

        return Math.Clamp(hostHeight * PanelPreferredFraction, minimum, maximum);
    }
}
