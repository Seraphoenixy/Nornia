using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Nornia.Storage;

namespace Nornia.Desktop.Configuration;

/// <summary>Transaction/session facade. JSONC, resolution and file watching live in dedicated
/// collaborators so this type only orders revisions and publishes effective changes.</summary>
public sealed class ScopedSettingsService : ISettingsService, IDisposable, IAsyncDisposable
{
    private readonly BuiltInSettingsCatalog _catalog;
    private readonly ISettingsDocumentStore _documents;
    private readonly ISettingsResolver _resolver;
    private readonly ISettingsChangeCoordinator _coordinator;
    private readonly string _userPath;
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly object _gate = new();
    private readonly List<Channel<SettingsChangeSet>> _subscribers = [];
    private readonly Dictionary<string, SettingsContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SettingsSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _externalChanges;
    private readonly bool _ownsCollaborators;
    private long _revision;
    private int _disposed;

    public ScopedSettingsService(BuiltInSettingsCatalog catalog) : this(catalog,
        new SettingsDocumentStore(), new SettingsResolver(catalog), new SettingsChangeCoordinator(),
        Path.Combine(NorniaPaths.DataDirectory, "settings.jsonc"))
    { _ownsCollaborators = true; }

    public ScopedSettingsService(BuiltInSettingsCatalog catalog, string userPath) : this(catalog,
        new SettingsDocumentStore(), new SettingsResolver(catalog), new SettingsChangeCoordinator(), userPath)
    { _ownsCollaborators = true; }

    public ScopedSettingsService(BuiltInSettingsCatalog catalog, ISettingsDocumentStore documents,
        ISettingsResolver resolver, ISettingsChangeCoordinator coordinator, string? userPath = null)
    {
        _catalog = catalog;
        _documents = documents;
        _resolver = resolver;
        _coordinator = coordinator;
        _userPath = Path.GetFullPath(userPath ?? Path.Combine(NorniaPaths.DataDirectory, "settings.jsonc"));
        _externalChanges = ObserveExternalChangesAsync(_lifetime.Token);
    }

    public string UserSettingsPath => _userPath;
    public string? GetWorkspaceSettingsPath(SettingsContext context) => context.NormalizedWorkspacePath is { } root
        ? Path.Combine(root, ".vscode", "settings.json") : null;

    public async Task<SettingsSnapshot> GetSnapshotAsync(SettingsContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(context, cancellationToken);
        Track(context, snapshot);
        return snapshot;
    }

    public async Task<SettingsCommitResult> CommitAsync(SettingsTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(transaction.LanguageId))
            transaction = transaction with
            {
                Operations = transaction.Operations.Select(operation => string.IsNullOrWhiteSpace(operation.LanguageId)
                    ? operation with { LanguageId = transaction.LanguageId }
                    : operation).ToArray(),
            };
        var scopeError = ValidateScope(transaction);
        if (scopeError is not null) return scopeError;
        var validation = ValidateOperations(transaction);
        if (validation.Count > 0) return new(SettingsCommitStatus.ValidationFailed, ValidationErrors: validation);

        await _commitGate.WaitAsync(cancellationToken);
        try
        {
            var context = transaction.Baseline.Context;
            var path = transaction.TargetScope == SettingScope.User ? _userPath : GetWorkspaceSettingsPath(context)!;
            var disk = await _documents.ReadAsync(path, cancellationToken);
            if (!disk.IsValid) return new(SettingsCommitStatus.FileError, ErrorMessage: disk.Diagnostic!.Message);
            var expectedRevision = transaction.TargetScope == SettingScope.User
                ? transaction.Baseline.UserFileRevision : transaction.Baseline.WorkspaceFileRevision;
            if (!string.Equals(expectedRevision, disk.Revision, StringComparison.Ordinal))
            {
                var conflicts = FindConflicts(transaction, disk.ParsedRoot);
                if (conflicts.Count > 0) return new(SettingsCommitStatus.Conflict, Conflicts: conflicts);
            }

            var oldSnapshot = await LoadSnapshotAsync(context, cancellationToken);
            var written = await _documents.PatchAsync(path, transaction.Operations, cancellationToken);
            _coordinator.AcknowledgeWrite(path, written.Revision);
            _coordinator.Track(path);
            var next = await LoadSnapshotAsync(context, cancellationToken);
            var changes = BuildChanges(oldSnapshot, next);
            Track(context, next);
            if (changes.Count > 0) Publish(new(next.Revision, transaction.TargetScope.ToString(), context, changes));
            return new(SettingsCommitStatus.Success, next);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(SettingsCommitStatus.FileError, ErrorMessage: exception.Message);
        }
        finally { _commitGate.Release(); }
    }

    public async Task<SettingsCommitResult> ResetAsync(string key, SettingScope scope, SettingsContext context,
        string? languageId = null, CancellationToken cancellationToken = default)
    {
        var baseline = await GetSnapshotAsync(context, cancellationToken);
        return await CommitAsync(new(baseline, scope,
            [new(key, null, true, languageId, ScopedBaseline(baseline, scope, key, languageId))]), cancellationToken);
    }

    public async Task<ISettingsSession> OpenSessionAsync(SettingsContext context,
        IReadOnlyCollection<string>? keys = null, CancellationToken cancellationToken = default)
    {
        var session = new SettingsSession(this, context, keys);
        await session.RefreshAsync(cancellationToken);
        return session;
    }

    public async IAsyncEnumerable<SettingsChangeSet> WatchAsync(SettingsContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<SettingsChangeSet>();
        lock (_gate) _subscribers.Add(channel);
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken))
                // User-scoped settings are global. A commit made while a workspace is open
                // carries that workspace's context, but it must still reach global consumers
                // such as the appearance controller (theme/accent/UI scale).
                if (SameContext(context, change.Context) ||
                    context.NormalizedWorkspacePath is null &&
                    string.Equals(change.Source, nameof(SettingScope.User), StringComparison.Ordinal))
                    yield return change;
        }
        finally { lock (_gate) _subscribers.Remove(channel); }
    }

    private async Task<SettingsSnapshot> LoadSnapshotAsync(SettingsContext context, CancellationToken cancellationToken)
    {
        var user = await _documents.ReadAsync(_userPath, cancellationToken);
        var workspacePath = GetWorkspaceSettingsPath(context);
        var workspace = workspacePath is null
            ? EmptyDocument("<no-workspace>")
            : await _documents.ReadAsync(workspacePath, cancellationToken);
        var resolution = _resolver.Resolve(context, user, workspace);
        SettingsSnapshot? previous;
        lock (_gate) _snapshots.TryGetValue(ContextKey(context), out previous);
        var effectiveChanged = previous is null || resolution.Values.Any(item =>
            !previous.Values.TryGetValue(item.Key, out var old) ||
            !Equals(old.EffectiveValue, item.Value.EffectiveValue) || old.Source != item.Value.Source);
        var revision = effectiveChanged ? Interlocked.Increment(ref _revision) : previous!.Revision;
        return new(revision, context, resolution.Values, resolution.UserValues, resolution.WorkspaceValues,
            user.Revision, workspace.Revision, resolution.Diagnostics);
    }

    private void Track(SettingsContext context, SettingsSnapshot snapshot)
    {
        lock (_gate)
        {
            _contexts[ContextKey(context)] = context;
            _snapshots[ContextKey(context)] = snapshot;
        }
        _coordinator.Track(_userPath);
        if (GetWorkspaceSettingsPath(context) is { } workspacePath) _coordinator.Track(workspacePath);
    }

    private async Task ObserveExternalChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in _coordinator.WatchAsync(cancellationToken))
            {
                try
                {
                    SettingsContext[] contexts;
                    lock (_gate) contexts = _contexts.Values.Where(context =>
                        string.Equals(path, _userPath, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(path, GetWorkspaceSettingsPath(context), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (contexts.Length == 0) continue;
                    var disk = await _documents.ReadAsync(path, cancellationToken);
                    if (_coordinator.ConsumeSelfWrite(path, disk.Revision)) continue;
                    foreach (var context in contexts)
                    {
                        SettingsSnapshot? previous;
                        lock (_gate) _snapshots.TryGetValue(ContextKey(context), out previous);
                        var next = await LoadSnapshotAsync(context, cancellationToken);
                        Track(context, next);
                        if (previous is null) continue;
                        var changes = BuildChanges(previous, next);
                        if (changes.Count > 0) Publish(new(next.Revision, "External", context, changes));
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // 单次外部变更处理失败(瞬时锁/IO)不得终止外部监视——否则此后其它
                    // 实例/编辑器对设置文件的修改永远不再同步。
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private SettingsCommitResult? ValidateScope(SettingsTransaction transaction)
    {
        if (transaction.TargetScope is not (SettingScope.User or SettingScope.Workspace))
            return new(SettingsCommitStatus.ValidationFailed, ValidationErrors: new Dictionary<string, string>
            { ["scope"] = "只能提交用户级或工作区级设置。" });
        if (transaction.TargetScope == SettingScope.Workspace && transaction.Baseline.Context.NormalizedWorkspacePath is null)
            return new(SettingsCommitStatus.ValidationFailed, ValidationErrors: new Dictionary<string, string>
            { ["scope"] = "没有打开的工作区。" });
        return null;
    }

    private Dictionary<string, string> ValidateOperations(SettingsTransaction transaction)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in transaction.Operations)
        {
            if (!_catalog.TryGet(operation.Key, out var definition)) { errors[operation.Key] = "未知的内置设置。"; continue; }
            if (!definition.AllowedScopes.HasFlag(transaction.TargetScope)) { errors[operation.Key] = "此设置不能写入所选作用域。"; continue; }
            if (operation.LanguageId is not null && !definition.AllowedScopes.HasFlag(SettingScope.Language))
            { errors[operation.Key] = "此设置不支持语言级覆盖。"; continue; }
            if (operation.Reset) continue;
            var error = definition.ValidateObject(definition.Read(operation.Value));
            if (error is not null) errors[operation.Key] = error;
        }
        return errors;
    }

    private static List<SettingsConflict> FindConflicts(SettingsTransaction transaction, JsonObject diskRoot)
    {
        var conflicts = new List<SettingsConflict>();
        foreach (var operation in transaction.Operations)
        {
            var baseline = operation.BaselineValue ?? ScopedBaseline(transaction.Baseline,
                transaction.TargetScope, operation.Key, operation.LanguageId);
            var disk = GetNode(diskRoot, operation.Key, operation.LanguageId);
            if (!JsonNode.DeepEquals(baseline, disk))
                conflicts.Add(new(operation.Key, baseline?.DeepClone(), disk?.DeepClone(), operation.Value?.DeepClone()));
        }
        return conflicts;
    }

    private static JsonNode? ScopedBaseline(SettingsSnapshot snapshot, SettingScope scope, string key, string? languageId)
    {
        var map = scope == SettingScope.User ? snapshot.UserValues : snapshot.WorkspaceValues;
        map.TryGetValue(string.IsNullOrWhiteSpace(languageId) ? key : $"[{languageId}]::{key}", out var value);
        return value?.DeepClone();
    }

    private static JsonNode? GetNode(JsonObject root, string key, string? languageId) =>
        string.IsNullOrWhiteSpace(languageId) ? root[key] : (root[$"[{languageId}]"] as JsonObject)?[key];

    private static IReadOnlyList<SettingsChange> BuildChanges(SettingsSnapshot oldSnapshot, SettingsSnapshot next) =>
        next.Values.Where(item => oldSnapshot.Values.TryGetValue(item.Key, out var old) &&
            (!Equals(old.EffectiveValue, item.Value.EffectiveValue) || old.Source != item.Value.Source))
        .Select(item =>
        {
            var old = oldSnapshot.Values[item.Key];
            return new SettingsChange(item.Key, old.EffectiveValue, item.Value.EffectiveValue, old.Source, item.Value.Source);
        }).ToArray();

    private void Publish(SettingsChangeSet change)
    {
        Channel<SettingsChangeSet>[] channels;
        lock (_gate) channels = [.. _subscribers];
        foreach (var channel in channels) channel.Writer.TryWrite(change);
    }

    private static SettingsDocumentSnapshot EmptyDocument(string path) => new(path, "{}", new(), new(), "EMPTY",
        null, new System.Text.UTF8Encoding(false), false, "\n");
    private static string ContextKey(SettingsContext context) =>
        $"{context.NormalizedWorkspacePath ?? "<user>"}|{context.LanguageId ?? "<all>"}";
    private static bool SameContext(SettingsContext left, SettingsContext right) =>
        string.Equals(left.NormalizedWorkspacePath, right.NormalizedWorkspacePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.LanguageId, right.LanguageId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Synchronous compatibility path (tests / explicit disposal); the container uses
    /// <see cref="DisposeAsync"/> during shutdown. Both cancel the external-change watcher without
    /// joining it (its continuations bind the UI dispatcher, which no longer pumps during OnExit).</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        // 本服务同时以 ScopedSettingsService 与 ISettingsService 别名注册,容器可能对同一
        // 实例触发两次释放(旧代码恰好幂等;取消/门清理是,CTS 二次 Cancel 不是)。
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        // 关停路径不 join _externalChanges:该任务在 UI 线程启动、续延绑定 UI 调度器,
        // OnExit 期间调度器已停止泵帧,等待它会挂死整个容器释放(实测吞掉整个 5s 关停预算)。
        // 订阅完成与协作方清理不依赖该任务结束:其挂起的 channel 读取会在
        // SettingsChangeCoordinator.Dispose 完成订阅通道后自然退出,关停时随进程回收。
        _lifetime.Cancel();
        lock (_gate)
        {
            foreach (var channel in _subscribers) channel.Writer.TryComplete();
            _subscribers.Clear();
        }
        if (_ownsCollaborators)
        {
            _coordinator.Dispose();
            (_documents as IDisposable)?.Dispose();
        }
        _commitGate.Dispose();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class SettingsSession : ISettingsSession
{
    private readonly ISettingsService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _watchTask;

    public SettingsSession(ISettingsService service, SettingsContext context, IReadOnlyCollection<string>? keys = null)
    {
        _service = service;
        Context = context;
        SubscribedKeys = keys is null ? null : keys.ToHashSet(StringComparer.Ordinal);
    }

    public SettingsContext Context { get; }
    public IReadOnlyCollection<string>? SubscribedKeys { get; }
    public SettingsSnapshot? Current { get; private set; }
    public event EventHandler<SettingsChangeSet>? Changed;

    public async Task<SettingsSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Current = await _service.GetSnapshotAsync(Context, cancellationToken);
        _watchTask ??= WatchCoreAsync();
        return Current;
    }

    public async Task<SettingsCommitResult> CommitAsync(SettingScope scope,
        IReadOnlyList<SettingOperation> operations, CancellationToken cancellationToken = default)
    {
        var baseline = Current ?? await RefreshAsync(cancellationToken);
        var enriched = operations.Select(operation => operation.BaselineValue is not null ? operation : operation with
        { BaselineValue = ScopedBaseline(baseline, scope, operation.Key, operation.LanguageId) }).ToArray();
        var result = await _service.CommitAsync(new(baseline, scope, enriched), cancellationToken);
        if (result.Snapshot is not null) Current = result.Snapshot;
        return result;
    }

    private async Task WatchCoreAsync()
    {
        try
        {
            await foreach (var change in _service.WatchAsync(Context, _lifetime.Token))
            {
                // 过滤是纯内存运算,放在守护之外;唯一可能抛异常的是快照重读(瞬时文件锁/IO)。
                var filtered = SubscribedKeys is null ? [.. change.Changes] :
                    change.Changes.Where(item => SubscribedKeys.Contains(item.Key)).ToList();
                if (filtered.Count == 0) continue;
                try
                {
                    Current = await _service.GetSnapshotAsync(Context, _lifetime.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // 瞬时文件锁/IO 不得杀死 watch 循环:循环一死,该会话后续所有设置变更
                    // 都收不到(表现为"设置不再实时生效")。稍候重读一次;仍失败则跳过本次
                    // 投递,下一次变更携带新快照自然自愈。
                    try
                    {
                        await Task.Delay(50, _lifetime.Token);
                        Current = await _service.GetSnapshotAsync(Context, _lifetime.Token);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                    catch { continue; }
                }

                Changed?.Invoke(this, change with { Changes = filtered });
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private static JsonNode? ScopedBaseline(SettingsSnapshot snapshot, SettingScope scope, string key, string? languageId)
    {
        var map = scope == SettingScope.User ? snapshot.UserValues : snapshot.WorkspaceValues;
        map.TryGetValue(string.IsNullOrWhiteSpace(languageId) ? key : $"[{languageId}]::{key}", out var value);
        return value?.DeepClone();
    }

    public ValueTask DisposeAsync()
    {
        // 关停路径不 join _watchTask:watch 任务在 UI 线程启动,其 async 续延绑定 UI 调度器;
        // OnExit 期间 WPF 调度器已停止泵帧,等待它会永远等不到(实测吞掉整个 5s 关停预算,
        // 触发"service disposal cut off")。取消后直接返回:订阅清理(finally 中的
        // _subscribers.Remove)在进程退出前不会执行,进程级影响为零。不 Dispose CTS——
        // 挂起中的 watch 任务仍持有 token 注册,CTS 与任务一并随进程回收。
        _lifetime.Cancel();
        return ValueTask.CompletedTask;
    }
}
