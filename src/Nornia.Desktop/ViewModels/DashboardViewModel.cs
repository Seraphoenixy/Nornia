using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;
using Nornia.Project.Models;
using Nornia.Storage.Database;
using System.Collections.ObjectModel;

namespace Nornia.Desktop.ViewModels;

public partial class DashboardViewModel(
    IRuntimeInventoryService inventoryService,
    IDashboardSummaryReader summaryReader,
    ICacheInventoryService cacheInventoryService,
    IDesktopNavigationService navigationService,
    IUiLogService logService,
    NorniaDatabase? database = null) : PageViewModel("任务中心", logService)
{
    public ObservableCollection<ActionableTask> Tasks { get; } = [];
    public IReadOnlyList<DashboardProjectReference> ProjectsNeedingAttention { get; private set; } = [];

    [ObservableProperty]
    private int installedRuntimeCount;

    [ObservableProperty]
    private int installedToolCount;

    [ObservableProperty]
    private int projectCount;

    [ObservableProperty]
    private int environmentIssueCount;

    [ObservableProperty] private int availableUpdateCount;
    [ObservableProperty] private int cacheCandidateCount;
    [ObservableProperty] private long reclaimableCacheBytes;
    /// <summary>缓存统计是否已扫描过。启动不自动扫描缓存(全盘目录遍历代价高),首次显式刷新前
    /// 卡片显示占位符而不是误导性的“0 B”。</summary>
    [ObservableProperty] private bool isCacheStatsLoaded;
    [ObservableProperty] private bool isFirstRun;
    [ObservableProperty] private string nextStepTitle = "扫描本机环境";
    [ObservableProperty] private string nextStepDescription = "读取 Runtime、开发工具、软件包和项目状态。";
    public string ReclaimableCacheSize => IsCacheStatsLoaded ? FormatSize(ReclaimableCacheBytes) : "—";
    public string CacheCandidateSummary => IsCacheStatsLoaded ? $"共 {CacheCandidateCount} 个候选" : "尚未扫描，点击刷新后统计";
    public bool HasTasks => Tasks.Count > 0;

    protected override Task OnFirstActivatedAsync()
    {
        // 启动自动刷新不扫描缓存:磁盘遍历代价高且用户未表达意图,显式点击刷新时才统计。
        return RefreshAsync(includeCache: false);
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshAsync(includeCache: true);

    private Task RefreshAsync(bool includeCache) => RunAsync("刷新任务中心", async cancellationToken =>
    {
        // 闸门:等待数据库初始化完成(启动时 DB 初始化在窗口显示后后台执行,首启时表可能尚未建出)。
        if (database is not null)
        {
            await database.Initialization;
        }

        // 缓存优先:先展示上次持久化的扫描快照(首启为空),启动画面不必空等全量环境扫描;
        // 内存 30s 缓存不跨会话,此持久化读取是"秒出"的来源。
        try
        {
            ApplyInventoryCounts(await inventoryService.GetPersistedAsync(cancellationToken));
        }
        catch
        {
            // 无可展示缓存(表尚未就绪等):以下全量扫描结果兜底。
        }

        // One environment scan, one aggregated database summary and a cached filesystem scan
        // replace the previous full-table reads.
        var runtimes = await inventoryService.RefreshAsync(cancellationToken);
        ApplyInventoryCounts(runtimes);
        var summary = await summaryReader.GetAsync(cancellationToken);
        ProjectCount = summary.ProjectCount;
        EnvironmentIssueCount = summary.EnvironmentIssueCount;
        AvailableUpdateCount = summary.AvailableUpdateCount;
        ProjectsNeedingAttention = summary.ProjectsNeedingAttention;
        if (includeCache)
        {
            var caches = await cacheInventoryService.ScanAsync(cancellationToken: cancellationToken);
            CacheCandidateCount = caches.Count;
            ReclaimableCacheBytes = caches.Where(cache => cache.Confidence == CacheConfidence.High).Sum(cache => cache.SizeBytes);
            IsCacheStatsLoaded = true;
        }
        OnPropertyChanged(nameof(ReclaimableCacheSize));
        OnPropertyChanged(nameof(CacheCandidateSummary));

        BuildTasks(ProjectCount, runtimes.Count);
    }, "如果扫描失败，请打开 Problems 查看诊断并重试。", canCancel: true);

    /// <summary>把一份扫描快照(持久化缓存或实时扫描)的运行库/开发工具计数写入卡片,
    /// 让首屏能先用上次结果占位、全量扫描完成后无缝更新。</summary>
    private void ApplyInventoryCounts(IReadOnlyList<CoreRuntime> runtimes)
    {
        InstalledRuntimeCount = runtimes.Count(runtime => EnvironmentComponentCatalog.Get(runtime.Name)?.Category == EnvironmentComponentCategory.Runtime);
        InstalledToolCount = runtimes.Count(runtime => EnvironmentComponentCatalog.Get(runtime.Name)?.Category == EnvironmentComponentCategory.DevelopmentTool);
    }

    [RelayCommand]
    private void OpenTask(ActionableTask? task)
    {
        if (task is not null) navigationService.Navigate(task.Destination, task.Context);
    }

    [RelayCommand]
    private void ScanEnvironment() => navigationService.Navigate(NavigationTargets.Runtime, "scan");

    [RelayCommand]
    private void CheckProjects() => navigationService.Navigate(NavigationTargets.Projects, "check");

    [RelayCommand]
    private void ViewRepairPlan() => navigationService.Navigate(NavigationTargets.Projects, "repair");

    /// <summary>Jump from the "运行库 / 工具" metric card straight to the Runtime page.</summary>
    [RelayCommand]
    private void GoToRuntimeDetails() => navigationService.Navigate(NavigationTargets.Runtime);

    /// <summary>Jump from the "项目" metric card to the Projects repair panel. When at least one
    /// project currently needs attention, the page is asked to auto-select that project and open the
    /// repair workflow so the user does not have to click twice.</summary>
    [RelayCommand]
    private void GoToProjectIssues()
    {
        var target = ProjectsNeedingAttention.FirstOrDefault();
        if (target is not null)
        {
            navigationService.Navigate(NavigationTargets.Projects, $"repair:project={target.ProjectPath}");
            return;
        }

        if (ProjectCount == 0)
        {
            navigationService.Navigate(NavigationTargets.Projects, "new");
            return;
        }

        navigationService.Navigate(NavigationTargets.Projects, "repair");
    }

    /// <summary>Jump from the "可用更新" metric card straight to the Packages page updates filter.</summary>
    [RelayCommand]
    private void GoToPackageUpdates() => navigationService.Navigate(NavigationTargets.Packages, "updates");

    /// <summary>Jump from the "高置信缓存" metric card to the Packages page cache tab, scoped to
    /// high-confidence entries. Cache management lives inside the package management page rather than
    /// as a standalone module.</summary>
    [RelayCommand]
    private void GoToCacheCandidates() => navigationService.Navigate(NavigationTargets.Packages, "cache:high-confidence");

    private void BuildTasks(int projects, int components)
    {
        Tasks.Clear();
        IsFirstRun = projects == 0 && components == 0;
        foreach (var task in DashboardTaskPlanner.Create(projects, components, EnvironmentIssueCount, AvailableUpdateCount, CacheCandidateCount, ReclaimableCacheSize)) Tasks.Add(task);
        OnPropertyChanged(nameof(HasTasks));
        var next = Tasks.FirstOrDefault();
        NextStepTitle = next?.Title ?? "环境状态良好";
        NextStepDescription = next?.Description ?? "目前没有需要立即处理的事项。";
    }

    private static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = size;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public enum ActionableTaskSeverity
{
    Info,
    Warning,
    Critical
}

public sealed record ActionableTask(
    string Title,
    string Description,
    string Destination,
    string? Context,
    ActionableTaskSeverity Severity,
    int Priority);

public static class DashboardTaskPlanner
{
    public static IReadOnlyList<ActionableTask> Create(int projectCount, int componentCount, int environmentIssueCount, int availableUpdateCount, int cacheCandidateCount, string reclaimableCacheSize)
    {
        var tasks = new List<ActionableTask>();
        if (projectCount == 0 && componentCount == 0) tasks.Add(new("开始扫描本机环境", "发现已安装的 Runtime 与开发工具。", NavigationTargets.Runtime, "scan", ActionableTaskSeverity.Info, 100));
        if (environmentIssueCount > 0) tasks.Add(new($"{environmentIssueCount} 个项目环境需要处理", "检查版本差异并预览修复计划。", NavigationTargets.Projects, "repair", ActionableTaskSeverity.Critical, 90));
        if (availableUpdateCount > 0) tasks.Add(new($"{availableUpdateCount} 个软件包可升级", "查看版本与来源后选择升级。", NavigationTargets.Packages, "updates", ActionableTaskSeverity.Warning, 70));
        if (cacheCandidateCount > 0) tasks.Add(new($"可安全审查 {cacheCandidateCount} 个缓存候选", $"高置信候选预计可释放 {reclaimableCacheSize}。", NavigationTargets.Packages, "cache:high-confidence", ActionableTaskSeverity.Info, 50));
        if (projectCount == 0) tasks.Add(new("登记第一个项目", "选择项目目录并初始化或加载 Nornia.yaml。", NavigationTargets.Projects, "new", ActionableTaskSeverity.Info, 40));
        return tasks.OrderByDescending(task => task.Priority).ToArray();
    }
}