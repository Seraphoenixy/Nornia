using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace Nornia.Desktop.ViewModels;

/// <summary>拆分方向。Vertical = 子区左右并排(向右拆分),Horizontal = 上下堆叠(向下拆分),
/// 与 VS Code gridview 的 "vertical"/"horizontal" 语义一致。</summary>
public enum EditorSplitOrientation
{
    Vertical,
    Horizontal,
}

/// <summary>标签右键菜单 / 拖拽发起的拆分请求:把 <see cref="Tab"/> 移入新拆出的组。</summary>
public sealed record SplitRequestedEventArgs(EditorSplitOrientation Orientation, EditorTabItem? Tab);

/// <summary>面包屑的一个分段(VS Code breadcrumb):显示文本 + 可定位的完整路径(目录段用于
/// 资源管理器选中)+ 是否目录 + 是否显示前导分隔符。</summary>
public sealed record BreadcrumbSegment(string Display, string? FullPath, bool IsDirectory, bool ShowChevron);

/// <summary>单个编辑器组:独立维护标签集合、当前标签与组 ID。所有布局结构操作(拆分/移动/关闭/
/// 合并)由 <see cref="EditorGroupsViewModel"/> 完成;本类只承载组内标签语义与交互请求事件。</summary>
public sealed partial class EditorGroupViewModel : ObservableObject
{
    private readonly List<EditorTabItem> _mru = [];

    public EditorGroupViewModel(string groupId)
    {
        GroupId = groupId;
        Tabs.CollectionChanged += (_, _) =>
        {
            // 集合增删后修剪 MRU 中已不在组内的标签(关闭/移动/驱逐)。
            _mru.RemoveAll(tab => !Tabs.Contains(tab));
            TabsChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>稳定组 ID(持久化到布局树,重启后恢复同一组)。</summary>
    public string GroupId { get; }

    public ObservableCollection<EditorTabItem> Tabs { get; } = [];

    [ObservableProperty]
    private EditorTabItem? selectedTab;

    /// <summary>The selected file tab rendered by this group's persistent preview surface.</summary>
    public FilePreviewTab? SelectedFileTab => SelectedTab as FilePreviewTab;

    /// <summary>The selected diff tab rendered by this group's diff content template.</summary>
    public DiffTab? SelectedDiffTab => SelectedTab as DiffTab;

    public bool HasSelectedFileTab => SelectedFileTab is not null;

    /// <summary>该组是否为当前活动组(驱动活动组边框高亮)。</summary>
    [ObservableProperty]
    private bool isActive;

    /// <summary>Raised whenever the group's selected tab changes.</summary>
    public event EventHandler? SelectedTabChanged;

    /// <summary>Raised when the group's tab collection membership or order changes.</summary>
    public event EventHandler? TabsChanged;

    /// <summary>关闭标签请求(由 EditorAreaViewModel 接线到阅读状态持久化 / 资源释放 / 空组移除)。</summary>
    public event EventHandler<EditorTabItem>? CloseTabRequested;

    /// <summary>关闭组内全部标签请求。</summary>
    public event EventHandler? CloseAllTabsRequested;

    /// <summary>关闭组内除 keep 外全部标签请求。</summary>
    public event EventHandler<EditorTabItem?>? CloseOtherTabsRequested;

    /// <summary>拆分请求(右键菜单 向右拆分 / 向下拆分)。</summary>
    public event EventHandler<SplitRequestedEventArgs>? SplitRequested;

    /// <summary>固定切换请求后由本类完成重排:固定标签稳定保持条带左端(块内相对顺序不变,
    /// VS Code pinned tabs 语义)。</summary>
    [RelayCommand]
    private void TogglePin(EditorTabItem? tab)
    {
        if (tab is null)
        {
            return;
        }

        tab.IsPinned = !tab.IsPinned;
        var pinned = Tabs.Where(candidate => candidate.IsPinned).ToArray();
        var unpinned = Tabs.Where(candidate => !candidate.IsPinned).ToArray();
        for (var i = 0; i < Tabs.Count; i++)
        {
            var wanted = i < pinned.Length ? pinned[i] : unpinned[i - pinned.Length];
            var current = Tabs.IndexOf(wanted);
            if (current != i)
            {
                Tabs.Move(current, i);
            }
        }
    }

    /// <summary>复制指定标签路径(标签右键菜单,不限于活动标签)。</summary>
    [RelayCommand]
    private void CopyPath(EditorTabItem? tab)
    {
        if (tab is not null)
        {
            CopyPathRequested?.Invoke(this, tab);
        }
    }

    /// <summary>在资源管理器中定位指定标签路径(标签右键菜单)。</summary>
    [RelayCommand]
    private void RevealInExplorer(EditorTabItem? tab)
    {
        if (tab is not null)
        {
            RevealInExplorerRequested?.Invoke(this, tab.Path);
        }
    }

    /// <summary>复制活动标签路径请求(由 EditorAreaViewModel 接线到剪贴板)。</summary>
    public event EventHandler<EditorTabItem>? CopyPathRequested;

    /// <summary>在资源管理器中定位文件路径请求(由 EditorAreaViewModel 接线到 explorer)。</summary>
    public event EventHandler<string>? RevealInExplorerRequested;

    /// <summary>在外部编辑器打开预览文件请求(经门面访问项目启动器)。</summary>
    public event EventHandler<FilePreviewTab>? OpenInExternalEditorRequested;

    partial void OnSelectedTabChanged(EditorTabItem? value)
    {
        if (value is not null)
        {
            // 保持组内标签 MRU:刚激活的标签提到最前 (VS Code editorGroupModel mru)。
            _mru.Remove(value);
            _mru.Insert(0, value);
        }

        // EditorAreaView is hosted once per group and inherits this view model as its DataContext.
        // Keep its type-specific projections in sync whenever the group's selection changes.
        OnPropertyChanged(nameof(SelectedFileTab));
        OnPropertyChanged(nameof(SelectedDiffTab));
        OnPropertyChanged(nameof(HasSelectedFileTab));

        SelectedTabChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>组内标签 MRU 序(最先 = 最近使用;仅包含当前存在的标签)。</summary>
    public IReadOnlyList<EditorTabItem> MruEditors => _mru;

    /// <summary>组内标签 MRU 键序(持久化用)。</summary>
    public IReadOnlyList<string> MruKeys => _mru.Select(tab => tab.TabKey).ToArray();

    /// <summary>恢复 MRU 序:按键映射到已恢复的标签(缺失键跳过),并保证当前标签在最前。</summary>
    public void RestoreMru(IEnumerable<string>? keys, EditorTabItem? active)
    {
        _mru.Clear();
        if (keys is not null)
        {
            foreach (var key in keys)
            {
                if (FindTab(key) is { } tab && !_mru.Contains(tab) && !ReferenceEquals(tab, active))
                {
                    _mru.Add(tab);
                }
            }
        }

        if (active is not null && Tabs.Contains(active) && !_mru.Contains(active))
        {
            _mru.Insert(0, active);
        }
    }

    /// <summary>关闭标签后的回选:最近使用的仍在组内的标签 (VS Code openNextRecentlyActiveEditor)。</summary>
    public EditorTabItem? SelectNextRecentlyActive() =>
        _mru.FirstOrDefault(tab => Tabs.Contains(tab) && !ReferenceEquals(tab, SelectedTab))
        ?? Tabs.LastOrDefault();

    /// <summary>超限驱逐:最近最少使用的标签(排除当前标签与固定标签;MRU 尾 → 头)。
    /// 固定标签豁免标签上限——无候选时返回 null,调用方不做驱逐。</summary>
    public EditorTabItem? EvictLeastRecentlyUsed()
    {
        for (var i = _mru.Count - 1; i >= 0; i--)
        {
            if (Tabs.Contains(_mru[i]) && !_mru[i].IsPinned && !ReferenceEquals(_mru[i], SelectedTab))
            {
                return _mru[i];
            }
        }

        return Tabs.FirstOrDefault(tab => !tab.IsPinned && !ReferenceEquals(tab, SelectedTab));
    }

    /// <summary>标签上限降低时的预览-only 回收:只驱逐最久未用的 <b>预览</b> 标签
    /// (排除固定/常驻标签;VS Code 语义:标签上限不强制关闭常驻标签)。无预览候选时返回 null。</summary>
    public EditorTabItem? EvictLeastRecentlyUsedPreview()
    {
        for (var i = _mru.Count - 1; i >= 0; i--)
        {
            if (Tabs.Contains(_mru[i]) && _mru[i] is FilePreviewTab { IsPreview: true } preview
                && !preview.IsPinned && !ReferenceEquals(preview, SelectedTab))
            {
                return preview;
            }
        }

        return Tabs.FirstOrDefault(tab => tab is FilePreviewTab { IsPreview: true } preview
            && !preview.IsPinned && !ReferenceEquals(preview, SelectedTab));
    }

    public EditorTabItem? FindTab(string tabKey) =>
        Tabs.FirstOrDefault(tab => string.Equals(tab.TabKey, tabKey, StringComparison.Ordinal));

    /// <summary>组内拖拽重排。</summary>
    public void MoveTab(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Tabs.Count || toIndex < 0)
        {
            return;
        }

        var clamped = Math.Min(toIndex, Tabs.Count - 1);
        if (fromIndex != clamped)
        {
            Tabs.Move(fromIndex, clamped);
        }
    }

    private void RaiseTabsChanged() => TabsChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void CloseTab(EditorTabItem? tab)
    {
        if (tab is not null)
        {
            CloseTabRequested?.Invoke(this, tab);
        }
    }

    [RelayCommand]
    private void CloseAllTabs() => CloseAllTabsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void CloseOtherTabs(EditorTabItem? keep) => CloseOtherTabsRequested?.Invoke(this, keep);

    [RelayCommand]
    private void SplitRight(EditorTabItem? tab) =>
        SplitRequested?.Invoke(this, new SplitRequestedEventArgs(EditorSplitOrientation.Vertical, tab));

    [RelayCommand]
    private void SplitDown(EditorTabItem? tab) =>
        SplitRequested?.Invoke(this, new SplitRequestedEventArgs(EditorSplitOrientation.Horizontal, tab));

    /// <summary>复制活动标签的完整路径(文件预览工具条按钮)。</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelectedTabPath))]
    private void CopySelectedTabPath()
    {
        if (SelectedTab is { } tab)
        {
            CopyPathRequested?.Invoke(this, tab);
        }
    }

    private bool CanCopySelectedTabPath() => SelectedTab is not null;

    /// <summary>在外部编辑器打开当前预览文件(文件预览工具条按钮)。
    /// 组标签右键菜单也会把 DiffTab 作为参数传入,因此这里先接收通用标签再做类型判断,
    /// 避免 CommunityToolkit 命令在执行前进行不兼容的参数转换。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInExternalEditor))]
    private void OpenInExternalEditor(EditorTabItem? tab)
    {
        if (tab is FilePreviewTab preview)
        {
            OpenInExternalEditorRequested?.Invoke(this, preview);
        }
    }

    private bool CanOpenInExternalEditor(EditorTabItem? tab) => tab is FilePreviewTab;
}
