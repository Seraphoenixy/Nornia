using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using Nornia.Core.Coalescing;
using Nornia.Desktop.Services;
using Nornia.Storage;

namespace Nornia.Desktop.Configuration;

public sealed record WindowBoundsInfo(double Left, double Top, double Width, double Height, bool Maximized);

public sealed record EditorReadingState(double VerticalOffset = 0, int CaretLine = 1, int CaretColumn = 1,
    double HorizontalOffset = 0,
    IReadOnlyList<int>? ExpandedRegions = null, string? SearchText = null,
    string? MarkdownMode = null, double MarkdownVerticalOffset = 0,
    string? MarkdownAnchor = null, int MarkdownHeadingLine = 0);

/// <summary>
/// 工作区级应用状态。schema v2 起携带可拆分的编辑器组布局树(<see cref="EditorLayout"/>);
/// 旧版扁平 <see cref="RecentTabs"/> / <see cref="ActiveEditor"/> 继续写入(便于旧版本降级启动),
/// 读取时 v2 布局优先,缺失则从扁平字段无损迁移为单编辑器组。
/// </summary>
public sealed record WorkspaceApplicationState(
    IReadOnlyList<string>? RecentTabs = null,
    IReadOnlyDictionary<string, EditorReadingState>? ReadingStates = null,
    string? ActiveEditor = null,
    string? ActiveSidebar = null,
    EditorLayoutState? EditorLayout = null)
{
    public IReadOnlyList<string> SafeRecentTabs => RecentTabs ?? [];
    public IReadOnlyDictionary<string, EditorReadingState> SafeReadingStates =>
        ReadingStates ?? ImmutableDictionary<string, EditorReadingState>.Empty;
}

public sealed record ApplicationState(
    int SchemaVersion = 1,
    int SettingsMigrationVersion = 0,
    WindowBoundsInfo? WindowBounds = null,
    double? SidebarWidth = null,
    double? PanelHeight = null,
    double? ScmChangesHeight = null,
    double? ScmGraphHeight = null,
    string? SearchResultsViewMode = null,
    string? ScmLayout = null,
    string? LastWorkspace = null,
    string? ActiveSidebar = null,
    bool? PanelVisible = null,
    bool? PanelMaximized = null,
    IReadOnlyList<string>? RecentTabs = null,
    IReadOnlyDictionary<string, EditorReadingState>? ReadingStates = null,
    IReadOnlyDictionary<string, WorkspaceApplicationState>? Workspaces = null)
{
    /// <summary>工作台布局 schema 版本:v2 引入编辑器组布局树与面板最大化状态。</summary>
    public const int CurrentSchemaVersion = 2;

    public IReadOnlyList<string> SafeRecentTabs => RecentTabs ?? [];
    public IReadOnlyDictionary<string, EditorReadingState> SafeReadingStates =>
        ReadingStates ?? ImmutableDictionary<string, EditorReadingState>.Empty;
}

public enum ApplicationStateField
{
    SettingsMigrationVersion, WindowBounds, SidebarWidth, PanelHeight, ScmChangesHeight, ScmGraphHeight,
    SearchResultsViewMode, ScmLayout,
    LastWorkspace, ActiveSidebar, PanelVisible, PanelMaximized, WorkspaceState,
    WorkspaceRecentTabs, WorkspaceReadingState, WorkspaceActiveEditor, WorkspaceEditorLayout,
}

public sealed record ApplicationStateOperation(ApplicationStateField Field, object? Value,
    string? WorkspacePath = null, string? ItemKey = null, bool Reset = false);
public sealed record ApplicationStateTransaction(IReadOnlyList<ApplicationStateOperation> Operations);

public sealed class ApplicationStateChangedEventArgs(ApplicationState state) : EventArgs
{
    public ApplicationState State { get; } = state;
}

public interface IApplicationStateStore
{
    event EventHandler<ApplicationStateChangedEventArgs>? Changed;
    Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default);
    Task<ApplicationState> CommitAsync(ApplicationStateTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>Persists the current in-memory state immediately (bypassing the write debounce).
    /// Called by the shutdown path so a single flush replaces the per-tab commit storm that
    /// previously meant N+1 full file read/writes on exit.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    Task ResetLayoutAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 工作区应用状态存储(V2 性能改进):
/// ① 状态常驻内存——首次 <see cref="LoadAsync"/> 后不再读盘,<see cref="CommitAsync"/> 只改内存;
/// ② 写盘经 <see cref="RunOnceScheduler"/> 尾部去抖合批(默认 200ms)——高频 commit(标签开关/
///    驱逐/拆分/拖拽)在窗口内合并为一次文件写;
/// ③ 内容未变不写盘——序列化文本与上次成功写入一致时直接跳过(同值跳过);
/// ④ <see cref="FlushAsync"/>/Dispose 强制落盘一次——关停时以单次写替代原先每标签一次的
///    全文件读改写风暴;
/// ⑤ 去掉 <c>WriteIndented</c>——文件更小,序列化/反序列化更快。
/// 磁盘写入保持原子(临时文件 + Replace + 退避重试),写失败保留脏标记由后续 commit/flush 重试。
/// </summary>
public sealed class ApplicationStateStore : IApplicationStateStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>写盘去抖窗口;100–250ms 为推荐区间(VS Code storage 的 ThrottledDelayer 为 100ms)。</summary>
    internal const int DefaultWriteDebounceMilliseconds = 200;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _writeGate = new();
    private readonly RunOnceScheduler _writeScheduler;
    private readonly string _path;

    private ApplicationState _state = new();
    private bool _stateLoaded;
    private volatile bool _dirty;
    private string? _lastWrittenJson;
    private Task? _activeWrite;
    private int _disposed;

    public ApplicationStateStore() : this(Path.Combine(NorniaPaths.DataDirectory, "state.json")) { }

    public ApplicationStateStore(string path) : this(path, DefaultWriteDebounceMilliseconds) { }

    /// <summary>Test seam: 0 disables the debounce (writes run on the next scheduler tick).</summary>
    internal ApplicationStateStore(string path, int writeDebounceMilliseconds)
    {
        _path = path;
        _writeScheduler = new RunOnceScheduler(Math.Max(0, writeDebounceMilliseconds))
        {
            Action = OnWriteDue,
            OnError = exception => LastWriteError = exception,
        };
    }

    public string StatePath => _path;
    public event EventHandler<ApplicationStateChangedEventArgs>? Changed;

    /// <summary>最近一次后台写盘失败(若任何);显式 <see cref="FlushAsync"/> 的失败会直接抛出。</summary>
    public Exception? LastWriteError { get; private set; }

    /// <summary>实际发生的文件写次数(测试用;去抖合批/同值跳过的验证锚点)。</summary>
    internal int FileWriteCalls { get; private set; }

    public async Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_stateLoaded)
            {
                _state = await LoadFromDiskAsync(cancellationToken);
                _stateLoaded = true;
            }

            return _state;
        }
        finally { _gate.Release(); }
    }

    private async Task<ApplicationState> CommitCoreAsync(Func<ApplicationState, ApplicationState> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ApplicationState result;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_stateLoaded)
            {
                _state = await LoadFromDiskAsync(cancellationToken);
                _stateLoaded = true;
            }

            _state = update(_state) with { SchemaVersion = ApplicationState.CurrentSchemaVersion };
            result = _state;
            _dirty = true;
            ScheduleWrite();
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, new(result));
        return result;
    }

    /// <summary>Persists all committed state right now (see the interface docs). Returns when the
    /// file on disk matches the in-memory state (or there is nothing to write).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        bool loaded;
        try { loaded = _stateLoaded; }
        finally { _gate.Release(); }
        if (!loaded) return;

        // A pending debounce is superseded by the immediate write below.
        _writeScheduler.Cancel();
        var write = StartWriteIfNoneActive();
        try
        {
            await write;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped waiting; the write keeps running and later commits/flushes see it.
        }
    }

    private void OnWriteDue()
    {
        try
        {
            var write = StartWriteIfNoneActive();
            if (!write.IsCompleted)
            {
                write.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        LastWriteError = task.Exception!.Flatten().InnerExceptions.First();
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception ex)
        {
            LastWriteError = ex;
        }
    }

    /// <summary>Attaches to (or starts) the single write pipeline. The writer loops until no
    /// commit has dirtied the state while it was writing, so every caller that observes a
    /// completed task is guaranteed the file matches the state at that moment or a later one.</summary>
    private Task StartWriteIfNoneActive()
    {
        lock (_writeGate)
        {
            if (_activeWrite is null)
            {
                _activeWrite = RunWriteLoopAsync();
            }

            return _activeWrite;
        }
    }

    private async Task RunWriteLoopAsync()
    {
        try
        {
            while (true)
            {
                string? json;
                bool dirty;
                await _gate.WaitAsync();
                try
                {
                    dirty = _dirty && _stateLoaded;
                    if (dirty) _dirty = false;
                    json = dirty ? JsonSerializer.Serialize(_state, JsonOptions) : null;
                }
                finally { _gate.Release(); }

                if (!dirty || json is null) break;
                if (json == _lastWrittenJson) break; // same-value skip: nothing changed since the last write

                await WriteCoreAsync(json!);
                await _gate.WaitAsync();
                try { _lastWrittenJson = json; }
                finally { _gate.Release(); }
                // Loop: a commit during the file write re-set _dirty.
            }
        }
        catch (Exception)
        {
            // Keep the state marked dirty: the next commit or flush retries the write.
            await _gate.WaitAsync();
            try { _dirty = true; }
            finally { _gate.Release(); }
            throw;
        }
        finally
        {
            lock (_writeGate) { _activeWrite = null; }
        }
    }

    /// <summary>Called with <see cref="_gate"/> held after the in-memory state changed: arm the
    /// debounce unless a write is already in flight (its loop picks up the dirty flag).</summary>
    private void ScheduleWrite()
    {
        bool writeActive;
        lock (_writeGate) { writeActive = _activeWrite is not null; }
        if (!writeActive)
        {
            _writeScheduler.Schedule();
        }
    }

    public Task ResetLayoutAsync(CancellationToken cancellationToken = default) => CommitAsync(new([
        new(ApplicationStateField.WindowBounds, null, Reset: true),
        new(ApplicationStateField.SidebarWidth, null, Reset: true),
        new(ApplicationStateField.PanelHeight, null, Reset: true),
        new(ApplicationStateField.ScmChangesHeight, null, Reset: true),
        new(ApplicationStateField.ScmGraphHeight, null, Reset: true),
        new(ApplicationStateField.SearchResultsViewMode, null, Reset: true),
        new(ApplicationStateField.ScmLayout, null, Reset: true),
        new(ApplicationStateField.ActiveSidebar, null, Reset: true),
        new(ApplicationStateField.PanelVisible, null, Reset: true),
        new(ApplicationStateField.PanelMaximized, null, Reset: true),
    ]), cancellationToken);

    public Task<ApplicationState> CommitAsync(ApplicationStateTransaction transaction,
        CancellationToken cancellationToken = default) => CommitCoreAsync(state =>
    {
        foreach (var operation in transaction.Operations) state = Apply(state, operation);
        return state;
    }, cancellationToken);

    private static ApplicationState Apply(ApplicationState state, ApplicationStateOperation operation)
    {
        object? ValueOrNull() => operation.Reset ? null : operation.Value;
        return operation.Field switch
        {
            ApplicationStateField.SettingsMigrationVersion => state with
            { SettingsMigrationVersion = operation.Reset ? 0 : Convert.ToInt32(operation.Value) },
            ApplicationStateField.WindowBounds => state with { WindowBounds = ValueOrNull() as WindowBoundsInfo },
            ApplicationStateField.SidebarWidth => state with { SidebarWidth = ValueOrNull() is double sidebar ? sidebar : null },
            ApplicationStateField.PanelHeight => state with { PanelHeight = ValueOrNull() is double panel ? panel : null },
            ApplicationStateField.ScmChangesHeight => state with { ScmChangesHeight = ValueOrNull() is double changes ? changes : null },
            ApplicationStateField.ScmGraphHeight => state with { ScmGraphHeight = ValueOrNull() is double graph ? graph : null },
            ApplicationStateField.SearchResultsViewMode => state with { SearchResultsViewMode = ValueOrNull() as string },
            ApplicationStateField.ScmLayout => state with { ScmLayout = ValueOrNull() as string },
            ApplicationStateField.LastWorkspace => state with { LastWorkspace = ValueOrNull() as string },
            ApplicationStateField.ActiveSidebar => state with { ActiveSidebar = ValueOrNull() as string },
            ApplicationStateField.PanelVisible => state with { PanelVisible = ValueOrNull() is bool visible ? visible : null },
            ApplicationStateField.PanelMaximized => state with { PanelMaximized = ValueOrNull() is bool maximized ? maximized : null },
            ApplicationStateField.WorkspaceState => ApplyWorkspaceState(state, operation),
            ApplicationStateField.WorkspaceRecentTabs => ApplyWorkspaceField(state, operation, (workspace, value) =>
                workspace with { RecentTabs = value as IReadOnlyList<string> }),
            ApplicationStateField.WorkspaceActiveEditor => ApplyWorkspaceField(state, operation, (workspace, value) =>
                workspace with { ActiveEditor = value as string }),
            ApplicationStateField.WorkspaceEditorLayout => ApplyWorkspaceField(state, operation, (workspace, value) =>
                workspace with { EditorLayout = value as EditorLayoutState }),
            ApplicationStateField.WorkspaceReadingState => ApplyReadingState(state, operation),
            _ => state,
        };
    }

    private static ApplicationState ApplyWorkspaceState(ApplicationState state, ApplicationStateOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.WorkspacePath)) return state;
        var key = Path.GetFullPath(operation.WorkspacePath).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var workspaces = (state.Workspaces ?? ImmutableDictionary<string, WorkspaceApplicationState>.Empty)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (operation.Reset) workspaces.Remove(key);
        else if (operation.Value is WorkspaceApplicationState workspace) workspaces[key] = workspace;
        return state with { Workspaces = workspaces };
    }

    private static ApplicationState ApplyWorkspaceField(ApplicationState state, ApplicationStateOperation operation,
        Func<WorkspaceApplicationState, object?, WorkspaceApplicationState> update)
    {
        if (string.IsNullOrWhiteSpace(operation.WorkspacePath)) return state;
        var (key, workspaces) = WorkspaceMap(state, operation.WorkspacePath);
        var current = workspaces.GetValueOrDefault(key) ?? new();
        workspaces[key] = update(current, operation.Reset ? null : operation.Value);
        return state with { Workspaces = workspaces };
    }

    private static ApplicationState ApplyReadingState(ApplicationState state, ApplicationStateOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.WorkspacePath) || string.IsNullOrWhiteSpace(operation.ItemKey)) return state;
        var (key, workspaces) = WorkspaceMap(state, operation.WorkspacePath);
        var current = workspaces.GetValueOrDefault(key) ?? new();
        var readings = current.SafeReadingStates.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        if (operation.Reset) readings.Remove(operation.ItemKey);
        else if (operation.Value is EditorReadingState reading) readings[operation.ItemKey] = reading;
        workspaces[key] = current with { ReadingStates = readings };
        return state with { Workspaces = workspaces };
    }

    private static (string Key, Dictionary<string, WorkspaceApplicationState> Workspaces) WorkspaceMap(
        ApplicationState state, string workspacePath)
    {
        var key = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var workspaces = (state.Workspaces ?? ImmutableDictionary<string, WorkspaceApplicationState>.Empty)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        return (key, workspaces);
    }

    private async Task<ApplicationState> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new();
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                16 * 1024, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<ApplicationState>(stream, JsonOptions, cancellationToken) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    private async Task WriteCoreAsync(string text)
    {
        FileWriteCalls++;
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, text, new UTF8Encoding(false));
            // Windows 实时防护/搜索索引可能瞬时持有 state.json 句柄,Replace 会报"无法删除要被
            // 替换的文件":对瞬时 IO 异常按退避重试(临时文件名每次写入唯一,重试幂等)。
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(_path)) File.Replace(temporaryPath, _path, null, true);
                    else File.Move(temporaryPath, _path);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(25 * attempt);
                }
            }
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            // 关停单次 Flush:把去抖窗口内尚未落盘的状态一次性写完(V2 ③)。阻塞等待有界——写盘
            // 本身是本地文件 + 退避重试,正常远小于关停预算;失败只留痕不抛出(尽力而为)。
            // 需要新启动写循环时必须放到线程池:OnExit 期间 UI 调度器已停止泵帧,在 UI 线程
            // 启动的 RunWriteLoopAsync 其 async 续延绑定死调度器、永远无法完成,有界等待必然
            // 空耗满 4s(实测)。线程池上启动则续延不依赖调度器,正常几十 ms 内完成;
            // 已在途的写(去抖定时器启动,本就在线程池)直接等待即可。
            _writeScheduler.Cancel();
            try
            {
                Task finalWrite;
                lock (_writeGate)
                {
                    if (_activeWrite is null)
                    {
                        _activeWrite = Task.Run(RunWriteLoopAsync);
                    }

                    finalWrite = _activeWrite;
                }

                finalWrite.Wait(TimeSpan.FromSeconds(4));
            }
            catch (Exception)
            {
                // Best effort: a failed final write must not hang or crash the shutdown.
            }
        }
        finally
        {
            _writeScheduler.Dispose();
            _gate.Dispose();
        }
    }
}
