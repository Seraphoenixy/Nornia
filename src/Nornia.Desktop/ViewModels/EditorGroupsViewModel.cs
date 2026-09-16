using CommunityToolkit.Mvvm.ComponentModel;
using Nornia.Desktop.Configuration;

namespace Nornia.Desktop.ViewModels;

/// <summary>布局树节点基类。</summary>
public abstract record EditorLayoutNode;

/// <summary>叶节点:一个编辑器组。</summary>
public sealed record EditorGroupNode(EditorGroupViewModel Group) : EditorLayoutNode;

/// <summary>分支节点:按方向并排 / 堆叠若干子节点,<see cref="Weights"/> 为归一化比例
/// (默认 50/50,限制在 20%–80%)。</summary>
public sealed record EditorSplitNode(
    EditorSplitOrientation Orientation,
    IReadOnlyList<EditorLayoutNode> Children,
    IReadOnlyList<double> Weights) : EditorLayoutNode;

/// <summary>
/// VS Code 风格编辑器组网格:持有布局树(GroupNode/SplitNode)、活动组,以及全部结构操作——
/// 拆分、移动标签、关闭组并自动合并、至少保留一个组的约束、比例钳制、布局重置与序列化恢复。
/// 纯逻辑实现(不依赖 WPF),所有布局行为均可单测。
/// </summary>
public sealed partial class EditorGroupsViewModel : ObservableObject
{
    /// <summary>单个分割的可用比例下限 / 上限(20%–80%)。</summary>
    public const double MinGroupRatio = 0.20;
    public const double MaxGroupRatio = 0.80;

    /// <summary>防御性上限:阻止无限制拆分把窗口切成不可用碎片。</summary>
    public const int MaxGroupCount = 16;

    private const int MaxLayoutDepth = 8;

    private readonly Dictionary<string, EditorGroupViewModel> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EditorGroupViewModel> _groupMru = [];
    private readonly List<EditorTabItem> _editorMru = [];
    private EditorLayoutNode _root = null!;

    // V8: AllTabs 物化缓存。旧实现是每次枚举都重跑的 SelectMany(组×标签),PruneEditorMru 等调用
    // 点变成 O(M×N);现在组/标签增删时整表重建一次(O(N)),读取 O(1),Contains 走引用哈希集
    // (标签引用唯一,对照 VS Code 的 tab 集合维护)。
    private readonly List<EditorTabItem> _allTabs = [];
    private readonly HashSet<EditorTabItem> _allTabsSet = new(ReferenceEqualityComparer.Instance);

    public EditorGroupsViewModel()
    {
        var initial = new EditorGroupViewModel(NewGroupId());
        _groups[initial.GroupId] = initial;
        TrackGroupTabs(initial);
        _groupMru.Add(initial);
        _root = new EditorGroupNode(initial);
        ActiveGroup = initial;
        RebuildAllTabs();
    }

    private void TrackGroupTabs(EditorGroupViewModel group) => group.TabsChanged += OnGroupTabsChanged;

    private void UntrackGroupTabs(EditorGroupViewModel group) => group.TabsChanged -= OnGroupTabsChanged;

    private void OnGroupTabsChanged(object? sender, EventArgs e) => RebuildAllTabs();

    private void RebuildAllTabs()
    {
        _allTabs.Clear();
        _allTabsSet.Clear();
        foreach (var group in _groups.Values)
        {
            foreach (var tab in group.Tabs)
            {
                _allTabs.Add(tab);
                _allTabsSet.Add(tab);
            }
        }

        OnPropertyChanged(nameof(AllTabs));
    }

    /// <summary>Raised when the active group changes (驱动活动组边框与投影重同步)。</summary>
    public event EventHandler? ActiveGroupChanged;

    /// <summary>Raised when the layout topology, tab membership or order changes (视图重建 / 持久化)。</summary>
    public event EventHandler? LayoutChanged;

    [ObservableProperty]
    private EditorGroupViewModel activeGroup = null!;

    partial void OnActiveGroupChanged(EditorGroupViewModel value)
    {
        foreach (var group in _groups.Values)
        {
            group.IsActive = ReferenceEquals(group, value);
        }

        if (_groups.ContainsKey(value.GroupId))
        {
            TouchGroupMru(value);
        }

        ActiveGroupChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>组级 MRU(最先 = 最近活动;仅包含现存组,VS Code mostRecentActiveGroups)。</summary>
    public IReadOnlyList<EditorGroupViewModel> GroupsInMruOrder =>
        _groupMru.Where(group => _groups.ContainsKey(group.GroupId)).ToArray();

    /// <summary>工作台级编辑器 MRU(最先 = 最近激活;仅包含现存标签)。供 Ctrl+Tab 切换器
    /// (workbench.action.openNextRecentlyUsedEditor) 使用。由 EditorAreaViewModel 在
    /// 组选中变化/标签集合变化时调用 <see cref="TouchEditorMru"/> / <see cref="PruneEditorMru"/>。</summary>
    public IReadOnlyList<EditorTabItem> EditorsInMruOrder =>
        _editorMru.Where(tab => _allTabsSet.Contains(tab)).ToArray();

    /// <summary>把刚激活的标签提到工作台 MRU 最前。</summary>
    public void TouchEditorMru(EditorTabItem? tab)
    {
        if (tab is null) return;
        _editorMru.Remove(tab);
        _editorMru.Insert(0, tab);
    }

    /// <summary>标签集合增删后修剪 MRU 中已不存在的标签(V8:存活判定走物化哈希集,
    /// 单次 O(M+N),不再对 SelectMany 序列逐标签 Contains)。</summary>
    public void PruneEditorMru()
    {
        if (_editorMru.Count == 0) return;
        if (_editorMru.All(tab => _allTabsSet.Contains(tab))) return;
        _editorMru.RemoveAll(tab => !_allTabsSet.Contains(tab));
    }

    /// <summary>组级 MRU 键序(持久化用)。</summary>
    public IReadOnlyList<string> GroupMruKeys => GroupsInMruOrder.Select(group => group.GroupId).ToArray();

    private void TouchGroupMru(EditorGroupViewModel group)
    {
        _groupMru.Remove(group);
        _groupMru.Insert(0, group);
    }

    private void SeedGroupMru(EditorGroupViewModel group)
    {
        if (!_groupMru.Contains(group))
        {
            _groupMru.Add(group);
        }
    }

    private void DropGroupMru(EditorGroupViewModel group) => _groupMru.Remove(group);

    public EditorLayoutNode Root => _root;
    public IReadOnlyCollection<EditorGroupViewModel> Groups => _groups.Values;
    public int GroupCount => _groups.Count;
    public bool HasMultipleGroups => _groups.Count > 1;

    /// <summary>布局序(前序遍历)下的全部组 — 用于 下一个/上一个组 与邻居选择。</summary>
    public IReadOnlyList<EditorGroupViewModel> GroupsInLayoutOrder => LeafGroups(_root).ToArray();

    /// <summary>全部组的全部标签(V8:物化缓存,组/标签增删时重建;类型与名称保持不变,
    /// 消费方按 IEnumerable 遍历不受影响)。</summary>
    public IEnumerable<EditorTabItem> AllTabs => _allTabs;

    // ===== 查找 =====

    public EditorGroupViewModel? FindGroupContaining(EditorTabItem tab) =>
        _groups.Values.FirstOrDefault(group => group.Tabs.Contains(tab));

    /// <summary>跨组查找同一 TabKey 的标签(同一个标签只允许存在于一个组)。</summary>
    public (EditorGroupViewModel Group, EditorTabItem Tab)? FindTabByKey(string tabKey)
    {
        foreach (var group in _groups.Values)
        {
            if (group.FindTab(tabKey) is { } tab)
            {
                return (group, tab);
            }
        }

        return null;
    }

    public EditorGroupViewModel? FindGroup(string groupId) =>
        _groups.TryGetValue(groupId, out var group) ? group : null;

    // ===== 拆分 =====

    /// <summary>向右拆分 / 向下拆分当前组(把活动标签移入新组,VS Code 行为)。</summary>
    public void SplitActiveGroup(EditorSplitOrientation orientation) =>
        SplitGroup(ActiveGroup, orientation, ActiveGroup.SelectedTab);

    /// <summary>拆分指定组:新组默认放在旧组之后(向右拆分 → 新组在右;向下拆分 → 新组在下),
    /// 并把 <paramref name="moveTab"/>(通常为该组当前标签)移入新组。</summary>
    public EditorGroupViewModel SplitGroup(EditorGroupViewModel group, EditorSplitOrientation orientation,
        EditorTabItem? moveTab = null, bool newGroupFirst = false)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!_groups.ContainsKey(group.GroupId))
        {
            return group;
        }

        if (_groups.Count >= MaxGroupCount)
        {
            return ActiveGroup;
        }

        var created = new EditorGroupViewModel(NewGroupId());
        _groups[created.GroupId] = created;
        TrackGroupTabs(created);
        SeedGroupMru(created);
        _root = Normalize(ReplaceLeaf(_root, group, node => CreateSplitAround(node, created, orientation, newGroupFirst)));

        if (moveTab is not null)
        {
            var source = FindGroupContaining(moveTab);
            if (source is not null && !ReferenceEquals(source, created))
            {
                var removedIndex = source.Tabs.IndexOf(moveTab);
                source.Tabs.Remove(moveTab);
                if (ReferenceEquals(source.SelectedTab, moveTab))
                {
                    // 被拆分移走的标签若为源组当前标签:选择相邻标签 (VS Code 拆分的组内选择回落)。
                    if (source.Tabs.Count > 0)
                    {
                        source.SelectedTab = source.Tabs[Math.Min(removedIndex, source.Tabs.Count - 1)];
                    }
                    else
                    {
                        source.SelectedTab = null;
                    }
                }

                if (source.Tabs.Count == 0 && GroupCount > 2)
                {
                    RemoveEmptyGroup(source);
                }
            }

            created.Tabs.Add(moveTab);
            created.SelectedTab = moveTab;
        }

        ActiveGroup = created;
        RaiseLayoutChanged();
        return created;
    }

    // ===== 移动标签 =====

    /// <summary>普通拖拽:把标签移动到目标组末尾(不复制),目标组变为活动组。</summary>
    public void MoveTabToGroup(EditorTabItem tab, EditorGroupViewModel targetGroup)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(targetGroup);
        var source = FindGroupContaining(tab);
        if (source is null)
        {
            return;
        }

        if (ReferenceEquals(source, targetGroup))
        {
            return;
        }

        // 预览标签被移动到另一组后不再是"新打开"的标签,保持原预览状态即可。
        source.Tabs.Remove(tab);
        if (source.Tabs.Count == 0)
        {
            RemoveEmptyGroup(source);
        }

        if (_groups.ContainsKey(targetGroup.GroupId))
        {
            targetGroup.Tabs.Add(tab);
            targetGroup.SelectedTab = tab;
        }
        else
        {
            // 目标组在空组移除后已不存在(理论分支):回退到活动组。
            ActiveGroup.Tabs.Add(tab);
            ActiveGroup.SelectedTab = tab;
        }

        ActiveGroup = _groups.ContainsKey(targetGroup.GroupId) ? targetGroup : ActiveGroup;
        RaiseLayoutChanged();
    }

    /// <summary>拖到目标组边缘:把目标组拆出一块并把标签移入新组(向右 → 新组在右,向下 → 在下;
    /// 左缘/上缘则新组在左/在上)。</summary>
    public void MoveTabToNewGroup(EditorTabItem tab, EditorGroupViewModel targetGroup,
        EditorSplitOrientation orientation, bool newGroupFirst)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(targetGroup);
        if (!_groups.ContainsKey(targetGroup.GroupId))
        {
            return;
        }

        if (_groups.Count >= MaxGroupCount)
        {
            MoveTabToGroup(tab, targetGroup);
            return;
        }

        var source = FindGroupContaining(tab);
        var created = SplitGroup(targetGroup, orientation, moveTab: null, newGroupFirst: newGroupFirst);
        source?.Tabs.Remove(tab);
        if (source is not null && source.Tabs.Count == 0 && GroupCount > 2 && !ReferenceEquals(source, created))
        {
            RemoveEmptyGroup(source);
        }

        created.Tabs.Add(tab);
        created.SelectedTab = tab;
        ActiveGroup = created;
        RaiseLayoutChanged();
    }

    /// <summary>组内重排(组标签条拖拽)。</summary>
    public void MoveTabWithinGroup(EditorGroupViewModel group, int fromIndex, int toIndex)
    {
        group.MoveTab(fromIndex, toIndex);
        RaiseLayoutChanged();
    }

    // ===== 关闭 =====

    /// <summary>移除一个(可能仍有标签的)组:剩余标签移到相邻组,空父级 SplitNode 自动合并,
    /// 始终至少保留一个编辑器组。</summary>
    public EditorGroupViewModel CloseGroup(EditorGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!_groups.ContainsKey(group.GroupId))
        {
            return ActiveGroup;
        }

        if (GroupCount <= 1)
        {
            // 唯一组不可移除:清空标签、保持空组。
            group.Tabs.Clear();
            group.SelectedTab = null;
            return group;
        }

        var ordered = GroupsInLayoutOrder;
        var index = IndexOfGroup(ordered, group);
        var neighbour = ordered[(index + 1) % ordered.Count];

        foreach (var tab in group.Tabs.ToArray())
        {
            group.Tabs.Remove(tab);
            neighbour.Tabs.Add(tab);
        }

        group.SelectedTab = null;
        _groups.Remove(group.GroupId);
        UntrackGroupTabs(group);
        DropGroupMru(group);
        _root = Normalize(RemoveLeaf(_root, group) ?? new EditorGroupNode(neighbour));
        ActiveGroup = neighbour;
        RaiseLayoutChanged();
        return neighbour;
    }

    /// <summary>组已为空时移除它(同时保证至少一个组)。若移除的是活动组,
    /// 按组级 MRU 激活"次最近活动"组 (VS Code doRemoveEmptyGroup),失败回退相邻组。</summary>
    public void RemoveEmptyGroup(EditorGroupViewModel group)
    {
        if (group.Tabs.Count > 0 || !_groups.ContainsKey(group.GroupId))
        {
            return;
        }

        if (GroupCount <= 1)
        {
            return;
        }

        var wasActive = ReferenceEquals(ActiveGroup, group);
        var ordered = GroupsInLayoutOrder;
        var index = IndexOfGroup(ordered, group);
        var neighbour = index + 1 < ordered.Count ? ordered[index + 1] : ordered[index - 1];
        _groups.Remove(group.GroupId);
        UntrackGroupTabs(group);
        DropGroupMru(group);
        _root = Normalize(RemoveLeaf(_root, group) ?? new EditorGroupNode(neighbour));
        if (wasActive)
        {
            ActiveGroup = GroupsInMruOrder.FirstOrDefault(candidate => !ReferenceEquals(candidate, group))
                ?? neighbour;
        }

        RaiseLayoutChanged();
    }

    /// <summary>关闭组内全部标签(组可能因空而被移除;始终保留一个组)。</summary>
    public void CloseAllTabsInGroup(EditorGroupViewModel group)
    {
        foreach (var tab in group.Tabs.ToArray())
        {
            group.Tabs.Remove(tab);
        }

        group.SelectedTab = null;
        if (group.Tabs.Count == 0)
        {
            RemoveEmptyGroup(group);
        }
    }

    /// <summary>关闭组内除 keep 外的全部标签。</summary>
    public void CloseOtherTabsInGroup(EditorGroupViewModel group, EditorTabItem? keep)
    {
        if (keep is not null)
        {
            foreach (var tab in group.Tabs.Where(tab => !ReferenceEquals(tab, keep)).ToArray())
            {
                group.Tabs.Remove(tab);
            }

            group.SelectedTab = keep;
        }
    }

    // ===== 焦点切换 =====

    public void FocusNextGroup()
    {
        var ordered = GroupsInLayoutOrder;
        if (ordered.Count < 2)
        {
            return;
        }

        var index = IndexOfGroup(ordered, ActiveGroup);
        ActiveGroup = ordered[(index + 1) % ordered.Count];
    }

    public void FocusPreviousGroup()
    {
        var ordered = GroupsInLayoutOrder;
        if (ordered.Count < 2)
        {
            return;
        }

        var index = IndexOfGroup(ordered, ActiveGroup);
        ActiveGroup = ordered[(index - 1 + ordered.Count) % ordered.Count];
    }

    // ===== 布局重置(合并为单组;不删除标签与阅读状态) =====

    public void ResetLayout()
    {
        var ordered = GroupsInLayoutOrder;
        if (ordered.Count <= 1)
        {
            return;
        }

        var primary = ordered[0];
        foreach (var group in ordered.Skip(1))
        {
            foreach (var tab in group.Tabs.ToArray())
            {
                group.Tabs.Remove(tab);
                primary.Tabs.Add(tab);
            }

            _groups.Remove(group.GroupId);
            UntrackGroupTabs(group);
            DropGroupMru(group);
        }

        _root = new EditorGroupNode(primary);
        if (primary.SelectedTab is null && primary.Tabs.Count > 0)
        {
            primary.SelectedTab = primary.Tabs[0];
        }

        ActiveGroup = primary;
        RaiseLayoutChanged();
    }

    /// <summary>恢复默认比例:所有 SplitNode 权重回到均分。</summary>
    public void ResetWeights()
    {
        ResetWeightsCore(ref _root);
        RaiseLayoutChanged();
    }

    private static void ResetWeightsCore(ref EditorLayoutNode node)
    {
        if (node is not EditorSplitNode split)
        {
            return;
        }

        var children = split.Children.ToArray();
        for (var i = 0; i < children.Length; i++)
        {
            var child = children[i];
            ResetWeightsCore(ref child);
            children[i] = child;
        }

        node = split with { Children = children, Weights = SplitWeights(children.Length) };
    }

    // ===== 分割比例 =====

    public static IReadOnlyList<double> SplitWeights(int count)
    {
        var weights = new double[Math.Max(1, count)];
        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] = 1.0 / weights.Length;
        }

        return weights;
    }

    /// <summary>比例钳制:每项落在 [20%, 80%](非法/NaN/越界值修复;不重归一化,
    /// 星形列按比例分配,和为 1 并非必要条件)。</summary>
    public static IReadOnlyList<double> ClampWeights(IReadOnlyList<double> weights)
    {
        if (weights is null || weights.Count == 0)
        {
            return [];
        }

        var clamped = new double[weights.Count];
        for (var i = 0; i < weights.Count; i++)
        {
            clamped[i] = weights[i] switch
            {
                double.NaN or double.PositiveInfinity or double.NegativeInfinity or <= 0 => MinGroupRatio,
                < MinGroupRatio => MinGroupRatio,
                > MaxGroupRatio => MaxGroupRatio,
                _ => weights[i],
            };
        }

        return clamped;
    }

    /// <summary>像素感知钳制:当可用空间不足时保证每个子区 ≥ minPixels(用于窗口缩小时的分割保护)。</summary>
    public static IReadOnlyList<double> ClampWeightsToPixels(IReadOnlyList<double> weights,
        double availablePixels, double minPixels)
    {
        if (weights is null || weights.Count == 0)
        {
            return weights!;
        }

        if (availablePixels <= 0 || minPixels <= 0)
        {
            return ClampWeights(weights);
        }

        var pixelMinimum = minPixels / availablePixels;
        if (weights.Count * pixelMinimum > 1)
        {
            return SplitWeights(weights.Count);
        }

        var floored = weights.Select(weight => Math.Max(weight, pixelMinimum)).ToArray();
        var adjustable = floored.Sum(weight => Math.Max(0, weight - pixelMinimum));
        if (adjustable <= 0)
        {
            return SplitWeights(weights.Count);
        }

        var scale = (1.0 - weights.Count * pixelMinimum) / adjustable;
        var result = new double[weights.Count];
        for (var i = 0; i < weights.Count; i++)
        {
            result[i] = floored[i] <= pixelMinimum + 1e-9
                ? floored[i]
                : pixelMinimum + (floored[i] - pixelMinimum) * scale;
        }

        return result;
    }

    /// <summary>分割拖拽结束后写回比例(先归一化再钳制,不触发结构重建)。</summary>
    public void SetSplitWeights(EditorSplitNode split, IReadOnlyList<double> weights)
    {
        if (split is null || weights is null)
        {
            return;
        }

        var sum = weights.Sum();
        IReadOnlyList<double> normalized = sum is > 0 && double.IsFinite(sum)
            ? weights.Select(weight => weight / sum).ToArray()
            : weights;
        var clamped = ClampWeights(normalized);
        ReplaceSplitNode(ref _root, split, node => node with { Weights = clamped });
    }

    private static void ReplaceSplitNode(ref EditorLayoutNode node, EditorSplitNode target,
        Func<EditorSplitNode, EditorSplitNode> update)
    {
        if (ReferenceEquals(node, target))
        {
            node = update(target);
            return;
        }

        if (node is EditorSplitNode split)
        {
            var children = split.Children.ToArray();
            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i];
                ReplaceSplitNode(ref child, target, update);
                children[i] = child;
            }

            node = split with { Children = children };
        }
    }

    // ===== 序列化:捕获 / 恢复 =====

    public EditorLayoutState CaptureLayout()
    {
        var node = CaptureNode(_root);
        return node with { ActiveGroupId = ActiveGroup?.GroupId, GroupMru = GroupMruKeys };
    }

    private static EditorLayoutState CaptureNode(EditorLayoutNode node) => node switch
    {
        EditorGroupNode group => new EditorLayoutState(
            GroupId: group.Group.GroupId,
            Tabs: group.Group.Tabs.Select(TabState).ToArray(),
            ActiveTabKey: group.Group.SelectedTab?.TabKey,
            Mru: group.Group.MruKeys),
        EditorSplitNode split => new EditorLayoutState(
            Orientation: split.Orientation == EditorSplitOrientation.Vertical
                ? EditorLayoutState.VerticalOrientation
                : EditorLayoutState.HorizontalOrientation,
            Children: split.Children.Select(CaptureNode).ToArray(),
            Weights: split.Weights),
        _ => new EditorLayoutState(),
    };

    private static EditorTabState TabState(EditorTabItem tab) => tab switch
    {
        FilePreviewTab preview => new EditorTabState(
            tab.TabKey, IsPreview: preview.IsPreview, FilePath: preview.Path),
        DiffTab diff => new EditorTabState(
            tab.TabKey,
            RepositoryPath: diff.Request.RepositoryPath,
            DiffPath: diff.Request.Path,
            IsStaged: diff.Request.IsStaged,
            IsUntracked: diff.Request.IsUntracked,
            CommitHash: diff.Request.CommitHash),
        _ => new EditorTabState(tab.TabKey),
    };

    /// <summary>
    /// 用恢复的布局树替换当前布局(v2)。<paramref name="tabFactory"/> 负责把标签描述转成可加载的
    /// 标签(文件不存在 / 无法重建时返回 null 并跳过)。恢复全程校验:非法节点、重复标签、越界比例、
    /// 缺失组 id 与活动组引用都会被修复,并保证至少一个组。标签与组的 MRU 序按持久化键恢复。
    /// </summary>
    public void ReplaceWithRestoredLayout(EditorLayoutState root, Func<EditorTabState, EditorTabItem?> tabFactory)
    {
        ArgumentNullException.ThrowIfNull(tabFactory);
        var seenGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTabKeys = new HashSet<string>(StringComparer.Ordinal);
        var restored = RestoreNode(root, tabFactory, seenGroupIds, seenTabKeys, 0);

        _groups.Clear();
        _groupMru.Clear();
        foreach (var group in LeafGroups(restored))
        {
            _groups[group.GroupId] = group;
            TrackGroupTabs(group);
            SeedGroupMru(group);
        }

        if (_groups.Count == 0)
        {
            var fallback = new EditorGroupViewModel(NewGroupId());
            _groups[fallback.GroupId] = fallback;
            TrackGroupTabs(fallback);
            SeedGroupMru(fallback);
            _root = new EditorGroupNode(fallback);
        }
        else
        {
            _root = restored;
        }

        // 组级 MRU:优先按持久化键序(校验:仅保留现存组),否则按布局序;随后设活动组时活动组被提到最前。
        var requestedGroupMru = root?.GroupMru;
        if (requestedGroupMru is { Count: > 0 })
        {
            var ordered = _groupMru.ToList();
            var priority = new List<EditorGroupViewModel>();
            foreach (var groupId in requestedGroupMru)
            {
                if (FindGroup(groupId) is { } group && !priority.Contains(group))
                {
                    priority.Add(group);
                }
            }

            foreach (var group in ordered.Where(group => !priority.Contains(group)))
            {
                priority.Add(group);
            }

            _groupMru.Clear();
            _groupMru.AddRange(priority);
        }

        var layoutOrder = GroupsInLayoutOrder;
        var activeId = root?.ActiveGroupId;
        ActiveGroup = layoutOrder.FirstOrDefault(group => string.Equals(group.GroupId, activeId, StringComparison.OrdinalIgnoreCase))
            ?? layoutOrder[0];
        RebuildAllTabs(); // V8: 恢复出的组/标签是替换进的新实例,物化缓存必须重建
        RaiseLayoutChanged();
    }

    private static EditorLayoutNode RestoreNode(EditorLayoutState? state,
        Func<EditorTabState, EditorTabItem?> tabFactory,
        HashSet<string> seenGroupIds,
        HashSet<string> seenTabKeys,
        int depth)
    {
        if (state is null || depth > MaxLayoutDepth)
        {
            return new EditorGroupNode(new EditorGroupViewModel(NewGroupId()));
        }

        if (!state.IsSplit)
        {
            return RestoreGroupNode(state, tabFactory, seenGroupIds, seenTabKeys);
        }

        var orientation = string.Equals(state.Orientation, EditorLayoutState.HorizontalOrientation, StringComparison.OrdinalIgnoreCase)
            ? EditorSplitOrientation.Horizontal
            : EditorSplitOrientation.Vertical;
        var rawChildren = state.Children ?? [];
        var children = rawChildren
            .Take(MaxGroupCount)
            .Select(child => RestoreNode(child, tabFactory, seenGroupIds, seenTabKeys, depth + 1))
            .ToArray();

        // 无效节点修复:分割节点至少需要两个子节点,否则退回单组。
        if (children.Length < 2)
        {
            var fallback = children.FirstOrDefault()
                ?? new EditorGroupNode(new EditorGroupViewModel(NewGroupId()));
            return Normalize(fallback);
        }

        var weights = state.Weights is { Count: > 0 } raw
            ? ClampWeights(raw.Take(children.Length).ToArray())
            : SplitWeights(children.Length);
        if (weights.Count != children.Length)
        {
            weights = SplitWeights(children.Length);
        }

        return new EditorSplitNode(orientation, children, weights);
    }

    private static EditorLayoutNode RestoreGroupNode(EditorLayoutState state,
        Func<EditorTabState, EditorTabItem?> tabFactory,
        HashSet<string> seenGroupIds,
        HashSet<string> seenTabKeys)
    {
        var groupId = string.IsNullOrWhiteSpace(state.GroupId) ? NewGroupId() : state.GroupId!;
        if (!seenGroupIds.Add(groupId))
        {
            // 重复组 id:重新生成,保持树的结构完整性。
            groupId = NewGroupId();
        }

        var group = new EditorGroupViewModel(groupId);
        foreach (var tabState in state.Tabs ?? [])
        {
            if (!seenTabKeys.Add(tabState.TabKey))
            {
                continue; // 跨组重复标签:保留第一个。
            }

            if (tabFactory(tabState) is { } tab)
            {
                group.Tabs.Add(tab);
            }
        }

        if (state.ActiveTabKey is { } activeKey && group.FindTab(activeKey) is { } activeTab)
        {
            group.SelectedTab = activeTab;
        }
        else if (group.Tabs.Count > 0)
        {
            group.SelectedTab = group.Tabs[0];
        }

        // 恢复组内标签 MRU(键映射,缺失键跳过);活动标签保证在最前。
        group.RestoreMru(state.Mru, group.SelectedTab);

        return new EditorGroupNode(group);
    }

    /// <summary>v1 迁移:把扁平 RecentTabs(TabKey 列表)恢复为单组;活动标签按 ActiveEditor 选择。</summary>
    public void ReplaceWithSingleGroup(IReadOnlyList<EditorTabItem> tabs, string? activeEditor)
    {
        var group = new EditorGroupViewModel(NewGroupId());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in tabs)
        {
            if (seen.Add(tab.TabKey))
            {
                group.Tabs.Add(tab);
            }
        }

        if (activeEditor is { } active && group.FindTab(active) is { } activeTab)
        {
            group.SelectedTab = activeTab;
        }
        else if (group.Tabs.Count > 0)
        {
            group.SelectedTab = group.Tabs[0];
        }

        _groups.Clear();
        _groupMru.Clear();
        _groups[group.GroupId] = group;
        TrackGroupTabs(group);
        _root = new EditorGroupNode(group);
        ActiveGroup = group;
        RebuildAllTabs(); // V8: 替换组为新实例,物化缓存必须重建
        RaiseLayoutChanged();
    }

    // ===== 树工具 =====

    /// <summary>2×2 角落 Sash 联动检测 (VS Code GridView.trySet2x2):
    /// 根分割恰好两个子节点,且两个子节点都是同向的 2 叶分割(与根方向正交)—— 此时两个内部分割
    /// 的分隔线应联动(拖动其一,另一按相同比例移动)。返回两个内部分割节点。</summary>
    public static bool TryGetLocked2x2(EditorLayoutNode root, out EditorSplitNode first, out EditorSplitNode second)
    {
        first = null!;
        second = null!;
        if (root is not EditorSplitNode outer || outer.Children.Count != 2)
        {
            return false;
        }

        if (outer.Children[0] is not EditorSplitNode a
            || outer.Children[1] is not EditorSplitNode b
            || a.Orientation != b.Orientation
            || a.Orientation == outer.Orientation
            || a.Children.Count != 2
            || b.Children.Count != 2
            || a.Children.Any(child => child is not EditorGroupNode)
            || b.Children.Any(child => child is not EditorGroupNode))
        {
            return false;
        }

        first = a;
        second = b;
        return true;
    }

    private static IEnumerable<EditorGroupViewModel> LeafGroups(EditorLayoutNode node) => node switch
    {
        EditorGroupNode group => [group.Group],
        EditorSplitNode split => split.Children.SelectMany(LeafGroups),
        _ => [],
    };

    private static int IndexOfGroup(IReadOnlyList<EditorGroupViewModel> ordered, EditorGroupViewModel group)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ReferenceEquals(ordered[i], group))
            {
                return i;
            }
        }

        return 0;
    }

    private static EditorLayoutNode CreateSplitAround(EditorLayoutNode leaf, EditorGroupViewModel created,
        EditorSplitOrientation orientation, bool newGroupFirst)
    {
        var weights = SplitWeights(2);
        return newGroupFirst
            ? new EditorSplitNode(orientation, [new EditorGroupNode(created), leaf], weights)
            : new EditorSplitNode(orientation, [leaf, new EditorGroupNode(created)], weights);
    }

    private static EditorLayoutNode ReplaceLeaf(EditorLayoutNode node, EditorGroupViewModel target,
        Func<EditorLayoutNode, EditorLayoutNode> replace)
    {
        if (node is EditorGroupNode groupNode && ReferenceEquals(groupNode.Group, target))
        {
            return replace(node);
        }

        if (node is EditorSplitNode split)
        {
            var children = split.Children.ToArray();
            var changed = false;
            for (var i = 0; i < children.Length; i++)
            {
                var replaced = ReplaceLeaf(children[i], target, replace);
                changed |= !ReferenceEquals(replaced, children[i]);
                children[i] = replaced;
            }

            return changed ? split with { Children = children } : split;
        }

        return node;
    }

    private static EditorLayoutNode? RemoveLeaf(EditorLayoutNode node, EditorGroupViewModel target)
    {
        if (node is EditorGroupNode groupNode)
        {
            return ReferenceEquals(groupNode.Group, target) ? null : node;
        }

        if (node is EditorSplitNode split)
        {
            var originalWeights = split.Weights is { Count: > 0 } raw
                ? raw
                : SplitWeights(split.Children.Count);
            var keptChildren = new List<EditorLayoutNode>();
            var keptWeights = new List<double>();
            for (var i = 0; i < split.Children.Count; i++)
            {
                var child = split.Children[i];
                var removed = RemoveLeaf(child, target);
                if (removed is null)
                {
                    continue;
                }

                keptChildren.Add(removed);
                keptWeights.Add(i < originalWeights.Count ? originalWeights[i] : 1.0 / split.Children.Count);
            }

            if (keptChildren.Count == 0)
            {
                return null;
            }

            // 保留剩余子节点的原比例(重归一化),关闭组不重置兄弟分割的比例。
            var total = keptWeights.Sum();
            var weights = total > 0
                ? keptWeights.Select(weight => weight / total).ToArray()
                : SplitWeights(keptChildren.Count);
            return new EditorSplitNode(split.Orientation, keptChildren, ClampWeights(weights));
        }

        return node;
    }

    /// <summary>整理树:折叠单子节点分割;把同方向的嵌套分割拼接到父级(避免出现多余分割线)。</summary>
    private static EditorLayoutNode Normalize(EditorLayoutNode node)
    {
        if (node is not EditorSplitNode split)
        {
            return node;
        }

        var children = split.Children.Select(Normalize).ToArray();
        if (children.Length == 1)
        {
            return children[0];
        }

        var flatChildren = new List<EditorLayoutNode>();
        var flatWeights = new List<double>();
        for (var i = 0; i < children.Length; i++)
        {
            var child = children[i];
            var share = 1.0 / children.Length;
            if (child is EditorSplitNode nested && nested.Orientation == split.Orientation)
            {
                for (var j = 0; j < nested.Children.Count; j++)
                {
                    flatChildren.Add(nested.Children[j]);
                    var nestedWeights = nested.Weights is { Count: > 0 } ? nested.Weights : SplitWeights(nested.Children.Count);
                    flatWeights.Add(share * (j < nestedWeights.Count ? nestedWeights[j] : 1.0 / nested.Children.Count));
                }
            }
            else
            {
                flatChildren.Add(child);
                flatWeights.Add(share);
            }
        }

        var weights = ClampWeights(flatWeights);
        return new EditorSplitNode(split.Orientation, flatChildren, weights);
    }

    /// <summary>Raised from structural operations so the view rebuilds and the facade persists.</summary>
    internal void RaiseLayoutChanged() => LayoutChanged?.Invoke(this, EventArgs.Empty);

    private static string NewGroupId() => Guid.NewGuid().ToString("N");
}
