using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Desktop.Services;
using Nornia.Desktop.Configuration;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;

namespace Nornia.Desktop.ViewModels;

public partial class TerminalViewModel : PageViewModel
{
    private readonly ITerminalService _terminalService;
    private readonly ISettingsService _scopedSettings;
    private readonly IProjectWorkspaceService _workspace;
    private ISettingsSession? _settingsSession;
    private readonly IClipboardService _clipboard;
    private readonly SemaphoreSlim _defaultProfileSaveGate = new(1, 1);
    private bool _profilesLoaded;
    private readonly RevisionGate _settingsRevisionGate = new();
    public ObservableCollection<ShellProfile> Profiles { get; } = [];
    public ObservableCollection<TerminalTab> Sessions { get; } = [];

    [ObservableProperty] private ShellProfile? selectedProfile;
    [ObservableProperty] private TerminalTab? selectedSession;
    [ObservableProperty] private string workingDirectory = Environment.CurrentDirectory;
    [ObservableProperty] private double fontSize = 13;
    /// <summary>终端字体族(terminal.integrated.fontFamily;视图传给 TerminalSurfaceControl)。</summary>
    [ObservableProperty] private string fontFamily = FontCatalog.DefaultTerminalFamily;
    [ObservableProperty] private bool showSessionSidebar = true;
    [ObservableProperty] private double sessionSidebarWidth = 190;

    public TerminalViewModel(ITerminalService terminalService, ISettingsService settings,
        IProjectWorkspaceService workspace, IUiLogService logService, IClipboardService clipboard) : base("终端", logService)
    {
        _terminalService = terminalService;
        _scopedSettings = settings;
        _workspace = workspace;
        _clipboard = clipboard;
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSessions));
        _workspace.ContextChanged += OnWorkspaceChangedAsync;
    }

    /// <summary>会话侧栏空状态:全部会话关闭后终端区显示“新建终端”引导。</summary>
    public bool HasSessions => Sessions.Count > 0;

    /// <summary>请求把键盘焦点交给终端窗口(新建会话 / 工作台“新建终端”命令后触发,
    /// 由视图在面板可见时聚焦 <c>TerminalSurfaceControl</c>)。</summary>
    public event EventHandler? FocusRequested;

    protected override Task OnFirstActivatedAsync() => LoadProfilesAsync(useSavedDefault: true);

    /// <summary>设置页保存自定义 Shell / 默认配置文件后,同步刷新终端的 Shell 下拉。</summary>
    public Task RefreshProfilesAsync() => LoadProfilesAsync(useSavedDefault: false);

    private async Task LoadProfilesAsync(bool useSavedDefault)
    {
        _settingsSession ??= await _scopedSettings.OpenSessionAsync(new(_workspace.Current?.ProjectPath),
            [
                BuiltInSettingsCatalog.TerminalFontSize.Id, BuiltInSettingsCatalog.TerminalFontFamily.Id,
                BuiltInSettingsCatalog.TerminalSidebarVisible.Id,
                BuiltInSettingsCatalog.TerminalSidebarWidth.Id, BuiltInSettingsCatalog.TerminalDefaultProfile.Id,
                BuiltInSettingsCatalog.TerminalCustomShells.Id,
            ]);
            _settingsSession.Changed -= OnScopedSettingsChanged;
            _settingsSession.Changed += OnScopedSettingsChanged;
            var snapshot = _settingsSession.Current!;
            ApplyTerminalSettings(snapshot);
            var previous = SelectedProfile?.Id;
            _profilesLoaded = false;
            Profiles.Clear();
            foreach (var profile in _terminalService.DiscoverProfiles()) Profiles.Add(profile);
            foreach (var path in snapshot.Effective(BuiltInSettingsCatalog.TerminalCustomShells))
                if (File.Exists(path) && Profiles.All(profile => !string.Equals(profile.Executable, path, StringComparison.OrdinalIgnoreCase)))
                    Profiles.Add(new($"custom-{path.GetHashCode(StringComparison.OrdinalIgnoreCase):X8}", Path.GetFileNameWithoutExtension(path), path));
            _profilesLoaded = true;
            var preferred = useSavedDefault ? snapshot.Effective(BuiltInSettingsCatalog.TerminalDefaultProfile) : previous;
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == preferred)
                ?? Profiles.FirstOrDefault(profile => profile.Id == "pwsh") ?? Profiles.FirstOrDefault();
            if (_workspace.Current?.ProjectPath is { } root) WorkingDirectory = root;
    }

    private void OnScopedSettingsChanged(object? sender, SettingsChangeSet change)
    {
        if (_settingsSession?.Current is not { } snapshot) return;
        ApplyTerminalSettings(snapshot);
        if (change.Changes.Any(item => item.Key is "nornia.terminal.defaultProfile" or "nornia.terminal.customShells"))
            _ = LoadProfilesAsync(useSavedDefault: true);
    }

    private void ApplyTerminalSettings(SettingsSnapshot snapshot)
    {
        if (!_settingsRevisionGate.TryAccept(snapshot)) return;
        void Apply()
        {
            FontSize = snapshot.Effective(BuiltInSettingsCatalog.TerminalFontSize);
            var family = snapshot.Effective(BuiltInSettingsCatalog.TerminalFontFamily);
            FontFamily = string.IsNullOrWhiteSpace(family) ? FontCatalog.DefaultTerminalFamily : family.Trim();
            ShowSessionSidebar = snapshot.Effective(BuiltInSettingsCatalog.TerminalSidebarVisible);
            SessionSidebarWidth = snapshot.Effective(BuiltInSettingsCatalog.TerminalSidebarWidth);
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply(); else _ = dispatcher.BeginInvoke(Apply);
    }

    private async Task OnWorkspaceChangedAsync(ProjectWorkspaceContext? context)
    {
        if (_settingsSession is not null) await _settingsSession.DisposeAsync();
        _settingsSession = null;
        await LoadProfilesAsync(useSavedDefault: true);
    }

    /// <summary>Shell 下拉切换即更新默认配置文件(持久化);已有会话不受影响。</summary>
    partial void OnSelectedProfileChanged(ShellProfile? value)
    {
        if (!_profilesLoaded || value is null)
        {
            LogService.Write("Debug", $"OnSelectedProfileChanged skipped: profilesLoaded={_profilesLoaded}, value=null={value is null}");
            return;
        }

        LogService.Write("Debug", $"OnSelectedProfileChanged: persisting profile {value.Id}");
        _ = PersistDefaultShellAsync(value.Id);
    }

    private async Task PersistDefaultShellAsync(string profileId)
    {
        if (_settingsSession is null) return;
        await _defaultProfileSaveGate.WaitAsync();
        try
        {
            var result = await _settingsSession.CommitAsync(SettingScope.User,
                [new(BuiltInSettingsCatalog.TerminalDefaultProfile.Id, System.Text.Json.Nodes.JsonValue.Create(profileId))]);
            if (!result.IsSuccess)
                LogService.Write("Warning", $"Failed to persist default shell: {result.Status} - {result.ErrorMessage}");
        }
        finally
        {
            _defaultProfileSaveGate.Release();
        }
    }

    /// <summary>新建终端:使用当前选定 Shell 与工作区目录启动会话(输入直接在终端内完成)。</summary>
    [RelayCommand]
    private async Task NewTerminalAsync()
    {
        if (SelectedProfile is null) return;
        await RunAsync($"启动 {SelectedProfile.Name}", async token =>
        {
            var session = await _terminalService.StartAsync(SelectedProfile, WorkingDirectory, token);
            var tab = new TerminalTab(session);
            // 渲染由 TerminalSurfaceControl 订阅 Screen.Changed 驱动;侧栏只反映会话状态,
            // 因此无需在每次输出块时刷新(旧的 per-chunk Refresh 会全量重建 ToPlainText 造成卡顿)。
            session.Exited += (_, _) => tab.Refresh();
            Sessions.Add(tab);
            SelectedSession = tab;
        }, canCancel: false);
    }

    /// <summary>切换会话(侧栏点选 / 关闭后自动选相邻)也请求聚焦终端表面:焦点不再留在
    /// 侧栏列表上,切换后无需点击即可直接打字。</summary>
    partial void OnSelectedSessionChanged(TerminalTab? value)
    {
        if (value is not null)
        {
            FocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CloseAsync(TerminalTab? tab)
    {
        if (tab is null) return;
        // 关闭后自动选择相邻会话:原位置还在则取原位,否则取左侧最后一个。
        var index = Sessions.IndexOf(tab);
        await _terminalService.StopAsync(tab.Session);
        Sessions.Remove(tab);
        SelectedSession = Sessions.Count == 0 ? null : Sessions[Math.Min(index, Sessions.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear(TerminalTab? tab) => tab?.Session.Clear();

    /// <summary>Retries a terminal that could not establish its ConPTY transport. The failed tab
    /// is replaced in place so selection and the side-bar ordering remain stable.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync(TerminalTab? tab)
    {
        if (tab is null || !tab.IsFailed)
        {
            return;
        }

        await RunAsync($"重试启动 {tab.Title}", async token =>
        {
            var replacement = new TerminalTab(await _terminalService.StartAsync(tab.Session.Profile, tab.Session.WorkingDirectory, token));
            replacement.Session.Exited += (_, _) => replacement.Refresh();
            var index = Sessions.IndexOf(tab);
            if (index >= 0)
            {
                Sessions[index] = replacement;
                SelectedSession = replacement;
            }

            await _terminalService.StopAsync(tab.Session);
        }, canCancel: false);
    }

    /// <summary>复制输出: copies the terminal tab's captured output to the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyTabOutput))]
    private void CopyTabOutput(TerminalTab? tab)
    {
        if (tab is null)
        {
            return;
        }

        _clipboard.SetText(tab.Session.Output);
    }

    private bool CanCopyTabOutput(TerminalTab? tab) => tab is not null;

    /// <summary>Ctrl+PgUp/PgDn: cycles the terminal sessions (VS Code-style).</summary>
    [RelayCommand]
    private void GoToAdjacentSession(int offset)
    {
        if (Sessions.Count == 0)
        {
            return;
        }

        var index = Sessions.IndexOf(SelectedSession ?? Sessions[^1]);
        SelectedSession = Sessions[(index + offset + Sessions.Count) % Sessions.Count];
    }

    private bool CanClose(TerminalTab? tab) => tab is not null;
    private bool CanClear(TerminalTab? tab) => tab is not null;
    private bool CanRetry(TerminalTab? tab) => tab?.IsFailed == true;
}

/// <summary>会话侧栏一行:稳定标题 + 可绑定的显示状态(会话退出/失败后不再变化丢失)。</summary>
public sealed partial class TerminalTab : ObservableObject
{
    public TerminalTab(TerminalSession session)
    {
        Session = session;
        Title = session.Profile.Name;
        Refresh();
    }

    public TerminalSession Session { get; }

    /// <summary>稳定标题:创建时取自 Shell 名称,运行期间保持不变。</summary>
    public string Title { get; }

    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string statusGlyph = string.Empty;

    public bool IsFailed => Session.State == TerminalSessionState.Failed;
    public string FailureReason => Session.FailureReason ?? string.Empty;

    /// <summary>仅按会话状态刷新侧栏(输出渲染走终端表面,不在此全量重建文本)。</summary>
    public void Refresh()
    {
        (StatusGlyph, StatusText) = Session.State switch
        {
            TerminalSessionState.Starting => (Codicons.Loading, "启动中"),
            TerminalSessionState.Running => Session.StartupWarning is null ? (Codicons.Terminal, "运行中") : (Codicons.Terminal, "运行中·回退"),
            TerminalSessionState.Exited => (Codicons.Remove, "已退出"),
            TerminalSessionState.Failed => (Codicons.Error, "启动失败"),
            _ => (string.Empty, string.Empty),
        };
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(FailureReason));
    }
}
