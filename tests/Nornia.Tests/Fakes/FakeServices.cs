using System.Collections.ObjectModel;
using System.IO;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Services;
using Nornia.Project.Models;
using Nornia.Project.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests.Fakes;

/// <summary>In-memory UiLog used by view model tests. Entries surface through a real
/// <see cref="ObservableCollection{T}"/> so collection-changed wiring can be exercised.</summary>
internal sealed class FakeUiLogService : IUiLogService
{
    private readonly ObservableCollection<UiLogEntry> _entries = [];

    public FakeUiLogService() => Entries = new ReadOnlyObservableCollection<UiLogEntry>(_entries);

    public ReadOnlyObservableCollection<UiLogEntry> Entries { get; }

    public void Write(string level, string message, Guid? correlationId = null) =>
        _entries.Add(new UiLogEntry(DateTimeOffset.Now, level, message, correlationId));

    public void WriteException(string level, string message, Exception exception, Guid? correlationId = null) =>
        _entries.Add(new UiLogEntry(DateTimeOffset.Now, level, ExceptionDiagnosticFormatter.Format(message, exception), correlationId));

    public IProgress<ProcessOutput> CreateProcessProgress(Guid? correlationId = null) =>
        // 与真实 UiLogService 一致:子进程普通 stdout 不入日志,仅错误行(见 UiLogService.CreateProcessProgress)。
        new Progress<ProcessOutput>(output =>
        {
            if (output.IsError)
            {
                Write("ERROR", output.Text, correlationId);
            }
        });

    public void Clear() => _entries.Clear();
}

/// <summary>Deterministic <see cref="IFileContentWatcher"/> stub: records Watch/Unwatch calls and
/// raises <see cref="IFileContentWatcher.FileChanged"/> on demand, so editor reload tests stay
/// deterministic (no real FileSystemWatcher timing).</summary>
internal sealed class FakeFileContentWatcher : IFileContentWatcher
{
    public event EventHandler<FileChangedEventArgs>? FileChanged;

    public List<string> Watched { get; } = [];
    public List<string> Unwatched { get; } = [];
    public bool Disposed { get; private set; }

    public void Watch(string path) => Watched.Add(Path.GetFullPath(path));

    public void Unwatch(string path) => Unwatched.Add(Path.GetFullPath(path));

    public void Raise(string path) => FileChanged?.Invoke(this, new FileChangedEventArgs(Path.GetFullPath(path)));

    public void Dispose() => Disposed = true;
}

internal sealed class FakeClipboardService : IClipboardService
{
    public List<string> WrittenTexts { get; } = [];
    public string? LastText => WrittenTexts.Count == 0 ? null : WrittenTexts[^1];

    public void SetText(string text) => WrittenTexts.Add(text);
}

/// <summary>Watcher fake for the SCM view-model tests: records attach / detach / suppression
/// calls and lets the test raise <see cref="ChangesDetected"/> deterministically.</summary>
internal sealed class FakeGitRepositoryWatcher : IGitRepositoryWatcher
{
    public List<string> AttachedPaths { get; } = [];
    public int DetachCalls { get; private set; }
    public int SuppressionWindows { get; private set; }
    public int OperationSuppressions { get; private set; }
    public bool Disposed { get; private set; }

    public event EventHandler<GitRepositoryChangesDetectedEventArgs>? ChangesDetected;

    public void Attach(string repositoryPath) => AttachedPaths.Add(repositoryPath);

    public void Detach() => DetachCalls++;

    public void BeginSuppressionWindow() => SuppressionWindows++;

    public IDisposable BeginOperationSuppression(IReadOnlyCollection<string>? workingTreePaths = null)
    {
        OperationSuppressions++;
        return NoopDisposable.Instance;
    }

    public void RaiseChangesDetected(
        string repositoryPath = @"C:\repo",
        IReadOnlySet<string>? changedPaths = null,
        bool indexChanged = false,
        bool headOrRefsChanged = false,
        bool isUnknown = false) =>
        ChangesDetected?.Invoke(this, new GitRepositoryChangesDetectedEventArgs(
            repositoryPath,
            changedPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            indexChanged,
            headOrRefsChanged,
            isUnknown));

    public void Dispose() => Disposed = true;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}

/// <summary>Watcher fake for the explorer tree auto-refresh tests: records attach calls and lets
/// the test raise <see cref="FilesChanged"/> deterministically (the production watcher debounces
/// and marshals; the VM tests drive the post-debounce event directly).</summary>
internal sealed class FakeWorkspaceFileWatcher : IWorkspaceFileWatcher
{
    public List<string> AttachedPaths { get; } = [];
    public List<IReadOnlyCollection<string>> WatchedDirectorySets { get; } = [];
    public int DetachCalls { get; private set; }
    public bool Disposed { get; private set; }

    public event EventHandler<WorkspaceFilesChangedEventArgs>? FilesChanged;

    public void Attach(string rootPath) => AttachedPaths.Add(rootPath);

    public void UpdateDirectories(IEnumerable<string> directories) =>
        WatchedDirectorySets.Add(directories.ToArray());

    public void Detach() => DetachCalls++;

    public void RaiseChanged(string rootPath, params string[] fullPaths) =>
        FilesChanged?.Invoke(this, new WorkspaceFilesChangedEventArgs(
            rootPath,
            new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase)));

    public void Dispose() => Disposed = true;
}

internal sealed class FakeRuntimeInventory(
    IReadOnlyList<CoreRuntime> refreshResult,
    IReadOnlyList<CoreRuntime>? persisted = null) : IRuntimeInventoryService
{
    public int RefreshCalls { get; private set; }
    public int ForcedRefreshCalls { get; private set; }

    public Task<IReadOnlyList<CoreRuntime>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCalls++;
        return Task.FromResult(refreshResult);
    }

    public Task<IReadOnlyList<CoreRuntime>> RefreshForcedAsync(CancellationToken cancellationToken = default)
    {
        ForcedRefreshCalls++;
        return Task.FromResult(refreshResult);
    }

    public Task<IReadOnlyList<CoreRuntime>> GetPersistedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(persisted ?? refreshResult);
}

internal sealed class FakeSummaryReader(DashboardSummary summary) : IDashboardSummaryReader
{
    public Task<DashboardSummary> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(summary);
}

internal sealed class FakePackageRepository : IPackageRepository
{
    public int ReplaceCalls { get; private set; }
    public IReadOnlyList<PackageInfo> Packages { get; set; } = [];

    public Task ReplaceSnapshotAsync(IReadOnlyCollection<PackageInfo> packages, long scannedAt, CancellationToken cancellationToken = default)
    {
        ReplaceCalls++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PackageInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Packages);
}

internal sealed class FakeCacheInventory(IReadOnlyList<CacheCandidate> candidates) : ICacheInventoryService
{
    public int ScanCalls { get; private set; }
    public int ForcedScanCalls { get; private set; }
    public int CachedScanCalls { get; private set; }

    public Task<IReadOnlyList<CacheCandidate>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ScanCalls++;
        CachedScanCalls++;
        return Task.FromResult(candidates);
    }

    public Task<IReadOnlyList<CacheCandidate>> ScanForcedAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ScanCalls++;
        ForcedScanCalls++;
        return Task.FromResult(candidates);
    }
}

internal sealed class FakeCacheCleanup : ICacheCleanupService
{
    public List<string> CleanedIds { get; } = [];

    public Task<IReadOnlyList<CacheCleanupResult>> CleanAsync(
        IReadOnlyCollection<string> candidateIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CleanedIds.AddRange(candidateIds);
        return Task.FromResult<IReadOnlyList<CacheCleanupResult>>(candidateIds
            .Select(id => new CacheCleanupResult(id, string.Empty, CacheCleanupStatus.Cleaned, 0, "cleaned"))
            .ToArray());
    }
}

internal sealed class FakePackageProvider : IPackageProvider
{
    public string Name => "Fake";
    public List<string> InstalledPackages { get; } = [];
    public List<string> UninstalledPackages { get; } = [];
    public List<string> UpgradedPackages { get; } = [];
    public IReadOnlyList<PackageInfo> SearchResults { get; set; } = [];
    public IReadOnlyList<PackageInfo> Installed { get; set; } = [];

    public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(SearchResults);

    public Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Installed);

    public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        InstalledPackages.Add(packageId);
        return Task.CompletedTask;
    }

    public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        UninstalledPackages.Add(packageId);
        return Task.CompletedTask;
    }

    public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        UpgradedPackages.Add(packageId);
        return Task.CompletedTask;
    }
}

internal sealed class FakePackageInventory(IReadOnlyList<PackageInfo> packages) : IPackageInventoryService
{
    public int RefreshCalls { get; private set; }
    public int ForcedRefreshCalls { get; private set; }

    public Task<IReadOnlyList<PackageInfo>> RefreshAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        RefreshCalls++;
        return Task.FromResult(packages);
    }

    public Task<IReadOnlyList<PackageInfo>> RefreshForcedAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        ForcedRefreshCalls++;
        return Task.FromResult(packages);
    }

    public Task<IReadOnlyList<PackageInfo>> GetPersistedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(packages);
}

internal sealed class FakePackageResolver : IRuntimePackageResolver
{
    public IReadOnlyList<string> SupportedRuntimeNames { get; init; } = ["visual-cpp-redistributable"];
    public IReadOnlyList<string> SupportedToolNames { get; init; } = ["dotnet", "node"];

    public IReadOnlyList<RuntimePackage> ResolveMany(string runtime, string version) =>
        [new RuntimePackage($"Fake.{runtime}", version)];

    public IReadOnlyList<RuntimePackageOperation>? TryResolveAll(string component, string targetVersion) =>
        [new RuntimePackageOperation(component, targetVersion, $"Fake.{component}", targetVersion)];
}

internal sealed class FakeProfileService : IEnvironmentProfileService
{
    public EnvironmentProfile? LoadResult { get; set; } = new() { Project = new ProjectMetadata { Name = "Test" } };
    public string InitializedPath { get; private set; } = string.Empty;

    public Task<EnvironmentProfile> LoadAsync(string projectPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(LoadResult ?? throw new FileNotFoundException("Nornia.yaml"));

    public Task<string> InitializeAsync(string projectPath, string? projectName = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        InitializedPath = projectPath;
        return Task.FromResult(Path.Combine(projectPath, "Nornia.yaml"));
    }

    public Task SaveAsync(string profilePath, EnvironmentProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ExportAsync(string projectPath, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ImportAsync(string sourcePath, string projectPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public string Serialize(EnvironmentProfile profile) => string.Empty;
}

internal sealed class FakeCheckEngine(IReadOnlyList<EnvironmentCheckResult> results) : IEnvironmentCheckEngine
{
    public IReadOnlyList<EnvironmentCheckResult> Check(EnvironmentProfile profile, IReadOnlyCollection<CoreRuntime> installedRuntimes) => results;

    public Task<EnvironmentCheckRuleReport> CheckWithRulesAsync(
        EnvironmentProfile profile,
        IReadOnlyCollection<CoreRuntime> installedRuntimes,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentCheckRuleReport(results, []));
}

internal sealed class FakeRepairPlanner(EnvironmentRepairPlan plan) : IEnvironmentRepairPlanner
{
    public EnvironmentRepairPlan CreatePlan(IReadOnlyCollection<EnvironmentCheckResult> checkResults, IReadOnlyCollection<CoreRuntime> installedRuntimes) => plan;
}

internal sealed class FakeRepairExecutor : IEnvironmentRepairExecutor
{
    public int ExecuteCalls { get; private set; }

    public Task ExecuteAsync(IReadOnlyCollection<RuntimePackageOperation> operations, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        ExecuteCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeProjectCatalog : IProjectCatalogService
{
    public IReadOnlyList<ProjectAsset> Projects { get; set; } = [];

    public Task<ProjectAsset> RegisterAsync(
        string projectPath,
        EnvironmentProfile? profile = null,
        IReadOnlyCollection<EnvironmentCheckResult>? checkResults = null,
        IReadOnlyCollection<CoreRuntime>? runtimes = null,
        bool opened = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProjectAsset(Guid.NewGuid(), "Test", projectPath, ProjectPathStatus.Available, 0, null, null, EnvironmentHealthStatus.Unknown));

    public Task<IReadOnlyList<ProjectAsset>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(Projects);

    public Task RemoveAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeProjectLauncher : IProjectLauncher
{
    public int OpenCalls { get; private set; }

    public Task OpenAsync(string projectPath, string editorCommand = "code", CancellationToken cancellationToken = default)
    {
        OpenCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeFolderPicker : IFolderPickerService
{
    public string? Result { get; set; }
    public string? PickFolder(string? initialDirectory = null) => Result;
}

/// <summary>Settings service fake backed by a real <see cref="ScopedSettingsService"/> over an
/// isolated temp file, so view models exercising <c>OpenSessionAsync</c> / snapshots / commits get
/// realistic defaults and persistence without touching the user's real settings.</summary>
internal sealed class FakeSettingsService : ISettingsService
{
    private readonly ScopedSettingsService _inner;

    public FakeSettingsService(string? path = null) =>
        _inner = new ScopedSettingsService(new BuiltInSettingsCatalog(),
            path ?? TestTempRoot.NewFile("fake-settings"));

    public string SettingsPath => _inner.UserSettingsPath;

    public Task<SettingsSnapshot> GetSnapshotAsync(SettingsContext context, CancellationToken cancellationToken = default) =>
        _inner.GetSnapshotAsync(context, cancellationToken);

    public Task<SettingsCommitResult> CommitAsync(SettingsTransaction transaction, CancellationToken cancellationToken = default) =>
        _inner.CommitAsync(transaction, cancellationToken);

    public Task<SettingsCommitResult> ResetAsync(string key, SettingScope scope, SettingsContext context,
        string? languageId = null, CancellationToken cancellationToken = default) =>
        _inner.ResetAsync(key, scope, context, languageId, cancellationToken);

    public IAsyncEnumerable<SettingsChangeSet> WatchAsync(SettingsContext context, CancellationToken cancellationToken = default) =>
        _inner.WatchAsync(context, cancellationToken);

    public Task<ISettingsSession> OpenSessionAsync(SettingsContext context, IReadOnlyCollection<string>? keys = null,
        CancellationToken cancellationToken = default) => _inner.OpenSessionAsync(context, keys, cancellationToken);
}

/// <summary>模拟瞬时提交失败(磁盘压力/杀软实时扫描下的 FileError、会话基线过期 → Conflict):
/// 打开的会话前 <see cref="InitialFailures"/> 次提交返回指定状态,之后透传内部真实服务,用于
/// 验证默认 Shell 持久化的重试路径。拦截在会话层:SettingsSession 绑定创建它的服务实例,
/// 只包装 ISettingsService 的 CommitAsync 拦不到会话提交。</summary>
internal sealed class FlakySettingsService : ISettingsService
{
    private readonly FakeSettingsService _inner = new();
    private int _commits;

    public FlakySettingsService(SettingsCommitStatus failureStatus = SettingsCommitStatus.FileError, int initialFailures = 2)
    {
        FailureStatus = failureStatus;
        InitialFailures = initialFailures;
    }

    public SettingsCommitStatus FailureStatus { get; }
    public int InitialFailures { get; }

    /// <summary>收到的会话提交总数(含注入的失败),供断言"发生了重试"。</summary>
    public int CommitCount => _commits;

    public string SettingsPath => _inner.SettingsPath;

    public Task<SettingsSnapshot> GetSnapshotAsync(SettingsContext context, CancellationToken cancellationToken = default) =>
        _inner.GetSnapshotAsync(context, cancellationToken);

    public Task<SettingsCommitResult> CommitAsync(SettingsTransaction transaction, CancellationToken cancellationToken = default) =>
        _inner.CommitAsync(transaction, cancellationToken);

    public Task<SettingsCommitResult> ResetAsync(string key, SettingScope scope, SettingsContext context,
        string? languageId = null, CancellationToken cancellationToken = default) =>
        _inner.ResetAsync(key, scope, context, languageId, cancellationToken);

    public IAsyncEnumerable<SettingsChangeSet> WatchAsync(SettingsContext context, CancellationToken cancellationToken = default) =>
        _inner.WatchAsync(context, cancellationToken);

    public async Task<ISettingsSession> OpenSessionAsync(SettingsContext context, IReadOnlyCollection<string>? keys = null,
        CancellationToken cancellationToken = default)
    {
        var inner = await _inner.OpenSessionAsync(context, keys, cancellationToken);
        return new FlakySession(inner, this);
    }

    private sealed class FlakySession(ISettingsSession inner, FlakySettingsService owner) : ISettingsSession
    {
        public SettingsContext Context => inner.Context;
        public SettingsSnapshot? Current => inner.Current;
        public IReadOnlyCollection<string>? SubscribedKeys => inner.SubscribedKeys;

        public event EventHandler<SettingsChangeSet>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Task<SettingsSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            inner.RefreshAsync(cancellationToken);

        public async Task<SettingsCommitResult> CommitAsync(SettingScope scope, IReadOnlyList<SettingOperation> operations,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref owner._commits) <= owner.InitialFailures)
            {
                return new SettingsCommitResult(owner.FailureStatus, ErrorMessage: "simulated transient failure");
            }

            return await inner.CommitAsync(scope, operations, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>Workspace service fake. By default reports no active workspace; tests can set
/// <see cref="Current"/> directly when a context is required.</summary>
internal sealed class FakeProjectWorkspaceService : IProjectWorkspaceService
{
    private long _generation;
    public ProjectWorkspaceContext? Current { get; set; }
    public bool IsStartupAutoRestore { get; set; }
    public event Func<ProjectWorkspaceContext?, Task>? ContextChanged;
    public List<string> ActivatedPaths { get; } = [];
    public Func<string, ProjectWorkspaceContext?>? ActivateResultFactory { get; set; }
    public Task EnsureInitializedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ActivateAsync(string path, CancellationToken cancellationToken = default)
    {
        ActivatedPaths.Add(path);
        if (ActivateResultFactory is not null)
        {
            Current = ActivateResultFactory(path) is { } next
                ? next with { Generation = Interlocked.Increment(ref _generation) }
                : null;
        }

        return ContextChanged?.Invoke(Current) ?? Task.CompletedTask;
    }
}

/// <summary>In-memory application state store used to satisfy the editor/workspace dependency chain.</summary>
internal sealed class FakeApplicationStateStore : IApplicationStateStore
{
    public ApplicationState State { get; set; } = new();
    public event EventHandler<ApplicationStateChangedEventArgs>? Changed;
    public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
    public Task<ApplicationState> CommitAsync(ApplicationStateTransaction transaction, CancellationToken cancellationToken = default)
    {
        Changed?.Invoke(this, new(State));
        return Task.FromResult(State);
    }
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResetLayoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeGitService : IGitService
{
    public GitRepositoryStatus Status { get; set; } = GitRepositoryStatus.NotARepository;
    public GitRepositoryStatus? StatusAfterInitialization { get; set; }
    public Exception? InitializeException { get; set; }
    public GitFileDiff? DiffResult { get; set; }

    /// <summary>When set, takes precedence over <see cref="DiffResult"/> per side
    /// (key: staged flag + untracked flag) — used to simulate "requested side empty, other side has
    /// content" for the diff empty-fallback tests.</summary>
    public Dictionary<(bool Staged, bool Untracked), GitFileDiff?>? DiffResultBySide { get; set; }

    public string DiffRevision { get; set; } = "revision-1";
    public Dictionary<string, string> DiffRevisions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string RepositoryPath, string Path, bool Staged, bool IsUntracked)> DiffRequests { get; } = [];
    public string RawDiff { get; set; } = string.Empty;
    public IReadOnlyList<GitCommitInfo> Logs { get; set; } = [];
    public IReadOnlyList<GitBranchInfo> Branches { get; set; } = [];
    public IReadOnlyList<GitBranchInfo> RemoteBranches { get; set; } = [];
    public IReadOnlyList<GitFileChange> CommitFiles { get; set; } = [];
    public GitFileDiff? CommitFileDiff { get; set; }

    public List<string> StatusRequests { get; } = [];
    public List<string> InitializedRepositoryPaths { get; } = [];

    /// <summary>Optional gate: status calls await it so tests can hold a refresh in flight.</summary>
    public Task? StatusGate { get; set; }
    public List<string> StagedPaths { get; } = [];
    public List<string> UnstagedPaths { get; } = [];
    public List<string> DiscardedPaths { get; } = [];
    public int StageCalls { get; private set; }
    public int UnstageCalls { get; private set; }
    public int DiscardCalls { get; private set; }
    public List<string> CommitMessages { get; } = [];
      public int PullCalls { get; private set; }
      public int PushCalls { get; private set; }
      public int FetchCalls { get; private set; }
    public List<string> CreatedBranches { get; } = [];
    public List<string> SwitchedBranches { get; } = [];
    public List<(string Hash, string Path)> CommitFileDiffRequests { get; } = [];
    public IReadOnlyList<GitCommitInfo> IncomingCommits { get; set; } = [];
    public IReadOnlyList<GitCommitInfo> OutgoingCommits { get; set; } = [];
    public IReadOnlyList<GitFileChange> IncomingFiles { get; set; } = [];
    public IReadOnlyList<GitFileChange> OutgoingFiles { get; set; } = [];
    public int IncomingFilesCalls { get; private set; }
    public int OutgoingFilesCalls { get; private set; }
    public IReadOnlyList<GitStashInfo> Stashes { get; set; } = [];
    public int StashPushCalls { get; private set; }
    public List<int> PoppedStashes { get; } = [];
    public List<int> DroppedStashes { get; } = [];
    public int LogCalls { get; private set; }
    /// <summary>When set, <see cref="GetLogAsync"/> faults with this exception (e.g. an empty
    /// repository whose branch has no commits yet).</summary>
    public Exception? LogException { get; set; }
    public int BranchCalls { get; private set; }
    public int StashListCalls { get; private set; }
    public int CommitFileListCalls { get; private set; }
    public List<string> CommitFileListRequests { get; } = [];
    public List<(string RepositoryPath, string Path, bool Staged, GitDiffHunk Hunk, GitHunkOperation Operation, int? BlockOrdinal)> HunkOperations { get; } = [];

    public async Task<GitRepositoryStatus> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default, bool includeAllUntracked = true)
    {
        StatusRequests.Add(repositoryPath);
        if (StatusGate is not null)
        {
            await StatusGate;
        }

        return Status;
    }

    public Task InitializeRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        InitializedRepositoryPaths.Add(repositoryPath);
        if (InitializeException is not null)
        {
            return Task.FromException(InitializeException);
        }

        if (StatusAfterInitialization is not null)
        {
            Status = StatusAfterInitialization;
        }

        return Task.CompletedTask;
    }

    public Task<GitFileDiff?> GetDiffAsync(string repositoryPath, string path, bool staged, bool isUntracked = false, CancellationToken cancellationToken = default)
    {
        DiffRequests.Add((repositoryPath, path, staged, isUntracked));
        var result = DiffResultBySide is { } bySide && bySide.TryGetValue((staged, isUntracked), out var sideResult)
            ? sideResult
            : DiffResult;
        return Task.FromResult(result);
    }

    public Task<string> GetDiffRevisionAsync(string repositoryPath, string path, bool staged, bool isUntracked = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(DiffRevisions.TryGetValue(path, out var revision) ? revision : DiffRevision);

    public Task<string> GetRawDiffAsync(string repositoryPath, bool staged, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(RawDiff);

    public Task ApplyHunkAsync(string repositoryPath, string path, bool staged, GitDiffHunk hunk, GitHunkOperation operation, int? blockOrdinal = null, CancellationToken cancellationToken = default)
    {
        HunkOperations.Add((repositoryPath, path, staged, hunk, operation, blockOrdinal));
        return Task.CompletedTask;
    }

    public Task StageAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        StageCalls++;
        StagedPaths.AddRange(paths.Count == 0 ? ["*"] : paths);
        return Task.CompletedTask;
    }

    public Task UnstageAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        UnstageCalls++;
        UnstagedPaths.AddRange(paths.Count == 0 ? ["*"] : paths);
        return Task.CompletedTask;
    }

    public Task DiscardAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        DiscardCalls++;
        DiscardedPaths.AddRange(paths.Count == 0 ? ["*"] : paths);
        return Task.CompletedTask;
    }

    public Task DiscardUnstagedAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        // 未暂存语义:记录到同一 DiscardedPaths/DiscardCalls,便于断言"丢弃"调用内容。
        DiscardCalls++;
        DiscardedPaths.AddRange(paths.Count == 0 ? ["*"] : paths);
        return Task.CompletedTask;
    }

    public Task CommitAsync(string repositoryPath, string message, CancellationToken cancellationToken = default)
    {
        CommitMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repositoryPath, int count = 30, CancellationToken cancellationToken = default)
    {
        LogCalls++;
        if (LogException is not null)
        {
            return Task.FromException<IReadOnlyList<GitCommitInfo>>(LogException);
        }
        return Task.FromResult<IReadOnlyList<GitCommitInfo>>(Logs.Take(Math.Max(1, count)).ToArray());
    }

    public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        BranchCalls++;
        return Task.FromResult(Branches);
    }

    public Task<IReadOnlyList<GitBranchInfo>> GetRemoteBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(RemoteBranches);

    public Task<IReadOnlyList<GitTagInfo>> GetTagsAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitTagInfo>>([]);

    public Task CreateTagAsync(string repositoryPath, string tagName, bool annotate = false, string? message = null, string? targetRef = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public List<string> DeletedTags { get; } = [];

    public Task DeleteTagAsync(string repositoryPath, string tagName, CancellationToken cancellationToken = default)
    {
        DeletedTags.Add(tagName);
        return Task.CompletedTask;
    }

    public Task PushTagAsync(string repositoryPath, string tagName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task PushAllTagsAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task FetchTagsAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task CheckoutTagAsync(string repositoryPath, string tagName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task CreateBranchAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default)
    {
        CreatedBranches.Add(branchName);
        return Task.CompletedTask;
    }

      public Task SwitchBranchAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default)
      {
          SwitchedBranches.Add(branchName);
          return Task.CompletedTask;
      }

      public Task FetchAsync(string repositoryPath, CancellationToken cancellationToken = default)
      {
          FetchCalls++;
          return Task.CompletedTask;
      }

      public Task PullAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        PullCalls++;
        return Task.CompletedTask;
    }

    public Task<GitPullResult> PullAsync(string repositoryPath, GitPullOptions options, CancellationToken cancellationToken = default)
    {
        PullCalls++;
        return Task.FromResult(new GitPullResult(GitPullResultKind.AlreadyUpToDate, 0, null, []));
    }

    public Task PushAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        PushCalls++;
        return Task.CompletedTask;
    }

    public Task PushAsync(string repositoryPath, GitPushOptions options, CancellationToken cancellationToken = default)
    {
        PushCalls++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GitFileChange>> GetCommitFilesAsync(string repositoryPath, string commitHash, CancellationToken cancellationToken = default)
    {
        CommitFileListCalls++;
        CommitFileListRequests.Add(commitHash);
        return Task.FromResult(CommitFiles);
    }

    public int IncomingCommitsCalls { get; private set; }

    public int OutgoingCommitsCalls { get; private set; }

    public Task<IReadOnlyList<GitCommitInfo>> GetIncomingCommitsAsync(string repositoryPath, string upstreamReference, int count = 30, CancellationToken cancellationToken = default)
    {
        IncomingCommitsCalls++;
        return Task.FromResult(IncomingCommits);
    }

    public Task<IReadOnlyList<GitCommitInfo>> GetOutgoingCommitsAsync(string repositoryPath, string upstreamReference, int count = 30, CancellationToken cancellationToken = default)
    {
        OutgoingCommitsCalls++;
        return Task.FromResult(OutgoingCommits);
    }

    public Task<IReadOnlyList<GitFileChange>> GetIncomingFilesAsync(string repositoryPath, string upstreamReference, CancellationToken cancellationToken = default)
    {
        IncomingFilesCalls++;
        return Task.FromResult(IncomingFiles);
    }

    public Task<IReadOnlyList<GitFileChange>> GetOutgoingFilesAsync(string repositoryPath, string upstreamReference, CancellationToken cancellationToken = default)
    {
        OutgoingFilesCalls++;
        return Task.FromResult(OutgoingFiles);
    }

    public Task<IReadOnlyList<GitStashInfo>> GetStashesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        StashListCalls++;
        return Task.FromResult(Stashes);
    }

    public Task StashAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        StashPushCalls++;
        return Task.CompletedTask;
    }

    public Task PopStashAsync(string repositoryPath, int index, CancellationToken cancellationToken = default)
    {
        PoppedStashes.Add(index);
        return Task.CompletedTask;
    }

    public Task DropStashAsync(string repositoryPath, int index, CancellationToken cancellationToken = default)
    {
        DroppedStashes.Add(index);
        return Task.CompletedTask;
    }

    public Task<GitFileDiff?> GetCommitFileDiffAsync(string repositoryPath, string commitHash, string path, CancellationToken cancellationToken = default)
    {
        CommitFileDiffRequests.Add((commitHash, path));
        return Task.FromResult(CommitFileDiff);
    }
}

internal sealed class FakeExecutablePicker : IExecutablePickerService
{
    public string? Result { get; set; }
    public string? PickExecutable() => Result;
}

internal sealed class FakeConfirmationService : IConfirmationService
{
    public bool Result { get; set; } = true;
    public int Calls { get; private set; }
    public List<(string Title, string Message)> Requests { get; } = [];
    public bool Confirm(string title, string message)
    {
        Calls++;
        Requests.Add((title, message));
        return Result;
    }
}

internal sealed class FakeUiDispatcher : IUiDispatcher
{
    public bool AccessGranted { get; set; } = true;
    public List<Action> Invoked { get; } = [];

    public bool CheckAccess() => AccessGranted;

    public void BeginInvoke(Action action) => Invoked.Add(action);

    public Task InvokeAsync(Action action)
    {
        if (AccessGranted)
        {
            action();
            return Task.CompletedTask;
        }

        Invoked.Add(action);
        return Task.CompletedTask;
    }
}

internal static class FakeRuntimes
{
    public static CoreRuntime Runtime(string name, string version) =>
        new(Guid.NewGuid(), name, version, $"C:\\{name}\\{version}", "X64", "Test", 0, RuntimeStatus.Installed);
}
