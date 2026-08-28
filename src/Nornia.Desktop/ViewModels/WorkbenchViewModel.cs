using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Desktop.Services;

namespace Nornia.Desktop.ViewModels;

/// <summary>VS Code-style tab manager for the main content region. Owns the unified mixed strip:
/// content-page tabs (环境管理 / 项目管理 / 设置) and file/diff document tabs projected from the
/// shared editor area; repeated opens activate the existing tab instead of duplicating it.
/// View pages (资源管理器 / 源代码管理) never produce tabs — their main content IS the editor.</summary>
public partial class WorkbenchViewModel : ObservableObject
{
    private readonly EditorAreaViewModel _editor;
    private readonly Dictionary<string, PageWorkbenchTab> _pageTabs = [];
    private bool _suppressEditorSync;

    public WorkbenchViewModel(EditorAreaViewModel editor, IReadOnlyList<NavigationItem> navigationItems)
    {
        _editor = editor;

        // 预建内容页标签(环境管理 / 项目管理 / 设置):不加入条带,首次导航才 OpenOrActivateTab。
        // 视图页(资源管理器 / 源代码管理)永不产生页面标签。
        foreach (var item in navigationItems)
        {
            if (item.Page is ExplorerPageViewModel or GitViewModel)
            {
                continue;
            }

            _pageTabs[item.Title] = new PageWorkbenchTab(item.Page, item.Glyph);
        }

        // Editor tabs are projected from every editor group. The workbench owns the global strip
        // order; group layout only owns each tab's content placement.
        _editor.OpenTabsChanged += (_, _) => SyncEditorTabs();
        _editor.SelectedTabChanged += (_, _) => SyncEditorTabs();
        SyncEditorTabs();

        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            SyncPageTabs();
            SyncEditorTabProjection();
            OnPropertyChanged(nameof(ShowEditorTabs));
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedTab))
            {
                HandleSelectedTabChanged();
            }
        };
    }

    public ObservableCollection<WorkbenchTabViewModel> Tabs { get; } = [];

    /// <summary>顶层文件/Diff 标签栏投影(按 <see cref="Tabs"/> 的全局打开顺序)。</summary>
    public ObservableCollection<EditorWorkbenchTab> EditorTabs { get; } = [];

    /// <summary>全局页面标签(环境管理 / 项目管理 / 设置)的独立视图集合,与编辑器组分离渲染。</summary>
    public ObservableCollection<WorkbenchTabViewModel> PageTabs { get; } = [];

    [ObservableProperty]
    private WorkbenchTabViewModel? selectedTab;

    /// <summary>顶层文件标签栏的选中项;设置时同时激活其所属编辑器组。</summary>
    public EditorWorkbenchTab? SelectedEditorTab
    {
        get => SelectedTab as EditorWorkbenchTab;
        set
        {
            if (!ReferenceEquals(SelectedTab, value))
            {
                SelectedTab = value;
            }
        }
    }

    /// <summary>页面标签条选中项:仅当选中页面标签时非空(文档选中/空时为 null)。</summary>
    public WorkbenchTabViewModel? SelectedPageTab
    {
        get => SelectedTab as PageWorkbenchTab;
        set
        {
            if (!ReferenceEquals(SelectedTab, value))
            {
                SelectedTab = value;
            }
        }
    }

    /// <summary>选中页面标签时,编辑器组区域让位于页面内容。</summary>
    public bool IsPageTabSelected => SelectedTab is PageWorkbenchTab;

    public object? SelectedPageContent => (SelectedTab as PageWorkbenchTab)?.Content;

    /// <summary>页面标签条可见条件:存在页面标签(启动不自动打开,首次导航才加入条带)。</summary>
    public bool HasPageTabs => PageTabs.Count > 0;

    /// <summary>编辑器组网格(活动组投影的渲染源)。</summary>
    public EditorGroupsViewModel Groups => _editor.Groups;

    /// <summary>The shared editor area that backs the file/diff tabs.</summary>
    public EditorAreaViewModel Editor => _editor;

    /// <summary>False once every tab has been closed (drives the empty state).</summary>
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>页面标签选中时页面内容独占编辑区,否则文件标签栏在有文件标签时显示。</summary>
    public bool ShowEditorTabs => !IsPageTabSelected && EditorTabs.Count > 0;

    /// <summary>Command wired by <see cref="MainViewModel"/> to reveal the output panel.</summary>
    public ICommand? ShowOperationLogCommand { get; set; }

    /// <summary>Wired by <see cref="MainViewModel"/>: copies text to the clipboard (tab context menu).</summary>
    public Action<string>? CopyToClipboard { get; set; }

    /// <summary>打开内容页标签(环境管理 / 项目管理 / 设置),已存在则激活;视图页(资源管理器 /
    /// 源代码管理)不创建、不激活、不选中——它们的主内容就是共享编辑器。</summary>
    public void OpenOrActivatePage(PageViewModel page)
    {
        if (page is ExplorerPageViewModel or GitViewModel)
        {
            return;
        }

        var tab = _pageTabs.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Content, page));
        if (tab is not null)
        {
            OpenOrActivateTab(tab);
        }
    }

    /// <summary>视图页规整:视图页下不允许页面标签处于选中态——活动文档保持选中,
    /// 其它情况(页面标签/空)置空选中。</summary>
    public void ClearNonDocumentSelection()
    {
        if (SelectedTab is not EditorWorkbenchTab)
        {
            SelectedTab = null;
        }
    }

    /// <summary>Open a document tab (or activate it when already open).</summary>
    public void OpenOrActivateTab(WorkbenchTabViewModel tab)
    {
        if (Tabs.Contains(tab))
        {
            SelectedTab = tab;
            return;
        }

        tab.CloseRequested += (_, _) => CloseTab(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    // ===== Editor projection =====

    private void SyncEditorTabs()
    {
        if (_suppressEditorSync)
        {
            return;
        }

        var live = _editor.Groups.GroupsInLayoutOrder.SelectMany(group => group.Tabs).ToArray();

        // Drop workbench tabs whose shared-editor tab is gone.
        foreach (var stale in Tabs.OfType<EditorWorkbenchTab>()
                     .Where(t => t.EditorTab.IsClosed && !live.Contains(t.EditorTab)).ToArray())
        {
            Tabs.Remove(stale);
        }

        // Append any new editor tabs in layout discovery order; existing workbench order is kept
        // so switching groups never reorders the global strip.
        foreach (var editorTab in live)
        {
            if (Tabs.OfType<EditorWorkbenchTab>().All(t => !ReferenceEquals(t.EditorTab, editorTab)))
            {
                var workbenchTab = new EditorWorkbenchTab(_editor, editorTab);
                workbenchTab.CloseRequested += (_, _) => CloseTab(workbenchTab);
                Tabs.Add(workbenchTab);
            }
        }

        SyncEditorTabProjection();

        // Keep the active workbench tab in step with the shared editor's selection.
        if (_editor.SelectedTab is { } selected
            && Tabs.OfType<EditorWorkbenchTab>().FirstOrDefault(t => ReferenceEquals(t.EditorTab, selected)) is { } match)
        {
            SelectedTab = match;
        }
    }

    private void SyncEditorTabProjection()
    {
        var desired = Tabs.OfType<EditorWorkbenchTab>().ToArray();
        for (var i = EditorTabs.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(EditorTabs[i]))
            {
                EditorTabs.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Length; i++)
        {
            if (i < EditorTabs.Count && ReferenceEquals(EditorTabs[i], desired[i]))
            {
                continue;
            }

            var existing = EditorTabs.IndexOf(desired[i]);
            if (existing >= 0)
            {
                EditorTabs.Move(existing, i);
            }
            else
            {
                EditorTabs.Insert(i, desired[i]);
            }
        }
    }

    /// <summary>页面标签独立集合:跟随 <see cref="Tabs"/> 中的页面标签(不进入任何编辑器组)。</summary>
    private void SyncPageTabs()
    {
        for (var i = PageTabs.Count - 1; i >= 0; i--)
        {
            if (!Tabs.Contains(PageTabs[i]))
            {
                PageTabs.RemoveAt(i);
            }
        }

        foreach (var tab in Tabs.OfType<PageWorkbenchTab>())
        {
            if (!PageTabs.Contains(tab))
            {
                PageTabs.Add(tab);
            }
        }

        OnPropertyChanged(nameof(HasPageTabs));
    }

    // ===== Commands =====

    /// <summary>Ctrl+W: close the active tab (no-op when nothing is selected). Closing picks the right
    /// neighbour, falling back to the left neighbour, then empties the strip.</summary>
    [RelayCommand]
    private void CloseActiveTab() => CloseTab(SelectedTab);

    [RelayCommand]
    private void CloseTab(WorkbenchTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        // Neighbour selection (right if present, else left) computed before removal.
        var neighbour = index + 1 < Tabs.Count
            ? Tabs[index + 1]
            : (index - 1 >= 0 ? Tabs[index - 1] : null);
        var wasSelected = ReferenceEquals(SelectedTab, tab);

        if (tab is EditorWorkbenchTab editorTab)
        {
            _editor.CloseTab(editorTab.EditorTab);
            // 幽灵标签兜底:无主标签(不属于任何编辑器组)的关闭由 EditorAreaViewModel 补齐
            // IsClosed/释放,但它不触发任何组事件,投影不会清理——这里直接移除条带项,
            // 保证条带不会残留无法关闭的标签(常规关闭时投影已同步移除,此 Remove 为 no-op)。
            if (editorTab.EditorTab.IsClosed
                && _editor.Groups.FindGroupContaining(editorTab.EditorTab) is null)
            {
                Tabs.Remove(tab);
            }
        }
        else
        {
            Tabs.RemoveAt(index);
        }

        // Closing a file can synchronously update the editor projection and temporarily leave
        // SelectedTab pointing at the removed item. Always converge to a surviving tab when the
        // strip still has one, otherwise the editor area appears empty while tabs remain open.
        if (wasSelected || SelectedTab is null || !Tabs.Contains(SelectedTab))
        {
            SelectedTab = neighbour is not null && Tabs.Contains(neighbour)
                ? neighbour
                : Tabs.Count > 0 ? Tabs[^1] : null;
        }
    }

    [RelayCommand]
    private void CloseAllTabs()
    {
        // Close page tabs through the same collection path as the other close commands. Editor
        // tabs must be closed by EditorAreaViewModel so their owning groups, resources and
        // projection are updated together.
        foreach (var page in Tabs.OfType<PageWorkbenchTab>().ToArray())
        {
            Tabs.Remove(page);
        }

        _editor.CloseAllTabs();
        SelectedTab = Tabs.Count > 0 ? Tabs[^1] : null;
    }

    [RelayCommand]
    private void CloseOtherTabs(WorkbenchTabViewModel? keep)
    {
        if (keep is null || !Tabs.Contains(keep))
        {
            return;
        }

        var targets = Tabs
            .Where(tab => !ReferenceEquals(tab, keep) && !tab.IsPinned)
            .ToArray();
        CloseWorkbenchTabs(targets);

        SelectSurvivingTab(keep);
    }

    /// <summary>"关闭右侧": closes every tab after the given tab (VS Code tab menu).</summary>
    [RelayCommand]
    private void CloseTabsToTheRight(WorkbenchTabViewModel? anchor)
    {
        if (anchor is null)
        {
            return;
        }

        var index = Tabs.IndexOf(anchor);
        if (index < 0)
        {
            return;
        }

        var targets = Tabs
            .Skip(index + 1)
            .Where(tab => !tab.IsPinned)
            .ToArray();
        CloseWorkbenchTabs(targets);

        SelectSurvivingTab(anchor);
    }

    /// <summary>"关闭未修改": closes unpinned tabs (this workbench is read-only, so every tab is
    /// technically unmodified; the pin is what keeps a tab open — same as VS Code "close saved").</summary>
    [RelayCommand]
    private void CloseUnpinned()
    {
        foreach (var tab in Tabs.Where(t => !t.IsPinned).ToArray())
        {
            CloseWorkbenchTab(tab);
        }

        if (SelectedTab is not null && !Tabs.Contains(SelectedTab))
        {
            SelectedTab = Tabs.Count > 0 ? Tabs[^1] : null;
        }
    }

    /// <summary>固定 / 取消固定: 固定标签具备实际保留语义——固定预览即常驻(斜体消失,
    /// 不再被下一次文件打开的预览槽替换),且固定标签豁免预览槽替换、标签上限驱逐,
    /// 并存活于 关闭其他 / 关闭右侧 / 关闭未修改。取消固定不会把标签重新降级为预览。</summary>
    [RelayCommand]
    private void TogglePin(WorkbenchTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        tab.IsPinned = !tab.IsPinned;
        if (tab is EditorWorkbenchTab editorTab)
        {
            editorTab.EditorTab.IsPinned = tab.IsPinned;
            if (tab.IsPinned)
            {
                editorTab.EditorTab.IsPreview = false;
            }
        }
    }

    /// <summary>复制标签标题 (右键菜单, 经 MainViewModel 注入剪贴板)。</summary>
    [RelayCommand(CanExecute = nameof(CanCopyTabTitle))]
    private void CopyTabTitle() => CopyToClipboard?.Invoke(SelectedTab?.Title ?? string.Empty);

    private bool CanCopyTabTitle() => SelectedTab is not null;

    /// <summary>复制标签完整路径 (仅文件/diff 标签有效)。</summary>
    [RelayCommand(CanExecute = nameof(CanCopyTabPath))]
    private void CopyTabPath(WorkbenchTabViewModel? tab) => CopyToClipboard?.Invoke(tab switch
    {
        EditorWorkbenchTab editor => editor.EditorTab.Path,
        _ => string.Empty,
    });

    private bool CanCopyTabPath(WorkbenchTabViewModel? tab) => tab is EditorWorkbenchTab;

    private void CloseWorkbenchTab(WorkbenchTabViewModel tab)
    {
        if (!Tabs.Contains(tab))
        {
            return;
        }

        if (tab is EditorWorkbenchTab editorTab)
        {
            _editor.CloseTab(editorTab.EditorTab);
        }
        else
        {
            Tabs.Remove(tab);
        }
    }

    /// <summary>关闭批量操作预先确定的标签快照,避免关闭一个编辑器触发投影同步后改变枚举源。</summary>
    private void CloseWorkbenchTabs(IEnumerable<WorkbenchTabViewModel> tabs)
    {
        foreach (var tab in tabs.ToArray())
        {
            CloseWorkbenchTab(tab);
        }
    }

    private void SelectSurvivingTab(WorkbenchTabViewModel preferred)
    {
        SelectedTab = Tabs.Contains(preferred)
            ? preferred
            : Tabs.Count > 0 ? Tabs[^1] : null;
    }

    /// <summary>拖拽重排混合页面标签条(保留原有命令兼容)。</summary>
    [RelayCommand]
    private void MoveTab(MoveTabArgs args)
    {
        if (args is null || args.FromIndex < 0 || args.ToIndex < 0 || args.FromIndex >= Tabs.Count)
        {
            return;
        }

        var toIndex = Math.Min(args.ToIndex, Tabs.Count - 1);
        if (args.FromIndex == toIndex)
        {
            return;
        }

        Tabs.Move(args.FromIndex, toIndex);

        SyncEditorTabProjection();
    }

    /// <summary>拖拽重排顶层文件标签;只改变全局标签顺序,不改变文件所属编辑器组。</summary>
    [RelayCommand]
    private void MoveEditorTab(MoveTabArgs args)
    {
        if (args is null || args.FromIndex < 0 || args.ToIndex < 0 || args.FromIndex >= EditorTabs.Count)
        {
            return;
        }

        var toIndex = Math.Min(args.ToIndex, EditorTabs.Count - 1);
        if (args.FromIndex == toIndex)
        {
            return;
        }

        var tab = EditorTabs[args.FromIndex];
        var from = Tabs.IndexOf(tab);
        var target = EditorTabs[toIndex];
        var destination = Tabs.IndexOf(target);
        if (from < 0 || destination < 0)
        {
            return;
        }

        Tabs.Move(from, destination);
        SyncEditorTabProjection();
    }

    [RelayCommand]
    private void SplitEditorTabRight(WorkbenchTabViewModel? tab) =>
        SplitEditorTab(tab as EditorWorkbenchTab, EditorSplitOrientation.Vertical);

    [RelayCommand]
    private void SplitEditorTabDown(WorkbenchTabViewModel? tab) =>
        SplitEditorTab(tab as EditorWorkbenchTab, EditorSplitOrientation.Horizontal);

    private void SplitEditorTab(EditorWorkbenchTab? tab, EditorSplitOrientation orientation)
    {
        if (tab is null || _editor.Groups.FindGroupContaining(tab.EditorTab) is not { } group)
        {
            return;
        }

        _editor.Groups.SplitGroup(group, orientation, tab.EditorTab);
    }

    [RelayCommand]
    private void RevealEditorTab(WorkbenchTabViewModel? tab)
    {
        if (tab is EditorWorkbenchTab editorTab
            && _editor.Groups.FindGroupContaining(editorTab.EditorTab) is { } group)
        {
            group.RevealInExplorerCommand.Execute(editorTab.EditorTab);
        }
    }

    [RelayCommand]
    private void OpenEditorTabExternally(WorkbenchTabViewModel? tab)
    {
        if (tab is EditorWorkbenchTab editorTab
            && _editor.Groups.FindGroupContaining(editorTab.EditorTab) is { } group)
        {
            group.OpenInExternalEditorCommand.Execute(editorTab.EditorTab);
        }
    }

    /// <summary>Ctrl+PageUp/PageDown: cycles the active tab (wraps around the strip, VS Code-style).</summary>
    [RelayCommand]
    private void GoToAdjacentTab(int offset)
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        var index = Tabs.IndexOf(SelectedTab ?? Tabs[^1]);
        var next = (index + offset + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[next];
    }

    private void HandleSelectedTabChanged()
    {
        OnPropertyChanged(nameof(SelectedPageTab));
        OnPropertyChanged(nameof(IsPageTabSelected));
        OnPropertyChanged(nameof(SelectedPageContent));
        OnPropertyChanged(nameof(SelectedEditorTab));
        OnPropertyChanged(nameof(ShowEditorTabs));
        // Selecting a top-level file tab activates the group that owns it, then selects the tab in
        // that group. This is the key difference from the former active-group-only projection.
        if (SelectedTab is EditorWorkbenchTab editorTab
            && _editor.Groups.FindGroupContaining(editorTab.EditorTab) is { } owner
            && (!ReferenceEquals(_editor.Groups.ActiveGroup, owner)
                || !ReferenceEquals(owner.SelectedTab, editorTab.EditorTab)))
        {
            _suppressEditorSync = true;
            try
            {
                _editor.ActivateTab(editorTab.EditorTab);
            }
            finally
            {
                _suppressEditorSync = false;
            }
        }
    }
}
