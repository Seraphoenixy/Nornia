using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;

namespace Nornia.Desktop.Views;

public partial class SearchSidebarView : UserControl
{
    private SearchViewModel? _attachedViewModel;
    private VirtualizedScrollAnchorGuard? _anchorGuard;
    private ListBox? _rows;

    public SearchSidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Attach(DataContext as SearchViewModel);
        Unloaded += (_, _) =>
        {
            Detach(DataContext as SearchViewModel);
            _anchorGuard?.Dispose();
            _anchorGuard = null;
            _rows = null;
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach(e.OldValue as SearchViewModel);
        Attach(e.NewValue as SearchViewModel);
    }

    private void Attach(SearchViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(viewModel, _attachedViewModel)) return;
        Detach(_attachedViewModel);
        _attachedViewModel = viewModel;
        viewModel.FocusRequested += OnFocusRequested;
        viewModel.Rows.CollectionChanged += OnRowsCollectionChanged;
    }

    private void Detach(SearchViewModel? viewModel)
    {
        if (viewModel is null || !ReferenceEquals(viewModel, _attachedViewModel)) return;
        viewModel.FocusRequested -= OnFocusRequested;
        viewModel.Rows.CollectionChanged -= OnRowsCollectionChanged;
        _attachedViewModel = null;
    }

    /// <summary>流式结果增量追加期间保持滚动锚点(可视首行),避免每批 16 个文件的
    /// 更新把列表滚回顶部(旧整表 Clear+重填行为)。订阅的是 VM 的 Rows 集合,
    /// 列表本身在嵌套作用域内,按视觉树定位。</summary>
    private void OnRowsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _rows ??= FindVisualChild<ListBox>(this);
        if (_rows is null)
        {
            return;
        }

        _anchorGuard ??= new VirtualizedScrollAnchorGuard();
        _anchorGuard.OnCollectionChanged(_rows);
    }

    private void OnFocusRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var searchBox = FindVisualChild<TextBox>(this);
            if (searchBox is null) return;
            searchBox.Focus();
            searchBox.SelectAll();
        });
    }

    /// <summary>Handles search-result activation at the ListBox boundary. InputBindings attached
    /// inside a virtualized DataTemplate are not reliable after the row is recycled, especially
    /// when the click lands on the nested file badge or preview segment. Click count preserves the
    /// VS Code behavior: a single click opens a preview and a double click promotes it.</summary>
    private void SearchRows_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list
            || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(list, source) is not ListBoxItem { DataContext: SearchTreeNode node }
            || !node.IsMatch
            || DataContext is not SearchViewModel viewModel)
        {
            return;
        }

        var command = e.ClickCount > 1
            ? viewModel.OpenMatchPermanentCommand
            : viewModel.OpenMatchCommand;
        if (command.CanExecute(node))
        {
            command.Execute(node);
            e.Handled = true;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }

        return null;
    }
}
