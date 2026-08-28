using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nornia.Desktop.Configuration;

[Flags]
public enum SettingScope
{
    None = 0,
    User = 1,
    Workspace = 2,
    Language = 4,
    MachineState = 8,
}

public enum SettingValueSource
{
    Default,
    User,
    Workspace,
    UserLanguage,
    WorkspaceLanguage,
}

public enum SettingEditorKind { Boolean, Enumeration, Number, String, Path, Glob, Color, Shell, Font }

public sealed record SettingsContext(string? WorkspacePath = null, string? LanguageId = null)
{
    public string? NormalizedWorkspacePath => string.IsNullOrWhiteSpace(WorkspacePath)
        ? null
        : Path.GetFullPath(WorkspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

public abstract record SettingDefinition(
    string Id,
    Type ValueType,
    object? DefaultValue,
    string Title,
    string Description,
    string Category,
    IReadOnlyList<string> Keywords,
    SettingScope AllowedScopes,
    SettingEditorKind EditorKind,
    bool RequiresRestart = false)
{
    public abstract string? ValidateObject(object? value);
    public abstract object? Read(JsonNode? node);
    public abstract JsonNode? Write(object? value);
}

public sealed record SettingDefinition<T>(
    SettingKey<T> Key,
    T Default,
    string DisplayTitle,
    string DisplayDescription,
    string DisplayCategory,
    IReadOnlyList<string> SearchKeywords,
    SettingScope Scopes,
    SettingEditorKind Kind,
    Func<T, string?>? Validator = null,
    IReadOnlyList<T>? EnumValues = null,
    bool RestartRequired = false)
    : SettingDefinition(Key.Id, typeof(T), Default, DisplayTitle, DisplayDescription, DisplayCategory,
        SearchKeywords, Scopes, Kind, RestartRequired)
{
    public override string? ValidateObject(object? value)
    {
        if (value is not T typed) return $"{Id} 需要 {typeof(T).Name} 类型的值。";
        if (EnumValues is not null && !EnumValues.Contains(typed)) return $"{Id} 的值不在允许范围内。";
        return Validator?.Invoke(typed);
    }

    public override object? Read(JsonNode? node)
    {
        if (node is null) return default(T);
        try { return node.Deserialize<T>(); }
        catch { return null; }
    }

    public override JsonNode? Write(object? value) => value is null ? null : JsonSerializer.SerializeToNode((T)value);
}

public readonly record struct SettingKey<T>(string Id)
{
    public override string ToString() => Id;
}

public readonly record struct OptionalValue<T>(bool HasValue, T? Value)
{
    public static OptionalValue<T> None => default;
    public static OptionalValue<T> Some(T value) => new(true, value);
    public T GetValueOrDefault(T fallback) => HasValue ? Value! : fallback;
}

public readonly record struct UntypedOptionalValue(bool HasValue, object? Value)
{
    public static UntypedOptionalValue None => default;
    public static UntypedOptionalValue Some(object? value) => new(true, value);
    public OptionalValue<T> As<T>() => HasValue ? OptionalValue<T>.Some((T)Value!) : OptionalValue<T>.None;
}

public sealed record SettingValue<T>(
    T DefaultValue,
    OptionalValue<T> UserValue,
    OptionalValue<T> WorkspaceValue,
    OptionalValue<T> UserLanguageValue,
    OptionalValue<T> WorkspaceLanguageValue,
    T EffectiveValue,
    SettingValueSource Source);

public sealed record UntypedSettingValue(
    object? DefaultValue,
    UntypedOptionalValue UserValue,
    UntypedOptionalValue WorkspaceValue,
    UntypedOptionalValue UserLanguageValue,
    UntypedOptionalValue WorkspaceLanguageValue,
    object? EffectiveValue,
    SettingValueSource Source);

public sealed class SettingsSnapshot
{
    internal SettingsSnapshot(long revision, SettingsContext context,
        ImmutableDictionary<string, UntypedSettingValue> values,
        ImmutableDictionary<string, JsonNode?> userValues,
        ImmutableDictionary<string, JsonNode?> workspaceValues,
        string userRevision, string workspaceRevision,
        IReadOnlyList<SettingsDiagnostic> diagnostics)
    {
        Revision = revision;
        Context = context;
        Values = values;
        UserValues = userValues;
        WorkspaceValues = workspaceValues;
        UserFileRevision = userRevision;
        WorkspaceFileRevision = workspaceRevision;
        Diagnostics = diagnostics;
    }

    public long Revision { get; }
    public SettingsContext Context { get; }
    public IReadOnlyDictionary<string, UntypedSettingValue> Values { get; }
    public IReadOnlyList<SettingsDiagnostic> Diagnostics { get; }
    internal ImmutableDictionary<string, JsonNode?> UserValues { get; }
    internal ImmutableDictionary<string, JsonNode?> WorkspaceValues { get; }
    public string UserFileRevision { get; }
    public string WorkspaceFileRevision { get; }

    public SettingValue<T> Get<T>(SettingKey<T> key)
    {
        var value = Values[key.Id];
        return new((T)value.DefaultValue!, value.UserValue.As<T>(), value.WorkspaceValue.As<T>(),
            value.UserLanguageValue.As<T>(), value.WorkspaceLanguageValue.As<T>(),
            (T)value.EffectiveValue!, value.Source);
    }

    public T Effective<T>(SettingKey<T> key) => Get(key).EffectiveValue;
}

public sealed record SettingsDiagnostic(string FilePath, int Line, int Column, string Message, bool IsError = true);

public enum SettingsCommitStatus { Success, ValidationFailed, FileError, Conflict }

public sealed record SettingOperation(string Key, JsonNode? Value, bool Reset = false, string? LanguageId = null,
    JsonNode? BaselineValue = null);

public sealed record SettingsTransaction(
    SettingsSnapshot Baseline,
    SettingScope TargetScope,
    IReadOnlyList<SettingOperation> Operations,
    string? LanguageId = null)
{
    public static SettingsTransaction Set<T>(SettingsSnapshot baseline, SettingScope scope,
        SettingKey<T> key, T value, string? languageId = null) =>
        new(baseline, scope, [new(key.Id, JsonValue.Create(value), false, languageId)]);
}

public sealed record SettingsConflict(string Key, JsonNode? BaselineValue, JsonNode? DiskValue, JsonNode? LocalValue);

public sealed record SettingsCommitResult(
    SettingsCommitStatus Status,
    SettingsSnapshot? Snapshot = null,
    IReadOnlyDictionary<string, string>? ValidationErrors = null,
    IReadOnlyList<SettingsConflict>? Conflicts = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == SettingsCommitStatus.Success;
}

public sealed record SettingsChange(string Key, object? OldValue, object? NewValue,
    SettingValueSource OldSource, SettingValueSource NewSource);
public sealed record SettingsChangeSet(long Revision, string Source, SettingsContext Context,
    IReadOnlyList<SettingsChange> Changes);

public interface ISettingsService
{
    Task<SettingsSnapshot> GetSnapshotAsync(SettingsContext context, CancellationToken cancellationToken = default);
    Task<SettingsCommitResult> CommitAsync(SettingsTransaction transaction, CancellationToken cancellationToken = default);
    Task<SettingsCommitResult> ResetAsync(string key, SettingScope scope, SettingsContext context,
        string? languageId = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SettingsChangeSet> WatchAsync(SettingsContext context, CancellationToken cancellationToken = default);
    Task<ISettingsSession> OpenSessionAsync(SettingsContext context, IReadOnlyCollection<string>? keys = null,
        CancellationToken cancellationToken = default);
}

public interface ISettingsSession : IAsyncDisposable
{
    SettingsContext Context { get; }
    SettingsSnapshot? Current { get; }
    event EventHandler<SettingsChangeSet>? Changed;
    IReadOnlyCollection<string>? SubscribedKeys { get; }
    Task<SettingsSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task<SettingsCommitResult> CommitAsync(SettingScope scope, IReadOnlyList<SettingOperation> operations,
        CancellationToken cancellationToken = default);
}
