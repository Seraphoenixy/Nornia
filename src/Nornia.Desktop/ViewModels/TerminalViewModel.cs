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
    private readonly SemaphoreSlim _workspaceContextGate = new(1, 1);
    private ProjectWorkspaceContext? _workspaceContext;
    private long _workspaceGeneration;
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
        _workspaceContext = workspace.Current;
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
        var generation = Volatile.Read(ref _workspaceGeneration);
        var context = _workspaceContext;
        var session = _settingsSession;
        if (session is null)
        {
            session = await _scopedSettings.OpenSessionAsync(new(context?.ProjectPath),
            [
                BuiltInSettingsCatalog.TerminalFontSize.Id, BuiltInSettingsCatalog.TerminalFontFamily.Id,
                BuiltInSettingsCatalog.TerminalSidebarVisible.Id,
                BuiltInSettingsCatalog.TerminalSidebarWidth.Id, BuiltInSettingsCatalog.TerminalDefaultProfile.Id,
                BuiltInSettingsCatalog.TerminalCustomShells.Id,
            ]);
            if (generation != Volatile.Read(ref _workspaceGeneration)
                || !ReferenceEquals(_workspaceContext, context))
            {
                await session.DisposeAsync();
                return;
            }

            _settingsSession = session;
            session.Changed += OnScopedSettingsChanged;
        }

        if (generation != Volatile.Read(ref _workspaceGeneration)
            || !ReferenceEquals(_workspaceContext, context)
            || !ReferenceEquals(_settingsSession, session))
        {
            return;
        }

        if (session.Current is not { } snapshot)
        {
            return;
        }
        ApplyTerminalSettings(snapshot);
        var previous = SelectedProfile?.Id;
        _profilesLoaded = false;
        Profiles.Clear();
        foreach (var profile in _terminalService.DiscoverProfiles()) Profiles.Add(profile);
        foreach (var path in snapshot.Effective(BuiltInSettingsCatalog.TerminalCustomShells))
            if (File.Exists(path) && Profiles.All(profile => !string.Equals(profile.Executable, path, StringComparison.OrdinalIgnoreCase)))
                Profiles.Add(new($"custom-{path.GetHashCode(StringComparison.OrdinalIgnoreCase):X8}", Path.GetFileNameWithoutExtension(path), path));

        if (generation != Volatile.Read(ref _workspaceGeneration)
            || !ReferenceEquals(_workspaceContext, context)
            || !ReferenceEquals(_settingsSession, session))
        {
            return;
        }

        _profilesLoaded = true;
        var preferred = useSavedDefault ? snapshot.Effective(BuiltInSettingsCatalog.TerminalDefaultProfile) : previous;
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == preferred)
            ?? Profiles.FirstOrDefault(profile => profile.Id == "pwsh") ?? Profiles.FirstOrDefault();
        if (context?.ProjectPath is { } root) WorkingDirectory = root;
    }

    private void OnScopedSettingsChanged(object? sender, SettingsChangeSet change)
    {
        if (sender is not ISettingsSession session || !ReferenceEquals(_settingsSession, session)
            || session.Current is not { } snapshot) return;
        ApplyTerminalSettings(snapshot);
        if (change.Changes.Any(item => item.Key is "nornia.terminal.defaultProfile" or "nornia.terminal.customShells"))
            _ = LoadProfilesSafelyAsync(useSavedDefault: true);
    }

    private async Task LoadProfilesSafelyAsync(bool useSavedDefault)
    {
        try
        {
            await LoadProfilesAsync(useSavedDefault);
        }
        catch (Exception ex)
        {
            LogService.Write("WARNING", $"刷新终端配置文件失败：{ex.Message}");
        }
    }

    private void ApplyTerminalSettings(SettingsSnapshot snapshot)
    {
        if (!IsSettingsContextCurrent(snapshot.Context)) return;
        if (!_settingsRevisionGate.TryAccept(snapshot)) return;
        void Apply()
        {
            if (!IsSettingsContextCurrent(snapshot.Context)) return;
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
        Interlocked.Increment(ref _workspaceGeneration);
        var oldSettings = Interlocked.Exchange(ref _settingsSession, null);
        if (oldSettings is not null) await oldSettings.DisposeAsync();

        await _workspaceContextGate.WaitAsync();
        try
        {
            _workspaceContext = context;
            var oldSessions = Sessions.ToArray();
            Sessions.Clear();
            SelectedSession = null;
            foreach (var tab in oldSessions)
            {
                try
                {
                    await _terminalService.StopAsync(tab.Session);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    LogService.Write("WARNING", $"关闭旧终端会话失败：{ex.Message}");
                }
            }

            WorkingDirectory = context?.ProjectPath ?? Environment.CurrentDirectory;
            await LoadProfilesAsync(useSavedDefault: true);
        }
        finally
        {
            _workspaceContextGate.Release();
        }
    }

    private bool IsSettingsContextCurrent(SettingsContext context)
    {
        var workspace = _workspaceContext?.ProjectPath;
        return string.Equals(context.NormalizedWorkspacePath,
            workspace is null ? null : Path.GetFullPath(workspace)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
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
        _ = PersistDefaultShellSafelyAsync(value.Id, _settingsSession);
    }

    private async Task PersistDefaultShellSafelyAsync(string profileId, ISettingsSession? session)
    {
        try
        {
            await PersistDefaultShellAsync(profileId, session);
        }
        catch (Exception ex)
        {
            LogService.Write("WARNING", $"无法保存终端默认配置文件：{ex.Message}");
        }
    }

    private async Task PersistDefaultShellAsync(string profileId, ISettingsSession? session)
    {
        if (session is null) return;
        await _defaultProfileSaveGate.WaitAsync();
        try
        {
            // 默认 Shell 是用户显式操作的结果,必须落盘。单次提交在磁盘压力下会瞬时失败:
            // 原子替换重试耗尽(杀毒实时扫描/高并发 IO)→ FileError;会话基线过期 → Conflict。
            // 此前单次失败即放弃,表现为"默认 Shell 静默丢失"(并行回归下的偶发超时同源)。
            // FileError/Conflict 带退避重试(Conflict 先刷新会话基线再提交);ValidationFailed
            // 属永久错误,不重试。
            for (var attempt = 1; ; attempt++)
            {
                var result = await session.CommitAsync(SettingScope.User,
                    [new(BuiltInSettingsCatalog.TerminalDefaultProfile.Id, System.Text.Json.Nodes.JsonValue.Create(profileId))]);
                if (result.IsSuccess) return;
                if (result.Status == SettingsCommitStatus.ValidationFailed || attempt >= 4)
                {
                    LogService.Write("Warning", $"Failed to persist default shell: {result.Status} - {result.ErrorMessage}");
                    return;
                }

                if (result.Status == SettingsCommitStatus.Conflict) await session.RefreshAsync();
                await Task.Delay(150 * attempt);
            }
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
        await _workspaceContextGate.WaitAsync();
        try
        {
            var profile = SelectedProfile;
            var context = _workspaceContext;
            var directory = WorkingDirectory;
            if (profile is null) return;

            await RunAsync($"启动 {profile.Name}", async token =>
            {
                var session = await _terminalService.StartAsync(profile, directory, token);
                if (!ReferenceEquals(_workspaceContext, context))
                {
                    await _terminalService.StopAsync(session);
                    return;
                }

                var tab = new TerminalTab(session);
                // 渲染由 TerminalSurfaceControl 订阅 Screen.Changed 驱动;侧栏只反映会话状态,
                // 因此无需在每次输出块时刷新(旧的 per-chunk Refresh 会全量重建 ToPlainText 造成卡顿)。
                session.Exited += (_, _) => tab.Refresh();
                Sessions.Add(tab);
                SelectedSession = tab;
            }, canCancel: false);
        }
        finally
        {
            _workspaceContextGate.Release();
        }
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
        await _workspaceContextGate.WaitAsync();
        try
        {
            if (!Sessions.Contains(tab)) return;
            // 关闭后自动选择相邻会话:原位置还在则取原位,否则取左侧最后一个。
            var index = Sessions.IndexOf(tab);
            await _terminalService.StopAsync(tab.Session);
            Sessions.Remove(tab);
            SelectedSession = Sessions.Count == 0 ? null : Sessions[Math.Min(index, Sessions.Count - 1)];
        }
        finally
        {
            _workspaceContextGate.Release();
        }
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

        await _workspaceContextGate.WaitAsync();
        try
        {
            if (!Sessions.Contains(tab)) return;
            var context = _workspaceContext;
            await RunAsync($"重试启动 {tab.Title}", async token =>
            {
                var replacementSession = await _terminalService.StartAsync(tab.Session.Profile, tab.Session.WorkingDirectory, token);
                if (!ReferenceEquals(_workspaceContext, context))
                {
                    await _terminalService.StopAsync(replacementSession);
                    return;
                }

                var replacement = new TerminalTab(replacementSession);
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
        finally
        {
            _workspaceContextGate.Release();
        }
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
