using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Models;
using Nornia.Desktop.Services;
using Nornia.Project.Services;
using System.IO;

namespace Nornia.Desktop.ViewModels;

/// <summary>Top-level "资源管理器" page (VS Code-style Explorer). Its secondary left sidebar is the
/// workspace tree (<see cref="WorkspaceViewModel"/>); its main content is the shared editor area.
/// Opening a project keeps the explorer, terminal, git repository and the project workflow pointed
/// at the same root, exactly like a VS Code workbench.</summary>
public partial class ExplorerPageViewModel : PageViewModel, INavigationTarget
{
    private readonly IProjectCatalogService _catalogService;
    private readonly IFolderPickerService _folderPicker;
    private readonly IDesktopNavigationService _navigation;
    private IProjectWorkspaceService? _workspaceService;
    private GitRepositoryStatus? _latestGitStatus;
    private CancellationTokenSource? _activeEditorRevealCancellation;

    public WorkspaceViewModel Explorer { get; }
    public EditorAreaViewModel Editor { get; }
    public TerminalViewModel Terminal { get; }
    public GitViewModel Git { get; }
    public ProjectsViewModel Projects { get; }

    [ObservableProperty] private ProjectAsset? currentProject;

    public ExplorerPageViewModel(
        WorkspaceViewModel explorer,
        EditorAreaViewModel editor,
        TerminalViewModel terminal,
        GitViewModel git,
        ProjectsViewModel projects,
        IProjectCatalogService catalogService,
        IFolderPickerService folderPicker,
        IDesktopNavigationService navigation,
        IUiLogService logService) : base("资源管理器", logService)
    {
        Explorer = explorer;
        Editor = editor;
        Terminal = terminal;
        Git = git;
        Projects = projects;
        _catalogService = catalogService;
        _folderPicker = folderPicker;
        _navigation = navigation;
        // Integration wiring: the explorer and source control share one editor area, one status map
        // and one project root, exactly like a VS Code workbench.
        Explorer.WorkspaceOpened += OnWorkspaceOpened;
        Explorer.WorkspaceSelectionRequested += (_, path) => _ = OpenProjectPathAsync(path);
        Explorer.FileOpenRequested += (_, path) => _ = OpenExplorerFileAsync(path);
        Explorer.FileOpenPermanentRequested += (_, path) => _ = OpenExplorerFileAsync(path, permanent: true);
        Editor.SelectedTabChanged += (_, _) => QueueActiveEditorReveal();
        Explorer.DiffOpenRequested += async (_, node) =>
        {
            var info = Explorer.GetGitInfo(node.RelativePath);
            if (info is null)
            {
                return;
            }

            await Editor.OpenDiffAsync(new GitDiffRequest(
                Git.RepositoryPath, ToRepositoryRelativePath(node.RelativePath), info.Value.IsStaged, info.Value.IsUntracked));
        };
        Git.StatusRefreshed += OnGitStatusRefreshed;
        Git.RevealRequested += (_, path) =>
        {
            var projectPath = ToProjectRelativePath(path);
            if (projectPath is null) return;
            _navigation.Navigate(NavigationTargets.Explorer, (string?)null);
            _ = Explorer.RevealNodeAsync(projectPath);
        };
    }

    /// <summary>Production constructor: all directory changes are mediated by the shared project
    /// workspace service. The legacy constructor above remains for the existing isolated tests.</summary>
    public ExplorerPageViewModel(
        WorkspaceViewModel explorer,
        EditorAreaViewModel editor,
        TerminalViewModel terminal,
        GitViewModel git,
        ProjectsViewModel projects,
        IProjectCatalogService catalogService,
        IFolderPickerService folderPicker,
        IDesktopNavigationService navigation,
        IUiLogService logService,
        IProjectWorkspaceService workspaceService)
        : this(explorer, editor, terminal, git, projects, catalogService, folderPicker, navigation, logService)
    {
        _workspaceService = workspaceService;
        _workspaceService.ContextChanged += ApplyWorkspaceContextAsync;
    }

    public bool HasCurrentProject => CurrentProject is not null;
    public string CurrentProjectName => CurrentProject?.Name ?? "未打开项目";
    public string CurrentProjectPath => CurrentProject?.Path ?? string.Empty;
    public EnvironmentHealthStatus CurrentProjectStatus => CurrentProject?.LastEnvironmentStatus ?? EnvironmentHealthStatus.Unknown;
    public string CurrentProjectStatusGlyph => CurrentProjectStatus switch
    {
        EnvironmentHealthStatus.Healthy => Codicons.Check,
        EnvironmentHealthStatus.NeedsAttention => Codicons.Warning,
        _ => Codicons.Info,
    };
    public string CurrentProjectStatusText => CurrentProjectStatus switch
    {
        EnvironmentHealthStatus.Healthy => "环境正常",
        EnvironmentHealthStatus.NeedsAttention => "环境需处理",
        _ => "环境未检查",
    };

    /// <summary>The secondary left sidebar shows the workspace tree (WorkspaceView).</summary>
    public override object? Sidebar => this;

    protected override async Task OnFirstActivatedAsync()
    {
        if (_workspaceService is not null)
        {
            await _workspaceService.EnsureInitializedAsync();
            return;
        }

        await Explorer.ActivateAsync();
    }

    /// <summary>The single entry point for opening a project: loads the Explorer tree (which also
    /// syncs the terminal working directory and the git repository), registers the project in the
    /// catalog, points the project workflow at the same directory and refreshes the project view +
    /// activity badge.</summary>
    public async Task OpenProjectPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (_workspaceService is not null)
            {
                await _workspaceService.ActivateAsync(path);
                return;
            }

            await Explorer.OpenWorkspaceAsync(path);
            if (_latestGitStatus is { } status)
            {
                Explorer.ApplyGitStatus(ProjectRelativeStatus(status));
            }
            var project = await _catalogService.RegisterAsync(path, opened: true);
            CurrentProject = project;
            // Keep the project workflow (and its "检查环境/修复" actions) pointed at the opened
            // directory; otherwise the Explorer "检查环境" button can inspect the wrong folder.
            Projects.ProjectPath = path;
            Projects.ProjectName = project.Name;
            await Projects.RefreshCatalogAsync();
            LogService.Write("INFO", $"已打开项目：{project.Name} ({path})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogService.Write("ERROR", $"无法打开项目 {path}：{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenProjectAsync(ProjectAsset? project)
    {
        if (project is null)
        {
            LogService.Write("INFO", "尚未选择项目。");
            return;
        }

        if (!Directory.Exists(project.Path))
        {
            LogService.Write("WARNING", $"项目目录不存在：{project.Path}");
            return;
        }

        await OpenProjectPathAsync(project.Path);
    }

    private bool CanOpenProject(ProjectAsset? project) => project is not null && Directory.Exists(project.Path);

    [RelayCommand]
    private async Task ChooseProjectAsync()
    {
        var selected = _folderPicker.PickFolder(Explorer.WorkspacePath);
        if (selected is not null) await OpenProjectPathAsync(selected);
    }

    [RelayCommand]
    private void CheckCurrentProject()
    {
        if (CurrentProject is null)
        {
            LogService.Write("INFO", "尚未打开项目。");
            return;
        }

        if (!Projects.CheckCommand.CanExecute(null))
        {
            LogService.Write("INFO", "当前项目未声明 Nornia.yaml，无法检查环境。");
            return;
        }

        _navigation.Navigate(NavigationTargets.Projects, $"check:project={CurrentProject.Path}");
    }

    /// <summary>Navigation context from the project catalog ("打开" row) or the git "在资源管理器中
    /// 显示" flow: a project path opens the workspace here; anything else is ignored.</summary>
    public void ApplyNavigationContext(NavigationContext? context)
    {
        switch (context)
        {
            case NavigationContext.OpenInExplorer(var path): _ = OpenProjectPathAsync(path); break;
            case NavigationContext.OpenProject(var projectPath): _ = OpenProjectPathAsync(projectPath); break;
        }
    }

    private void OnWorkspaceOpened(object? sender, string root)
    {
        Terminal.WorkingDirectory = root;
        // 布局恢复可能先于工作区树加载完成；树就绪后再同步一次当前活动文件。
        QueueActiveEditorReveal();
    }

    /// <summary>活动编辑器变化时在资源管理器树中自动定位对应文件。快速切换标签时取消旧定位，
    /// 防止较慢的目录枚举完成后把选区倒退到已经失活的文件。</summary>
    private void QueueActiveEditorReveal()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _activeEditorRevealCancellation, cancellation);
        previous?.Cancel();
        _ = RevealActiveEditorAsync(cancellation);
    }

    private async Task RevealActiveEditorAsync(CancellationTokenSource cancellation)
    {
        try
        {
            var relativePath = ActiveEditorProjectRelativePath();
            if (relativePath is not null)
            {
                await Explorer.RevealNodeAsync(relativePath, cancellation.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogService.Write("WARNING", $"无法在资源管理器中定位活动文件：{ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeEditorRevealCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private string? ActiveEditorProjectRelativePath()
    {
        if (string.IsNullOrWhiteSpace(Explorer.WorkspacePath))
        {
            return null;
        }

        var fullPath = Editor.SelectedTab switch
        {
            FilePreviewTab file => file.Path,
            DiffTab diff => Path.Combine(diff.Request.RepositoryPath, diff.Request.Path),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(Explorer.WorkspacePath), Path.GetFullPath(fullPath));
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }

    /// <summary>Explorer events cannot await the preview operation. Keep failures at this boundary
    /// from becoming dispatcher-level exceptions that close the desktop application.</summary>
    private async Task OpenExplorerFileAsync(string path, bool permanent = false)
    {
        try
        {
            await Editor.OpenFileAsync(path, permanent);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            LogService.Write("ERROR", $"无法打开文件 {path}：{ex.Message}");
        }
    }

    private async Task ApplyWorkspaceContextAsync(ProjectWorkspaceContext? context)
    {
        CurrentProject = context?.Project;
        if (context is null) return;

        await Explorer.OpenWorkspaceAsync(context.ProjectPath);
        // Git may publish its status before this handler opens the explorer tree because both
        // listen to the same workspace-context event. Reapply the latest status after the tree
        // exists; OpenWorkspaceAsync clears the previous repository's decorations by design.
        if (_latestGitStatus is { } status)
        {
            Explorer.ApplyGitStatus(ProjectRelativeStatus(status));
        }
        Terminal.WorkingDirectory = context.ProjectPath;
    }

    private void OnGitStatusRefreshed(object? sender, GitRepositoryStatus status)
    {
        _latestGitStatus = status;
        Explorer.ApplyGitStatus(ProjectRelativeStatus(status));
    }

    /// <summary>Git reports paths relative to its repository root. When a project is a directory
    /// inside that repository, only project descendants belong in the Explorer decorations.</summary>
    private GitRepositoryStatus ProjectRelativeStatus(GitRepositoryStatus status)
    {
        var context = _workspaceService?.Current;
        if (context?.GitRepositoryPath is not { Length: > 0 } gitRoot || !status.IsRepository)
        {
            return status;
        }

        var relativeProjectPath = Path.GetRelativePath(gitRoot, context.ProjectPath).Replace('\\', '/').Trim('/');
        if (relativeProjectPath.Length == 0 || relativeProjectPath == ".") return status;
        var prefix = relativeProjectPath + "/";
        GitFileChange? Rebase(GitFileChange change)
        {
            if (!change.Path.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var original = change.OriginalPath;
            if (!string.IsNullOrWhiteSpace(original) && original.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                original = original[prefix.Length..];
            }

            return change with { Path = change.Path.Replace('\\', '/')[prefix.Length..], OriginalPath = original };
        }

        return status with
        {
            StagedChanges = status.StagedChanges.Select(Rebase).OfType<GitFileChange>().ToArray(),
            UnstagedChanges = status.UnstagedChanges.Select(Rebase).OfType<GitFileChange>().ToArray()
        };
    }

    private string ToRepositoryRelativePath(string projectRelativePath)
    {
        var context = _workspaceService?.Current;
        if (context?.GitRepositoryPath is not { Length: > 0 } gitRoot) return projectRelativePath;
        var relativeProjectPath = Path.GetRelativePath(gitRoot, context.ProjectPath).Replace('\\', '/').Trim('/');
        return relativeProjectPath.Length == 0 || relativeProjectPath == "."
            ? projectRelativePath
            : $"{relativeProjectPath}/{projectRelativePath}";
    }

    private string? ToProjectRelativePath(string repositoryRelativePath)
    {
        var context = _workspaceService?.Current;
        if (context?.GitRepositoryPath is not { Length: > 0 } gitRoot) return repositoryRelativePath;
        var relativeProjectPath = Path.GetRelativePath(gitRoot, context.ProjectPath).Replace('\\', '/').Trim('/');
        if (relativeProjectPath.Length == 0 || relativeProjectPath == ".") return repositoryRelativePath;
        var prefix = relativeProjectPath + "/";
        var normalized = repositoryRelativePath.Replace('\\', '/');
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : null;
    }

    partial void OnCurrentProjectChanged(ProjectAsset? value)
    {
        OnPropertyChanged(nameof(HasCurrentProject));
        OnPropertyChanged(nameof(CurrentProjectName));
        OnPropertyChanged(nameof(CurrentProjectPath));
        OnPropertyChanged(nameof(CurrentProjectStatus));
        OnPropertyChanged(nameof(CurrentProjectStatusGlyph));
        OnPropertyChanged(nameof(CurrentProjectStatusText));
    }
}
