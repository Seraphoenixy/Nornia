using Nornia.Core.Models;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Project.Models;
using Nornia.Tests.Fakes;
using NSubstitute;

namespace Nornia.Tests;

/// <summary>Covers the top-level 资源管理器 page: one "open project" entry point keeps the explorer,
/// terminal, project catalog and git repository synchronized, the explorer and source control share
/// a single editor area, and dashboard navigation routes into the project view.</summary>
public sealed class ExplorerPageViewModelTests : IDisposable
{
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), $"nornia-explorer-{Guid.NewGuid():N}");

    public ExplorerPageViewModelTests()
    {
        Directory.CreateDirectory(_projectPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_projectPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp project directory.
        }
    }

    private (ExplorerPageViewModel Page, FakeProjectCatalog Catalog, FakeFolderPicker Folder, FakeGitService Git, FakeSettingsService Settings) Create()
    {
        var logs = new FakeUiLogService();
        var settings = new FakeSettingsService();
        var folder = new FakeFolderPicker();
        var catalog = new FakeProjectCatalog();
        var gitService = new FakeGitService();
        var explorer = new WorkspaceViewModel(folder, settings, new FakeProjectWorkspaceService(), logs, new FakeClipboardService());
        var editor = new EditorAreaViewModel(gitService, logs);
        var terminal = new TerminalViewModel(Substitute.For<ITerminalService>(), settings, new FakeProjectWorkspaceService(), logs, new FakeClipboardService());
        var projects = new ProjectsViewModel(
            new FakeProfileService(),
            new FakeCheckEngine([]),
            new FakeRepairPlanner(new EnvironmentRepairPlan([])),
            new FakeRepairExecutor(),
            new FakeProjectLauncher(),
            new FakeRuntimeInventory([]),
            catalog,
            folder,
            new FakeConfirmationService(),
            settings,
            new DesktopNavigationService(),
            logs,
            new FakeClipboardService(),
            new FakeProjectWorkspaceService());
        var git = new GitViewModel(gitService, folder, new FakeConfirmationService(), catalog, editor, logs,
            new FakeClipboardService(), new FakeGitRepositoryWatcher(), new FakeProjectWorkspaceService(), new FakeApplicationStateStore(), settings);
        var page = new ExplorerPageViewModel(explorer, editor, terminal, git, projects, catalog, folder, new DesktopNavigationService(), logs);
        return (page, catalog, folder, gitService, settings);
    }

    [Fact]
    public async Task OpenProjectPathAsync_SynchronizesExplorerTerminalAndCatalog()
    {
        var (page, catalog, _, _, _) = Create();
        catalog.Projects =
        [
            new ProjectAsset(Guid.NewGuid(), "Demo", _projectPath, ProjectPathStatus.Available, 0, null, null, EnvironmentHealthStatus.Healthy)
        ];

        await page.OpenProjectPathAsync(_projectPath);

        Assert.Equal(_projectPath, page.Explorer.WorkspacePath);
        Assert.Equal(_projectPath, page.Terminal.WorkingDirectory);
        Assert.NotNull(page.CurrentProject);
        Assert.Equal(_projectPath, page.CurrentProject.Path);
        Assert.Equal("", page.Projects.AttentionBadge); // 健康项目不产生徽标(catalog refreshed after register)
    }

    [Fact]
    public async Task OpenProjectPathAsync_StillOpensWorkspace()
    {
        var (page, _, _, _, _) = Create();

        await page.OpenProjectPathAsync(_projectPath);

        Assert.Equal(_projectPath, page.Explorer.WorkspacePath);
        Assert.NotNull(page.CurrentProject);
    }

    [Fact]
    public async Task OpenProjectCommand_OpensProject()
    {
        var (page, _, _, _, _) = Create();
        var project = new ProjectAsset(Guid.NewGuid(), "Demo", _projectPath, ProjectPathStatus.Available, 0, null, null, EnvironmentHealthStatus.Healthy);

        await page.OpenProjectCommand.ExecuteAsync(project);

        Assert.Equal(_projectPath, page.Explorer.WorkspacePath);
        Assert.NotNull(page.CurrentProject);
        Assert.Equal(_projectPath, page.CurrentProject.Path);
    }

    [Fact]
    public async Task ChooseProjectCommand_OpensFolderPickedByUser()
    {
        var (page, _, folder, _, _) = Create();
        folder.Result = _projectPath;

        await page.ChooseProjectCommand.ExecuteAsync(null);

        Assert.Equal(_projectPath, page.Explorer.WorkspacePath);
        Assert.NotNull(page.CurrentProject);
    }

    [Fact]
    public async Task ApplyNavigationContext_OpensProjectPath()
    {
        var (page, _, _, _, _) = Create();

        page.ApplyNavigationContext(new NavigationContext.OpenInExplorer(_projectPath));

        // 导航打开是 fire-and-forget,目录枚举已移到线程池(会真正挂起),等待打开链完成。
        var deadline = Environment.TickCount + 5000;
        while (page.CurrentProject is null && Environment.TickCount < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(_projectPath, page.Explorer.WorkspacePath);
        Assert.NotNull(page.CurrentProject);
    }

    [Fact]
    public async Task ApplyNavigationContext_IgnoresEmptyContext()
    {
        var (page, _, _, _, _) = Create();

        page.ApplyNavigationContext(null);

        Assert.Null(page.CurrentProject);
    }

    [Fact]
    public async Task AttentionBadge_CountsOnlyProjectsNeedingAttention()
    {
        var (page, catalog, _, _, _) = Create();
        catalog.Projects =
        [
            new ProjectAsset(Guid.NewGuid(), "A", @"C:\Code\A", ProjectPathStatus.Available, 0, null, null, EnvironmentHealthStatus.Healthy),
            new ProjectAsset(Guid.NewGuid(), "B", @"C:\Code\B", ProjectPathStatus.Available, 0, null, null, EnvironmentHealthStatus.NeedsAttention),
            new ProjectAsset(Guid.NewGuid(), "C", @"C:\Code\C", ProjectPathStatus.Missing, 0, null, null, EnvironmentHealthStatus.Healthy),
            new ProjectAsset(Guid.NewGuid(), "D", @"C:\Code\D", ProjectPathStatus.Available, 0, null, null, EnvironmentHealthStatus.Unknown),
        ];

        await page.Projects.RefreshCatalogAsync();

        Assert.Equal(4, page.Projects.ProjectCount);
        Assert.Equal("2", page.Projects.AttentionBadge); // B(检查未过) + C(目录缺失), D 未检查不计数
    }

    [Fact]
    public async Task ExplorerFileOpen_OpensPreviewTabInSharedEditor()
    {
        var (page, _, _, _, _) = Create();
        var file = Path.Combine(_projectPath, "src", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "hello");

        await page.OpenProjectPathAsync(_projectPath);
        var src = page.Explorer.RootNodes[0].Children.First(child => child.Name == "src");
        await src.LoadChildrenAsync();
        var node = src.Children.First(child => child.Name == "A.cs");

        page.Explorer.OpenNodeCommand.Execute(node);
        await Task.Delay(30);

        var preview = Assert.IsType<FilePreviewTab>(Assert.Single(page.Editor.OpenTabs));
        Assert.Equal(file, preview.Path);
        Assert.Equal("hello", preview.Content);
    }

    [Fact]
    public async Task ExplorerFileOpenPermanent_OpensRegularTabInSharedEditor()
    {
        // 双击树文件 → 共享编辑器打开常驻标签(非预览)。
        var (page, _, _, _, _) = Create();
        var file = Path.Combine(_projectPath, "src", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "hello");

        await page.OpenProjectPathAsync(_projectPath);
        var src = page.Explorer.RootNodes[0].Children.First(child => child.Name == "src");
        await src.LoadChildrenAsync();
        var node = src.Children.First(child => child.Name == "A.cs");

        page.Explorer.OpenTreeFilePermanentCommand.Execute(new WorkspaceFileRow(node, depth: 1));
        await Task.Delay(30);

        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(page.Editor.OpenTabs));
        Assert.Equal(file, tab.Path);
        Assert.False(tab.IsPreview);
    }

    [Fact]
    public async Task OpenProjectPathAsync_SyncsProjectWorkflowPath()
    {
        var (page, _, _, _, _) = Create();

        await page.OpenProjectPathAsync(_projectPath);

        // The project workflow (检查环境/修复) must point at the same directory as the explorer,
        // otherwise the Explorer "检查环境" button would inspect the wrong folder.
        Assert.Equal(_projectPath, page.Projects.ProjectPath);
        Assert.Equal("Test", page.Projects.ProjectName); // FakeProjectCatalog derives the name
    }

    [Fact]
    public async Task GitStatusRefresh_ResyncsExplorerDecorations()
    {
        var (page, _, _, git, _) = Create();
        var status = new GitRepositoryStatus(true, "main", "origin/main", 1, 0,
            [], [new GitFileChange("src/A.cs", GitChangeStatus.Unmodified, GitChangeStatus.Modified)]);
        page.Git.RepositoryPath = _projectPath;
        git.Status = status;

        await page.Git.RefreshCommand.ExecuteAsync(null);

        // StatusRefreshed wiring (Git → Explorer.ApplyGitStatus) decorates the workspace status map.
        var info = page.Explorer.GetGitInfo("src/A.cs");
        Assert.NotNull(info);
        Assert.False(info!.Value.IsStaged);
        Assert.False(info.Value.IsUntracked);
    }
}
