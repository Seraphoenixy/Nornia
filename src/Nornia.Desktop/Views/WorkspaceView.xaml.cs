using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nornia.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    private WorkspaceViewModel? _viewModel;
    private VirtualizedScrollAnchorGuard? _anchorGuard;
    private ListBox? _rows;

    public WorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        if (DataContext is WorkspaceViewModel initial)
        {
            AttachViewModel(initial);
        }
    }

    // 行列表位于 SidebarShell.BodyContent 的内容属性元素内,在此嵌套作用域内无法直接
    // x:Name(MC3093 名称作用域冲突),故需要时沿视觉树定位唯一的行 ListBox。
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _rows ??= FindFirstListBox(this);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _rows = null;
        _anchorGuard?.Dispose();
        _anchorGuard = null;
    }

    private static ListBox? FindFirstListBox(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListBox list)
            {
                return list;
            }

            var nested = FindFirstListBox(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnRowsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 增量更新期间保持滚动锚点(可视首行),下一渲染帧恢复。
        // 对照 VS Code 树/列表刷新保持视口;旧整表 Reset 会丢失滚动位置并闪烁。
        _rows ??= FindFirstListBox(this);
        if (_rows is null)
        {
            return;
        }

        _anchorGuard ??= new VirtualizedScrollAnchorGuard();
        _anchorGuard.OnCollectionChanged(_rows);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.RevealRowRequested -= OnRevealRowRequested;
            _viewModel.WorkspaceTreeRows.CollectionChanged -= OnRowsCollectionChanged;
            _viewModel = null;
        }

        AttachViewModel(DataContext as WorkspaceViewModel);
    }

    private void AttachViewModel(WorkspaceViewModel? viewModel)
    {
        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.RevealRowRequested += OnRevealRowRequested;
            viewModel.WorkspaceTreeRows.CollectionChanged += OnRowsCollectionChanged;
        }
    }

    /// <summary>reveal 定位:把目标行滚入视野并显示在树视口垂直居中位置(点击跳转行为),再选中。</summary>
    private void OnRevealRowRequested(WorkspaceTreeRow row)
    {
        var rows = _rows;
        if (rows is not null && rows.Items.Contains(row))
        {
            rows.ScrollIntoView(row);
            CenterRevealedRow(rows, row);
        }
    }

    /// <summary>把已实现的目标行容器修正到树视口垂直居中(点击跳转:目标显示在屏幕中间)。
    /// 行列表用 Pixel 滚动单位(WorkspaceView.xaml,与 GitView 提交历史一致),偏移/extent
    /// 都是像素值,按目标行的绝对坐标直接精确居中;行高不统一的树也不受整数行滚动限制。
    /// 若列表未启用像素单位(防御),回退为项滚动的整行号居中(±半行误差)。</summary>
    internal static void CenterRevealedRow(ListBox rows, object row)
    {
        var index = rows.Items.IndexOf(row);
        if (index < 0 || FindScrollViewer(rows) is not ScrollViewer scroll)
        {
            return;
        }

        rows.UpdateLayout();
        if (rows.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement { ActualHeight: > 0 } container)
        {
            return;
        }

        if (VirtualizingPanel.GetScrollUnit(rows) != ScrollUnit.Pixel)
        {
            if (FindContentPanel(container) is not Panel { ActualHeight: > 0 } itemPanel)
            {
                return;
            }

            var visibleRows = itemPanel.ActualHeight / container.ActualHeight;
            var itemOffset = Math.Clamp(index - visibleRows / 2.0, 0, Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight));
            scroll.ScrollToVerticalOffset(Math.Round(itemOffset));
            return;
        }

        CenterPixel(rows, scroll, row);
    }

    /// <summary>Pixel 滚动单位下的像素居中:按目标行相对视口的位置 + 当前像素偏移得到其
    /// 绝对内容坐标,滚动到行的中心与视口中心线重合(文档不足视口时贴住边界)。</summary>
    private static void CenterPixel(ListBox rows, ScrollViewer scroll, object row)
    {
        rows.UpdateLayout();
        if (rows.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement { ActualHeight: > 0 } container
            || scroll.ViewportHeight <= 0)
        {
            return;
        }

        // 容器相对视口顶部的 Y + 当前像素偏移 = 目标行的绝对内容坐标
        // (以视口为参照,不受虚拟化缓冲行布局影响)。
        var yInViewport = container.TranslatePoint(new Point(0, 0), scroll).Y;
        var absolute = scroll.VerticalOffset + yInViewport;
        var target = Math.Clamp(
            absolute - (scroll.ViewportHeight - container.ActualHeight) / 2,
            0,
            Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight));
        scroll.ScrollToVerticalOffset(target);
    }

    /// <summary>容器视觉树上最近的 Panel 祖先(行列表内容面板,计算行绝对坐标的原点)。</summary>
    private static Panel? FindContentPanel(DependencyObject start)
    {
        for (var parent = VisualTreeHelper.GetParent(start);
            parent is not null;
            parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Panel panel)
            {
                return panel;
            }
        }

        return null;
    }

    /// <summary>行列表的内部滚动宿主(项滚动偏移 API 在 ScrollViewer 上,不在 ListBox 上)。</summary>
    private static ScrollViewer? FindScrollViewer(FrameworkElement root)
    {
        if (root is Control control && control.Template is not null
            && control.Template.FindName("PART_ScrollViewer", control) is ScrollViewer named)
        {
            return named;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (child is FrameworkElement element)
            {
                var nested = FindScrollViewer(element);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private void WorkspaceRows_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.AreTreeGuidesVisible = true;
        }
    }

    private void WorkspaceRows_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.AreTreeGuidesVisible = false;
        }
    }
}
