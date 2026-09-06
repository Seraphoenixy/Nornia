using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;

namespace Nornia.Desktop.Views;

/// <summary>
/// 编辑器工作台根视图:渲染全局页面标签条 + 递归编辑器组网格(SplitNode → Grid + GridSplitter,
/// GroupNode → EditorGroupView)。分割比例写回 <see cref="EditorSplitNode.Weights"/> 由
/// EditorGroupsViewModel 钳制,视图重建由 LayoutChanged 事件驱动。
/// </summary>
public partial class EditorGroupsView : UserControl
{
    private const string WorkbenchTabDragFormat = "NorniaWorkbenchTabDrag";
    private EditorGroupsViewModel? _groups;
    private WorkbenchViewModel? _workbench;
    private readonly List<(EditorSplitNode Split, Grid Grid)> _splitGrids = [];
    private Point _editorTabDragStart;
    private WorkbenchTabViewModel? _dragTab;
    private WorkbenchTabViewModel? _contextTab;

    private const string SplitterColumn = "GroupSplitterStyle";

    /// <summary>供 <see cref="EditorGroupView"/> 解析的组网格所有者。</summary>
    internal EditorGroupsViewModel? GroupsForView => _groups;

    public EditorGroupsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => ReapplyPixelClamps();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is WorkbenchViewModel workbench)
        {
            _workbench = workbench;
            if (workbench.Editor?.Groups is { } groups && !ReferenceEquals(_groups, groups))
            {
                if (_groups is not null)
                {
                    _groups.LayoutChanged -= OnLayoutChanged;
                }

                _groups = groups;
                _groups.LayoutChanged += OnLayoutChanged;
                Rebuild();
            }
        }
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        if (_groups is null || GroupsHost is null)
        {
            return;
        }

        _splitGrids.Clear();
        GroupsHost.Child = BuildNode(_groups.Root);
        WireLocked2x2();
    }

    /// <summary>2×2 角落 Sash 联动 (VS Code GridView.trySet2x2):根分割的两个内部分割
    /// 共享同一分隔比例 —— 拖动其一,另一个在拖拽中实时镜像,结束时两边模型同时写回。</summary>
    private void WireLocked2x2()
    {
        if (_groups is null || !EditorGroupsViewModel.TryGetLocked2x2(_groups.Root, out var first, out var second))
        {
            return;
        }

        var gridA = _splitGrids.FirstOrDefault(item => ReferenceEquals(item.Split, first)).Grid;
        var gridB = _splitGrids.FirstOrDefault(item => ReferenceEquals(item.Split, second)).Grid;
        var splitterA = gridA?.Children.OfType<GridSplitter>().FirstOrDefault();
        var splitterB = gridB?.Children.OfType<GridSplitter>().FirstOrDefault();
        if (gridA is null || gridB is null || splitterA is null || splitterB is null)
        {
            return;
        }

        bool vertical = first.Orientation == EditorSplitOrientation.Vertical;
        var capturedGridA = gridA;
        var capturedGridB = gridB;
        var capturedSplitB = second;

        splitterA.DragDelta += (_, _) => MirrorRatio(capturedGridA, capturedGridB, vertical);
        splitterA.DragCompleted += (_, _) =>
        {
            WriteWeightsFromGrid(first, capturedGridA, vertical);
            WriteMirroredModel(capturedGridA, capturedSplitB, vertical);
            MirrorRatio(capturedGridA, capturedGridB, vertical);
        };
        splitterB.DragDelta += (_, _) => MirrorRatio(capturedGridB, capturedGridA, vertical);
        splitterB.DragCompleted += (_, _) =>
        {
            WriteWeightsFromGrid(second, capturedGridB, vertical);
            WriteMirroredModel(capturedGridB, first, vertical);
            MirrorRatio(capturedGridB, capturedGridA, vertical);
        };
    }

    /// <summary>把来源网格的当前分隔比例镜像到目标网格(纯视觉,拖拽中实时)。</summary>
    private void MirrorRatio(Grid source, Grid target, bool vertical)
    {
        var sizeA = vertical ? Star(source, 0, vertical) : RowStar(source, 0);
        var sizeB = vertical ? Star(source, 2, vertical) : RowStar(source, 2);
        if (sizeA is null || sizeB is null || sizeA.Value + sizeB.Value <= 0)
        {
            return;
        }

        var total = sizeA.Value + sizeB.Value;
        if (total <= 0 || !double.IsFinite(total))
        {
            return;
        }

        var ratio = sizeA.Value / total;
        ReapplyStarSizes(target, [ratio, 1.0 - ratio], vertical);
    }

    /// <summary>镜像目标的模型比例(拖拽结束时写回,与来源同占比)。</summary>
    private void WriteMirroredModel(Grid source, EditorSplitNode mirroredSplit, bool vertical)
    {
        if (_groups is null)
        {
            return;
        }

        var sizeA = vertical ? Star(source, 0, vertical) : RowStar(source, 0);
        var sizeB = vertical ? Star(source, 2, vertical) : RowStar(source, 2);
        if (sizeA is null || sizeB is null || sizeA.Value + sizeB.Value <= 0)
        {
            return;
        }

        var ratio = sizeA.Value / (sizeA.Value + sizeB.Value);
        var clamped = EditorGroupsViewModel.ClampWeights([ratio, 1.0 - ratio]);
        _groups.SetSplitWeights(mirroredSplit, clamped);
    }

    private static double? Star(Grid grid, int index, bool vertical)
    {
        if (grid.ColumnDefinitions.Count <= index)
        {
            return null;
        }

        return grid.ColumnDefinitions[index].Width.Value;
    }

    private static double? RowStar(Grid grid, int index)
    {
        if (grid.RowDefinitions.Count <= index)
        {
            return null;
        }

        return grid.RowDefinitions[index].Height.Value;
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

    private FrameworkElement BuildNode(EditorLayoutNode node) => node switch
    {
        EditorGroupNode group => BuildGroupView(group.Group),
        EditorSplitNode split => BuildSplit(split),
        _ => BuildGroupView(_groups!.ActiveGroup),
    };

    private EditorGroupView BuildGroupView(EditorGroupViewModel group)
    {
        var view = new EditorGroupView { DataContext = group };
        // 编辑器组最小宽高:组拆分后即使窗口变窄也不至于坍缩为不可用窄条。
        var minimum = MinimumGroupSize();
        view.MinWidth = minimum;
        view.MinHeight = MinimumGroupHeight();
        view.GroupActivated += View_GroupActivated;
        return view;
    }

    private void View_GroupActivated(object? sender, EditorGroupViewModel group)
    {
        if (_groups is not null && !ReferenceEquals(_groups.ActiveGroup, group))
        {
            _groups.ActiveGroup = group;
        }
    }

    // ===== 顶层文件标签:统一选择、重排、中键关闭、双击转正 =====

    private void EditorTabList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _editorTabDragStart = e.GetPosition(EditorTabList);
        var source = e.OriginalSource as DependencyObject;
        _dragTab = FindAncestor<Button>(source) is not null
            ? null
            : FindAncestor<ListBoxItem>(source)?.DataContext as WorkbenchTabViewModel;

        // 惰性恢复:点中"已选中"的恢复标签不会触发选中变化链,在此直接触发首次加载;
        // 点其它标签时与选中链路的加载重复无碍(LoadAsync 幂等)。按钮(关闭/固定)上不加载。
        if (_dragTab is EditorWorkbenchTab { EditorTab: FilePreviewTab { IsLoadFinished: false } preview })
        {
            _ = preview.LoadAsync();
        }
    }

    private void EditorTabList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _workbench is null)
        {
            return;
        }

        var current = e.GetPosition(EditorTabList);
        if (Math.Abs(current.X - _editorTabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _editorTabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var tab = _dragTab;
        if (tab is null)
        {
            return;
        }

        _workbench.SelectedTab = tab;
        try
        {
            var data = new DataObject(WorkbenchTabDragFormat, tab.TabKey);
            if (tab is EditorWorkbenchTab editorTab
                && _workbench.Groups.FindGroupContaining(editorTab.EditorTab) is { } owner)
            {
                // Keep the editor payload as a second format so the same drag can still be
                // dropped onto an editor group to move/split the file tab.
                data.SetData(EditorTabDrag.Format,
                    new EditorTabDrag.Payload(owner.GroupId, editorTab.EditorTab.TabKey));
            }

            DragDrop.DoDragDrop(EditorTabList, data, DragDropEffects.Move);
        }
        finally
        {
            _dragTab = null;
        }
    }

    private void EditorTabList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(WorkbenchTabDragFormat)
            || e.Data.GetDataPresent(EditorTabDrag.Format)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void EditorTabList_Drop(object sender, DragEventArgs e)
    {
        if (_workbench is null)
        {
            return;
        }

        var source = e.Data.GetData(WorkbenchTabDragFormat) is string tabKey
            ? _workbench.Tabs.FirstOrDefault(tab => string.Equals(tab.TabKey, tabKey, StringComparison.Ordinal))
            : e.Data.GetData(EditorTabDrag.Format) is EditorTabDrag.Payload editorPayload
                ? _workbench.Tabs.OfType<EditorWorkbenchTab>().FirstOrDefault(tab =>
                    string.Equals(tab.EditorTab.TabKey, editorPayload.TabKey, StringComparison.Ordinal))
                : null;
        if (source is null)
        {
            e.Handled = true;
            return;
        }

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var target = container?.DataContext as WorkbenchTabViewModel;
        var targetIndex = target is not null
            ? _workbench.Tabs.IndexOf(target)
            : _workbench.Tabs.Count - 1;
        var sourceIndex = _workbench.Tabs.IndexOf(source);
        if (sourceIndex >= 0 && targetIndex >= 0)
        {
            _workbench.MoveTabCommand.Execute(new MoveTabArgs(sourceIndex, targetIndex));
        }

        e.Handled = true;
    }

    /// <summary>双击:未转正(预览/斜体)的文件标签转正为常驻标签;已转正标签不受影响。</summary>
    private void EditorTabList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_workbench is null)
        {
            return;
        }

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is EditorWorkbenchTab tab && tab.IsPreview)
        {
            tab.EditorTab.IsPreview = false;
        }
    }

    private void EditorTabList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || e.ButtonState != MouseButtonState.Pressed
            || _workbench is null)
        {
            return;
        }

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is WorkbenchTabViewModel tab)
        {
            _workbench.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    /// <summary>从按钮自身的数据上下文关闭标签,不依赖 ListBox 的当前选中项。</summary>
    private void TabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workbench is not null
            && (sender as FrameworkElement)?.DataContext is WorkbenchTabViewModel tab)
        {
            _workbench.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    /// <summary>右键菜单始终以鼠标下的标签为目标,而不是以菜单打开时的 SelectedItem 为目标。</summary>
    private void EditorTabList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not WorkbenchTabViewModel tab)
        {
            _contextTab = null;
            return;
        }

        _contextTab = tab;
        EditorTabList.SelectedItem = tab;
        e.Handled = true;
    }

    private void EditorTabList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox list || list.ContextMenu is not { } menu)
        {
            return;
        }

        // Keyboard/context-key invocation has no preceding mouse target, so fall back to the
        // selected tab. A mouse invocation has already captured the exact item above.
        menu.Tag = _contextTab ?? list.SelectedItem as WorkbenchTabViewModel;
    }

    private void EditorTabList_ContextMenuClosing(object sender, ContextMenuEventArgs e)
    {
        _contextTab = null;
        if (sender is ListBox list && list.ContextMenu is { } menu)
        {
            menu.Tag = null;
        }
    }

    private FrameworkElement BuildSplit(EditorSplitNode split)
    {
        bool vertical = split.Orientation == EditorSplitOrientation.Vertical;
        var count = split.Children.Count;
        var grid = new Grid();
        var weights = split.Weights is { Count: > 0 } raw && raw.Count == count
            ? raw
            : EditorGroupsViewModel.SplitWeights(count);
        weights = EditorGroupsViewModel.ClampWeights(weights);

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                var splitter = new GridSplitter
                {
                    Style = (Style)FindResource(vertical ? "VerticalSashStyle" : "HorizontalSashStyle"),
                    ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                if (vertical)
                {
                    splitter.Width = SplitterHitArea();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(splitter.Width) });
                }
                else
                {
                    splitter.Height = SplitterHitArea();
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(splitter.Height) });
                }

                var capturedSplit = split;
                splitter.DragCompleted += (_, _) => WriteWeightsFromGrid(capturedSplit, grid, vertical);
                grid.Children.Add(splitter);
                Grid.SetColumn(splitter, i * 2 - 1);
                Grid.SetRow(splitter, i * 2 - 1);
            }

            var child = BuildNode(split.Children[i]);
            if (vertical)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weights[i], GridUnitType.Star) });
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(weights[i], GridUnitType.Star) });
            }

            grid.Children.Add(child);
            Grid.SetColumn(child, i * 2);
            Grid.SetRow(child, i * 2);
        }

        _splitGrids.Add((split, grid));
        return grid;
    }

    private void WriteWeightsFromGrid(EditorSplitNode split, Grid grid, bool vertical)
    {
        if (_groups is null)
        {
            return;
        }

        var count = split.Children.Count;
        var raw = new double[count];
        for (var i = 0; i < count; i++)
        {
            raw[i] = vertical
                ? grid.ColumnDefinitions[i * 2].Width.Value
                : grid.RowDefinitions[i * 2].Height.Value;
        }

        // 先按当前可用尺寸做像素感知钳制(拖拽后模型也不得低于单组最小宽高),再归一化钳制到 20%–80%。
        var available = AvailableSplitPixels(split, grid, vertical);
        var weights = available > 0
            ? EditorGroupsViewModel.ClampWeightsToPixels(raw, available, MinimumGroupSize())
            : EditorGroupsViewModel.ClampWeights(raw);
        _groups.SetSplitWeights(split, weights);
        ReapplyStarSizes(grid, weights, vertical);
    }

    private static void ReapplyStarSizes(Grid grid, IReadOnlyList<double> weights, bool vertical)
    {
        for (var i = 0; i < weights.Count; i++)
        {
            if (vertical)
            {
                var definition = grid.ColumnDefinitions[i * 2];
                if (definition.Width.IsStar)
                {
                    definition.Width = new GridLength(weights[i], GridUnitType.Star);
                }
            }
            else
            {
                var definition = grid.RowDefinitions[i * 2];
                if (definition.Height.IsStar)
                {
                    definition.Height = new GridLength(weights[i], GridUnitType.Star);
                }
            }
        }
    }

    private void ReapplyPixelClamps()
    {
        if (_groups is null || GroupsHost.ActualWidth <= 0)
        {
            return;
        }

        var minimum = MinimumGroupSize();
        foreach (var (split, grid) in _splitGrids)
        {
            var vertical = split.Orientation == EditorSplitOrientation.Vertical;
            var available = AvailableSplitPixels(split, grid, vertical);
            if (available <= 0)
            {
                continue;
            }

            var weights = EditorGroupsViewModel.ClampWeightsToPixels(split.Weights, available, minimum);
            // Only re-apply when the current layout actually violates the minimum (window shrank).
            var current = split.Weights;
            var needsFix = false;
            for (var i = 0; i < current.Count; i++)
            {
                if (current[i] * available < minimum - 0.5)
                {
                    needsFix = true;
                    break;
                }
            }

            if (needsFix)
            {
                _groups.SetSplitWeights(split, weights);
                ReapplyStarSizes(grid, weights, vertical);
            }
        }
    }

    private double AvailableSplitPixels(EditorSplitNode split, Grid grid, bool vertical)
    {
        var total = vertical ? grid.ActualWidth : grid.ActualHeight;
        return Math.Max(0, total - Math.Max(0, split.Children.Count - 1) * SplitterHitArea());
    }

    private double SplitterHitArea()
    {
        var value = TryFindResource("SizeSashHitArea") as double? ?? WorkbenchLayoutMetrics.SashHitArea;
        return value;
    }

    private double MinimumGroupSize()
    {
        var value = TryFindResource("MinEditorGroupWidth") as double? ?? WorkbenchLayoutMetrics.EditorGroupMinimumWidth;
        return value;
    }

    private double MinimumGroupHeight()
    {
        var value = TryFindResource("MinEditorGroupHeight") as double? ?? WorkbenchLayoutMetrics.EditorGroupMinimumHeight;
        return value;
    }
}
