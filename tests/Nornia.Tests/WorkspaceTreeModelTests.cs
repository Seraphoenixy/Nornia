using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>V4 完整模型护栏:每节点可见行数缓存(VisibleRowCount)与扁平下标导出
/// (TryGetRowSpan)必须与真实扁平行序列逐节点一致;展开/折叠经 ToggleTreeFolderCommand
/// 走 O(Δ) 区间拼接(计数器),整表重建只在契约回退时发生。</summary>
public sealed class WorkspaceTreeModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nornia-tree-model-{Guid.NewGuid():N}");

    public WorkspaceTreeModelTests()
    {
        foreach (var dir in new[] { "src/sub", "src/plain", "notes", "docs/guide" })
        {
            Directory.CreateDirectory(Path.Combine(_root, dir));
        }

        foreach (var file in new[]
        {
            "src/a.cs", "src/sub/b.cs", "src/sub/c.cs", "src/plain/p.cs",
            "notes/todo.txt", "docs/guide/g.md", "readme.md",
        })
        {
            File.WriteAllText(Path.Combine(_root, file), "x");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<WorkspaceViewModel> CreateAsync()
    {
        var viewModel = new WorkspaceViewModel(
            new FakeFolderPicker(), new FakeSettingsService(), new FakeProjectWorkspaceService(),
            new FakeUiLogService(), new FakeClipboardService());
        await viewModel.OpenWorkspaceAsync(_root);
        return viewModel;
    }

    private static async Task ExpandAsync(WorkspaceNode node)
    {
        node.IsExpanded = true;
        await node.LoadChildrenAsync();
    }

    private static WorkspaceNode? Find(WorkspaceNode root, string relativePath)
    {
        if (string.Equals(root.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)) return root;
        foreach (var child in root.Children)
        {
            if (Find(child, relativePath) is { } match) return match;
        }

        return null;
    }

    private static bool IsDescendantOf(WorkspaceNode node, WorkspaceNode ancestor)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }

        return false;
    }

    private static WorkspaceFolderRow? FolderRow(WorkspaceViewModel viewModel, WorkspaceNode node) =>
        viewModel.TreeRowsSnapshot().OfType<WorkspaceFolderRow>().FirstOrDefault(row => ReferenceEquals(row.Node, node));

    /// <summary>逐节点校验 TryGetRowSpan == 扁平行序列中的真实区间,且 count 与
    /// VisibleRowCount 一致(完整"可见行数 + 扁平下标"模型的一致性锚点)。</summary>
    [Fact]
    public async Task TryGetRowSpan_MatchesFlatSequence_ForEveryLoadedNode()
    {
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        await ExpandAsync(Find(root, "src")!);
        await ExpandAsync(Find(root, "src/sub")!);
        await ExpandAsync(Find(root, "src/plain")!);
        await ExpandAsync(Find(root, "notes")!);
        await ExpandAsync(Find(root, "docs")!);
        await ExpandAsync(Find(root, "docs/guide")!);
        viewModel.SyncTreeRowsForTest();

        var rows = viewModel.TreeRowsSnapshot();
        var checkedNodes = 0;
        void VerifyNode(WorkspaceNode node)
        {
            if (node.IsPlaceholder) return;
            checkedNodes++;

            Assert.True(viewModel.TryGetRowSpan(node, out var start, out var count), $"{node.RelativePath} 无法导出区间");
            var expectedStart = -1;
            for (var i = 0; i < rows.Length; i++)
            {
                if (ReferenceEquals(rows[i].Node, node))
                {
                    expectedStart = i;
                    break;
                }
            }

            var __dump = string.Join("\n", rows.Select(r => $"{r.Node.RelativePath}"));
            Assert.True(expectedStart >= 0, $"{node.RelativePath} 不在行序列\n{__dump}");
            Assert.Equal(expectedStart, start);
            Assert.Equal(node.VisibleRowCount, count);
            for (var i = start; i < start + count; i++)
            {
                Assert.True(IsDescendantOf(rows[i].Node, node), $"{node.RelativePath} 区间越界 #{i}");
            }

            if (start + count < rows.Length)
            {
                Assert.False(IsDescendantOf(rows[start + count].Node, node),
                    $"{node.RelativePath} 区间后沿未闭合");
            }

            foreach (var child in node.Children) VerifyNode(child);
        }

        VerifyNode(root);
        Assert.True(checkedNodes >= 8, $"应覆盖全部加载节点,实际 {checkedNodes}");
    }

    [Fact]
    public async Task Expand_Collapse_SpliceOnlyDeltaRows_NoFullRebuild()
    {
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        await ExpandAsync(Find(root, "src")!);
        await ExpandAsync(Find(root, "src/sub")!);
        await ExpandAsync(Find(root, "src/plain")!);
        await ExpandAsync(Find(root, "notes")!);
        await ExpandAsync(Find(root, "docs")!);
        await ExpandAsync(Find(root, "docs/guide")!);
        viewModel.SyncTreeRowsForTest();
        var rowsBefore = viewModel.TreeRowsSnapshot().Length;
        var fullBefore = viewModel.FullSyncCount;

        var src = Find(root, "src")!;
        var srcRow = FolderRow(viewModel, src)!;
        Assert.True(viewModel.TryGetRowSpan(src, out var startBefore, out var countBefore));

        // 折叠:O(Δ) 区间拼接,不整表重建;行数按子树叶减一。
        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow);
        Assert.Equal(0, viewModel.FullSyncCount - fullBefore);
        Assert.True(viewModel.IncrementalSpliceCount > 0);
        Assert.True(viewModel.TryGetRowSpan(src, out var startAfter, out var countAfter));
        Assert.Equal(startBefore, startAfter);
        Assert.True(countAfter < countBefore);
        Assert.Equal(src.VisibleRowCount, countAfter);
        Assert.Equal(rowsBefore - (countBefore - 1), viewModel.TreeRowsSnapshot().Length);

        // 再展开(已加载):恢复全部行,依然不整表重建。
        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow);
        Assert.Equal(0, viewModel.FullSyncCount - fullBefore);
        Assert.Equal(rowsBefore, viewModel.TreeRowsSnapshot().Length);
        Assert.True(viewModel.TryGetRowSpan(src, out _, out var countRebuilt));
        Assert.Equal(countBefore, countRebuilt);
    }

    [Fact]
    public async Task SiblingStructureOffsets_StayConsistentAfterSplices()
    {
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        await ExpandAsync(Find(root, "src")!);
        await ExpandAsync(Find(root, "src/sub")!);
        await ExpandAsync(Find(root, "notes")!);
        viewModel.SyncTreeRowsForTest();

        // 折叠 src/sub 后,排序上位于其后的兄弟行(src 子节点按 目录→文件、字母序,
        // a.cs 恒在子目录之后)必须按新投影移动 -(subCount-1) 行。
        var sub = Find(root, "src/sub")!;
        var afterSibling = Find(root, "src/a.cs")!;
        Assert.True(viewModel.TryGetRowSpan(sub, out var subStart, out var subCount));
        var siblingBefore = viewModel.TreeRowsSnapshot().ToList().FindIndex(row => ReferenceEquals(row.Node, afterSibling));

        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(FolderRow(viewModel, sub)!);
        Assert.True(viewModel.TryGetRowSpan(sub, out var subStartAfter, out _));
        Assert.Equal(subStart, subStartAfter);
        var siblingAfter = viewModel.TreeRowsSnapshot().ToList().FindIndex(row => ReferenceEquals(row.Node, afterSibling));
        Assert.Equal(siblingBefore - (subCount - 1), siblingAfter); // 折叠后移去了 subCount-1 行

        // 折叠后全树 TryGetRowSpan 与行序列仍逐节点一致(拼接不破坏下标模型)。
        viewModel.SyncTreeRowsForTest();
        var rows = viewModel.TreeRowsSnapshot();
        void VerifyNode(WorkspaceNode node)
        {
            if (node.IsPlaceholder) return;
            if (!viewModel.TryGetRowSpan(node, out var start, out var count))
            {
                return; // 紧凑链中间成员无行(未开启紧凑时不可能出现)
            }

            Assert.True(rows[start].Node == node, node.RelativePath);
            for (var i = start; i < start + count; i++)
            {
                Assert.True(IsDescendantOf(rows[i].Node, node), $"{node.RelativePath} 区间越界 #{i}");
            }

            foreach (var child in node.Children) VerifyNode(child);
        }

        VerifyNode(root);
    }
}

/// <summary>测试专用导出:直接调用 VM 内部成员(InternalsVisibleTo 已开启)。</summary>
internal static class WorkspaceTreeModelTestExports
{
    internal static void SyncTreeRowsForTest(this WorkspaceViewModel viewModel) => viewModel.SyncTreeRowsForTest();
}