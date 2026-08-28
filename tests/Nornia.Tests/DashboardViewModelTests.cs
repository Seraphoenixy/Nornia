using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Project.Services;
using Nornia.Tests.Fakes;
using NSubstitute;

namespace Nornia.Tests;

public sealed class DashboardViewModelTests
{
    private static DashboardViewModel Create(
        FakeRuntimeInventory inventory,
        FakeSummaryReader summary,
        FakeCacheInventory caches,
        out FakeUiLogService logService,
        out DesktopNavigationService navigation)
    {
        logService = new FakeUiLogService();
        navigation = new DesktopNavigationService();
        return new DashboardViewModel(inventory, summary, caches, navigation, logService);
    }

    [Fact]
    public async Task RefreshAsync_AggregatesCountersFromSummaryAndScan()
    {
        var inventory = new FakeRuntimeInventory(
        [
            FakeRuntimes.Runtime("Visual C++ Redistributable", "14.51"),
            FakeRuntimes.Runtime(".NET", "10.0.100"),
            FakeRuntimes.Runtime("Node.js", "22.14.0")
        ]);
        var summary = new FakeSummaryReader(new DashboardSummary(3, 1, 2));
        var caches = new FakeCacheInventory(
        [
            new CacheCandidate("id-1", "AppData", "C:\\x\\cache", 1024, CacheConfidence.High, "high"),
            new CacheCandidate("id-2", "AppData", "C:\\x\\review", 512, CacheConfidence.Review, "review")
        ]);
        var viewModel = Create(inventory, summary, caches, out _, out _);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, viewModel.InstalledRuntimeCount);   // Visual C++ Redistributable is a Runtime
        Assert.Equal(2, viewModel.InstalledToolCount);      // .NET and Node.js are development tools
        Assert.Equal(3, viewModel.ProjectCount);
        Assert.Equal(1, viewModel.EnvironmentIssueCount);
        Assert.Equal(2, viewModel.AvailableUpdateCount);
        Assert.Equal(2, viewModel.CacheCandidateCount);
        Assert.Equal(1024, viewModel.ReclaimableCacheBytes);
        Assert.Equal("1 KB", viewModel.ReclaimableCacheSize);
    }

    [Fact]
    public async Task OnFirstActivated_DoesNotScanCache_ExplicitRefreshDoes()
    {
        // 启动不自动扫描缓存:首次激活的自动刷新跳过缓存统计(卡片显示占位),显式点击刷新才扫描。
        var inventory = new FakeRuntimeInventory([]);
        var summary = new FakeSummaryReader(new DashboardSummary(0, 0, 0));
        var caches = new FakeCacheInventory(
        [
            new CacheCandidate("id-1", "AppData", "C:\\x\\cache", 1024, CacheConfidence.High, "high")
        ]);
        var viewModel = Create(inventory, summary, caches, out _, out _);

        await viewModel.ActivateAsync();

        Assert.Equal(0, caches.ScanCalls);
        Assert.False(viewModel.IsCacheStatsLoaded);
        Assert.Equal("—", viewModel.ReclaimableCacheSize);
        Assert.Equal("尚未扫描，点击刷新后统计", viewModel.CacheCandidateSummary);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, caches.ScanCalls);
        Assert.True(viewModel.IsCacheStatsLoaded);
        Assert.Equal("1 KB", viewModel.ReclaimableCacheSize);
        Assert.Equal("共 1 个候选", viewModel.CacheCandidateSummary);
    }

    [Fact]
    public async Task RefreshAsync_PopulatesFirstRunTask()
    {
        var inventory = new FakeRuntimeInventory([]);
        var summary = new FakeSummaryReader(new DashboardSummary(0, 0, 0));
        var caches = new FakeCacheInventory([]);
        var viewModel = Create(inventory, summary, caches, out _, out _);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsFirstRun);
        Assert.Equal("开始扫描本机环境", viewModel.Tasks[0].Title);
        Assert.True(viewModel.HasTasks);
    }

    [Fact]
    public void ScanEnvironment_NavigatesToRuntimeTarget()
    {
        var viewModel = Create(new FakeRuntimeInventory([]), new FakeSummaryReader(new DashboardSummary(0, 0, 0)), new FakeCacheInventory([]), out _, out var navigation);

        NavigationRequest? received = null;
        navigation.NavigationRequested += (_, request) => received = request;

        viewModel.ScanEnvironmentCommand.Execute(null);

        Assert.Equal(new NavigationRequest(NavigationTargets.Runtime, "scan"), received);
    }

    [Fact]
    public void CheckProjects_NavigatesToProjectsTarget()
    {
        var viewModel = Create(new FakeRuntimeInventory([]), new FakeSummaryReader(new DashboardSummary(0, 0, 0)), new FakeCacheInventory([]), out _, out var navigation);

        NavigationRequest? received = null;
        navigation.NavigationRequested += (_, request) => received = request;

        viewModel.CheckProjectsCommand.Execute(null);

        Assert.Equal(new NavigationRequest(NavigationTargets.Projects, "check"), received);
    }

    [Fact]
    public void GoToRuntimeDetails_NavigatesToRuntimePage()
    {
        var viewModel = Create(new FakeRuntimeInventory([]), new FakeSummaryReader(new DashboardSummary(0, 0, 0)), new FakeCacheInventory([]), out _, out var navigation);

        NavigationRequest? received = null;
        navigation.NavigationRequested += (_, request) => received = request;

        viewModel.GoToRuntimeDetailsCommand.Execute(null);

        Assert.Equal(new NavigationRequest(NavigationTargets.Runtime, (NavigationContext?)null), received);
    }

    [Fact]
    public async Task GoToProjectIssues_WhenProjectNeedsAttention_NavigatesWithRepairHint()
    {
        var inventory = new FakeRuntimeInventory([]);
        var projectId = Guid.NewGuid();
        var summary = new FakeSummaryReader(new DashboardSummary(3, 1, 0)
        {
            ProjectsNeedingAttention =
            [
                new DashboardProjectReference(projectId, "需要修复的项目", @"C:\Code\NeedsFix", EnvironmentHealthStatus.NeedsAttention)
            ]
        });
        var viewModel = Create(inventory, summary, new FakeCacheInventory([]), out _, out var navigation);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        NavigationRequest? received = null;
        navigation.NavigationRequested += (_, request) => received = request;

        viewModel.GoToProjectIssuesCommand.Execute(null);

        Assert.Equal(NavigationTargets.Projects, received?.Destination);
        var context = Assert.IsType<NavigationContext.RepairProject>(received?.Context);
        Assert.EndsWith(@"C:\Code\NeedsFix", context.ProjectPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoToProjectIssues_WhenNoIssueProject_NavigatesToRepairList()
    {
        var inventory = new FakeRuntimeInventory([]);
        var summary = new FakeSummaryReader(new DashboardSummary(5, 0, 0) { ProjectsNeedingAttention = [] });
        var viewModel = Create(inventory, summary, new FakeCacheInventory([]), out _, out var navigation);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        NavigationRequest? received = null;
        navigation.NavigationRequested += (_, request) => received = request;

        viewModel.GoToProjectIssuesCommand.Execute(null);

        Assert.Equal(new NavigationRequest(NavigationTargets.Projects, "repair"), received);
    }

    [Fact]
    public void GoToPackageUpdates_NavigatesToPackagesUpdatesFilter()
    {
        var viewModel = Create(new FakeRuntimeInventory([]), new FakeSummaryReader(new DashboardSummary(0, 0, 0)), new FakeCacheInventory([]), out _, out var navigation);

        NavigationRequest? received = null;
        navigation.NavigationRequested += (_, request) => received = request;

        viewModel.GoToPackageUpdatesCommand.Execute(null);

        Assert.Equal(new NavigationRequest(NavigationTargets.Packages, "updates"), received);
    }

    [Fact]
    public void GoToCacheCandidates_NavigatesToPackagesCacheTabWithHighConfidence()
    {
        var viewModel = Create(new FakeRuntimeInventory([]), new FakeSummaryReader(new DashboardSummary(0, 0, 0)), new FakeCacheInventory([]), out _, out var navigation);

        NavigationRequest? received = null;
        navigation.NavigationRequested += (_, request) => received = request;

        viewModel.GoToCacheCandidatesCommand.Execute(null);

        Assert.Equal(new NavigationRequest(NavigationTargets.Packages, "cache:high-confidence"), received);
    }
}

public sealed class ProjectNavigationContextTests
{
    private static ProjectsViewModel CreateForNavigation()
    {
        var profiles = Substitute.For<IEnvironmentProfileService>();
        var engine = Substitute.For<IEnvironmentCheckEngine>();
        var planner = Substitute.For<IEnvironmentRepairPlanner>();
        var executor = Substitute.For<IEnvironmentRepairExecutor>();
        var launcher = Substitute.For<IProjectLauncher>();
        var inventory = Substitute.For<IRuntimeInventoryService>();
        var catalog = Substitute.For<IProjectCatalogService>();
        catalog.GetAllAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<ProjectAsset>>(
        [
            new ProjectAsset(Guid.NewGuid(), "Match", @"C:\Code\Match", ProjectPathStatus.Available, 0, 0, 0, EnvironmentHealthStatus.Healthy)
        ]);
        var folder = Substitute.For<IFolderPickerService>();
        var confirm = Substitute.For<IConfirmationService>();
        var settingsService = new FakeSettingsService();
        var workspaceService = new FakeProjectWorkspaceService();
        var logs = new FakeUiLogService();
        return new ProjectsViewModel(profiles, engine, planner, executor, launcher, inventory, catalog, folder, confirm, settingsService, new DesktopNavigationService(), logs);
    }

    [Fact]
    public void ApplyNavigationContext_ParsesLegacyRepairToken()
    {
        var vm = CreateForNavigation();

        vm.ApplyNavigationContext(new NavigationContext.Repair());

        Assert.Equal(0, vm.SelectedWorkspaceTab);
        Assert.Contains("修复计划", vm.WorkflowHint, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyNavigationContext_ParsesRepairProjectHintAndPrefillsPath()
    {
        var vm = CreateForNavigation();

        vm.ApplyNavigationContext(new NavigationContext.RepairProject(@"C:\Code\NewProject"));

        Assert.Equal(@"C:\Code\NewProject", vm.ProjectPath);
        Assert.Equal("NewProject", vm.ProjectName);
    }

    [Fact]
    public async Task ApplyNavigationContext_WhenProjectRegistered_SelectsExistingProjectEntry()
    {
        var vm = CreateForNavigation();

        vm.ApplyNavigationContext(new NavigationContext.RepairProject(@"C:\Code\Match"));
        // Give the fire-and-forget task a short window so the catalog load and SelectedProject
        // assignment runs. We never want the test to be slow, so keep it minimal.
        await Task.Delay(30);

        Assert.NotNull(vm.SelectedProject);
        Assert.Equal("Match", vm.SelectedProject.Name);
    }
}