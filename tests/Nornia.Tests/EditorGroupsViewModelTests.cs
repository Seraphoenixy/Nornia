using Nornia.Desktop.Configuration;
using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>
/// 编辑器组布局树(GroupNode/SplitNode)与全部结构操作:拆分、移动标签、关闭组并自动合并、
/// 至少保留一个组、焦点切换、比例钳制,以及 v2 布局树序列化/反序列化与非法状态修复。
/// </summary>
public sealed class EditorGroupsViewModelTests
{
    private static EditorTabItem Tab(string path) =>
        new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);

    private static EditorTabItem? RestoredTab(EditorTabState state)
    {
        var path = state.FilePath ?? state.TabKey["file:".Length..];
        return Tab(path);
    }

    // ===== 初始状态 =====

    [Fact]
    public void Create_StartsWithSingleGroup()
    {
        var groups = new EditorGroupsViewModel();

        Assert.Equal(1, groups.GroupCount);
        Assert.False(groups.HasMultipleGroups);
        Assert.NotNull(groups.ActiveGroup);
        Assert.IsType<EditorGroupNode>(groups.Root);
        Assert.Same(groups.ActiveGroup, groups.GroupsInLayoutOrder[0]);
    }

    // ===== 拆分 =====

    [Fact]
    public void SplitActiveGroup_CreatesSecondGroup_MovesActiveTab()
    {
        var groups = new EditorGroupsViewModel();
        var source = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");
        source.Tabs.Add(tabA);
        source.Tabs.Add(tabB);
        source.SelectedTab = tabB;

        groups.SplitActiveGroup(EditorSplitOrientation.Vertical);

        Assert.Equal(2, groups.GroupCount);
        Assert.True(groups.HasMultipleGroups);
        Assert.Same(groups.ActiveGroup, groups.GroupsInLayoutOrder[1]);
        Assert.Contains(groups.ActiveGroup.Tabs, t => ReferenceEquals(t, tabB));
        Assert.DoesNotContain(source.Tabs, t => ReferenceEquals(t, tabB));
        Assert.Single(source.Tabs);
        // 根节点成为垂直分割。
        var split = Assert.IsType<EditorSplitNode>(groups.Root);
        Assert.Equal(EditorSplitOrientation.Vertical, split.Orientation);
        Assert.Equal([0.5, 0.5], split.Weights);
    }

    [Fact]
    public void SplitGroup_HorizontalOrientation()
    {
        var groups = new EditorGroupsViewModel();
        var source = groups.ActiveGroup;

        groups.SplitGroup(source, EditorSplitOrientation.Horizontal);

        var split = Assert.IsType<EditorSplitNode>(groups.Root);
        Assert.Equal(EditorSplitOrientation.Horizontal, split.Orientation);
        Assert.Equal(2, split.Children.Count);
    }

    [Fact]
    public void SplitSameOrientation_AppendsToParentSplit_NoNestedSameOrientation()
    {
        var groups = new EditorGroupsViewModel();
        groups.SplitActiveGroup(EditorSplitOrientation.Vertical); // 2 组
        var second = groups.ActiveGroup;

        groups.SplitActiveGroup(EditorSplitOrientation.Vertical); // 3 组

        var split = Assert.IsType<EditorSplitNode>(groups.Root);
        Assert.Equal(3, split.Children.Count);
        Assert.All(split.Children, child => Assert.IsType<EditorGroupNode>(child));
    }

    [Fact]
    public void SplitDifferentOrientation_NestsSplit()
    {
        var groups = new EditorGroupsViewModel();
        groups.SplitActiveGroup(EditorSplitOrientation.Vertical); // 根垂直分割 2 组
        var left = groups.GroupsInLayoutOrder[0];

        groups.SplitGroup(left, EditorSplitOrientation.Horizontal);

        var root = Assert.IsType<EditorSplitNode>(groups.Root);
        Assert.Equal(EditorSplitOrientation.Vertical, root.Orientation);
        var nested = Assert.IsType<EditorSplitNode>(root.Children[0]);
        Assert.Equal(EditorSplitOrientation.Horizontal, nested.Orientation);
        Assert.Equal(3, groups.GroupCount);
    }

    [Fact]
    public void Split_TabMovesIntoNewGroup_WhenSpecified()
    {
        var groups = new EditorGroupsViewModel();
        var source = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        source.Tabs.Add(tabA);

        var created = groups.SplitGroup(source, EditorSplitOrientation.Vertical, moveTab: tabA);

        Assert.Same(tabA, Assert.Single(created.Tabs));
        Assert.Empty(source.Tabs);
    }

    // ===== 关闭组 / 空组 =====

    [Fact]
    public void CloseGroup_MovesTabsToNeighbour_MergesParentSplit()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        first.Tabs.Add(tabA);
        var second = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: tabA); // tabA 移入 second
        var tabB = Tab(@"C:\b.txt");
        second.Tabs.Add(tabB);
        var third = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null);
        Assert.Equal(3, groups.GroupCount);

        // 关闭第二组:其标签(tabA + tabB)移到布局序中的相邻组(first),父分割收敛。
        var neighbour = groups.CloseGroup(second);

        Assert.Equal(2, groups.GroupCount);
        Assert.Same(neighbour, groups.ActiveGroup);
        Assert.Empty(second.Tabs);
        Assert.Equal(2, neighbour.Tabs.Count);
        Assert.Contains(tabA, neighbour.Tabs);
        Assert.Contains(tabB, neighbour.Tabs);
        var split = Assert.IsType<EditorSplitNode>(groups.Root);
        Assert.Equal(2, split.Children.Count);
        Assert.DoesNotContain(split.Children, child => child is EditorGroupNode g && ReferenceEquals(g.Group, second));
    }

    [Fact]
    public void CloseTab_LastTab_RemovesEmptyGroup()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        var second = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null);
        var tab = Tab(@"C:\b.txt");
        second.Tabs.Add(tab);

        second.Tabs.Remove(tab);
        groups.RemoveEmptyGroup(second);

        Assert.Equal(1, groups.GroupCount);
        Assert.Same(first, groups.ActiveGroup);
        Assert.IsType<EditorGroupNode>(groups.Root);
    }

    [Fact]
    public void RemoveEmptyGroup_KeepsAtLeastOneGroup()
    {
        var groups = new EditorGroupsViewModel();
        var only = groups.ActiveGroup;

        only.Tabs.Clear();
        groups.RemoveEmptyGroup(only);

        Assert.Equal(1, groups.GroupCount);
        Assert.Same(only, groups.ActiveGroup);
    }

    [Fact]
    public void CloseOnlyGroup_KeepsIt()
    {
        var groups = new EditorGroupsViewModel();
        var only = groups.ActiveGroup;
        only.Tabs.Add(Tab(@"C:\a.txt"));

        groups.CloseGroup(only);

        Assert.Equal(1, groups.GroupCount);
        Assert.Same(only, groups.ActiveGroup);
        Assert.Empty(only.Tabs);
        Assert.Null(only.SelectedTab);
    }

    // ===== 移动标签 =====

    [Fact]
    public void MoveTabToGroup_RemovesFromSource_ActivatesTarget()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        first.Tabs.Add(tabA);
        var second = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null);
        var tabB = Tab(@"C:\b.txt");
        second.Tabs.Add(tabB);

        groups.MoveTabToGroup(tabA, second);

        Assert.Empty(first.Tabs);
        Assert.Contains(tabA, second.Tabs);
        Assert.Same(tabA, second.SelectedTab);
        Assert.Same(second, groups.ActiveGroup);
        // 源组为空 → 自动移除。
        Assert.Equal(1, groups.GroupCount);
    }

    [Fact]
    public void MoveTabToSameGroup_IsNoOp()
    {
        var groups = new EditorGroupsViewModel();
        var group = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");
        group.Tabs.Add(tabA);
        group.Tabs.Add(tabB);

        groups.MoveTabToGroup(tabA, group);

        Assert.Equal(2, group.Tabs.Count);
    }

    [Fact]
    public void MoveTabToNewGroup_SplitsTargetAndMovesTab()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        first.Tabs.Add(tabA);
        var second = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null);
        var tabB = Tab(@"C:\b.txt");
        second.Tabs.Add(tabB);
        first.Tabs.Add(Tab(@"C:\c.txt"));

        groups.MoveTabToNewGroup(tabB, second, EditorSplitOrientation.Vertical, newGroupFirst: false);

        // 目标组被拆分,原目标组因标签移空而自动移除 → 剩 first + created 两组。
        Assert.Equal(2, groups.GroupCount);
        Assert.Contains(tabB, groups.ActiveGroup.Tabs);
        Assert.DoesNotContain(tabB, second.Tabs);
        // 新组位于原目标组右侧(垂直分割的第二子节点)。
        var split = Assert.IsType<EditorSplitNode>(groups.Root);
        var secondChild = Assert.IsType<EditorGroupNode>(split.Children[^1]);
        Assert.Contains(tabB, secondChild.Group.Tabs);
        Assert.Same(groups.ActiveGroup, secondChild.Group);
    }

    [Fact]
    public void MoveTabWithinGroup_ReordersTabs()
    {
        var groups = new EditorGroupsViewModel();
        var group = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");
        var tabC = Tab(@"C:\c.txt");
        group.Tabs.Add(tabA);
        group.Tabs.Add(tabB);
        group.Tabs.Add(tabC);

        groups.MoveTabWithinGroup(group, 0, 2);

        Assert.Equal([tabB, tabC, tabA], group.Tabs);
    }

    // ===== 焦点切换 =====

    [Fact]
    public void FocusNextGroup_WrapsAroundLayoutOrder()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        // 逐次拆分最右侧组,得到布局序 [first, second, third]。
        var second = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null);
        var third = groups.SplitGroup(second, EditorSplitOrientation.Vertical, moveTab: null);
        groups.ActiveGroup = first;

        groups.FocusNextGroup();
        Assert.Same(second, groups.ActiveGroup);
        groups.FocusNextGroup();
        Assert.Same(third, groups.ActiveGroup);
        groups.FocusNextGroup();
        Assert.Same(first, groups.ActiveGroup);
    }

    [Fact]
    public void FocusPreviousGroup_WrapsAroundLayoutOrder()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null);
        groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null);
        groups.ActiveGroup = first;

        groups.FocusPreviousGroup();

        Assert.Same(groups.GroupsInLayoutOrder[^1], groups.ActiveGroup);
    }

    // ===== 布局重置 =====

    [Fact]
    public void ResetLayout_MergesAllGroups_KeepsTabOrderAndSelection()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");
        first.Tabs.Add(tabA);
        first.Tabs.Add(tabB);
        first.SelectedTab = tabB;
        var second = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: tabB);
        var tabC = Tab(@"C:\c.txt");
        second.Tabs.Add(tabC);

        groups.ResetLayout();

        Assert.Equal(1, groups.GroupCount);
        Assert.IsType<EditorGroupNode>(groups.Root);
        var merged = groups.ActiveGroup;
        // 合并顺序 = 各组原有顺序(second 内 tabB 先于 tabC)。
        Assert.Equal([tabA, tabB, tabC], merged.Tabs);
    }

    // ===== 比例钳制 =====

    [Fact]
    public void ClampWeights_ClampsTo20_80Percent()
    {
        var clamped = EditorGroupsViewModel.ClampWeights([0.1, 0.5, 0.4]);

        Assert.Equal(0.2, clamped[0], 6);
        Assert.Equal(0.5, clamped[1], 6);
        Assert.Equal(0.4, clamped[2], 6);
    }

    [Fact]
    public void ClampWeights_RepairsNaNAndOutOfRange()
    {
        var clamped = EditorGroupsViewModel.ClampWeights([double.NaN, 0.95, double.PositiveInfinity]);

        Assert.All(clamped, value => Assert.InRange(value, 0.2, 0.8));
        Assert.Equal(0.2, clamped[0], 6);
        Assert.Equal(0.8, clamped[1], 6);
        Assert.Equal(0.2, clamped[2], 6);
    }

    [Fact]
    public void ClampWeightsToPixels_EnforcesMinimumWhenSpaceIsTight()
    {
        var clamped = EditorGroupsViewModel.ClampWeightsToPixels([0.8, 0.2], availablePixels: 250, minPixels: 200);

        // 250px / 2 个组,每组 200px 需求 → 均分 50/50。
        Assert.Equal(0.5, clamped[0], 6);
        Assert.Equal(0.5, clamped[1], 6);
    }

    [Fact]
    public void SetSplitWeights_ClampsWithoutChangingStructure()
    {
        var groups = new EditorGroupsViewModel();
        groups.SplitActiveGroup(EditorSplitOrientation.Vertical);
        var split = (EditorSplitNode)groups.Root;

        groups.SetSplitWeights(split, [0.1, 0.9]);

        var updated = Assert.IsType<EditorSplitNode>(groups.Root);
        Assert.Equal(0.2, updated.Weights[0], 6);
        Assert.Equal(0.8, updated.Weights[1], 6);
        Assert.Equal(2, groups.GroupCount);
    }

    // ===== 序列化:捕获 / 恢复 =====

    [Fact]
    public void CaptureRoundTrip_PreservesStructureWeightsTabsAndActiveGroup()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");
        first.Tabs.Add(tabA);
        first.Tabs.Add(tabB);
        first.SelectedTab = tabB;
        var second = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: tabB); // tabB 移入新组
        var tabC = Tab(@"C:\c.txt");
        second.Tabs.Add(tabC);
        groups.SetSplitWeights((EditorSplitNode)groups.Root, [0.35, 0.65]);

        var captured = groups.CaptureLayout();
        var restored = new EditorGroupsViewModel();
        restored.ReplaceWithRestoredLayout(captured, RestoredTab);

        Assert.Equal(2, restored.GroupCount);
        var restoredRoot = Assert.IsType<EditorSplitNode>(restored.Root);
        Assert.Equal(EditorSplitOrientation.Vertical, restoredRoot.Orientation);
        Assert.Equal(0.35, restoredRoot.Weights[0], 6);
        Assert.Equal(0.65, restoredRoot.Weights[1], 6);
        var groupsOrdered = restored.GroupsInLayoutOrder;
        Assert.Equal(["C:\\a.txt"], groupsOrdered[0].Tabs.Select(t => t.Path));
        Assert.Equal(["C:\\b.txt", "C:\\c.txt"], groupsOrdered[1].Tabs.Select(t => t.Path));
        // 源组拆走标签后的选择回落为 a.txt;新组选中移入的 b.txt。
        Assert.Equal("C:\\a.txt", groupsOrdered[0].SelectedTab?.Path);
        Assert.Equal("C:\\b.txt", groupsOrdered[1].SelectedTab?.Path);
        // 活动组为拆分后的新组(捕获时的活动组)。
        Assert.Same(restored.ActiveGroup, groupsOrdered[1]);
        Assert.Equal(captured.ActiveGroupId, restored.ActiveGroup.GroupId);
    }

    [Fact]
    public void CaptureRoundTrip_StateRecordsSerializeThroughJson()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        first.Tabs.Add(tabA);
        groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: tabA);
        var captured = groups.CaptureLayout();

        var json = System.Text.Json.JsonSerializer.Serialize(captured);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<EditorLayoutState>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal("vertical", roundTripped!.Orientation);
        Assert.Equal(2, roundTripped.Children!.Count);
        Assert.Equal(["file:C:\\a.txt"], roundTripped.Children![1].Tabs!.Select(t => t.TabKey));
        Assert.Equal(captured.ActiveGroupId, roundTripped.ActiveGroupId);
    }

    [Fact]
    public void Restore_InvalidSplitWithSingleChild_CollapsesToGroup()
    {
        var groups = new EditorGroupsViewModel();
        var invalid = new EditorLayoutState(
            Orientation: "vertical",
            Children: [new EditorLayoutState(GroupId: "g1", Tabs: [new EditorTabState("file:C:\\a.txt")])],
            Weights: [1.0]);

        groups.ReplaceWithRestoredLayout(invalid, _ => Tab(@"C:\a.txt"));

        Assert.Equal(1, groups.GroupCount);
        Assert.IsType<EditorGroupNode>(groups.Root);
    }

    [Fact]
    public void Restore_NullOrEmptyRoot_CreatesSingleGroup()
    {
        var groups = new EditorGroupsViewModel();

        groups.ReplaceWithRestoredLayout(null!, _ => Tab(@"C:\a.txt"));

        Assert.Equal(1, groups.GroupCount);
        Assert.NotNull(groups.ActiveGroup);
        Assert.NotNull(groups.ActiveGroup.GroupId);
    }

    [Fact]
    public void Restore_DuplicateTabKeysAcrossGroups_KeepsFirst()
    {
        var groups = new EditorGroupsViewModel();
        var duplicate = new EditorLayoutState(
            Orientation: "vertical",
            Children:
            [
                new EditorLayoutState(GroupId: "g1", Tabs:
                [
                    new EditorTabState("file:C:\\same.txt"),
                    new EditorTabState("file:C:\\a.txt"),
                ]),
                new EditorLayoutState(GroupId: "g2", Tabs: [new EditorTabState("file:C:\\same.txt")]),
            ],
            Weights: [0.5, 0.5]);

        groups.ReplaceWithRestoredLayout(duplicate, RestoredTab);

        var ordered = groups.GroupsInLayoutOrder;
        Assert.Equal(2, groups.GroupCount);
        Assert.Equal(2, ordered[0].Tabs.Count);
        // 重复 TabKey 在第二个组被跳过(保留第一个),第二个组保持为空组。
        Assert.Empty(ordered[1].Tabs);
    }

    [Fact]
    public void Restore_OutOfRangeWeights_Clamped()
    {
        var groups = new EditorGroupsViewModel();
        var state = new EditorLayoutState(
            Orientation: "vertical",
            Children:
            [
                new EditorLayoutState(GroupId: "g1"),
                new EditorLayoutState(GroupId: "g2"),
            ],
            Weights: [0.05, 0.95]);

        groups.ReplaceWithRestoredLayout(state, _ => null);

        var split = Assert.IsType<EditorSplitNode>(groups.Root);
        Assert.Equal(0.2, split.Weights[0], 6);
        Assert.Equal(0.8, split.Weights[1], 6);
    }

    [Fact]
    public void Restore_UnknownOrientation_FallsBackToVertical()
    {
        var groups = new EditorGroupsViewModel();
        var state = new EditorLayoutState(
            Orientation: "diagonal",
            Children: [new EditorLayoutState(GroupId: "g1"), new EditorLayoutState(GroupId: "g2")],
            Weights: [0.5, 0.5]);

        groups.ReplaceWithRestoredLayout(state, _ => null);

        var split = Assert.IsType<EditorSplitNode>(groups.Root);
        Assert.Equal(EditorSplitOrientation.Vertical, split.Orientation);
    }

    [Fact]
    public void Restore_MissingActiveGroupId_FallsBackToFirstGroup()
    {
        var groups = new EditorGroupsViewModel();
        var state = new EditorLayoutState(
            Orientation: "vertical",
            Children: [new EditorLayoutState(GroupId: "g1"), new EditorLayoutState(GroupId: "g2")],
            Weights: [0.5, 0.5],
            ActiveGroupId: "does-not-exist");

        groups.ReplaceWithRestoredLayout(state, _ => null);

        Assert.Same(groups.GroupsInLayoutOrder[0], groups.ActiveGroup);
    }

    [Fact]
    public void Restore_DuplicateGroupIds_AreRegenerated()
    {
        var groups = new EditorGroupsViewModel();
        var state = new EditorLayoutState(
            Orientation: "vertical",
            Children: [new EditorLayoutState(GroupId: "dup"), new EditorLayoutState(GroupId: "dup")],
            Weights: [0.5, 0.5]);

        groups.ReplaceWithRestoredLayout(state, _ => null);

        Assert.Equal(2, groups.GroupCount);
        Assert.NotEqual(groups.GroupsInLayoutOrder[0].GroupId, groups.GroupsInLayoutOrder[1].GroupId);
    }

    [Fact]
    public void Restore_DeepNesting_IsCapped()
    {
        // 构造 10 层嵌套分割(超过 MaxLayoutDepth=8):恢复后仍能得到至少一个组,不递归爆炸。
        EditorLayoutState state = new(GroupId: "bottom");
        for (var i = 0; i < 10; i++)
        {
            state = new EditorLayoutState(
                Orientation: "vertical", Children: [state, new EditorLayoutState(GroupId: $"g{i}")], Weights: [0.5, 0.5]);
        }

        var groups = new EditorGroupsViewModel();
        groups.ReplaceWithRestoredLayout(state, _ => null);

        Assert.True(groups.GroupCount >= 1);
        Assert.NotNull(groups.ActiveGroup);
    }

    // ===== v1 迁移 =====

    [Fact]
    public void ReplaceWithSingleGroup_FromLegacyTabs_SelectsActiveEditor()
    {
        var groups = new EditorGroupsViewModel();
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");

        groups.ReplaceWithSingleGroup([tabA, tabB, tabA], "file:C:\\b.txt");

        Assert.Equal(1, groups.GroupCount);
        var group = groups.ActiveGroup;
        Assert.Equal([tabA, tabB], group.Tabs);
        Assert.Equal(tabB, group.SelectedTab);
    }

    // ===== MRU(最近使用)语义 =====

    [Fact]
    public void EvictLeastRecentlyUsed_EvictsLeastRecentlyUsed_NotTheOldest()
    {
        var group = new EditorGroupViewModel("g1");
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");
        var tabC = Tab(@"C:\c.txt");
        group.Tabs.Add(tabA);
        group.Tabs.Add(tabB);
        group.Tabs.Add(tabC);
        group.SelectedTab = tabA; // MRU: [A]
        group.SelectedTab = tabB; // MRU: [B, A]
        group.SelectedTab = tabC; // MRU: [C, B, A]

        // 最早打开的 A 同时也是 MRU 尾部 → 驱逐的是 A;但若把 A 重新激活,则驱逐 B。
        Assert.Same(tabA, group.EvictLeastRecentlyUsed());

        group.SelectedTab = tabA; // MRU: [A, C, B]
        Assert.Same(tabB, group.EvictLeastRecentlyUsed());
        // 当前标签(C)永不被驱逐。
        group.SelectedTab = tabC;
        Assert.NotSame(tabC, group.EvictLeastRecentlyUsed());
    }

    [Fact]
    public void EvictLeastRecentlyUsed_SkipsPinnedTabs()
    {
        var group = new EditorGroupViewModel("g1");
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");
        var tabC = Tab(@"C:\c.txt");
        group.Tabs.Add(tabA);
        group.Tabs.Add(tabB);
        group.Tabs.Add(tabC);
        group.SelectedTab = tabA; // MRU: [A]
        group.SelectedTab = tabB; // MRU: [B, A]
        group.SelectedTab = tabC; // MRU: [C, B, A]

        // A 是 MRU 尾部(本应最先被驱逐),固定标签豁免标签上限 → 改驱逐 B。
        tabA.IsPinned = true;
        Assert.Same(tabB, group.EvictLeastRecentlyUsed());

        // 其余候选全部固定、只剩当前标签时:不做驱逐。
        tabB.IsPinned = true;
        Assert.Null(group.EvictLeastRecentlyUsed());
    }

    [Fact]
    public void SelectNextRecentlyActive_AfterClose_PicksMostRecentlyUsedRemaining()
    {
        var group = new EditorGroupViewModel("g1");
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");
        var tabC = Tab(@"C:\c.txt");
        group.Tabs.Add(tabA);
        group.Tabs.Add(tabB);
        group.Tabs.Add(tabC);
        group.SelectedTab = tabA;
        group.SelectedTab = tabB; // MRU: [B, A, C]

        // 关闭正在使用的 B → 回选最近使用的 A(而非标签条邻居 C)。
        group.Tabs.Remove(tabB);
        Assert.Same(tabA, group.SelectNextRecentlyActive());
    }

    [Fact]
    public void RemoveEmptyGroup_ActivatesMostRecentlyActiveGroup_WhenActiveRemoved()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;                 // 创建序 [first, second, third]
        var second = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null);
        var third = groups.SplitGroup(second, EditorSplitOrientation.Vertical, moveTab: null);
        Assert.Equal([first, second, third], groups.GroupsInLayoutOrder);

        // 构造组级 MRU [third, first, second](third 最近活动,first 次之)。
        groups.ActiveGroup = first;
        groups.ActiveGroup = third;
        third.Tabs.Clear();                             // third 为空

        groups.RemoveEmptyGroup(third);

        // 移除的是活动组 → 按组级 MRU 激活"次最近活动"组(first),而非布局邻居 second (VS Code 语义)。
        Assert.Equal(2, groups.GroupCount);
        Assert.Same(first, groups.ActiveGroup);
    }

    [Fact]
    public void CaptureAndRestore_PreservesTabMruAndGroupMru()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        var tabA = Tab(@"C:\a.txt");
        var tabB = Tab(@"C:\b.txt");
        first.Tabs.Add(tabA);
        first.Tabs.Add(tabB);
        first.SelectedTab = tabA;
        first.SelectedTab = tabB; // 组 A 标签 MRU: [b, a]
        var second = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null);
        second.Tabs.Add(Tab(@"C:\c.txt"));
        groups.ActiveGroup = first;                     // 组级 MRU: [first, second]

        var captured = groups.CaptureLayout();
        var restored = new EditorGroupsViewModel();
        restored.ReplaceWithRestoredLayout(captured, RestoredTab);

        var restoredFirst = restored.GroupsInLayoutOrder[0];
        Assert.Equal(["file:C:\\b.txt", "file:C:\\a.txt"], captured.Children![0].Mru);
        Assert.Equal(["file:C:\\b.txt", "file:C:\\a.txt"], restoredFirst.MruKeys);
        Assert.Equal(groups.GroupMruKeys, captured.GroupMru);
        // 恢复后的组级 MRU 按持久化键序(活动组在最前)。
        Assert.Equal(restored.GroupMruKeys, restored.GroupsInMruOrder.Select(g => g.GroupId));
        Assert.Same(restoredFirst, restored.ActiveGroup);
    }

    [Fact]
    public void Restore_GroupMruWithStaleIds_FallsBackToActiveFirstLayoutOrder()
    {
        var groups = new EditorGroupsViewModel();
        var state = new EditorLayoutState(
            Orientation: "vertical",
            Children: [new EditorLayoutState(GroupId: "g1"), new EditorLayoutState(GroupId: "g2")],
            Weights: [0.5, 0.5],
            ActiveGroupId: "g2",
            GroupMru: ["stale-1", "stale-2"]);

        groups.ReplaceWithRestoredLayout(state, _ => null);

        // 持久化的组级 MRU 全部失效 → 回退为布局序,活动组(g2)位于最前。
        Assert.Equal(["g2", "g1"], groups.GroupMruKeys);
        Assert.Equal(groups.GroupMruKeys, groups.GroupsInMruOrder.Select(g => g.GroupId));
    }

    [Fact]
    public void Restore_TabMruWithStaleKeys_KeepsValidOrderAndActiveFirst()
    {
        var groups = new EditorGroupsViewModel();
        var state = new EditorLayoutState(GroupId: "g1", Tabs:
        [
            new EditorTabState("file:C:\\a.txt"),
            new EditorTabState("file:C:\\b.txt"),
        ],
            ActiveTabKey: "file:C:\\b.txt",
            Mru: ["file:C:\\missing.txt", "file:C:\\a.txt", "file:C:\\b.txt"]);

        groups.ReplaceWithRestoredLayout(state, RestoredTab);

        var restored = groups.ActiveGroup;
        // 缺失键跳过;活动标签 b 提到最前 → [b, a]。
        Assert.Equal(["file:C:\\b.txt", "file:C:\\a.txt"], restored.MruKeys);
    }

    // ===== 2×2 角落 Sash 联动检测 =====

    [Fact]
    public void TryGetLocked2x2_DetectsTrue2x2Matrix()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        var right = groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null); // 根垂直 [first, right]
        groups.SplitGroup(first, EditorSplitOrientation.Horizontal, moveTab: null);            // first → 上下 2 叶
        groups.SplitGroup(right, EditorSplitOrientation.Horizontal, moveTab: null);            // right → 上下 2 叶

        Assert.True(EditorGroupsViewModel.TryGetLocked2x2(groups.Root, out var a, out var b));
        Assert.Equal(EditorSplitOrientation.Horizontal, a.Orientation);
        Assert.Equal(EditorSplitOrientation.Horizontal, b.Orientation);
        Assert.Equal(2, a.Children.Count);
        Assert.Equal(2, b.Children.Count);
        Assert.Equal(4, groups.GroupCount);
    }

    [Fact]
    public void TryGetLocked2x2_ReturnsFalseForNonMatrixLayouts()
    {
        var groups = new EditorGroupsViewModel();
        var first = groups.ActiveGroup;
        groups.SplitGroup(first, EditorSplitOrientation.Vertical, moveTab: null); // 2 组:非 2×2
        Assert.False(EditorGroupsViewModel.TryGetLocked2x2(groups.Root, out _, out _));

        // 3 组(垂 3 叶):不联动。
        var second = groups.GroupsInLayoutOrder[1];
        groups.SplitGroup(second, EditorSplitOrientation.Vertical, moveTab: null);
        Assert.False(EditorGroupsViewModel.TryGetLocked2x2(groups.Root, out _, out _));

        // 单组根:不联动。
        groups.ResetLayout();
        Assert.False(EditorGroupsViewModel.TryGetLocked2x2(groups.Root, out _, out _));
    }
}