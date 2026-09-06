using Nornia.Desktop.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nornia.Tests;

/// <summary>资源管理器 reveal 定位:目标行显示在树视口垂直居中位置(点击跳转行为)。
/// 使用真实虚拟化 ListBox(项滚动 CanContentScroll=True)+ 真实窗口布局,验证
/// ScrollIntoView(贴边)之后的像素级居中修正。</summary>
[Collection("WpfStaSequential")]
public sealed class WorkspaceRevealCenteringTests
{
    [Fact]
    public void CenterRevealedRow_PlacesTargetRowAtViewportCenter()
    {
        WpfStaContext.Run(() =>
        {
            const int totalRows = 200;
            var list = new ListBox { Width = 240, Height = 160 };
            for (var i = 1; i <= totalRows; i++)
            {
                list.Items.Add($"row-{i}");
            }
            ScrollViewer.SetCanContentScroll(list, true);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            // 与 WorkspaceView.xaml 的树列表一致:Pixel 滚动单位(像素精度偏移)。
            VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);

            var host = new Window { Content = list, Width = 260, Height = 200, ShowInTaskbar = false };
            host.Show();
            try
            {
                host.UpdateLayout();

                const int target = 150;
                var item = $"row-{target}";
                list.ScrollIntoView(item); // 只保证可见(贴边),不保证居中
                list.UpdateLayout();

                WorkspaceView.CenterRevealedRow(list, item);
                host.UpdateLayout();

                var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(item));
                var scroll = FindFirstScrollViewer(list);

                // Pixel 滚动单位:目标行相对视口顶部的 Y 应落在视口垂直中心(±2px)。
                var yInViewport = container.TranslatePoint(new Point(0, 0), scroll).Y;
                var expected = (scroll.ViewportHeight - container.ActualHeight) / 2;
                Assert.True(Math.Abs(yInViewport - expected) <= 2.0,
                    $"目标行未居中: 视口内 Y={yInViewport:F1}, 期望(视口中心)={expected:F1}, 偏移={scroll.VerticalOffset:F1}");
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static ScrollViewer FindFirstScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            var nested = FindFirstScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        throw new InvalidOperationException("未找到 ScrollViewer。");
    }
}
