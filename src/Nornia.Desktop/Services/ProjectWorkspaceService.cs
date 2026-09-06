using Nornia.Core.Models;
using Nornia.Project.Services;
using System.IO;
using Nornia.Desktop.Configuration;

namespace Nornia.Desktop.Services;

/// <summary>Single source of truth for the directory currently open in the desktop workbench.
/// The project directory is deliberately distinct from the Git root: a project may live inside a
/// larger repository.</summary>
public sealed record ProjectWorkspaceContext(
    ProjectAsset Project,
    string ProjectPath,
    string? GitRepositoryPath,
    long Generation = 0);

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
    private long _generation;
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
            await BindRestoreSessionAsync(cancellationToken);
            await TryRestoreLastWorkspaceAsync(cancellationToken);
            _initialized = true;
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
        var session = await settingsService.OpenSessionAsync(new(),
            [BuiltInSettingsCatalog.RestoreLastWorkspace.Id], cancellationToken);
        _restoreSession = session;
        _restoreSessionBound = true;
        session.Changed += (_, _) => _ = HandleRestoreSessionChangedAsync();
    }

    private async Task HandleRestoreSessionChangedAsync()
    {
        try
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
        }
        catch (Exception ex)
        {
            // Settings notifications are synchronous .NET events. Keep the async continuation
            // self-observing so a transient settings/file-system failure cannot become an
            // unhandled async-void exception or poison future manual activation.
            System.Diagnostics.Trace.WriteLine($"自动恢复工作区失败：{ex}");
        }
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
            try
            {
                await ActivateCoreAsync(fallback, cancellationToken);
            }
            catch
            {
                IsStartupAutoRestore = false;
                throw;
            }
        }
    }

    public async Task ActivateAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 用户主动激活 → 清除启动标记,恢复编辑器布局回放。
            IsStartupAutoRestore = false;
            await ActivateCoreAsync(path, cancellationToken);
            // 只有一次完整激活成功后才阻止后续的自动初始化;无效路径或失败的上下文
            // 发布仍允许下一次 EnsureInitializedAsync 重试。
            _initialized = true;
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
        var next = new ProjectWorkspaceContext(project, projectPath, gitRoot,
            Interlocked.Increment(ref _generation));
        await stateStore.CommitAsync(new([
            new(ApplicationStateField.LastWorkspace, projectPath),
        ]), cancellationToken);

        var previous = Current;
        Current = next;
        try
        {
            await PublishAsync(next);
        }
        catch
        {
            // Context consumers update several independent projections. If one consumer fails,
            // leave the service and the persisted last-workspace value on the same context and
            // give every consumer a fresh rollback generation so in-flight work from the failed
            // context cannot be accepted after the rollback.
            var rollback = previous is null
                ? null
                : previous with { Generation = Interlocked.Increment(ref _generation) };
            Current = rollback;
            try
            {
                await stateStore.CommitAsync(new([
                    new(ApplicationStateField.LastWorkspace, rollback?.ProjectPath),
                ]), CancellationToken.None);
            }
            catch
            {
                // The in-memory rollback is still valuable when the state file is unavailable.
            }

            try
            {
                await PublishAsync(rollback);
            }
            catch
            {
                // Preserve the original activation failure; consumers are best-effort during
                // rollback and the service remains on the rollback context above.
            }

            throw;
        }
    }

    private async Task PublishAsync(ProjectWorkspaceContext? context)
    {
        var handlers = ContextChanged;
        if (handlers is null) return;

        List<Exception>? failures = null;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<ProjectWorkspaceContext?, Task>>())
        {
            try
            {
                await handler(context);
            }
            catch (Exception ex)
            {
                // A single page must not prevent the remaining projections from moving to the
                // same context. The caller still receives the failure and can roll back.
                (failures ??= []).Add(ex);
            }
        }

        if (failures is { Count: 1 })
        {
            throw failures[0];
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException("一个或多个工作区上下文订阅者切换失败。", failures);
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
