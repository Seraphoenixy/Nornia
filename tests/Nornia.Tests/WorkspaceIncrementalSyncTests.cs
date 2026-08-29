using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Covers the incremental row projection of the explorer (P0-1): row objects are reused
/// across syncs, unaffected rows keep their identity, selection survives sibling changes, removed
/// rows detach from their node, and RevealNodeAsync raises an explicit reveal request.</summary>
public sealed class WorkspaceIncrementalSyncTests : IDisposable
{
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), $"nornia-incremental-{Guid.NewGuid():N}");

    public WorkspaceIncrementalSyncTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspacePath, "src"));
        Directory.CreateDirectory(Path.Combine(_workspacePath, "notes"));
        File.WriteAllText(Path.Combine(_workspacePath, "src", "A.cs"), "a");
        File.WriteAllText(Path.Combine(_workspacePath, "src", "B.cs"), "b");
        File.WriteAllText(Path.Combine(_workspacePath, "notes", "todo.txt"), "todo");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private async Task<WorkspaceViewModel> CreateAsync()
    {
        var viewModel = new WorkspaceViewModel(new FakeFolderPicker(), new FakeSettingsService(), new FakeProjectWorkspaceService(), new FakeUiLogService(), new FakeClipboardService());
        await viewModel.OpenWorkspaceAsync(_workspacePath);
        return viewModel;
    }

    private static WorkspaceFolderRow FolderRow(WorkspaceViewModel vm, string name) =>
        vm.WorkspaceTreeRows.OfType<WorkspaceFolderRow>().First(row => row.Node.Name == name);

    [Fact]
    public async Task ExpandFolder_ReusesUnaffectedRowObjectsAndRaisesRowsChanged()
    {
        var viewModel = await CreateAsync();
        var rootRow = viewModel.WorkspaceTreeRows[0];
        var srcRow = FolderRow(viewModel, "src");
        var notifications = 0;
        viewModel.TreeRowsChanged += (_, _) => notifications++;

        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow);

        Assert.Equal(5, viewModel.WorkspaceTreeRows.Count); // root + notes + src + A.cs + B.cs
        Assert.Same(rootRow, viewModel.WorkspaceTreeRows[0]); // unaffected row keeps its object
        // Expanded row is reused (same instance), with its children projected right after it.
        var srcIndex = viewModel.WorkspaceTreeRows.IndexOf(srcRow);
        Assert.True(srcIndex > 0);
        Assert.Equal("A.cs", viewModel.WorkspaceTreeRows[srcIndex + 1].Node.Name);
        Assert.True(notifications > 0);
    }

    [Fact]
    public async Task CollapseAfterExpand_RemovesRowsAndDetachesThem()
    {
        var viewModel = await CreateAsync();
        var srcRow = FolderRow(viewModel, "src");

        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow); // expand
        var aRow = viewModel.WorkspaceTreeRows.OfType<WorkspaceFileRow>().First(row => row.Node.Name == "A.cs");

        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow); // collapse

        Assert.Equal(3, viewModel.WorkspaceTreeRows.Count);
        Assert.DoesNotContain(viewModel.WorkspaceTreeRows, row => ReferenceEquals(row, aRow));

        // A removed row is detached: later decoration changes on its node no longer raise its
        // PropertyChanged (the old full-rebuild leaked those subscriptions on every rebuild).
        var changes = 0;
        aRow.PropertyChanged += (_, _) => changes++;
        viewModel.ApplyGitStatus(new GitRepositoryStatus(
            true, "main", "origin/main", 0, 0,
            [new GitFileChange("src/A.cs", GitChangeStatus.Added, GitChangeStatus.Unmodified)],
            []));
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ExpandingSibling_PreservesSelectionOnSurvivingFileRow()
    {
        var viewModel = await CreateAsync();
        var srcRow = FolderRow(viewModel, "src");
        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow);
        var aRow = viewModel.WorkspaceTreeRows.OfType<WorkspaceFileRow>().First(row => row.Node.Name == "A.cs");

        // 文件行选区由 ViewModel 持有(文件夹行选区是视图态,选中即回清)。
        viewModel.SelectedTreeRow = aRow;
        var notesRow = FolderRow(viewModel, "notes");

        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(notesRow);

        Assert.Same(aRow, viewModel.SelectedTreeRow); // 展开兄弟目录不扰动存活行的选区
        Assert.Contains(viewModel.WorkspaceTreeRows, row => ReferenceEquals(row, aRow));
    }

    [Fact]
    public async Task RevealNodeAsync_RaisesRevealRequestForTheSurvivingRowObject()
    {
        var viewModel = await CreateAsync();
        WorkspaceTreeRow? revealed = null;
        viewModel.RevealRowRequested += row => revealed = row;

        var ok = await viewModel.RevealNodeAsync("src/A.cs");

        Assert.True(ok);
        Assert.NotNull(revealed);
        Assert.Equal("A.cs", revealed!.Node.Name);
        // The revealed row is the exact object now in the projection (row reuse).
        Assert.Same(revealed, viewModel.WorkspaceTreeRows.Single(row => row.Node.Name == "A.cs"));
    }

    [Fact]
    public async Task ExpandAll_ParallelExpandsWholeTreeAndProjectsRows()
    {
        var deep = Path.Combine(_workspacePath, "deep", "deeper");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "leaf.txt"), "x");
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        await root.LoadChildrenAsync();
        var deepRoot = root.Children.First(child => child.Name == "deep");

        await viewModel.ExpandAllCommand.ExecuteAsync(deepRoot);

        Assert.True(deepRoot.IsExpanded);
        var deeper = deepRoot.Children.Single(child => child.Name == "deeper");
        Assert.True(deeper.IsExpanded);
        // The fully-expanded subtree is projected as flat rows.
        Assert.Contains(viewModel.WorkspaceTreeRows, row => row.Node.Name == "leaf.txt");
    }

    [Fact]
    public async Task Refresh_ReplacesRowsButKeepsTheCollectionIncremental()
    {
        var viewModel = await CreateAsync();
        var srcRow = FolderRow(viewModel, "src");
        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow);
        var aRow = viewModel.WorkspaceTreeRows.OfType<WorkspaceFileRow>().First(row => row.Node.Name == "A.cs");
        var rootRow = viewModel.WorkspaceTreeRows[0];

        await viewModel.RefreshCommand.ExecuteAsync(null);

        // Refresh drops and reloads nodes: the root node is the same instance, so its row survives.
        Assert.Same(rootRow, viewModel.WorkspaceTreeRows[0]);
        // Dropped file rows must not linger in the projection.
        Assert.DoesNotContain(viewModel.WorkspaceTreeRows, row => ReferenceEquals(row, aRow));
    }

    [Fact]
    public async Task Refresh_ResetsExpansionState_FirstChevronClickExpandsInOneClick()
    {
        var viewModel = await CreateAsync();
        var srcRow = FolderRow(viewModel, "src");
        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow); // expand
        Assert.Contains(viewModel.WorkspaceTreeRows, row => row.Node.Name == "A.cs");

        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 刷新后子节点是新实例:展开态必须同步重置(RefreshAsync 契约 "Expansion state resets for
        // the refreshed subtree"),且 chevron 绑定(行 IsExpanded)与投影一致——两者都显示折叠。
        // 旧行为遗留 IsExpanded=true 但无子行,第一次点击只翻转空展开(delta=0,无可见变化),
        // 用户需点两次才真正展开。
        var srcNode = viewModel.RootNodes[0].Children.Single(child => child.Name == "src");
        Assert.False(srcNode.IsExpanded);
        var srcRowAfterRefresh = FolderRow(viewModel, "src");
        Assert.False(srcRowAfterRefresh.IsExpanded);
        Assert.DoesNotContain(viewModel.WorkspaceTreeRows, row => row.Node.Name == "A.cs");

        // 刷新后的第一次点击必须直接展开(加载 + 投影),无需第二次点击。
        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRowAfterRefresh);

        Assert.True(srcNode.IsExpanded);
        Assert.Contains(viewModel.WorkspaceTreeRows, row => row.Node.Name == "A.cs");
        Assert.Contains(viewModel.WorkspaceTreeRows, row => row.Node.Name == "B.cs");
    }

    [Fact]
    public async Task StaleExpandedButUnloadedState_SingleClickLoadsAndProjects()
    {
        var viewModel = await CreateAsync();
        var notesNode = viewModel.RootNodes[0].Children.Single(child => child.Name == "notes");
        var notesRow = FolderRow(viewModel, "notes");

        // 直接构造旧刷新遗留的不一致状态:chevron 显示已展开但子树未加载。先真实展开并等加载
        // 完成,再把 IsLoaded 拨回 false(等价于旧 DropChildren:丢弃子树 + IsLoaded=false 但
        // 不重置 IsExpanded;此时无在途加载,与生产遗留状态同构)。
        notesNode.IsExpanded = true;
        await notesNode.LoadChildrenAsync();
        notesNode.IsLoaded = false;

        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(notesRow);

        // 防御兜底应把这次点击变成真实展开:加载 + 重投影,而不是停留在"首次点击无效"。
        Assert.True(notesNode.IsExpanded);
        Assert.True(notesNode.IsLoaded);
        Assert.Contains(viewModel.WorkspaceTreeRows, row => row.Node.Name == "todo.txt");
    }

    [Fact]
    public async Task ToggleWhileLoading_AllowsFollowUpClickInsteadOfSilentSwallow()
    {
        var viewModel = await CreateAsync();
        var srcRow = FolderRow(viewModel, "src");

        // 第一次点击开始冷展开:命令在 await 磁盘枚举期间保持运行。AsyncRelayCommand 默认
        // allowConcurrentExecutions=false → 运行中 CanExecute=false,用户在枚举窗口内的补点
        // 会被静默吞掉(表现为"点两次才生效")。此断言钉住"窗口内仍可执行"。
        var first = viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow);
        Assert.True(viewModel.ToggleTreeFolderCommand.CanExecute(srcRow));

        // 窗口内补点(折叠):按正常切换语义处理,而不是被吞。
        var second = viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow);
        await Task.WhenAll(first, second);

        var srcNode = viewModel.RootNodes[0].Children.Single(child => child.Name == "src");
        Assert.True(srcNode.IsLoaded); // 在途枚举仍完成,子树可用
        Assert.False(srcNode.IsExpanded); // 两次点击 = 展开+折叠,净效果折叠(标准双击语义)
        // 在途展开落空不投影幽灵子行:折叠态下无 A.cs 行。
        Assert.DoesNotContain(viewModel.WorkspaceTreeRows, row => row.Node.Name == "A.cs");
    }
}
