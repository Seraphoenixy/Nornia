using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Nornia.Desktop.Configuration;
using Nornia.Storage;

namespace Nornia.Desktop.Commands;

public sealed record KeybindingDefinition(string Key, string Command, string? When = null, JsonNode? Args = null,
    bool IsDefault = false, int Order = 0)
{
    public bool RemovesDefault => Command.StartsWith("-", StringComparison.Ordinal);
    public string EffectiveCommand => RemovesDefault ? Command[1..] : Command;
    public IReadOnlyList<string> Chord => KeyGestureNormalizer.Normalize(Key).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

public sealed record KeybindingConflict(KeybindingDefinition Binding, KeybindingDefinition Other, string Kind);
public sealed record KeybindingDiagnostic(KeybindingDefinition Binding, string Message);

public static class KeyGestureNormalizer
{
    private static readonly string[] ModifierOrder = ["ctrl", "shift", "alt", "win"];

    public static string Normalize(string gesture) => string.Join(' ', gesture.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(NormalizeStroke));

    public static string NormalizeStroke(string stroke)
    {
        var parts = stroke.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant()).ToArray();
        var modifiers = ModifierOrder.Where(parts.Contains);
        var key = parts.LastOrDefault(item => !ModifierOrder.Contains(item)) ?? string.Empty;
        key = key switch { "control" => "ctrl", "pageup" => "pageup", "pagedown" => "pagedown", "oemtilde" => "`",
            "oembackslash" => "\\", "oempipe" => "\\", _ => key };
        return string.Join('+', modifiers.Append(key).Where(item => item.Length > 0));
    }
}

public interface IKeybindingService
{
    string KeybindingsPath { get; }
    IReadOnlyList<KeybindingDefinition> Bindings { get; }
    IReadOnlyList<KeybindingConflict> Conflicts { get; }
    IReadOnlyList<KeybindingDiagnostic> Diagnostics { get; }
    event EventHandler? BindingsChanged;
    Task ReloadAsync(CancellationToken cancellationToken = default);
    Task SaveUserBindingsAsync(IReadOnlyList<KeybindingDefinition> bindings, CancellationToken cancellationToken = default);
    Task<bool> DispatchAsync(string stroke, CancellationToken cancellationToken = default);
    string? GetPrimaryBinding(string commandId);
    string? PendingChord { get; }
    event EventHandler? PendingChordChanged;
    void CancelChord();
}

public sealed class KeybindingService : IKeybindingService, IDisposable
{
    private readonly ICommandRegistry _registry;
    private readonly IContextKeyService _context;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private IReadOnlyList<KeybindingDefinition> _bindings = [];
    private IReadOnlyList<KeybindingDefinition> _baselineUsers = [];
    private string? _pendingChord;
    private DateTimeOffset _pendingSince;
    private CancellationTokenSource? _chordTimeout;
    private readonly ISettingsChangeCoordinator? _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    public KeybindingService(ICommandRegistry registry, IContextKeyService context) : this(registry, context, Path.Combine(
        NorniaPaths.DataDirectory, "keybindings.json"), null) { }

    public KeybindingService(ICommandRegistry registry, IContextKeyService context,
        ISettingsChangeCoordinator coordinator) : this(registry, context, Path.Combine(
        NorniaPaths.DataDirectory, "keybindings.json"), coordinator) { }

    public KeybindingService(ICommandRegistry registry, IContextKeyService context, string keybindingsPath)
        : this(registry, context, keybindingsPath, null) { }

    private KeybindingService(ICommandRegistry registry, IContextKeyService context, string keybindingsPath,
        ISettingsChangeCoordinator? coordinator)
    {
        _registry = registry;
        _context = context;
        KeybindingsPath = keybindingsPath;
        _coordinator = coordinator;
        _coordinator?.Track(KeybindingsPath);
        // 生命周期由 _lifetime 控制;关停时只取消不 join(见 Dispose 注释),无需持有任务引用。
        if (_coordinator is not null) _ = WatchFileAsync(_lifetime.Token);
    }

    public string KeybindingsPath { get; }
    public IReadOnlyList<KeybindingDefinition> Bindings => _bindings;
    public IReadOnlyList<KeybindingConflict> Conflicts { get; private set; } = [];
    public IReadOnlyList<KeybindingDiagnostic> Diagnostics { get; private set; } = [];
    public event EventHandler? BindingsChanged;
    public string? PendingChord => _pendingChord;
    public event EventHandler? PendingChordChanged;

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var defaults = _registry.Commands.SelectMany(command => command.DefaultKeybindings.Select((binding, index) =>
                binding with { Command = command.Id, IsDefault = true, Order = index })).ToList();
            var users = await ReadUserBindingsAsync(cancellationToken);
            _baselineUsers = users;
            foreach (var removal in users.Where(item => item.RemovesDefault))
                defaults.RemoveAll(item => string.Equals(item.Command, removal.EffectiveCommand, StringComparison.Ordinal) &&
                    string.Equals(KeyGestureNormalizer.Normalize(item.Key), KeyGestureNormalizer.Normalize(removal.Key), StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(removal.When) || string.Equals(item.When, removal.When, StringComparison.Ordinal)));
            _bindings = defaults.Concat(users.Where(item => !item.RemovesDefault)).ToArray();
            Conflicts = FindConflicts(_bindings);
            Diagnostics = _bindings.Where(item => !string.IsNullOrWhiteSpace(item.When)).Select(item =>
            {
                try { _ = WhenExpression.Parse(item.When!); return null; }
                catch (Exception exception) when (exception is FormatException or ArgumentException)
                { return new KeybindingDiagnostic(item, exception.Message); }
            }).Where(item => item is not null).Cast<KeybindingDiagnostic>().ToArray();
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveUserBindingsAsync(IReadOnlyList<KeybindingDefinition> bindings,
        CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try { await SaveUserBindingsCoreAsync(bindings, cancellationToken); }
        finally { _saveGate.Release(); }
    }

    private async Task SaveUserBindingsCoreAsync(IReadOnlyList<KeybindingDefinition> bindings,
        CancellationToken cancellationToken)
    {
        var document = await JsoncKeybindingDocument.LoadAsync(KeybindingsPath, cancellationToken);
        var disk = document.ReadBindings();
        var local = bindings.Where(item => !item.IsDefault).ToArray();
        var commands = _baselineUsers.Concat(disk).Concat(local).Select(item => item.EffectiveCommand)
            .ToHashSet(StringComparer.Ordinal);
        var merged = new List<KeybindingDefinition>();
        var conflicts = new List<KeybindingConflict>();
        foreach (var command in commands)
        {
            var baselineGroup = Group(_baselineUsers, command);
            var diskGroup = Group(disk, command);
            var localGroup = Group(local, command);
            var diskChanged = !SameBindings(baselineGroup, diskGroup);
            var localChanged = !SameBindings(baselineGroup, localGroup);
            if (diskChanged && localChanged && !SameBindings(diskGroup, localGroup))
            {
                var localItem = localGroup.FirstOrDefault() ?? new(string.Empty, command);
                var diskItem = diskGroup.FirstOrDefault() ?? new(string.Empty, command);
                conflicts.Add(new(localItem, diskItem, "外部同命令冲突"));
                continue;
            }
            merged.AddRange(localChanged ? localGroup : diskGroup);
        }
        if (conflicts.Count > 0)
        {
            Conflicts = conflicts;
            throw new InvalidOperationException("keybindings.json 包含同命令的外部修改，请重新加载后重试。");
        }
        var text = document.Patch(merged);
        var directory = Path.GetDirectoryName(KeybindingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".keybindings.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = document.Encoding.GetBytes(text);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (document.HasBom) await stream.WriteAsync(document.Encoding.GetPreamble(), cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (File.Exists(KeybindingsPath)) File.Replace(temporary, KeybindingsPath, null, true);
            else File.Move(temporary, KeybindingsPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (_coordinator is not null)
        {
            var written = await JsoncKeybindingDocument.LoadAsync(KeybindingsPath, cancellationToken);
            _coordinator.AcknowledgeWrite(KeybindingsPath, written.Revision);
        }
        await ReloadAsync(cancellationToken);
    }

    public async Task<bool> DispatchAsync(string stroke, CancellationToken cancellationToken = default)
    {
        if (_bindings.Count == 0) await ReloadAsync(cancellationToken);
        var normalized = KeyGestureNormalizer.NormalizeStroke(stroke);
        if (_pendingChord is not null && normalized == "escape")
        {
            CancelChord();
            return true;
        }
        if (_pendingChord is not null && DateTimeOffset.UtcNow - _pendingSince > TimeSpan.FromSeconds(1)) SetPendingChord(null);
        var candidate = _pendingChord is null ? normalized : $"{_pendingChord} {normalized}";
        var matches = _bindings.Where(item => _context.Matches(item.When)).Reverse().ToArray();
        var exact = matches.FirstOrDefault(item => string.Equals(KeyGestureNormalizer.Normalize(item.Key), candidate,
            StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            SetPendingChord(null);
            return await _registry.ExecuteAsync(exact.Command, exact.Args, cancellationToken);
        }
        if (matches.Any(item => KeyGestureNormalizer.Normalize(item.Key).StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase)))
        {
            SetPendingChord(candidate);
            _pendingSince = DateTimeOffset.UtcNow;
            return true;
        }
        SetPendingChord(null);
        return false;
    }

    public void CancelChord() => SetPendingChord(null);

    private void SetPendingChord(string? value)
    {
        if (string.Equals(_pendingChord, value, StringComparison.Ordinal)) return;
        _chordTimeout?.Cancel();
        _chordTimeout?.Dispose();
        _chordTimeout = null;
        _pendingChord = value;
        PendingChordChanged?.Invoke(this, EventArgs.Empty);
        if (value is null) return;
        var timeout = _chordTimeout = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
                if (ReferenceEquals(_chordTimeout, timeout)) SetPendingChord(null);
            }
            catch (OperationCanceledException) { }
        });
    }

    public string? GetPrimaryBinding(string commandId) => _bindings.LastOrDefault(item =>
        string.Equals(item.Command, commandId, StringComparison.Ordinal) && _context.Matches(item.When))?.Key;

    private async Task<IReadOnlyList<KeybindingDefinition>> ReadUserBindingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(KeybindingsPath)) return [];
        return (await JsoncKeybindingDocument.LoadAsync(KeybindingsPath, cancellationToken)).ReadBindings();
    }

    private static IReadOnlyList<KeybindingConflict> FindConflicts(IReadOnlyList<KeybindingDefinition> bindings)
    {
        var result = new List<KeybindingConflict>();
        for (var index = 0; index < bindings.Count; index++)
        for (var other = index + 1; other < bindings.Count; other++)
            if (string.Equals(KeyGestureNormalizer.Normalize(bindings[index].Key), KeyGestureNormalizer.Normalize(bindings[other].Key), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(bindings[index].When, bindings[other].When, StringComparison.OrdinalIgnoreCase))
                result.Add(new(bindings[other], bindings[index], "冲突"));
        return result;
    }

    private static KeybindingDefinition[] Group(IEnumerable<KeybindingDefinition> source, string command) => source
        .Where(item => string.Equals(item.EffectiveCommand, command, StringComparison.Ordinal)).ToArray();

    private static bool SameBindings(IReadOnlyList<KeybindingDefinition> left, IReadOnlyList<KeybindingDefinition> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(KeyGestureNormalizer.Normalize(pair.First.Key), KeyGestureNormalizer.Normalize(pair.Second.Key), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.Command, pair.Second.Command, StringComparison.Ordinal) &&
            string.Equals(pair.First.When, pair.Second.When, StringComparison.Ordinal) &&
            JsonNode.DeepEquals(pair.First.Args, pair.Second.Args));

    private async Task WatchFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in _coordinator!.WatchAsync(cancellationToken))
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(KeybindingsPath), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var document = await JsoncKeybindingDocument.LoadAsync(KeybindingsPath, cancellationToken);
                        if (!_coordinator.ConsumeSelfWrite(KeybindingsPath, document.Revision))
                            await ReloadAsync(cancellationToken);
                    }
                    catch (JsonException exception)
                    {
                        Diagnostics = [new(new(string.Empty, string.Empty), exception.Message)];
                        BindingsChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        // 关停路径不 join _watchTask:watch 任务在 UI 线程启动、续延绑定 UI 调度器,
        // OnExit 期间调度器已停止泵帧,同步 GetAwaiter().GetResult() 会冻结释放线程直到
        // 5s 看门狗放弃整个容器释放(实测挂死源)。取消后直接返回,任务随进程回收;
        // CTS 一并不 Dispose——挂起中的任务仍持有 token 注册。
        _lifetime.Cancel();
        _chordTimeout?.Cancel();
        _chordTimeout?.Dispose();
        _gate.Dispose();
        _saveGate.Dispose();
    }
}
