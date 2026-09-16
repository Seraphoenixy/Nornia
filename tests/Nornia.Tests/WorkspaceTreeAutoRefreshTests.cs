using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Covers the explorer tree auto-refresh: structural file changes (create / delete /
/// rename) from the workspace watcher update the visible rows without a manual refresh, unloaded
/// folders stay lazy, and irrelevant churn (git metadata, name-ignored directories, excluded
/// files) never triggers a single directory re-enumeration.</summary>
public sealed class WorkspaceTreeAutoRefreshTests : IDisposable
{
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), $"nornia-tree-auto-{Guid.NewGuid():N}");

    public WorkspaceTreeAutoRefreshTests()
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

    private async Task<(WorkspaceViewModel ViewModel, FakeWorkspaceFileWatcher Watcher)> CreateAsync()
    {
        var watcher = new FakeWorkspaceFileWatcher();
        var viewModel = new WorkspaceViewModel(new FakeFolderPicker(), new FakeSettingsService(),
            new FakeProjectWorkspaceService(), new FakeUiLogService(), new FakeClipboardService(), watcher);
        await viewModel.OpenWorkspaceAsync(_workspacePath);
        return (viewModel, watcher);
    }

    private static WorkspaceFolderRow FolderRow(WorkspaceViewModel vm, string name) =>
        vm.WorkspaceTreeRows.OfType<WorkspaceFolderRow>().First(row => row.Node.Name == name);

    /// <summary>轮询等待条件成立。测试进程无 UI 消息泵:fire-and-forget 的树刷新续延可能在线程
    /// 池执行,轮询枚举活集合的瞬间恰逢行拼接会抛 InvalidOperationException——这是竞态而非
    /// 结果,重试即可(生产环境刷新在 UI 线程,不存在此交错)。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // 集合正在被增量拼接;稍后重试。
            }

            await Task.Delay(15);
        }

        Assert.True(condition(), failure);
    }

    [Fact]
    public async Task OpenWorkspace_AttachesWatcherToTheRoot()
    {
        var (_, watcher) = await CreateAsync();

        Assert.Contains(watcher.AttachedPaths, p =>
            string.Equals(Path.GetFullPath(p), Path.GetFullPath(_workspacePath), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FileCreatedInLoadedFolder_AppearsInTreeWithoutManualRefresh()
    {
        var (viewModel, watcher) = await CreateAsync();
        var srcRow = FolderRow(viewModel, "src");
        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow); // expand src (A.cs + B.cs visible)

        File.WriteAllText(Path.Combine(_workspacePath, "src", "C.cs"), "c");
        watcher.RaiseChanged(_workspacePath, Path.Combine(_workspacePath, "src", "C.cs"));

        await WaitUntilAsync(
            () => viewModel.WorkspaceTreeRows.OfType<WorkspaceFileRow>().Any(row => row.Node.Name == "C.cs"),
            "新建文件未自动出现在树中");
        Assert.True(viewModel.TreeAutoRefreshRuns >= 1);
    }

    [Fact]
    public async Task FileDeleted_RowRemovedWithoutManualRefresh()
    {
        var (viewModel, watcher) = await CreateAsync();
        var srcRow = FolderRow(viewModel, "src");
        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow); // expand src

        File.Delete(Path.Combine(_workspacePath, "src", "A.cs"));
        watcher.RaiseChanged(_workspacePath, Path.Combine(_workspacePath, "src", "A.cs"));

        await WaitUntilAsync(
            () => !viewModel.WorkspaceTreeRows.OfType<WorkspaceFileRow>().Any(row => row.Node.Name == "A.cs"),
            "已删除文件的行未自动移除");
    }

    [Fact]
    public async Task NewTopLevelFolder_AppearsInTreeWithoutManualRefresh()
    {
        var (viewModel, watcher) = await CreateAsync();

        Directory.CreateDirectory(Path.Combine(_workspacePath, "newdir"));
        watcher.RaiseChanged(_workspacePath, Path.Combine(_workspacePath, "newdir"));

        await WaitUntilAsync(
            () => viewModel.WorkspaceTreeRows.OfType<WorkspaceFolderRow>().Any(row => row.Node.Name == "newdir"),
            "新建顶层目录未自动出现在树中");
    }

    [Fact]
    public async Task ChangeInsideUnloadedFolder_DoesNotForceLoadButReflectsOnNextExpand()
    {
        var (viewModel, watcher) = await CreateAsync();
        var srcRow = FolderRow(viewModel, "src");
        var srcNode = srcRow.Node;
        Assert.False(srcNode.IsLoaded); // src is collapsed / never expanded

        File.WriteAllText(Path.Combine(_workspacePath, "src", "Hidden.cs"), "h");
        watcher.RaiseChanged(_workspacePath, Path.Combine(_workspacePath, "src", "Hidden.cs"));

        // 未加载目录不提前枚举(惰性语义保持);刷新不得强制展开它。
        await Task.Delay(300);
        Assert.False(srcNode.IsLoaded);
        Assert.DoesNotContain(
            viewModel.WorkspaceTreeRows.OfType<WorkspaceFileRow>(),
            row => row.Node.Name == "Hidden.cs");

        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow); // 展开时按磁盘实况枚举
        Assert.Contains(
            viewModel.WorkspaceTreeRows.OfType<WorkspaceFileRow>(),
            row => row.Node.Name == "Hidden.cs");
    }

    [Fact]
    public async Task GitMetadataAndIgnoredDirectoryChurn_NeverTriggersTreeRefresh()
    {
        var (viewModel, watcher) = await CreateAsync();
        Directory.CreateDirectory(Path.Combine(_workspacePath, "obj"));
        File.WriteAllText(Path.Combine(_workspacePath, "obj", "x.dll.bytes"), "x");
        Directory.CreateDirectory(Path.Combine(_workspacePath, "node_modules"));
        File.WriteAllText(Path.Combine(_workspacePath, "node_modules", "y.js"), "y");

        watcher.RaiseChanged(_workspacePath,
            Path.Combine(_workspacePath, ".git", "index"),
            Path.Combine(_workspacePath, ".git", "HEAD"),
            Path.Combine(_workspacePath, "obj", "x.dll.bytes"),
            Path.Combine(_workspacePath, "node_modules", "y.js"));

        // 全部被过滤:不启动任何目录重枚举,树保持原样。
        await Task.Delay(700);
        Assert.Equal(0, viewModel.TreeAutoRefreshRuns);
        Assert.DoesNotContain(viewModel.WorkspaceTreeRows, row => row.Node.Name == "obj");
        Assert.DoesNotContain(viewModel.WorkspaceTreeRows, row => row.Node.Name == "node_modules");
    }

    [Fact]
    public async Task BurstOfChanges_CoalescesIntoOneRefreshPass()
    {
        var (viewModel, watcher) = await CreateAsync();
        var srcRow = FolderRow(viewModel, "src");
        await viewModel.ToggleTreeFolderCommand.ExecuteAsync(srcRow); // expand src

        // 同一去抖窗内的多路径变化(监视器已合并;这里直接给一批)→ 至多两轮刷新(冷却合并)。
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_workspacePath, "src", $"Batch{i}.cs"), "b");
        }

        watcher.RaiseChanged(_workspacePath,
            Path.Combine(_workspacePath, "src", "Batch0.cs"),
            Path.Combine(_workspacePath, "src", "Batch1.cs"),
            Path.Combine(_workspacePath, "src", "Batch2.cs"),
            Path.Combine(_workspacePath, "src", "Batch3.cs"),
            Path.Combine(_workspacePath, "src", "Batch4.cs"));

        await WaitUntilAsync(
            () => viewModel.WorkspaceTreeRows.OfType<WorkspaceFileRow>()
                .Count(row => row.Node.Name.StartsWith("Batch")) == 5,
            "批量新建文件未全部出现");
        Assert.True(viewModel.TreeAutoRefreshRuns <= 2, $"一批合并的变化触发了 {viewModel.TreeAutoRefreshRuns} 轮刷新");
    }

    [Fact]
    public async Task ManualRefresh_ReattachesWatcher()
    {
        var (viewModel, watcher) = await CreateAsync();
        var attachesBefore = watcher.AttachedPaths.Count;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(watcher.AttachedPaths.Count > attachesBefore, "手动刷新未重新挂载文件监视器");
    }
}
