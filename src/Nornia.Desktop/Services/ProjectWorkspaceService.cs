using Nornia.Core.Models;
using Nornia.Project.Services;
using System.IO;
using Nornia.Desktop.Configuration;

namespace Nornia.Desktop.Services;

/// <summary>Single source of truth for the directory currently open in the desktop workbench.
/// The project directory is deliberately distinct from the Git root: a project may live inside a
/// larger repository.</summary>
public sealed record ProjectWorkspaceContext(ProjectAsset Project, string ProjectPath, string? GitRepositoryPath);

public interface IProjectWorkspaceService
{
    ProjectWorkspaceContext? Current { get; }

    /// <summary>本次工作区激活是否来自启动时的自动恢复(<see cref="EnsureInitializedAsync"/> 在
    /// <c>恢复上次工作区</c> 开启时自动激活上次目录)。用户主动 <see cref="ActivateAsync"/> 后为
    /// <c>false</c>。供编辑器区判断是否跳过"启动自动回放源码文件标签"。</summary>
    bool IsStartupAutoRestore { get; }

    event Func<ProjectWorkspaceContext?, Task>? ContextChanged;
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);
    Task ActivateAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class ProjectWorkspaceService(
    IProjectCatalogService projectCatalogService,
    ISettingsService settingsService,
    IApplicationStateStore stateStore) : IProjectWorkspaceService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private ISettingsSession? _restoreSession;
    private bool _restoreSessionBound;

    public ProjectWorkspaceContext? Current { get; private set; }
    public bool IsStartupAutoRestore { get; private set; }
    public event Func<ProjectWorkspaceContext?, Task>? ContextChanged;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            _initialized = true;
            await BindRestoreSessionAsync(cancellationToken);
            await TryRestoreLastWorkspaceAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>订阅"恢复上次工作区":会话中把开关打开且当前没有工作区时,立即尝试恢复
    /// 最近工作区;关闭时保留当前打开的工作区,仅停止后续自动恢复(启动恢复已在此会话完成)。</summary>
    private async Task BindRestoreSessionAsync(CancellationToken cancellationToken)
    {
        if (_restoreSessionBound) return;
        _restoreSessionBound = true;
        _restoreSession = await settingsService.OpenSessionAsync(new(),
            [BuiltInSettingsCatalog.RestoreLastWorkspace.Id], cancellationToken);
        _restoreSession.Changed += async (_, _) =>
        {
            if (_restoreSession?.Current is not { } snapshot ||
                !snapshot.Effective(BuiltInSettingsCatalog.RestoreLastWorkspace) ||
                Current is not null)
            {
                return;
            }

            await _gate.WaitAsync();
            try
            {
                if (Current is not null) return; // 等待门闩期间用户已手动打开工作区
                await TryRestoreLastWorkspaceAsync(CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }
        };
    }

    private async Task TryRestoreLastWorkspaceAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetSnapshotAsync(new(), cancellationToken);
        if (!settings.Effective(BuiltInSettingsCatalog.RestoreLastWorkspace))
        {
            return;
        }

        var fallback = (await stateStore.LoadAsync(cancellationToken)).LastWorkspace;

        if ((string.IsNullOrWhiteSpace(fallback) || !Directory.Exists(fallback)))
        {
            fallback = (await projectCatalogService.GetAllAsync(cancellationToken))
                .FirstOrDefault(project => project.PathStatus == ProjectPathStatus.Available && Directory.Exists(project.Path))?.Path;
        }

        if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
        {
            // 自动恢复(启动或会话中开关打开):标记来源,让编辑器区跳过"回放源码文件标签"。
            IsStartupAutoRestore = true;
            await ActivateCoreAsync(fallback, cancellationToken);
        }
    }

    public async Task ActivateAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _initialized = true;
            // 用户主动激活 → 清除启动标记,恢复编辑器布局回放。
            IsStartupAutoRestore = false;
            await ActivateCoreAsync(path, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ActivateCoreAsync(string path, CancellationToken cancellationToken)
    {
        var projectPath = NormalizeProjectPath(path);
        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException($"项目目录不存在：{projectPath}");
        }

        var project = await projectCatalogService.RegisterAsync(projectPath, opened: true, cancellationToken: cancellationToken);
        var gitRoot = FindGitRoot(projectPath);
        var next = new ProjectWorkspaceContext(project, projectPath, gitRoot);
        await stateStore.CommitAsync(new([
            new(ApplicationStateField.LastWorkspace, projectPath),
        ]), cancellationToken);
        Current = next;
        await PublishAsync(next);
    }

    private async Task PublishAsync(ProjectWorkspaceContext? context)
    {
        var handlers = ContextChanged;
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Func<ProjectWorkspaceContext?, Task>>())
        {
            await handler(context);
        }
    }

    private static string? FindGitRoot(string projectPath)
    {
        for (var current = new DirectoryInfo(projectPath); current is not null; current = current.Parent)
        {
            var gitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath)) return current.FullName;
        }

        return null;
    }

    private static string NormalizeProjectPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath.Length > root.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }
}
