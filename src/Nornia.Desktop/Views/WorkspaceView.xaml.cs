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

    /// <summary>reveal 定位:把目标行滚入视野并选中。</summary>
    private void OnRevealRowRequested(WorkspaceTreeRow row)
    {
        var rows = _rows;
        if (rows is not null && rows.Items.Contains(row))
        {
            rows.ScrollIntoView(row);
        }
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
