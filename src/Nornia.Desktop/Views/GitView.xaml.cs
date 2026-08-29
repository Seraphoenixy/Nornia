using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using Nornia.Desktop.ViewModels;

namespace Nornia.Desktop.Views;

public partial class GitView : UserControl
{
    /// <summary>拖放暂存的数据格式:负载为仓库相对路径数组(string[])。</summary>
    private const string ChangeDragFormat = "NorniaGitChangePaths";
    /// <summary>拖放源更改分区(staged / unstaged)。目标分区据此忽略原地放置，避免一次普通
    /// 点击或行内操作被误识别为跨分区的暂存/取消暂存。</summary>
    private const string ChangeDragSourceSectionFormat = "NorniaGitChangeSourceSection";

    /// <summary>树形布局的层级导线只在鼠标移入树内时绘制(仿 VS Code indent guides 悬停行为)。
    /// 行模板通过 RelativeSource 绑定本 DP,由树 ListBox 的 MouseEnter/MouseLeave 置位。</summary>
    public static readonly DependencyProperty AreTreeGuidesVisibleProperty =
        DependencyProperty.Register(
            nameof(AreTreeGuidesVisible),
            typeof(bool),
            typeof(GitView),
            new PropertyMetadata(false));

    public bool AreTreeGuidesVisible
    {
        get => (bool)GetValue(AreTreeGuidesVisibleProperty);
        set => SetValue(AreTreeGuidesVisibleProperty, value);
    }

    private void TreeList_MouseEnter(object sender, MouseEventArgs e) => AreTreeGuidesVisible = true;

    private void TreeList_MouseLeave(object sender, MouseEventArgs e) => AreTreeGuidesVisible = false;

    private GridLength _changesExpandedHeight = new(1, GridUnitType.Star);
    private GridLength _graphExpandedHeight = new(1, GridUnitType.Star);
    private GitViewModel? _layoutViewModel;
    private GitChangeItem? _flatChangeReopenCandidate;
    private ListBox? _flatChangeReopenList;
    // ContextMenu 在点击其 PlacementTarget 时会先自动关闭、随后才触发 Button.Click。
    // 记录这种关闭，避免 Click 处理器立即把同一个菜单重新打开。
    private readonly HashSet<Button> _suppressMenuOpenButtons = [];

    public GitView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += async (_, _) =>
        {
            if (_layoutViewModel is not null)
            {
                try
                {
                    await _layoutViewModel.RestoreScmPaneHeightsAsync();
                }
                catch (Exception)
                {
                    // 分隔高度恢复是尽力而为(与 RestoreScmLayoutAsync 同约定):状态存储的
                    // 瞬时读取失败不得经 Task 续体变成"未处理的界面异常",用默认分割即可。
                }

                ApplyRestoredHeights();
            }

            UpdatePanelLayout();
        };
    }

    /// <summary>Applies the persisted changes/graph pane split (pixels) before the layout pass.</summary>
    private void ApplyRestoredHeights()
    {
        if (_layoutViewModel is null)
        {
            return;
        }

        var changesHeight = _layoutViewModel.RestoredScmChangesHeight;
        var graphHeight = _layoutViewModel.RestoredScmGraphHeight;
        if (changesHeight is > 0 && graphHeight is > 0)
        {
            _changesExpandedHeight = new GridLength(changesHeight.Value, GridUnitType.Pixel);
            _graphExpandedHeight = new GridLength(graphHeight.Value, GridUnitType.Pixel);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_layoutViewModel is not null)
        {
            _layoutViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _layoutViewModel = e.NewValue as GitViewModel;
        if (_layoutViewModel is not null)
        {
            _layoutViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdatePanelLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GitViewModel.IsChangesViewExpanded) or nameof(GitViewModel.IsGraphViewExpanded))
        {
            // Section headers change the row that contains both the splitter and the next header.
            // Deferring this update leaves the old splitter/hit-test rectangle in place for one
            // input turn, so the next expand/collapse click is swallowed and users need to click
            // twice. Apply it in the same UI turn whenever possible.
            if (Dispatcher.CheckAccess())
            {
                UpdatePanelLayout();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(UpdatePanelLayout, System.Windows.Threading.DispatcherPriority.DataBind);
            }
        }
    }

    /// <summary>Keeps the two VS Code-style view containers independent: a collapsed container
    /// takes only its header and the remaining expanded container owns the available sidebar space.</summary>
    private void UpdatePanelLayout()
    {
        if (!IsLoaded || _layoutViewModel is null)
        {
            return;
        }

        // 异步 Loaded 续体可能在容器已离开视觉树之后恢复(视图被卸载、模板正在重建),
        // 视觉搜索此时找不到容器:静默跳过本次,后续加载/分区展开事件会重试布局。
        // 此前属性 getter 在此处抛出 InvalidOperationException,经 Task 续体变成
        // "未处理的界面异常" 冒泡到 DispatcherUnhandledException。
        if (ScmViewsGrid is not { } grid
            || FindVisualChild<GridSplitter>(this, element => Equals(element.Tag, "ScmPanelSplitter")) is not { } splitter)
        {
            return;
        }

        var changesRow = grid.RowDefinitions[0];
        var splitterRow = grid.RowDefinitions[1];
        var graphRow = grid.RowDefinitions[2];
        var changesExpanded = _layoutViewModel.IsChangesViewExpanded;
        var graphExpanded = _layoutViewModel.IsGraphViewExpanded;
        if (changesExpanded && graphExpanded)
        {
            changesRow.Height = _changesExpandedHeight;
            graphRow.Height = _graphExpandedHeight;
            splitterRow.Height = new GridLength(5);
            splitter.Visibility = Visibility.Visible;
        }
        else if (changesExpanded)
        {
            changesRow.Height = new GridLength(1, GridUnitType.Star);
            graphRow.Height = GridLength.Auto;
            splitterRow.Height = new GridLength(0);
            splitter.Visibility = Visibility.Collapsed;
        }
        else if (graphExpanded)
        {
            changesRow.Height = GridLength.Auto;
            graphRow.Height = new GridLength(1, GridUnitType.Star);
            splitterRow.Height = new GridLength(0);
            splitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            changesRow.Height = GridLength.Auto;
            graphRow.Height = GridLength.Auto;
            splitterRow.Height = new GridLength(0);
            splitter.Visibility = Visibility.Collapsed;
        }

        // Make the revised row geometry visible to WPF hit testing before the next pointer event.
        grid.InvalidateMeasure();
    }

    /// <summary>按 Tag 的视觉树搜索:容器不在视觉树中时(异步续体/模板重建时序)返回 null,
    /// 调用方必须判空跳过,不得抛出。</summary>
    private Grid? ScmViewsGrid => FindVisualChild<Grid>(this, element => Equals(element.Tag, "ScmViewsGrid"));

    private RowDefinition? ChangesPanelRow => ScmViewsGrid?.RowDefinitions[0];
    private RowDefinition? GraphPanelRow => ScmViewsGrid?.RowDefinitions[2];

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match)) return match;
            if (FindVisualChild(child, predicate) is { } nested) return nested;
        }

        return null;
    }

    private void ScmPanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ChangesPanelRow is { ActualHeight: > 0 } changes && GraphPanelRow is { ActualHeight: > 0 } graph)
        {
            _changesExpandedHeight = new GridLength(changes.ActualHeight, GridUnitType.Pixel);
            _graphExpandedHeight = new GridLength(graph.ActualHeight, GridUnitType.Pixel);
            _layoutViewModel?.RecordScmPaneHeights(changes.ActualHeight, graph.ActualHeight);
        }
    }

    /// <summary>Opens the "more actions" menu on left click (pure view behavior; the menu entries
    /// bind through the button's DataContext to the GitViewModel commands).</summary>
    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            ToggleButtonContextMenu(button, menu);
        }
    }

    /// <summary>Opens the branch switcher attached to the current branch name.</summary>
    private void CurrentBranchSelector_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            ToggleButtonContextMenu(button, menu);
            e.Handled = true;
        }
    }

    private void ToggleButtonContextMenu(Button button, ContextMenu menu)
    {
        if (_suppressMenuOpenButtons.Remove(button))
        {
            return;
        }

        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.Closed -= ButtonContextMenu_Closed;
        menu.Closed += ButtonContextMenu_Closed;
        menu.IsOpen = true;
    }

    private void ButtonContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: Button button }
            && button.IsMouseOver
            && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _suppressMenuOpenButtons.Add(button);
        }
    }

    private void FolderRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // The folder row owns the normal click for expand/collapse. A button inside the row owns
        // its click, so do not collapse the folder after a batch action was invoked.
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: ScmFolderNode folder }
            && ViewModel?.ToggleFolderCommand.CanExecute(folder) == true)
        {
            ViewModel.ToggleFolderCommand.Execute(folder);
            e.Handled = true;
        }
    }

    private static bool IsInsideButton(DependencyObject source)
        => FindAncestor<Button>(source) is not null;
    /// <summary>Commit-row expansion is handled on mouse-up instead of a <see cref="MouseBinding"/>.
    /// ListBoxItem handles the first left-button-down to establish selection; MouseBinding can then
    /// miss that first gesture after the row is retemplated by an expand/collapse. The full-width
    /// header Border's bubbling mouse-up is stable across template and row-height changes.</summary>
    private void CommitBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitLogRow row }
            && ViewModel?.ToggleLogRowCommand.CanExecute(row) == true)
        {
            ViewModel.ToggleLogRowCommand.Execute(row);
            e.Handled = true;
        }
    }

    /// <summary>
    /// ListBox 的 SelectedItem 只在选择发生变化时通知 VM；已选中平铺行再次单击不会重开
    /// 已被其他预览替换/关闭的 diff。按下时记录“原本已选中”的行，释放仍命中同一行时补发。
    /// </summary>
    private void FlatChangeList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _flatChangeReopenCandidate = null;
        _flatChangeReopenList = null;
        if (sender is not ListBox list
            || Keyboard.Modifiers != ModifierKeys.None
            || e.OriginalSource is not DependencyObject source
            || IsInsideButton(source)
            || FindAncestor<ListBoxItem>(source) is not { IsSelected: true, DataContext: GitChangeItem item } container
            || ItemsControl.ItemsControlFromItemContainer(container) != list
            || !ReferenceEquals(list.SelectedItem, item))
        {
            return;
        }

        _flatChangeReopenCandidate = item;
        _flatChangeReopenList = list;
    }

    private void FlatChangeList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var candidate = _flatChangeReopenCandidate;
        var candidateList = _flatChangeReopenList;
        _flatChangeReopenCandidate = null;
        _flatChangeReopenList = null;
        if (candidate is null
            || sender is not ListBox list
            || list != candidateList
            || e.OriginalSource is not DependencyObject source
            || FindAncestor<ListBoxItem>(source)?.DataContext is not GitChangeItem releasedItem
            || !ReferenceEquals(candidate, releasedItem)
            || ViewModel?.OpenChangeDiffCommand.CanExecute(candidate) != true)
        {
            return;
        }

        ViewModel.OpenChangeDiffCommand.Execute(candidate);
    }

    // ===== 拖放暂存:未暂存 ↔ 已暂存分区之间拖动文件行(VS Code SCM) =====

    /// <summary>按下并移动选中的更改行时发起拖动;平铺与树状列表的选择统一收集为路径。</summary>
    private void ChangeList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListBox { SelectedItems.Count: > 0 } list)
        {
            return;
        }

        // 行内按钮的 Click 在 MouseUp 才执行。若这里把按钮上的微小鼠标移动解释为拖放，
        // Drop 会抢先执行错误的跨分区命令，导致“丢弃”被误报为“取消暂存拖入”。
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
        {
            return;
        }

        var paths = list.SelectedItems.OfType<GitChangeItem>().Select(item => item.Path)
            .Concat(list.SelectedItems.OfType<ScmFileNode>().Select(node => node.Change.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var data = new DataObject(ChangeDragFormat, paths);
        data.SetData(ChangeDragSourceSectionFormat, list.Tag as string ?? string.Empty);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Move);
    }

    /// <summary>只有携带更改路径的拖动才允许放置到任一更改分区。</summary>
    private void ChangeSection_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ChangeDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void StagedSection_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.Data.GetData(ChangeDragFormat) is string[] paths && !IsSourceSection(e, "staged"))
        {
            _ = viewModel.StageDroppedChangesCommand.ExecuteAsync(paths);
        }

        e.Handled = true;
    }

    private void UnstagedSection_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.Data.GetData(ChangeDragFormat) is string[] paths && !IsSourceSection(e, "unstaged"))
        {
            _ = viewModel.UnstageDroppedChangesCommand.ExecuteAsync(paths);
        }

        e.Handled = true;
    }

    private static bool IsSourceSection(DragEventArgs e, string section) =>
        string.Equals(e.Data.GetData(ChangeDragSourceSectionFormat) as string, section, StringComparison.Ordinal);

    // ===== 更改分区:嵌套滚动链(列表内部 ScrollViewer → 外层分区 ScrollViewer) =====

    /// <summary>更改分区是嵌套滚动容器:各列表以 MaxHeight=外层视口高 限定自身视口,内容更长时
    /// 在列表自带 ScrollViewer 内滚动。此时鼠标滚轮被内部 ScrollViewer 消费,外层滚动条
    /// (整个更改分区)对滚轮无响应。这里用 PreviewMouseWheel(隧道路径,先于内部 ScrollViewer
    /// 的滚轮处理)拦截:内层列表在滚轮方向已到头/尾(无法再滚)时,把滚轮量转发给外层
    /// ScrollViewer 并标记已处理——模拟 VS Code 单一滚动面;内层还能滚时事件照旧冒泡,
    /// 列表内滚动行为不变。指针不在列表上(分区头/提交栏/空区)时视觉树上没有 ListBox 祖先,
    /// 直接放行,外层 ScrollViewer 自身的滚轮处理自然接管。</summary>
    private void ChangesSection_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer outer)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindAncestor<ListBox>(source) is not { } list
            || FindVisualChild<ScrollViewer>(list, _ => true) is not { } inner)
        {
            return;
        }

        var atTop = inner.VerticalOffset <= 0.5;
        var atBottom = inner.VerticalOffset + inner.ViewportHeight >= inner.ExtentHeight - 0.5;
        if (e.Delta > 0 ? !atTop : !atBottom)
        {
            return; // 内层列表在该方向还能滚:交给内部 ScrollViewer。
        }

        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// 祖先查找同时支持 Visual 与 FrameworkContentElement。TextBlock 使用内联 Run 后，
    /// 鼠标事件的 OriginalSource 可能是 Run；它不是 Visual，不能直接传给 VisualTreeHelper。
    /// </summary>
    internal static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = node switch
            {
                FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
                ContentElement content => ContentOperations.GetParent(content),
                Visual or System.Windows.Media.Media3D.Visual3D =>
                    VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node),
                _ => LogicalTreeHelper.GetParent(node),
            };
        }

        return null;
    }

    private GitViewModel? ViewModel => DataContext as GitViewModel;
}

/// <summary>分区行高转换器 —— 分区"展开且有条目"时行高为 * (列表获得有界视口 →
/// 真虚拟化),否则收拢为 Auto(折叠/空分区只占头部高度,不留下大片空白)。
/// 现仅用于图表视图的提交列表行;更改视图的分区改为 Auto 堆叠 + 外层滚动区
/// (见 GitView.xaml 的 ChangesSectionsScroll),使已暂存/未暂存分区上下相邻。</summary>
public sealed class ScmSectionRowHeightConverter : IMultiValueConverter
{
    /// <summary>values: 普通分区 [仓库可用][分区展开][条目数];贮藏分区(ConverterParameter="stash")
    /// 传 [仓库可用][贮藏数] 且视为恒展开。仓库不可用/未展开/无条目 → Auto。</summary>
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var repository = values is { Length: > 0 } && values[0] is bool r && r;
        var expanded = true;
        var count = 0;
        if (parameter as string == "stash")
        {
            count = values is { Length: > 1 } && values[1] is int stashCount ? stashCount : 0;
        }
        else
        {
            expanded = values is { Length: > 1 } && values[1] is bool e && e;
            count = values is { Length: > 2 } && values[2] is int c ? c : 0;
        }

        return repository && expanded && count > 0
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>V8 统一文件行模板(<c>ScmFileRowTemplate</c>)的按行类型派生值转换器:平铺更改行
/// (<see cref="GitChangeItem"/>)、树状更改文件行(<see cref="ScmFileNode"/>)与历史提交文件行
/// (<see cref="LogFileRow"/>)共用一个模板,模板内所有"因行类型而异"的显示值/交互值都由本转换器
/// 从行对象派生(绑定无 Path,参数即行 DataContext;ConverterParameter 选择派生项)。
/// 三个旧模板(ScmChangeRowTemplate / ScmTreeFileRowTemplate / ScmCommitFileRowTemplate)合并后
/// 不再需要,视觉与交互逐项保持原样。</summary>
public sealed class ScmFileRowConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        switch (parameter as string)
        {
            // 类型判别(DataTrigger 条件):
            case "IsChangeRow":
                return value is GitChangeItem or ScmFileNode;
            case "IsFlatRow":
                return value is GitChangeItem;
            case "IsCommitFile":
                return value is LogFileRow;

            // 文件类型徽标的 Path:
            case "BadgePath":
                return value switch
                {
                    GitChangeItem item => item.Path,
                    ScmFileNode node => node.Change.Path,
                    LogFileRow file => file.Change.Path,
                    _ => DependencyProperty.UnsetValue
                };

            // 行主文本:平铺/树状显示文件名,历史文件行显示完整路径(原模板如此)。
            case "Name":
                return value switch
                {
                    GitChangeItem item => item.FileName,
                    ScmFileNode node => node.Change.FileName,
                    LogFileRow file => file.Path,
                    _ => string.Empty
                };

            case "ToolTip":
                return value switch
                {
                    GitChangeItem item => item.DisplayPath,
                    ScmFileNode node => node.Change.DisplayPath,
                    LogFileRow file => file.DisplayPath,
                    _ => string.Empty
                };

            case "RenameSuffix":
                return value switch
                {
                    GitChangeItem item => item.RenameSuffix,
                    ScmFileNode node => node.Change.RenameSuffix,
                    _ => string.Empty
                };

            case "RenameSuffixGlyph":
                return value switch
                {
                    GitChangeItem item => item.RenameSuffixGlyph,
                    ScmFileNode node => node.Change.RenameSuffixGlyph,
                    _ => string.Empty
                };

            case "RenameSuffixVisibility":
                return value is GitChangeItem { RenameSuffix.Length: > 0 }
                    or ScmFileNode { Change.RenameSuffix.Length: > 0 }
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            // 动态列宽(替代旧定长 MaxWidth=200):重命名后缀/父目录段可见时各占 1 份 star,
            // 隐藏时列宽 0,主名称恒占 2 份 —— 行内各段随行的可显示范围(侧栏宽度/缩进深度)
            // 伸缩,不再截断在固定 200px。
            case "RenameSuffixColumnWidth":
                return value is GitChangeItem { RenameSuffix.Length: > 0 }
                           or ScmFileNode { Change.RenameSuffix.Length: > 0 }
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(0);
            case "DirectoryColumnWidth":
                return value is GitChangeItem { RelativeDirectory.Length: > 0 }
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(0);

            // 仅平铺行显示父目录后缀(树状行靠缩进表达层级,历史行无目录概念)。
            case "Directory":
                return value is GitChangeItem { RelativeDirectory.Length: > 0 } change ? change.RelativeDirectory : string.Empty;

            case "DirectoryVisibility":
                return value is GitChangeItem { RelativeDirectory.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

            case "StatusLetter":
                return value switch
                {
                    GitChangeItem item => item.StatusLetter,
                    ScmFileNode node => node.Change.StatusLetter,
                    LogFileRow file => file.StatusLetter,
                    _ => string.Empty
                };

            // 历史文件行右侧留白 8px(原模板 Margin="8,0,8,0"),其余行贴右。
            case "StatusLetterMargin":
                return value is LogFileRow
                    ? new Thickness(8, 0, 8, 0)
                    : new Thickness(8, 0, 0, 0);

            // 行内动作的参数:平铺行传 GitChangeItem,树状行传其 Change(与原两模板一致)。
            case "ChangeTarget":
                return value switch
                {
                    GitChangeItem item => item,
                    ScmFileNode node => node.Change,
                    _ => null
                };

            // 历史文件行的动作参数:仅 LogFileRow 传自身(其余行类型为 null → 命令可执行性
            // 为 false 而非参数类型不匹配抛异常——统一模板里更改行也会求值这些按钮的绑定)。
            case "LogFileTarget":
                return value is LogFileRow log ? log : null;

            // 树状行缩进(原 IndentMargin 绑定),其余行零缩进。
            case "IndentMargin":
                return value is ScmRowNode { IndentMargin: { } indent } ? indent : new Thickness(0);

            default:
                return DependencyProperty.UnsetValue;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>树状文件行重命名后缀段的列宽:原路径非空 → 1 star,空 → 0。
/// 与 <see cref="ScmFileRowConverter"/> 的 RenameSuffixColumnWidth 同语义(定长 200px 已废除,
/// 可显示长度随行宽动态伸缩)。</summary>
public sealed class RenameSuffixColumnWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 }
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
