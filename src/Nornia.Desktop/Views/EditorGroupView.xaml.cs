using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nornia.Desktop.ViewModels;

namespace Nornia.Desktop.Views;

/// <summary>拖拽负载格式与命中区枚举(组内标签重排 / 跨组移动 / 边缘拆分)。</summary>
public static class EditorTabDrag
{
    public const string Format = "NorniaEditorTabDrag";

    public sealed record Payload(string SourceGroupId, string TabKey);

    public enum Zone
    {
        None,
        Center,
        Left,
        Right,
        Top,
        Bottom,
    }
}

/// <summary>
/// 一个编辑器组视图:内容区 + 拆分拖拽提示层。文件标签统一由 EditorGroupsView 渲染；
/// 拖到其它组中央 → 移动标签,拖到上下左右边缘 → 拆分目标组并移入标签。
/// </summary>
public partial class EditorGroupView : UserControl
{
    private EditorTabDrag.Zone _activeZone = EditorTabDrag.Zone.None;

    public EditorGroupView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user activates this group (clicking a tab / background) —
    /// EditorGroupsView switches the active group.</summary>
    public event EventHandler<EditorGroupViewModel>? GroupActivated;

    private EditorGroupViewModel? ViewModel => DataContext as EditorGroupViewModel;

    private EditorGroupsViewModel? GroupsViewModel => FindAncestor<EditorGroupsView>(this)?.GroupsForView;

    // ===== 组激活:点击标签/背景 → 该组成为活动组 =====

    private void GroupRoot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { } group)
        {
            GroupActivated?.Invoke(this, group);
        }
    }

    // ===== 组级拖拽:中央移动 / 边缘拆分 =====

    private void GroupRoot_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled || !e.Data.GetDataPresent(EditorTabDrag.Format) || ViewModel is null)
        {
            return;
        }

        var zone = ComputeZone(e.GetPosition(this), ActualWidth, ActualHeight);
        ShowDropOverlay(zone);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void GroupRoot_DragLeave(object sender, DragEventArgs e) => HideDropOverlay();

    private void GroupRoot_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled || e.Data.GetData(EditorTabDrag.Format) is not EditorTabDrag.Payload payload)
        {
            return;
        }

        var zone = _activeZone;
        HideDropOverlay();
        if (ViewModel is not { } group || GroupsViewModel is not { } groups)
        {
            return;
        }

        var tab = groups.FindTabByKey(payload.TabKey)?.Tab;
        if (tab is null)
        {
            e.Handled = true;
            return;
        }

        switch (zone)
        {
            case EditorTabDrag.Zone.Center:
                groups.MoveTabToGroup(tab, group);
                break;
            case EditorTabDrag.Zone.Left:
                groups.MoveTabToNewGroup(tab, group, EditorSplitOrientation.Vertical, newGroupFirst: true);
                break;
            case EditorTabDrag.Zone.Right:
                groups.MoveTabToNewGroup(tab, group, EditorSplitOrientation.Vertical, newGroupFirst: false);
                break;
            case EditorTabDrag.Zone.Top:
                groups.MoveTabToNewGroup(tab, group, EditorSplitOrientation.Horizontal, newGroupFirst: true);
                break;
            case EditorTabDrag.Zone.Bottom:
                groups.MoveTabToNewGroup(tab, group, EditorSplitOrientation.Horizontal, newGroupFirst: false);
                break;
        }

        e.Handled = true;
    }

    /// <summary>命中区几何对齐 VS Code editorDropTarget 默认(openSideBySideDirection: 'right'
    /// → 优先左右拆分):中心 80%×80% 为合并区;外缘区内外侧 1/3 为左右拆分,
    /// 中间 1/3 列上下两半为上下拆分。</summary>
    private static EditorTabDrag.Zone ComputeZone(Point position, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return EditorTabDrag.Zone.Center;
        }

        var x = position.X / width;
        var y = position.Y / height;
        const double edge = 0.10;

        if (x > edge && x < 1 - edge && y > edge && y < 1 - edge)
        {
            return EditorTabDrag.Zone.Center;
        }

        if (x < 1.0 / 3.0)
        {
            return EditorTabDrag.Zone.Left;
        }

        if (x > 2.0 / 3.0)
        {
            return EditorTabDrag.Zone.Right;
        }

        return y < 0.5 ? EditorTabDrag.Zone.Top : EditorTabDrag.Zone.Bottom;
    }

    private void ShowDropOverlay(EditorTabDrag.Zone zone)
    {
        _activeZone = zone;
        DropOverlay.Visibility = Visibility.Visible;
        DropZoneCenter.Visibility = zone == EditorTabDrag.Zone.Center ? Visibility.Visible : Visibility.Collapsed;
        DropZoneLeft.Visibility = zone == EditorTabDrag.Zone.Left ? Visibility.Visible : Visibility.Collapsed;
        DropZoneRight.Visibility = zone == EditorTabDrag.Zone.Right ? Visibility.Visible : Visibility.Collapsed;
        DropZoneTop.Visibility = zone == EditorTabDrag.Zone.Top ? Visibility.Visible : Visibility.Collapsed;
        DropZoneBottom.Visibility = zone == EditorTabDrag.Zone.Bottom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideDropOverlay()
    {
        _activeZone = EditorTabDrag.Zone.None;
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current switch
            {
                FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
                ContentElement content => ContentOperations.GetParent(content),
                Visual or System.Windows.Media.Media3D.Visual3D =>
                    VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current),
            };
        }

        return null;
    }

}
