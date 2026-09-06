using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Interfaces;
using Nornia.Core.Collections;
using Nornia.Core.Services;
using Nornia.Core.Models;
using Nornia.Desktop.Services;
using Nornia.Desktop.Configuration;
using Nornia.Project.Models;
using Nornia.Project.Services;
using Nornia.Runtime.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Desktop.ViewModels;

public partial class ProjectsViewModel : PageViewModel, INavigationTarget
{
    private readonly IEnvironmentProfileService _profileService;
    private readonly IEnvironmentCheckEngine _checkEngine;
    private readonly IEnvironmentRepairPlanner _repairPlanner;
    private readonly IEnvironmentRepairExecutor _repairExecutor;
    private readonly IProjectLauncher _projectLauncher;
    private readonly IRuntimeInventoryService _inventoryService;
    private readonly IProjectCatalogService _projectCatalogService;
    private readonly IFolderPickerService _folderPicker;
    private readonly IConfirmationService _confirmationService;
    private readonly ISettingsService _settingsService;
    private readonly IDesktopNavigationService _navigationService;
    private readonly IUiPerformanceMetrics? _performanceMetrics;
    private readonly IClipboardService _clipboard;
    private IProjectWorkspaceService? _workspaceService;
    private readonly SemaphoreSlim _workspaceContextGate = new(1, 1);
    private ProjectWorkspaceContext? _workspaceContext;
    private long _workspaceGeneration;

    public ProjectsViewModel(
        IEnvironmentProfileService profileService,
        IEnvironmentCheckEngine checkEngine,
        IEnvironmentRepairPlanner repairPlanner,
        IEnvironmentRepairExecutor repairExecutor,
        IProjectLauncher projectLauncher,
        IRuntimeInventoryService inventoryService,
        IProjectCatalogService projectCatalogService,
        IFolderPickerService folderPicker,
        IConfirmationService confirmationService,
        ISettingsService settingsService,
        IDesktopNavigationService navigationService,
        IUiLogService logService,
        IClipboardService? clipboard = null,
        IProjectWorkspaceService? workspaceService = null,
        IUiPerformanceMetrics? performanceMetrics = null)
        : base("项目管理", logService)
    {
        _profileService = profileService;
        _checkEngine = checkEngine;
        _repairPlanner = repairPlanner;
        _repairExecutor = repairExecutor;
        _projectLauncher = projectLauncher;
        _inventoryService = inventoryService;
        _projectCatalogService = projectCatalogService;
        _folderPicker = folderPicker;
        _confirmationService = confirmationService;
        _settingsService = settingsService;
        _navigationService = navigationService;
        _performanceMetrics = performanceMetrics;
        _clipboard = clipboard ?? NullClipboardService.Instance;
        _workspaceService = workspaceService;
        _workspaceContext = workspaceService?.Current;
        if (_workspaceService is not null)
        {
            _workspaceService.ContextChanged += ApplyWorkspaceContextAsync;
        }
    }

    public BulkObservableCollection<EnvironmentCheckResult> CheckResults { get; } = [];
    public BulkObservableCollection<ProjectAsset> Projects { get; } = [];

    /// <summary>Multi-selection for the registered-project side-bar list (Ctrl+C / 复制选中).</summary>
    public ObservableCollection<ProjectAsset> SelectedProjects { get; } = [];

    private void HookSelectionChanges<T>(ObservableCollection<T> collection) =>
        collection.CollectionChanged += (_, _) => NotifyCopyCommands();

    private void NotifyCopyCommands()
    {
        CopySelectedProjectsCommand.NotifyCanExecuteChanged();
        CopyAllProjectsCommand.NotifyCanExecuteChanged();
    }

    public bool IsCheckResultsEmpty => CheckResults.Count == 0;
    public int ProjectCount => Projects.Count;

    /// <summary>Badge text for the project activity-bar icon: the count of projects needing
    /// attention — last environment check failed (<see cref="EnvironmentHealthStatus.NeedsAttention"/>)
    /// or the registered directory is missing (<see cref="ProjectPathStatus.Missing"/>). Empty hides
    /// the badge; mirrors the attention semantics of the environment (problems) and git (changes) badges.</summary>
    public string AttentionBadge
    {
        get
        {
            var attention = Projects.Count(project =>
                project.LastEnvironmentStatus == EnvironmentHealthStatus.NeedsAttention
                || project.PathStatus == ProjectPathStatus.Missing);
            return attention > 0 ? attention.ToString() : string.Empty;
        }
    }

    /// <summary>The secondary left sidebar shows the registered-project catalog.</summary>
    public override object? Sidebar => this;

    /// <summary>Reloads the registered-project catalog from the repository (called by the workbench
    /// after opening a project so the catalog and the activity badge stay in sync).</summary>
    public Task RefreshCatalogAsync(CancellationToken cancellationToken = default) => LoadProjectsAsync(cancellationToken);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            var normalizedLeft = Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRight = Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool IsProjectContextCurrent(ProjectWorkspaceContext? context, string path)
    {
        if (_workspaceService is null)
        {
            return PathsEqual(ProjectPath, path);
        }

        return ReferenceEquals(_workspaceContext, context)
            && PathsEqual(ProjectPath, path);
    }

    private void EnsureProjectContextCurrent(
        ProjectWorkspaceContext? context,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsProjectContextCurrent(context, path))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private async Task<bool> RunProjectAsync(
        string operation,
        Func<string, ProjectWorkspaceContext?, CancellationToken, Task> action,
        string recommendedNextStep = "",
        bool canCancel = true)
    {
        await _workspaceContextGate.WaitAsync();
        try
        {
            var context = _workspaceContext;
            var path = ProjectPath;
            if (!IsProjectContextCurrent(context, path)) return false;

            return await RunAsync(operation, async cancellationToken =>
            {
                EnsureProjectContextCurrent(context, path, cancellationToken);
                await action(path, context, cancellationToken);
                EnsureProjectContextCurrent(context, path, cancellationToken);
            }, recommendedNextStep, canCancel);
        }
        finally
        {
            _workspaceContextGate.Release();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveProjectCommand))]
    private ProjectAsset? selectedProject;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InitializeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlanRepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private string projectPath = Environment.CurrentDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InitializeCommand))]
    private string projectName = new DirectoryInfo(Environment.CurrentDirectory).Name;

    [ObservableProperty]
    private string profileSummary = "尚未加载项目环境";

    [ObservableProperty]
    private string repairSummary = string.Empty;

    /// <summary>Whether the current project has been checked (results grid non-empty). Gates the
    /// "预览修复" action so it only enables after an inspection.</summary>
    public bool HasCheckResults => CheckResults.Count > 0;

    /// <summary>Whether the current project's repair plan contains at least one automatic operation.
    /// Gates the "应用修复" action; manual-only plans leave it disabled.</summary>
    public bool HasAutomaticRepair { get; private set; }

    [ObservableProperty] private int selectedWorkspaceTab;
    [ObservableProperty] private string workflowHint = "1. 选择项目目录  2. 初始化或检查  3. 预览修复计划  4. 确认后应用";

    [RelayCommand]
    private async Task Browse()
    {
        var selected = _folderPicker.PickFolder(ProjectPath);
        if (selected is null)
        {
            return;
        }

        if (_workspaceService is not null)
        {
            await _workspaceService.ActivateAsync(selected);
        }
        else
        {
            ProjectPath = selected;
            ProjectName = new DirectoryInfo(selected).Name;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInitialize))]
    private async Task InitializeAsync()
    {
        string? initializedPath = null;
        ProjectWorkspaceContext? initializedContext = null;
        var initialized = await RunProjectAsync("初始化项目环境", async (path, context, cancellationToken) =>
        {
            initializedContext = context;
            initializedPath = path;
            var createdPath = await _profileService.InitializeAsync(path, ProjectName);
            var profile = await _profileService.LoadAsync(path, cancellationToken);
            await _projectCatalogService.RegisterAsync(path, profile);
            await LoadProjectsAsync(cancellationToken);
            ProfileSummary = $"已创建 {createdPath}";
            NotifyProjectCommands();
        });

        if (initialized && initializedPath is { } path && _workspaceService is not null
            && ReferenceEquals(_workspaceService.Current, initializedContext)
            && ReferenceEquals(_workspaceContext, initializedContext))
        {
            // Rebind only when the user has not requested another project while initialization was
            // running; the pending context event must remain the winner otherwise.
            await _workspaceService.ActivateAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanInspect))]
    private Task CheckAsync() => RunProjectAsync("检查项目环境", async (path, context, cancellationToken) =>
    {
        var (_, runtimes, results) = await EvaluateAsync(path, context, cancellationToken);
        EnsureProjectContextCurrent(context, path, cancellationToken);
        UpdateCheckResults(results, CurrentOperation?.CorrelationId);
        SetRepairPlan(_repairPlanner.CreatePlan(results, runtimes));
        await LoadProjectsAsync(cancellationToken);
    }, "确认项目包含有效的 Nornia.yaml，或查看 Problems。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanPlanRepair))]
    private Task PlanRepairAsync() => RunProjectAsync("生成修复计划", async (path, context, cancellationToken) =>
    {
        var (_, runtimes, results) = await EvaluateAsync(path, context, cancellationToken);
        EnsureProjectContextCurrent(context, path, cancellationToken);
        UpdateCheckResults(results, CurrentOperation?.CorrelationId);
        var plan = _repairPlanner.CreatePlan(results, runtimes);
        SetRepairPlan(plan);
        WriteRepairPlan(plan, CurrentOperation?.CorrelationId);
    }, "修正无效版本约束后重新生成计划。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanApplyRepair))]
    private Task ApplyRepairAsync() => RunProjectAsync("应用环境修复", async (path, context, cancellationToken) =>
    {
        var (_, runtimes, results) = await EvaluateAsync(path, context, cancellationToken);
        EnsureProjectContextCurrent(context, path, cancellationToken);
        var plan = _repairPlanner.CreatePlan(results, runtimes);
        SetRepairPlan(plan);
        WriteRepairPlan(plan, CurrentOperation?.CorrelationId);
        if (plan.Operations.Count == 0)
        {
            LogService.Write("WARNING", "没有可自动执行的修复项。请根据修复计划手动处理。");
            return;
        }

        var details = string.Join(Environment.NewLine, plan.Operations.Select(operation =>
            $"• {operation.Component} {operation.TargetVersion} ({operation.PackageId})"));
        if (!_confirmationService.Confirm("确认应用环境修复", $"将通过可用的软件包管理器安装或升级以下组件：{Environment.NewLine}{Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}操作会修改系统环境；开始后不保证可以自动回滚。选择“否”可安全取消。"))
        {
            LogService.Write("INFO", "用户取消了环境修复。");
            return;
        }

        await _repairExecutor.ExecuteAsync(plan.Operations, OperationProgress, cancellationToken);
        EnsureProjectContextCurrent(context, path, cancellationToken);
        var (_, refreshedRuntimes, refreshedResults) = await EvaluateAsync(path, context, cancellationToken);
        EnsureProjectContextCurrent(context, path, cancellationToken);
        UpdateCheckResults(refreshedResults, CurrentOperation?.CorrelationId);
        SetRepairPlan(_repairPlanner.CreatePlan(refreshedResults, refreshedRuntimes));
        await LoadProjectsAsync(cancellationToken);
    }, "查看 Problems，确认包管理器可用后重新生成修复计划。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync() => RunProjectAsync("打开项目",
        (path, context, cancellationToken) => OpenProjectCoreAsync(path, context, cancellationToken));

    /// <summary>Opens the project in the top-level 资源管理器 page (the Explorer sidebar + terminal
    /// sync); the workspace tree, catalog registration and project workflow stay synchronized.</summary>
    [RelayCommand]
    private void OpenInExplorer(ProjectAsset? project)
    {
        if (project is null || !Directory.Exists(project.Path))
        {
            LogService.Write("WARNING", $"项目目录不存在：{project?.Path}");
            return;
        }

        if (_workspaceService is not null)
        {
            _ = ActivateAndNavigateToExplorerAsync(project.Path);
            return;
        }

        _navigationService.Navigate(NavigationTargets.Explorer, project.Path);
    }

    [RelayCommand]
    private Task OpenProjectAsync(ProjectAsset? project) => RunAsync("打开项目", async () =>
    {
        if (project is null) throw new InvalidOperationException("请先选择一个项目。");
        if (!Directory.Exists(project.Path))
        {
            LogService.Write("WARNING", $"项目目录不存在：{project.Path}");
            return;
        }

        ProjectPath = project.Path;
        ProjectName = project.Name;
        if (_workspaceService is not null)
        {
            await _workspaceService.ActivateAsync(project.Path);
        }
        await OpenProjectCoreAsync(project.Path, _workspaceContext ?? _workspaceService?.Current, cancellationToken: default);
    });

    private async Task OpenProjectCoreAsync(
        string path,
        ProjectWorkspaceContext? context = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = await _settingsService.GetSnapshotAsync(new(context?.ProjectPath ?? path), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await _projectLauncher.OpenAsync(path, settings.Effective(BuiltInSettingsCatalog.ExternalEditor));
        await _projectCatalogService.RegisterAsync(path, opened: true, cancellationToken: cancellationToken);
        await LoadProjectsAsync(cancellationToken);
    }

    [RelayCommand]
    private Task RefreshProjectsAsync() => RunAsync("刷新项目列表", cancellationToken => LoadProjectsAsync(cancellationToken));

    protected override Task OnFirstActivatedAsync()
    {
        HookSelectionChanges(SelectedProjects);
        return InitializePageAsync();
    }

    private async Task InitializePageAsync()
    {
        if (_workspaceService is not null)
        {
            await _workspaceService.EnsureInitializedAsync();
        }

        await RefreshProjectsAsync();
    }

    private async Task LoadProjectsAsync(CancellationToken cancellationToken = default, long? expectedGeneration = null)
    {
        var snapshot = await _projectCatalogService.GetAllAsync(cancellationToken);
        if (expectedGeneration is { } expected && expected != Volatile.Read(ref _workspaceGeneration)) return;
        using var performance = _performanceMetrics?.Begin("projects.list.publish", snapshot.Count, "ui-batch");
        Projects.ReplaceRange(snapshot);

        if (_workspaceContext is { } context)
        {
            SelectedProject = snapshot.FirstOrDefault(project => PathsEqual(project.Path, context.ProjectPath));
        }

        OnPropertyChanged(nameof(ProjectCount));
        OnPropertyChanged(nameof(AttentionBadge));
        NotifyCopyCommands();
    }

    // ===== Copy commands (side-bar 复制选中 / 复制全部) =====

    [RelayCommand(CanExecute = nameof(CanCopySelectedProjects))]
    private void CopySelectedProjects() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedProjects.Select(project => project.Path)));

    private bool CanCopySelectedProjects() => SelectedProjects.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllProjects))]
    private void CopyAllProjects() =>
        _clipboard.SetText(string.Join(Environment.NewLine, Projects.Select(project => project.Path)));

    private bool CanCopyAllProjects() => Projects.Count > 0;

    private async Task<(EnvironmentProfile Profile, IReadOnlyList<CoreRuntime> Runtimes, IReadOnlyList<EnvironmentCheckResult> Results)> EvaluateAsync(
        string path,
        ProjectWorkspaceContext? context,
        CancellationToken cancellationToken = default)
    {
        EnsureProjectContextCurrent(context, path, cancellationToken);
        var profile = await _profileService.LoadAsync(path, cancellationToken);
        EnsureProjectContextCurrent(context, path, cancellationToken);
        var runtimes = await _inventoryService.RefreshForcedAsync(cancellationToken);
        EnsureProjectContextCurrent(context, path, cancellationToken);
        var results = _checkEngine.Check(profile, runtimes);
        await _projectCatalogService.RegisterAsync(path, profile, results, runtimes, cancellationToken: cancellationToken);
        EnsureProjectContextCurrent(context, path, cancellationToken);
        return (profile, runtimes, results);
    }

    private void UpdateCheckResults(IReadOnlyList<EnvironmentCheckResult> results, Guid? correlationId = null)
    {
        using var performance = _performanceMetrics?.Begin("projects.check.publish", results.Count, "ui-batch");
        CheckResults.ReplaceRange(results);
        foreach (var result in results)
            LogService.Write(result.Status.ToString().ToUpperInvariant(), FormatCheckResult(result), correlationId);

        OnPropertyChanged(nameof(HasCheckResults));
        OnPropertyChanged(nameof(IsCheckResultsEmpty));
        OnPropertyChanged(nameof(PlanRepairTooltip));
        OnPropertyChanged(nameof(ApplyRepairTooltip));
        PlanRepairCommand.NotifyCanExecuteChanged();
        ProfileSummary = results.Count == 0
            ? "未声明环境要求"
            : $"{results.Count(result => result.Status == EnvironmentCheckStatus.Pass)}/{results.Count} 项环境要求已满足";
    }

    private static string FormatCheckResult(EnvironmentCheckResult result)
    {
        var level = result.Status switch
        {
            EnvironmentCheckStatus.Fail => "失败",
            EnvironmentCheckStatus.Warning => "警告",
            _ => "通过"
        };
        var installed = string.IsNullOrWhiteSpace(result.InstalledVersion) ? "未安装" : result.InstalledVersion;
        var suggestion = result.Reason switch
        {
            EnvironmentCheckReason.Missing => "请安装满足要求的组件，或生成修复计划。",
            EnvironmentCheckReason.VersionMismatch => "请升级/降级到满足要求的版本后重新检查。",
            EnvironmentCheckReason.InvalidConstraint => "请修正 Nornia.yaml 中的版本约束。",
            _ => "无需处理。"
        };
        return string.Join(Environment.NewLine,
        [
            $"环境检查：{result.Component}（{level}）",
            $"  要求版本：{result.RequiredVersion}",
            $"  已安装版本：{installed}",
            $"  原因：{result.Message}",
            $"  建议：{suggestion}"
        ]);
    }

    private void WriteRepairPlan(EnvironmentRepairPlan plan, Guid? correlationId)
    {
        foreach (var action in plan.Actions.Where(action => action.Disposition != EnvironmentRepairDisposition.NoAction))
        {
            var operation = action.Operation;
            var details = operation is null
                ? $"  处理方式：手动处理{Environment.NewLine}  原因：{action.Message}"
                : string.Join(Environment.NewLine,
                [
                    $"  处理方式：自动安装/升级",
                    $"  Provider：{operation.PreferredProvider ?? "自动选择"}",
                    $"  包：{operation.PackageId}",
                    $"  目标版本：{operation.TargetVersion}",
                    $"  说明：{action.Message}"
                ]);
            LogService.Write("INFO", $"修复计划：{action.Component}{Environment.NewLine}{details}", correlationId);
        }
    }

    private static string DescribePlan(EnvironmentRepairPlan plan)
    {
        var automatic = plan.Actions.Count(action => action.Disposition == EnvironmentRepairDisposition.InstallOrUpgrade);
        var manual = plan.Actions.Count(action => action.Disposition == EnvironmentRepairDisposition.Manual);
        return plan.Actions.Count == 0
            ? "未声明环境要求"
            : $"修复计划：{automatic} 项可自动修复，{manual} 项需要手动处理";
    }

    /// <summary>Records the current repair plan: refreshes the summary line and toggles the
    /// "应用修复" action based on whether automatic operations are available.</summary>
    private void SetRepairPlan(EnvironmentRepairPlan plan)
    {
        RepairSummary = DescribePlan(plan);
        HasAutomaticRepair = plan.Operations.Count > 0;
        OnPropertyChanged(nameof(ApplyRepairTooltip));
        ApplyRepairCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Clears check results and repair state (used when the current project context is
    /// removed).</summary>
    private void ResetRepairState()
    {
        CheckResults.Clear();
        OnPropertyChanged(nameof(HasCheckResults));
        OnPropertyChanged(nameof(IsCheckResultsEmpty));
        HasAutomaticRepair = false;
        RepairSummary = string.Empty;
        PlanRepairCommand.NotifyCanExecuteChanged();
        ApplyRepairCommand.NotifyCanExecuteChanged();
        ProfileSummary = "尚未加载项目环境";
    }

    /// <summary>Removes a registered project record. Accepts the row the command was invoked from
    /// (VS Code-style per-row action) and falls back to the current selection for the legacy flow.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveProject))]
    private Task RemoveProjectAsync(ProjectAsset? project) => RunAsync("移除项目记录", async () =>
    {
        project ??= SelectedProject ?? throw new InvalidOperationException("请先选择一个项目。");
        if (!_confirmationService.Confirm("确认移除项目记录", $"将从 Nornia 中移除项目记录：{Environment.NewLine}{Environment.NewLine}{project.Name}{Environment.NewLine}{project.Path}{Environment.NewLine}{Environment.NewLine}不会删除项目目录或其中的文件。"))
        {
            LogService.Write("INFO", "用户取消了项目记录移除。");
            return;
        }

        OnPropertyChanged(nameof(IsCheckResultsEmpty));
        await _projectCatalogService.RemoveAsync(project.Id);
        if (string.Equals(ProjectPath, project.Path, StringComparison.OrdinalIgnoreCase))
        {
            ResetRepairState();
        }

        if (ReferenceEquals(SelectedProject, project))
        {
            SelectedProject = null;
        }

        await LoadProjectsAsync();
    });

    partial void OnSelectedProjectChanged(ProjectAsset? value)
    {
        if (value is not null)
        {
            ProjectPath = value.Path;
            ProjectName = value.Name;
            ProfileSummary = value.PathStatus == ProjectPathStatus.Missing
                ? "项目目录不存在，保留历史记录"
                : $"上次环境状态：{value.LastEnvironmentStatus}";
            HasAutomaticRepair = false;
            RepairSummary = string.Empty;
            ApplyRepairCommand.NotifyCanExecuteChanged();
            if (_workspaceService is not null && Directory.Exists(value.Path)
                && !PathsEqual(value.Path, _workspaceContext?.ProjectPath))
            {
                _ = ActivateWorkspaceSafelyAsync(value.Path);
            }
        }

        NotifyProjectCommands();
    }

    private async Task ActivateWorkspaceSafelyAsync(string path)
    {
        try
        {
            await _workspaceService!.ActivateAsync(path);
        }
        catch (Exception ex)
        {
            LogService.WriteException("ERROR", $"切换工作区失败：{path}", ex);
        }
    }

    partial void OnProjectPathChanged(string value)
    {
        HasAutomaticRepair = false;
        RepairSummary = string.Empty;
        OnPropertyChanged(nameof(InitializeTooltip));
        OnPropertyChanged(nameof(CheckTooltip));
        OnPropertyChanged(nameof(PlanRepairTooltip));
        OnPropertyChanged(nameof(ApplyRepairTooltip));
        ApplyRepairCommand.NotifyCanExecuteChanged();
    }

    private bool CanInitialize() => Directory.Exists(ProjectPath)
        && !string.IsNullOrWhiteSpace(ProjectName)
        && !File.Exists(Path.Combine(ProjectPath, "Nornia.yaml"));

    private bool CanInspect() => Directory.Exists(ProjectPath)
        && File.Exists(Path.Combine(ProjectPath, "Nornia.yaml"));

    private bool CanPlanRepair() => CanInspect() && HasCheckResults;
    private bool CanApplyRepair() => CanInspect() && HasAutomaticRepair;
    private bool CanOpen() => Directory.Exists(ProjectPath);
    private bool CanRemoveProject(ProjectAsset? project) => project is not null || SelectedProject is not null;

    private void NotifyProjectCommands()
    {
        InitializeCommand.NotifyCanExecuteChanged();
        CheckCommand.NotifyCanExecuteChanged();
        PlanRepairCommand.NotifyCanExecuteChanged();
        ApplyRepairCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        RemoveProjectCommand.NotifyCanExecuteChanged();
    }

    public string InitializeTooltip
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProjectPath)) return "请先选择项目目录";
            if (File.Exists(Path.Combine(ProjectPath, "Nornia.yaml"))) return "项目已存在 Nornia.yaml，如需重新生成请先删除现有文件";
            return "生成 Nornia.yaml 环境声明（项目没有声明文件时可用）";
        }
    }

    public string CheckTooltip
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProjectPath)) return "请先选择项目目录";
            if (!File.Exists(Path.Combine(ProjectPath, "Nornia.yaml"))) return "项目没有 Nornia.yaml，请先初始化环境";
            return "按 Nornia.yaml 检查本机环境差异";
        }
    }

    public string PlanRepairTooltip
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProjectPath)) return "请先选择项目目录";
            if (!File.Exists(Path.Combine(ProjectPath, "Nornia.yaml"))) return "项目没有 Nornia.yaml，请先初始化环境或检查";
            if (!HasCheckResults) return "请先运行「检查环境」以了解版本差异";
            return "生成不修改系统的修复计划；需先检查环境";
        }
    }

    public string ApplyRepairTooltip
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProjectPath)) return "请先选择项目目录";
            if (HasCheckResults && CheckResults.Count == 0) return "环境已符合声明，无需修复";
            if (!HasCheckResults) return "请先运行「检查环境」后再应用修复";
            if (!HasAutomaticRepair) return "当前没有可应用的修复计划";
            return "按修复计划安装或升级组件；执行前会确认";
        }
    }

    public void ApplyNavigationContext(NavigationContext? context)
    {
        if (context is null)
        {
            SelectedWorkspaceTab = 0;
            WorkflowHint = "1. 选择项目目录  2. 初始化或检查  3. 预览修复计划  4. 确认后应用";
            return;
        }

        switch (context)
        {
            case NavigationContext.History:
                SelectedWorkspaceTab = 1;
                WorkflowHint = "查看项目历史记录";
                break;
            case NavigationContext.CheckProject(var path):
                SelectedWorkspaceTab = 0;
                WorkflowHint = "已从 Dashboard 选中项目；运行环境检查，Nornia 会给出版本差异。";
                _ = ApplyProjectNavigationAsync(path, doCheck: true, doRepair: false);
                break;
            case NavigationContext.RepairProject(var path):
                SelectedWorkspaceTab = 0;
                WorkflowHint = "已从 Dashboard 选中项目；现在可以直接生成修复计划（计划本身不会修改系统）。";
                _ = ApplyProjectNavigationAsync(path, doCheck: false, doRepair: true);
                break;
            case NavigationContext.NewProject(var path):
                SelectedWorkspaceTab = 0;
                WorkflowHint = "选择项目目录；没有 Nornia.yaml 时可初始化环境声明。";
                _ = ApplyProjectNavigationAsync(path, doCheck: false, doRepair: false);
                break;
            case NavigationContext.Check:
                SelectedWorkspaceTab = 0;
                WorkflowHint = "已从 Dashboard 选中项目；运行环境检查，Nornia 会给出版本差异。";
                break;
            case NavigationContext.Repair:
                SelectedWorkspaceTab = 0;
                WorkflowHint = "已从 Dashboard 选中项目；现在可以直接生成修复计划（计划本身不会修改系统）。";
                break;
            case NavigationContext.New:
                SelectedWorkspaceTab = 0;
                WorkflowHint = "选择项目目录；没有 Nornia.yaml 时可初始化环境声明。";
                break;
            default:
                SelectedWorkspaceTab = 0;
                WorkflowHint = "1. 选择项目目录  2. 初始化或检查  3. 预览修复计划  4. 确认后应用";
                break;
        }
    }

    private async Task ApplyProjectNavigationAsync(string? projectPath, bool doCheck, bool doRepair)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return;

        if (_workspaceService is not null && Directory.Exists(projectPath))
        {
            await _workspaceService.ActivateAsync(projectPath);
        }

        if (Projects.Count == 0)
        {
            try { await LoadProjectsAsync(); }
            catch { /* ignore: user can still pick the path manually */ }
        }

        var matched = Projects.FirstOrDefault(proj =>
            string.Equals(proj.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
        {
            SelectedProject = matched;
        }
        else
        {
            ProjectPath = projectPath;
            ProjectName = new DirectoryInfo(projectPath).Name;
        }

        if (doCheck && CanInspect())
        {
            await CheckAsync();
        }
        else if (doRepair && CanInspect())
        {
            await PlanRepairAsync();
        }
    }

    private async Task ActivateAndNavigateToExplorerAsync(string path)
    {
        try
        {
            await _workspaceService!.ActivateAsync(path);
            _navigationService.Navigate(NavigationTargets.Explorer, (string?)null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogService.Write("ERROR", $"无法打开项目 {path}：{ex.Message}");
        }
    }

    private async Task ApplyWorkspaceContextAsync(ProjectWorkspaceContext? context)
    {
        var generation = Interlocked.Increment(ref _workspaceGeneration);
        await _workspaceContextGate.WaitAsync();
        try
        {
            _workspaceContext = context;
            if (context is null)
            {
                SelectedProject = null;
                ProjectPath = string.Empty;
                ProjectName = string.Empty;
                ResetRepairState();
                return;
            }

            ProjectPath = context.ProjectPath;
            ProjectName = context.Project.Name;
            ResetRepairState();
            await LoadProjectsAsync(expectedGeneration: generation);
        }
        finally
        {
            _workspaceContextGate.Release();
        }
    }
}
