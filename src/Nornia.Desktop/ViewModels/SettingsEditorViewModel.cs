using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Commands;
using Nornia.Desktop.Services;

namespace Nornia.Desktop.ViewModels;

/// <summary>设置下拉框的显示层选项:界面展示 <see cref="Text"/>(中文含义/格式化后的文本),
/// 提交仍使用 <see cref="Value"/> 原始配置值 —— 显示文本与实际提交值分离,不改变配置格式。
/// 仅用于设置页展示;配置模型、快照与文件格式不感知该类型。</summary>
public sealed record SettingDisplayOption(object Value, string Text);

public partial class SettingEditorItem : ObservableObject
{
    private readonly Func<SettingEditorItem, Task> _save;
    private bool _loading;

    public SettingEditorItem(SettingDefinition definition, Func<SettingEditorItem, Task> save)
    {
        Definition = definition;
        _save = save;
        EnumOptions = definition is SettingDefinition<string> strings && strings.EnumValues is { } values
            ? values.Select(value => new SettingDisplayOption(value, SettingDisplayFormatter.Format(value))).ToArray()
            : [];
    }

    public SettingDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public string Category => Definition.Category;
    public SettingEditorKind EditorKind => Definition.EditorKind;
    public bool IsBoolean => EditorKind == SettingEditorKind.Boolean;
    public bool IsEnumeration => EditorKind == SettingEditorKind.Enumeration;
    public bool IsFontEditor => EditorKind == SettingEditorKind.Font;
    public bool IsTextEditor => !IsBoolean && !IsEnumeration && !IsFontEditor;
    public IReadOnlyList<SettingDisplayOption> EnumOptions { get; }
    public string SearchIndex => _searchIndex ??= string.Join(' ', new[]
    { Title, Id, Description, Category }.Concat(Definition.Keywords)).ToUpperInvariant();
    private string? _searchIndex;
    public bool IsModified => Source != SettingValueSource.Default;
    public string SourceText => Source switch
    {
        SettingValueSource.User => "用户",
        SettingValueSource.Workspace => "工作区",
        SettingValueSource.UserLanguage => "用户 · 语言覆盖",
        SettingValueSource.WorkspaceLanguage => "工作区 · 语言覆盖",
        _ => "内置",
    };

    [ObservableProperty] private SettingValueSource source;
    [ObservableProperty] private bool booleanValue;
    [ObservableProperty] private string valueText = string.Empty;
    [ObservableProperty] private object? selectedEnum;
    [ObservableProperty] private string errorText = string.Empty;
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private string sourceChainText = "默认值";
    [ObservableProperty] private string currentScopeValueText = "未设置";
    [ObservableProperty] private string effectiveValueText = string.Empty;
    [ObservableProperty] private string defaultValueText = string.Empty;

    public void Apply(UntypedSettingValue value, SettingScope scope, bool languageOverride)
    {
        _loading = true;
        try
        {
            Source = value.Source;
            var scoped = (scope, languageOverride) switch
            {
                (SettingScope.User, true) => value.UserLanguageValue,
                (SettingScope.Workspace, true) => value.WorkspaceLanguageValue,
                (SettingScope.User, false) => value.UserValue,
                _ => value.WorkspaceValue,
            };
            // 未配置的作用域显示"未设置(继承)";生效值始终显示最终有效值。
            CurrentScopeValueText = scoped.HasValue ? SettingDisplayFormatter.Format(scoped.Value) : "未设置";
            EffectiveValueText = SettingDisplayFormatter.Format(value.EffectiveValue);
            DefaultValueText = SettingDisplayFormatter.Format(value.DefaultValue);
            // 来源链:内置 → 用户 → 工作区 → 用户语言 → 工作区语言,每段带实际值。
            SourceChainText = string.Join(" → ", ChainSegments(value));
            if (value.EffectiveValue is bool boolean) BooleanValue = boolean;
            else if (IsEnumeration || IsFontEditor)
            {
                SelectedEnum = EnumOptions.FirstOrDefault(option => Equals(option.Value, value.EffectiveValue))
                    // 兜底:磁盘/语言覆盖中出现未列入候选的值时仍可见,不丢内容。
                    ?? (value.EffectiveValue is null
                        ? null
                        : new SettingDisplayOption(value.EffectiveValue, SettingDisplayFormatter.Format(value.EffectiveValue)));
            }
            else if (Definition.ValueType == typeof(Dictionary<string, bool>) || Definition.ValueType == typeof(string[]))
                ValueText = JsonSerializer.Serialize(value.EffectiveValue);
            else ValueText = Convert.ToString(value.EffectiveValue, CultureInfo.InvariantCulture) ?? string.Empty;
            ErrorText = string.Empty;
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(SourceText));
        }
        finally { _loading = false; }
    }

    private static IEnumerable<string> ChainSegments(UntypedSettingValue value)
    {
        yield return $"内置：{SettingDisplayFormatter.Format(value.DefaultValue)}";
        if (value.UserValue.HasValue) yield return $"用户：{SettingDisplayFormatter.Format(value.UserValue.Value)}";
        if (value.WorkspaceValue.HasValue) yield return $"工作区：{SettingDisplayFormatter.Format(value.WorkspaceValue.Value)}";
        if (value.UserLanguageValue.HasValue) yield return $"用户语言：{SettingDisplayFormatter.Format(value.UserLanguageValue.Value)}";
        if (value.WorkspaceLanguageValue.HasValue) yield return $"工作区语言：{SettingDisplayFormatter.Format(value.WorkspaceLanguageValue.Value)}";
    }

    public bool TryCreateOperation(string? languageId, out SettingOperation operation)
    {
        object? value;
        if (IsBoolean) value = BooleanValue;
        else if (IsEnumeration || IsFontEditor) value = (SelectedEnum as SettingDisplayOption)?.Value;
        else if (Definition.ValueType == typeof(double) && double.TryParse(ValueText, NumberStyles.Float,
                     CultureInfo.InvariantCulture, out var number)) value = number;
        else if (Definition.ValueType == typeof(int) && int.TryParse(ValueText, NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out var integer)) value = integer;
        else if (Definition.ValueType == typeof(string)) value = ValueText;
        else if (Definition.ValueType == typeof(Dictionary<string, bool>))
        {
            try { value = JsonSerializer.Deserialize<Dictionary<string, bool>>(ValueText); }
            catch (JsonException) { value = null; }
        }
        else if (Definition.ValueType == typeof(string[]))
        {
            try { value = JsonSerializer.Deserialize<string[]>(ValueText); }
            catch (JsonException) { value = null; }
        }
        else value = null;
        var error = Definition.ValidateObject(value);
        ErrorText = error ?? string.Empty;
        operation = new(Id, error is null ? JsonSerializer.SerializeToNode(value, Definition.ValueType) : null,
            false, languageId);
        return error is null;
    }

    partial void OnSourceChanged(SettingValueSource value) { OnPropertyChanged(nameof(IsModified)); OnPropertyChanged(nameof(SourceText)); }
    partial void OnBooleanValueChanged(bool value) => Queue();
    partial void OnValueTextChanged(string value) => Queue();
    partial void OnSelectedEnumChanged(object? value) => Queue();
    private void Queue() { if (!_loading) _ = _save(this); }
}

public partial class SettingsEditorViewModel : ObservableObject, IDisposable, IAsyncDisposable, IShutdownParticipant
{
    private readonly ISettingsService _settings;
    private readonly BuiltInSettingsCatalog _catalog;
    private readonly IProjectWorkspaceService _workspace;
    private readonly IKeybindingService _keybindings;
    private readonly ICommandRegistry _commands;
    private readonly IApplicationStateStore _stateStore;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, SettingEditorItem> _pending = new(StringComparer.Ordinal);
    private SettingsSnapshot? _snapshot;
    private ISettingsSession? _session;
    private string _editingLanguage = "所有语言";

    public SettingsEditorViewModel(ISettingsService settings, BuiltInSettingsCatalog catalog,
        IProjectWorkspaceService workspace, IKeybindingService keybindings, ICommandRegistry commands,
        IApplicationStateStore stateStore)
    {
        _settings = settings;
        _catalog = catalog;
        _workspace = workspace;
        _keybindings = keybindings;
        _commands = commands;
        _stateStore = stateStore;
        Items = new(_catalog.Definitions.Values.Select(definition => new SettingEditorItem(definition, QueueSaveAsync)));
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
        Languages = ["所有语言", "csharp", "typescript", "javascript", "json", "xaml", "xml", "python", "yaml", "markdown"];
        ShortcutItems = [];
        Conflicts = [];
        ShortcutView = CollectionViewSource.GetDefaultView(ShortcutItems);
        ShortcutView.Filter = FilterShortcut;
        Categories = [new("全部", Codicons.ListTree)];
        // 固定且有意义的分类顺序;目录中不存在的分类跳过,新增的未知分类按名称追加到末尾。
        var fixedCategories = new[] { "常用", "工作台", "编辑器", "Diff", "终端", "工作区与源代码管理", "外观", "布局" };
        var present = _catalog.Definitions.Values.Select(item => item.Category).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var category in fixedCategories.Where(present.Contains)
                     .Concat(present.Where(item => !fixedCategories.Contains(item)).OrderBy(item => item, StringComparer.Ordinal)))
            Categories.Add(new(category, CategoryGlyph(category)));
        selectedCategory = Categories[0];
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await FlushAsync(); };
        _workspace.ContextChanged += OnWorkspaceChangedAsync;
        _keybindings.BindingsChanged += OnBindingsChanged;
    }

    public ObservableCollection<SettingEditorItem> Items { get; }
    public int ShutdownOrder => 100;
    public ICollectionView View { get; }
    public IReadOnlyList<string> Languages { get; }
    public ObservableCollection<KeyboardShortcutItem> ShortcutItems { get; }
    public ObservableCollection<SettingsConflict> Conflicts { get; }
    public ICollectionView ShortcutView { get; }
    public ObservableCollection<SettingsCategoryItem> Categories { get; }
    public bool HasWorkspace => _workspace.Current is not null;
    public bool HasConflicts => Conflicts.Count > 0;
    public bool IsWorkspaceScope => SelectedScope == SettingScope.Workspace;
    public string ScopeDescription => IsWorkspaceScope
        ? HasWorkspace ? "写入 .vscode/settings.json，可与团队共享。" : "请先打开工作区。"
        : "写入本机用户设置，应用于所有工作区。";

    [ObservableProperty] private SettingScope selectedScope = SettingScope.User;
    [ObservableProperty] private string selectedLanguage = "所有语言";
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string saveStatus = "已保存";
    [ObservableProperty] private bool showShortcuts;
    [ObservableProperty] private SettingsCategoryItem selectedCategory;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ReloadAsync(cancellationToken);
        await ReloadShortcutsAsync(cancellationToken);
    }

    [RelayCommand] private void ShowSettings() => ShowShortcuts = false;
    [RelayCommand] private void ShowKeyboardShortcuts() => ShowShortcuts = true;

    [RelayCommand]
    private async Task SaveShortcutAsync(KeyboardShortcutItem item)
    {
        var users = _keybindings.Bindings.Where(binding => !binding.IsDefault &&
            !string.Equals(binding.EffectiveCommand, item.CommandId, StringComparison.Ordinal)).ToList();
        var descriptor = _commands.TryGet(item.CommandId, out var command) ? command : null;
        var defaultKey = descriptor?.DefaultKeybindings.FirstOrDefault()?.Key;
        if (!string.IsNullOrWhiteSpace(defaultKey) && !string.Equals(KeyGestureNormalizer.Normalize(defaultKey),
                KeyGestureNormalizer.Normalize(item.Key), StringComparison.OrdinalIgnoreCase))
            users.Add(new(defaultKey, $"-{item.CommandId}"));
        if (!string.IsNullOrWhiteSpace(item.Key)) users.Add(new(item.Key, item.CommandId, item.When));
        try
        {
            await _keybindings.SaveUserBindingsAsync(users);
            await ReloadShortcutsAsync();
            SaveStatus = "已保存";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SaveStatus = exception.Message;
        }
    }

    [RelayCommand]
    private async Task ResetShortcutAsync(KeyboardShortcutItem item)
    {
        var users = _keybindings.Bindings.Where(binding => !binding.IsDefault &&
            !string.Equals(binding.EffectiveCommand, item.CommandId, StringComparison.Ordinal)).ToArray();
        try
        {
            await _keybindings.SaveUserBindingsAsync(users);
            await ReloadShortcutsAsync();
            SaveStatus = "已保存";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SaveStatus = exception.Message;
        }
    }

    [RelayCommand]
    private async Task SelectScopeAsync(string scope)
    {
        await FlushAsync();
        SelectedScope = string.Equals(scope, "Workspace", StringComparison.OrdinalIgnoreCase)
            ? SettingScope.Workspace : SettingScope.User;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ResetSettingAsync(SettingEditorItem item)
    {
        var context = CurrentContext();
        var language = _editingLanguage == "所有语言" ? null : _editingLanguage;
        var result = await _settings.ResetAsync(item.Id, SelectedScope, context, language);
        await HandleResultAsync(result);
    }

    [RelayCommand]
    private async Task ResetCurrentCategoryAsync()
    {
        if (_snapshot is null) return;
        var language = _editingLanguage == "所有语言" ? null : _editingLanguage;
        var operations = _catalog.Definitions.Values
            .Where(definition => (SelectedCategory.Name == "全部" || definition.Category == SelectedCategory.Name) &&
                definition.AllowedScopes.HasFlag(SelectedScope) &&
                (language is null || definition.AllowedScopes.HasFlag(SettingScope.Language)))
            .Select(definition => new SettingOperation(definition.Id, null, true, language)).ToArray();
        if (operations.Length > 0) await HandleResultAsync(await _settings.CommitAsync(new(_snapshot, SelectedScope, operations)));
    }

    [RelayCommand]
    private async Task ResetLayoutStateAsync()
    {
        await _stateStore.ResetLayoutAsync();
        ResetEditorLayout?.Invoke();
        SaveStatus = "布局状态已恢复";
    }

    /// <summary>恢复布局时由 MainViewModel 接线:把编辑器组合并为单组(不删除标签与阅读状态)。</summary>
    public event Action? ResetEditorLayout;

    [RelayCommand]
    private static void CopySettingId(SettingEditorItem item) => System.Windows.Clipboard.SetText(item.Id);

    private Task QueueSaveAsync(SettingEditorItem item)
    {
        _pending[item.Id] = item;
        SaveStatus = "正在保存…";
        _saveTimer.Stop();
        _saveTimer.Start();
        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _saveTimer.Stop();
        if (_pending.Count == 0 || _snapshot is null) return;
        var language = _editingLanguage == "所有语言" ? null : _editingLanguage;
        var operations = new List<SettingOperation>();
        foreach (var item in _pending.Values)
            if (item.TryCreateOperation(language, out var operation)) operations.Add(operation);
        _pending.Clear();
        if (operations.Count == 0) { SaveStatus = "存在无效设置"; return; }
        var result = await _settings.CommitAsync(new(_snapshot, SelectedScope, operations), cancellationToken);
        await HandleResultAsync(result);
    }

    private async Task HandleResultAsync(SettingsCommitResult result)
    {
        Conflicts.Clear();
        OnPropertyChanged(nameof(HasConflicts));
        if (result.IsSuccess)
        {
            _snapshot = result.Snapshot;
            ApplySnapshot();
            SaveStatus = "已保存";
            return;
        }
        foreach (var conflict in result.Conflicts ?? []) Conflicts.Add(conflict);
        OnPropertyChanged(nameof(HasConflicts));
        SaveStatus = result.Status switch
        {
            SettingsCommitStatus.Conflict => "文件已被外部修改，请重新加载后重试",
            SettingsCommitStatus.ValidationFailed => "设置值无效",
            _ => $"保存失败：{result.ErrorMessage}",
        };
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ReloadConflictsAsync()
    {
        Conflicts.Clear();
        OnPropertyChanged(nameof(HasConflicts));
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task KeepLocalConflictsAsync()
    {
        if (Conflicts.Count == 0) return;
        var fresh = await _settings.GetSnapshotAsync(CurrentContext());
        var language = _editingLanguage == "所有语言" ? null : _editingLanguage;
        var operations = Conflicts.Select(conflict => new SettingOperation(conflict.Key,
            conflict.LocalValue?.DeepClone(), conflict.LocalValue is null, language, conflict.DiskValue?.DeepClone())).ToArray();
        await HandleResultAsync(await _settings.CommitAsync(new(fresh, SelectedScope, operations)));
    }

    [RelayCommand]
    private void CopyConflictDiff()
    {
        if (Conflicts.Count == 0) return;
        var text = string.Join(Environment.NewLine + Environment.NewLine, Conflicts.Select(conflict =>
            $"{conflict.Key}\nBASE: {conflict.BaselineValue}\nLOCAL: {conflict.LocalValue}\nDISK: {conflict.DiskValue}"));
        System.Windows.Clipboard.SetText(text);
        SaveStatus = "冲突差异已复制";
    }

    private async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var context = CurrentContext();
        if (_session is null || !Equals(_session.Context, context))
        {
            if (_session is not null)
            {
                _session.Changed -= OnSessionChanged;
                await _session.DisposeAsync();
            }
            _session = await _settings.OpenSessionAsync(context, _catalog.Definitions.Keys.ToArray(), cancellationToken);
            _session.Changed += OnSessionChanged;
        }
        _snapshot = _session.Current ?? await _session.RefreshAsync(cancellationToken);
        if (!Equals(CurrentContext(), context)) return;
        // 激活链(SettingsViewModel → PageViewModel.ActivateAsync)可能在没有同步上下文的
        // 线程上启动:await 之后续体会落到线程池,而绑定集合(Items/View 及其
        // CollectionView)只能在调度程序线程上变更。与 OnSessionChanged/OnBindingsChanged
        // 回调同一约定。
        RunOnDispatcher(() =>
        {
            if (!Equals(CurrentContext(), context)) return;
            ApplySnapshot();
            SaveStatus = _snapshot.Diagnostics.Count == 0 ? "已保存" : _snapshot.Diagnostics[0].Message;
            OnPropertyChanged(nameof(HasWorkspace));
            OnPropertyChanged(nameof(IsWorkspaceScope));
            OnPropertyChanged(nameof(ScopeDescription));
        });
    }

    private void OnSessionChanged(object? sender, SettingsChangeSet change)
    {
        if (sender is not ISettingsSession session || !ReferenceEquals(_session, session)
            || session.Current is not { } current) return;
        var sessionContext = current.Context;
        void Update()
        {
            if (!Equals(CurrentContext(), sessionContext)) return;
            _snapshot = current;
            ApplySnapshot();
            SaveStatus = current.Diagnostics.Count == 0 ? "已保存" : current.Diagnostics[0].Message;
        }
        if (_dispatcher.CheckAccess()) Update(); else _dispatcher.BeginInvoke(Update);
    }

    private async Task ReloadShortcutsAsync(CancellationToken cancellationToken = default)
    {
        await _keybindings.ReloadAsync(cancellationToken);
        // ShortcutItems 及其 CollectionView 只能在调度程序线程上变更(同 ReloadAsync 尾段)。
        RunOnDispatcher(PopulateShortcutItems);
    }

    /// <summary>绑定集合(Items/ShortcutItems/Conflicts 及其 CollectionView)只能从调度程序线程
    /// 变更,否则 WPF 抛出"不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改"。
    /// OnSessionChanged/OnBindingsChanged 事件回调已按此约定;激活链(InitializeAsync)可能运行
    /// 在没有同步上下文的后台线程,await 续体落在线程池,必须显式回到调度程序线程。</summary>
    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private void PopulateShortcutItems()
    {
        ShortcutItems.Clear();
        foreach (var command in _commands.Commands)
        {
            var effective = _keybindings.Bindings.LastOrDefault(binding =>
                string.Equals(binding.Command, command.Id, StringComparison.Ordinal));
            var conflict = effective is not null && _keybindings.Conflicts.Any(item => ReferenceEquals(item.Binding, effective) ||
                ReferenceEquals(item.Other, effective));
            var diagnostic = _keybindings.Diagnostics.FirstOrDefault(item =>
                string.Equals(item.Binding.EffectiveCommand, command.Id, StringComparison.Ordinal));
            ShortcutItems.Add(new(command.Id, command.Title, command.Category,
                effective?.Key ?? string.Empty, effective?.When ?? string.Empty, effective?.IsDefault == true, conflict,
                diagnostic?.Message ?? string.Empty));
        }
    }

    private void OnBindingsChanged(object? sender, EventArgs e)
    {
        void Update()
        {
            if (_keybindings.Diagnostics.FirstOrDefault() is { } diagnostic) SaveStatus = diagnostic.Message;
            PopulateShortcutItems();
        }
        if (_dispatcher.CheckAccess()) Update();
        else _dispatcher.BeginInvoke(Update);
    }

    private void ApplySnapshot()
    {
        if (_snapshot is null) return;
        foreach (var item in Items) item.Apply(_snapshot.Values[item.Id], SelectedScope,
            SelectedLanguage != "所有语言");
        View.Refresh();
    }

    private SettingsContext CurrentContext() => new(_workspace.Current?.ProjectPath,
        SelectedLanguage == "所有语言" ? null : SelectedLanguage);

    private bool Filter(object value)
    {
        if (value is not SettingEditorItem item) return false;
        // The filter may run while the View is hooked up in the constructor, before selectedCategory is set.
        var category = SelectedCategory?.Name;
        if (category is not (null or "全部") && !string.Equals(item.Category, category, StringComparison.Ordinal))
            return false;
        var query = SearchText.Trim();
        if (query.Length == 0) return true;
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("@modified", StringComparison.OrdinalIgnoreCase) && !item.IsModified) return false;
            if (token.Equals("@workspace", StringComparison.OrdinalIgnoreCase) && item.Source is not
                (SettingValueSource.Workspace or SettingValueSource.WorkspaceLanguage)) return false;
            if (token.Equals("@user", StringComparison.OrdinalIgnoreCase) && item.Source is not
                (SettingValueSource.User or SettingValueSource.UserLanguage)) return false;
            if (token.StartsWith("@id:", StringComparison.OrdinalIgnoreCase) &&
                !item.Id.Contains(token[4..], StringComparison.OrdinalIgnoreCase)) return false;
            if (token.StartsWith("@lang:", StringComparison.OrdinalIgnoreCase) &&
                (!item.Definition.AllowedScopes.HasFlag(SettingScope.Language) ||
                 !string.Equals(SelectedLanguage, token[6..], StringComparison.OrdinalIgnoreCase))) return false;
            if (token.StartsWith("@category:", StringComparison.OrdinalIgnoreCase) &&
                !item.Category.Contains(token[10..], StringComparison.CurrentCultureIgnoreCase)) return false;
            if (token.Equals("@error", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(item.ErrorText)) return false;
            if (token.StartsWith("@", StringComparison.Ordinal)) continue;
            if (!item.SearchIndex.Contains(token.ToUpperInvariant(), StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private bool FilterShortcut(object value)
    {
        if (value is not KeyboardShortcutItem item) return false;
        var query = SearchText.Trim();
        return query.Length == 0 || $"{item.Title} {item.CommandId} {item.Key} {item.When} {item.Category}"
            .Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    partial void OnSearchTextChanged(string value) { View.Refresh(); ShortcutView.Refresh(); }
    partial void OnSelectedCategoryChanged(SettingsCategoryItem value) => View.Refresh();
    partial void OnSelectedScopeChanged(SettingScope value)
    {
        OnPropertyChanged(nameof(IsWorkspaceScope));
        OnPropertyChanged(nameof(ScopeDescription));
        ApplySnapshot();
        View.Refresh();
    }
    partial void OnSelectedLanguageChanged(string value) => _ = SwitchLanguageAsync(value);
    private async Task SwitchLanguageAsync(string value)
    {
        await FlushAsync();
        _editingLanguage = value;
        await ReloadAsync();
    }
    private async Task OnWorkspaceChangedAsync(ProjectWorkspaceContext? context) { await FlushAsync(); await ReloadAsync(); }

    /// <summary>Synchronous compatibility path; the container uses <see cref="DisposeAsync"/> during
    /// shutdown so the settings session is released without blocking a thread-pool worker.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _workspace.ContextChanged -= OnWorkspaceChangedAsync;
        _keybindings.BindingsChanged -= OnBindingsChanged;
        _saveTimer.Stop();
        if (_session is not null)
        {
            _session.Changed -= OnSessionChanged;
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string CategoryGlyph(string category) => category switch
    {
        "常用" => Codicons.Settings,
        "工作台" => Codicons.EmptyWindow,
        "外观" => Codicons.Gear,
        "编辑器" => Codicons.Code,
        "Diff" => Codicons.Diff,
        "终端" => Codicons.Terminal,
        "布局" => Codicons.LayoutPanel,
        _ => Codicons.Folder,
    };
}

public sealed record SettingsCategoryItem(string Name, string Glyph);

/// <summary>设置页的统一显示格式化(仅展示层):布尔 → 启用/禁用;off/on → 关闭/开启;
/// 主题/换行/行号/强调色等枚举 → 中文含义;空数组/空对象 → "未配置";非空集合 → 数量摘要。
/// 编辑控件仍保存并按原样提交原始值,显示文本与实际提交值分离。</summary>
internal static class SettingDisplayFormatter
{
    public static string Format(object? value) => value switch
    {
        null => "未配置",
        bool boolean => boolean ? "启用" : "禁用",
        string text => FormatEnumToken(text),
        int number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),
        System.Collections.IDictionary dictionary => dictionary.Count == 0 ? "未配置" : $"{dictionary.Count} 项",
        System.Collections.ICollection collection => collection.Count == 0 ? "未配置" : $"{collection.Count} 项",
        _ => JsonSerializer.Serialize(value),
    };

    /// <summary>枚举令牌的展示文本(值本身保持不变)。未映射的令牌原样显示。</summary>
    private static string FormatEnumToken(string text) => text switch
    {
        "off" => "关闭",
        "on" => "开启",
        "wordWrapColumn" => "按列宽换行",
        "bounded" => "视口受限换行",
        "relative" => "相对当前行",
        "interval" => "间隔显示",
        "Dark" => "深色",
        "Light" => "浅色",
        "HighContrast" => "高对比度",
        "Default" => "主题默认色",
        "Teal" => "青绿色",
        "Iris" => "鸢尾紫",
        _ => text,
    };
}

public partial class KeyboardShortcutItem(string commandId, string title, string category, string key,
    string when, bool isDefault, bool hasConflict, string diagnostic = "") : ObservableObject
{
    public string CommandId { get; } = commandId;
    public string Title { get; } = title;
    public string Category { get; } = category;
    public bool IsDefault { get; } = isDefault;
    public bool HasConflict { get; } = hasConflict;
    public string Diagnostic { get; } = diagnostic;
    [ObservableProperty] private string key = key;
    [ObservableProperty] private string when = when;
}
