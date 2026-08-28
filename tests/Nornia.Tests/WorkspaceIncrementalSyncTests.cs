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
}
