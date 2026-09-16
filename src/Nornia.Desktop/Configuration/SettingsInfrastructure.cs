using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Nornia.Desktop.Configuration;

public sealed record SettingsDocumentSnapshot(
    string Path,
    string Text,
    JsonObject ParsedRoot,
    JsonObject EffectiveRoot,
    string Revision,
    SettingsDiagnostic? Diagnostic,
    Encoding Encoding,
    bool HasByteOrderMark,
    string NewLine)
{
    public bool IsValid => Diagnostic is null;
}

public interface ISettingsDocumentStore
{
    Task<SettingsDocumentSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default);
    Task<SettingsDocumentSnapshot> PatchAsync(string path, IReadOnlyList<SettingOperation> operations,
        CancellationToken cancellationToken = default);
}

public sealed class SettingsDocumentStore : ISettingsDocumentStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, JsonObject> _lastValidRoots = new(StringComparer.OrdinalIgnoreCase);
    // S5: the read cache is a lock-free concurrent map. The snapshot value type is immutable once
    // published (callers treat Root/EffectiveRoot as read-only), so a reader never needs the gate:
    // a synchronous _gate.Wait on this hot path would block whatever thread calls it — including
    // the WPF dispatcher when a settings read happens during window-layout restore, which deadlocks
    // the shared test STA pump (everything that needs the dispatcher parks forever).
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length, SettingsDocumentSnapshot Snapshot)> _readCache
        = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCachedDocuments = 32;

    public async Task<SettingsDocumentSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);

        // S5: settings are re-read on every snapshot (e.g. each search keystroke). If the file's
        // mtime + length are unchanged, the shared cached snapshot is returned without a disk read
        // or JSONC re-parse. Callers must treat snapshot Root/EffectiveRoot as read-only (the
        // resolver and all value consumers only read them).
        (DateTime LastWriteUtc, long Length)? fileKey;
        try
        {
            var info = new FileInfo(path);
            fileKey = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, 0L);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fileKey = null; // stat failed: fall back to a full read
        }

        if (fileKey is { } hit &&
            _readCache.TryGetValue(path, out var cached) &&
            cached.LastWriteUtc == hit.LastWriteUtc && cached.Length == hit.Length)
        {
            return cached.Snapshot;
        }

        var document = await JsoncSettingsDocument.LoadAsync(path, cancellationToken);
        SettingsDocumentSnapshot snapshot;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            JsonObject effective;
            if (document.IsValid)
            {
                effective = (JsonObject)document.Root.DeepClone();
                _lastValidRoots[path] = (JsonObject)effective.DeepClone();
            }
            else
            {
                effective = _lastValidRoots.TryGetValue(path, out var previous)
                    ? (JsonObject)previous.DeepClone()
                    : new JsonObject();
            }

            snapshot = new(path, document.Text, document.Root, effective, document.Revision,
                document.Diagnostic, document.TextEncoding, document.HasByteOrderMark, document.NewLine);

            if (fileKey is { } key)
            {
                // Best-effort cap: clearing from multiple threads is harmless (worst case the
                // cache restarts cold).
                if (_readCache.Count > MaxCachedDocuments) _readCache.Clear();
                _readCache[path] = (key.LastWriteUtc, key.Length, snapshot);
            }
        }
        finally { _gate.Release(); }

        return snapshot;
    }

    public async Task<SettingsDocumentSnapshot> PatchAsync(string path, IReadOnlyList<SettingOperation> operations,
        CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        // Hold the document gate across the entire write + post-write read. This prevents the
        // FileSystemWatcher-driven ObserveExternalChangesAsync path from reading the file
        // mid-write or immediately after File.Replace, where the OS may still have the
        // previous generation pinned ("file is being used by another process").
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // The read and patch calculation must be in the same critical section as the write;
            // otherwise two concurrent commits can both patch the same stale document and the
            // second atomic replace silently loses the first commit.
            var current = await JsoncSettingsDocument.LoadAsync(path, cancellationToken);
            if (!current.IsValid) throw new InvalidOperationException($"设置文件包含错误：{current.Diagnostic!.Message}");
            var patched = current.Patch(operations);
            await AtomicWriteAsync(path, patched, current.TextEncoding, current.HasByteOrderMark, cancellationToken);
            // The write lands in the same mtime tick and length class as the cached entry only
            // in the self-commit case; drop the cache entry explicitly so the next read cannot
            // ever observe the pre-write document.
            _readCache.TryRemove(path, out _);
            var document = await JsoncSettingsDocument.LoadAsync(path, cancellationToken);
            JsonObject effective;
            if (document.IsValid)
            {
                effective = (JsonObject)document.Root.DeepClone();
                _lastValidRoots[path] = (JsonObject)effective.DeepClone();
            }
            else
            {
                effective = _lastValidRoots.TryGetValue(path, out var previous)
                    ? (JsonObject)previous.DeepClone()
                    : new JsonObject();
            }
            return new(path, document.Text, document.Root, effective, document.Revision, document.Diagnostic,
                document.TextEncoding, document.HasByteOrderMark, document.NewLine);
        }
        finally { _gate.Release(); }
    }

    private static async Task AtomicWriteAsync(string path, string text, Encoding encoding, bool bom,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var bytes = encoding.GetBytes(text);
        // 6 次指数退避(100→1600ms,总计约 3.1s):2 核 CI 全量套件并行下 %TEMP% 上的
        // 共享Violation/替换冲突远超旧预算(3 次/560ms);提交失败会静默返回 FileError,
        // 订阅者收不到变更,设置"实时生效"测试表现为永久超时,因此必须给足重试。
        var maxRetries = 6;
        var retryDelay = 100;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    if (bom)
                    {
                        var preamble = encoding.GetPreamble();
                        if (preamble.Length > 0) await stream.WriteAsync(preamble, cancellationToken);
                    }
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null, true);
                }
                else
                {
                    File.Move(temporary, path);
                }
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                if (File.Exists(temporary)) try { File.Delete(temporary); } catch { }
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay *= 2;
            }
        }
        throw new IOException($"Failed to atomically write '{path}' after {maxRetries} attempts.");
    }

    public void Dispose() => _gate.Dispose();
}

public sealed record SettingsResolution(
    ImmutableDictionary<string, UntypedSettingValue> Values,
    ImmutableDictionary<string, JsonNode?> UserValues,
    ImmutableDictionary<string, JsonNode?> WorkspaceValues,
    IReadOnlyList<SettingsDiagnostic> Diagnostics);

public interface ISettingsResolver
{
    SettingsResolution Resolve(SettingsContext context, SettingsDocumentSnapshot user,
        SettingsDocumentSnapshot workspace);
}

public sealed class SettingsResolver(BuiltInSettingsCatalog catalog) : ISettingsResolver
{
    public SettingsResolution Resolve(SettingsContext context, SettingsDocumentSnapshot user,
        SettingsDocumentSnapshot workspace)
    {
        var values = ImmutableDictionary.CreateBuilder<string, UntypedSettingValue>(StringComparer.Ordinal);
        var diagnostics = new List<SettingsDiagnostic>();
        if (user.Diagnostic is not null) diagnostics.Add(user.Diagnostic);
        if (workspace.Diagnostic is not null) diagnostics.Add(workspace.Diagnostic);
        foreach (var definition in catalog.Definitions.Values)
        {
            var userNode = GetNode(user.EffectiveRoot, definition.Id);
            var workspaceNode = GetNode(workspace.EffectiveRoot, definition.Id);
            var userLanguageNode = GetNode(user.EffectiveRoot, definition.Id, context.LanguageId);
            var workspaceLanguageNode = GetNode(workspace.EffectiveRoot, definition.Id, context.LanguageId);
            if ((workspaceNode is not null || workspaceLanguageNode is not null) &&
                !definition.AllowedScopes.HasFlag(SettingScope.Workspace))
                diagnostics.Add(new(workspace.Path, 1, 1,
                    $"{definition.Id}: 出于安全原因忽略工作区中的用户级设置。", false));
            if ((userLanguageNode is not null || workspaceLanguageNode is not null) &&
                !definition.AllowedScopes.HasFlag(SettingScope.Language))
                diagnostics.Add(new(workspaceLanguageNode is null ? user.Path : workspace.Path, 1, 1,
                    $"{definition.Id}: 此设置不支持语言级覆盖，已保留原文并忽略。", false));
            var userValue = Read(definition, userNode, user.Path, diagnostics);
            var workspaceValue = Read(definition, workspaceNode, workspace.Path, diagnostics);
            var userLanguageValue = Read(definition, userLanguageNode, user.Path, diagnostics);
            var workspaceLanguageValue = Read(definition, workspaceLanguageNode, workspace.Path, diagnostics);
            var effective = definition.DefaultValue;
            var source = SettingValueSource.Default;
            if (userValue.HasValue) { effective = userValue.Value; source = SettingValueSource.User; }
            if (workspaceValue.HasValue && definition.AllowedScopes.HasFlag(SettingScope.Workspace))
            { effective = workspaceValue.Value; source = SettingValueSource.Workspace; }
            if (userLanguageValue.HasValue && definition.AllowedScopes.HasFlag(SettingScope.Language))
            { effective = userLanguageValue.Value; source = SettingValueSource.UserLanguage; }
            if (workspaceLanguageValue.HasValue && definition.AllowedScopes.HasFlag(SettingScope.Language) &&
                definition.AllowedScopes.HasFlag(SettingScope.Workspace))
            { effective = workspaceLanguageValue.Value; source = SettingValueSource.WorkspaceLanguage; }
            values[definition.Id] = new(definition.DefaultValue, userValue, workspaceValue,
                userLanguageValue, workspaceLanguageValue, effective, source);
        }
        return new(values.ToImmutable(), Flatten(user.EffectiveRoot), Flatten(workspace.EffectiveRoot), diagnostics);
    }

    private static UntypedOptionalValue Read(SettingDefinition definition, JsonNode? node, string path,
        ICollection<SettingsDiagnostic> diagnostics)
    {
        if (node is null) return UntypedOptionalValue.None;
        var value = definition.Read(node);
        var error = definition.ValidateObject(value);
        if (error is null) return UntypedOptionalValue.Some(value);
        diagnostics.Add(new(path, 1, 1, $"{definition.Id}: {error}", false));
        return UntypedOptionalValue.None;
    }

    private static JsonNode? GetNode(JsonObject root, string key, string? languageId = null) =>
        string.IsNullOrWhiteSpace(languageId) ? root[key] : (root[$"[{languageId}]"] as JsonObject)?[key];

    private static ImmutableDictionary<string, JsonNode?> Flatten(JsonObject root)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var property in root)
        {
            if (property.Key.StartsWith('[') && property.Key.EndsWith(']') && property.Value is JsonObject language)
            {
                var languageId = property.Key[1..^1];
                foreach (var setting in language) builder[$"[{languageId}]::{setting.Key}"] = setting.Value?.DeepClone();
            }
            else builder[property.Key] = property.Value?.DeepClone();
        }
        return builder.ToImmutable();
    }
}

public interface ISettingsChangeCoordinator : IDisposable
{
    IAsyncEnumerable<string> WatchAsync(CancellationToken cancellationToken = default);
    void Track(string path);
    void AcknowledgeWrite(string path, string revision);
    bool ConsumeSelfWrite(string path, string revision);
}

public sealed class SettingsChangeCoordinator : ISettingsChangeCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _debounces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _selfWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Channel<string>> _subscribers = [];

    public void Track(string path)
    {
        path = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_watchers.ContainsKey(path)) return;
            var directory = Path.GetDirectoryName(path)!;
            while (!Directory.Exists(directory) && Path.GetDirectoryName(directory) is { } parent)
                directory = parent;
            if (!Directory.Exists(directory)) return;
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            void Changed(string candidate)
            {
                if (string.Equals(Path.GetFullPath(candidate), path, StringComparison.OrdinalIgnoreCase)) Debounce(path);
            }
            watcher.Changed += (_, args) => Changed(args.FullPath);
            watcher.Created += (_, args) => Changed(args.FullPath);
            watcher.Deleted += (_, args) => Changed(args.FullPath);
            watcher.Renamed += (_, args) => Changed(args.FullPath);
            _watchers[path] = watcher;
        }
    }

    public void AcknowledgeWrite(string path, string revision)
    {
        lock (_gate) _selfWrites[Path.GetFullPath(path)] = revision;
    }

    public async IAsyncEnumerable<string> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<string>();
        lock (_gate) _subscribers.Add(channel);
        try
        {
            await foreach (var path in channel.Reader.ReadAllAsync(cancellationToken)) yield return path;
        }
        finally { lock (_gate) _subscribers.Remove(channel); }
    }

    public bool ConsumeSelfWrite(string path, string revision)
    {
        path = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_selfWrites.TryGetValue(path, out var expected) || expected != revision) return false;
            _selfWrites.Remove(path);
            return true;
        }
    }

    private void Debounce(string path)
    {
        CancellationTokenSource lifetime;
        lock (_gate)
        {
            if (_debounces.Remove(path, out var previous)) { previous.Cancel(); previous.Dispose(); }
            lifetime = new CancellationTokenSource();
            _debounces[path] = lifetime;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, lifetime.Token);
                Channel<string>[] targets;
                lock (_gate) targets = [.. _subscribers];
                foreach (var target in targets) await target.Writer.WriteAsync(path, lifetime.Token);
            }
            catch (OperationCanceledException) { }
        });
    }

    public void Dispose()
    {
        List<FileSystemWatcher> watchers;
        lock (_gate)
        {
            watchers = [.. _watchers.Values];
            foreach (var debounce in _debounces.Values) { debounce.Cancel(); debounce.Dispose(); }
            _watchers.Clear();
            _debounces.Clear();
            foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }

        // FileSystemWatcher teardown can block for seconds while the OS drains a flooded event
        // buffer. This runs on the shutdown path, so stop event generation synchronously and
        // release each native watch handle off-thread; the OS reclaims the handles at process
        // exit either way.
        foreach (var watcher in watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
            }
            catch (Exception) { /* already torn down */ }
            Task.Run(() =>
            {
                try { watcher.Dispose(); }
                catch (Exception) { /* best-effort; the process owns the handle until exit */ }
            });
        }
    }
}
