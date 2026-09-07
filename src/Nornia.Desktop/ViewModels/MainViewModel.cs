using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Desktop.Code;
using Nornia.Desktop.Commands;
using Nornia.Desktop.Localization;
using Nornia.Desktop.Services;
using Nornia.Core.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace Nornia.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private ICommandRegistry? _commandRegistry;
    private IKeybindingService? _keybindings;
    public CommandAccessor Commands { get; } = new();
    private readonly IUiLogService _logService;
    private readonly IClipboardService _clipboard;
    private readonly BulkObservableCollection<UiLogEntry> _problemEntries = [];
    private readonly IDesktopNavigationService _navigationService;
    private readonly EnvironmentManagementViewModel? _environment;
    private readonly ExplorerPageViewModel? _explorer;
    private readonly SearchViewModel? _search;
    private readonly GitViewModel? _git;
    private readonly ProjectsViewModel? _projects;
    private readonly SettingsViewModel? _settings;
    private readonly object _quickOpenCacheGate = new();
    private string? _quickOpenCacheRoot;
    private IReadOnlyList<string>? _quickOpenFilesCache;
    private string? _quickOpenBuildRoot;
    private Task<IReadOnlyList<string>>? _quickOpenBuildTask;
    private long _quickOpenCacheGeneration;

    /// <summary>Guard for the 标签→活动栏 direction only: while a page-tab selection mirrors back
    /// into the activity bar, <see cref="OnSelectedNavigationItemChanged"/> must not re-open the
    /// page tab (loop).</summary>
    private bool _syncingActivitySelection;
    private readonly CancellationTokenSource _preloadCancellation = new();
    private bool _preloadEnabled;
    private bool _preloading;

    // ===== 顶栏返回/前进:新增的内存导航历史(不持久化) =====
    private readonly List<NavLocation> _backStack = [];
    private readonly List<NavLocation> _forwardStack = [];
    private NavLocation? _historyAnchor;
    private bool _suppressHistoryRecording;
    private const int HistoryCapacity = 100;

    /// <summary>VS Code-style QuickInput overlay state (command palette / quick open).</summary>
    public QuickInputViewModel QuickInput { get; } = new();

    public MainViewModel(
        EnvironmentManagementViewModel environment,
        ExplorerPageViewModel explorer,
        GitViewModel git,
        ProjectsViewModel projects,
        SettingsViewModel settings,
        TerminalViewModel terminal,
        IUiLogService logService,
        IDesktopNavigationService navigationService,
        IClipboardService clipboard,
        SearchViewModel? search = null)
    {
        _logService = logService;
        _clipboard = clipboard;
        _navigationService = navigationService;
        _environment = environment;
        _explorer = explorer;
        _search = search;
        _git = git;
        _projects = projects;
        _settings = settings;
        var navigationItems = new ObservableCollection<NavigationItem>(
        [
            new NavigationItem("Nav_Explorer", Codicons.Files, explorer, NavigationTargets.Explorer),
            new NavigationItem("Nav_Git", Codicons.SourceControl, git, NavigationTargets.Git),
            new NavigationItem("Nav_Projects", Codicons.FolderLibrary, projects, NavigationTargets.Projects),
            new NavigationItem("Nav_Environment", Codicons.Dashboard, environment,
                NavigationTargets.Dashboard, NavigationTargets.Runtime, NavigationTargets.Tools,
                NavigationTargets.Packages, NavigationTargets.Cache),
            new NavigationItem("Nav_Settings", Codicons.Settings, settings, NavigationTargets.Settings)
        ]);
        if (search is not null)
        {
            navigationItems.Insert(1, new NavigationItem("Nav_Search", Codicons.Search, search, NavigationTargets.Search));
        }
        Initialize(navigationItems);
        Terminal = terminal;
        // The terminal is a shell-level bottom panel now (no workbench page owns it); discover its
        // profiles once at startup.
        _ = ActivatePageSafelyAsync(terminal);
        // 统一标签条:内容页标签与文件/diff 文档标签同条混排(视图页永不产生标签)。
        Workbench = new WorkbenchViewModel(explorer.Editor, navigationItems);
        Workbench.ShowOperationLogCommand = new RelayCommand(() => SelectPanel(WorkbenchPanel.Output));
        // 面包屑符号段点击 → QuickInput 符号选择器(Phase 6 将统一为 '@' 前缀快速打开)。
        Workbench.Editor.SymbolPickerRequested += (_, tab) => OpenSymbolPicker(tab);
        // 状态栏语言按钮 → QuickInput 语言选择器(VS Code"更改语言模式")。
        Workbench.Editor.LanguagePickerRequested += (_, tab) => OpenLanguagePicker(tab);
        // Tab context-menu copy commands (标题/路径) ride the shared clipboard.
        Workbench.CopyToClipboard = text => _clipboard.SetText(text);
        // 恢复布局(设置页):合并编辑器组为单组(不删除标签与阅读状态),比例/面板尺寸由状态存储复位。
        if (_settings is { Editor: { } settingsEditor })
        {
            settingsEditor.ResetEditorLayout += () => Workbench?.Editor?.Groups.ResetLayout();
        }
        // 标签→活动栏联动 + 欢迎层/状态栏反馈源跟随选中标签。
        Workbench.PropertyChanged += OnWorkbenchPropertyChanged;
        // 环境页内部切小节 → 状态栏反馈源跟随当前小节。
        if (_environment is not null)
        {
            _environment.PropertyChanged += OnEnvironmentPropertyChanged;
        }

        // 顶栏命令中心标题:随工作区(项目)切换实时刷新。
        _explorer.Explorer.PropertyChanged += OnWorkspaceSourceChanged;
        _explorer.Explorer.WorkspaceFilesChanged += OnWorkspaceFilesChanged;

        // 启动默认定位资源管理器，不打开内容页标签；环境管理等内容页仅在导航时打开。

        // 预加载:先让窗口壳和默认页完成首屏，再在空闲时分散激活其它页面，避免启动阶段
        // 与首屏渲染争抢 CPU、磁盘和外部进程。用户开始导航后会暂停低优先级预热。
        _preloadEnabled = true;
        _ = PreloadPagesAsync();
    }

    /// <summary>Best-effort, idle-scheduled page warmup. The default page is the only critical
    /// page; remaining pages are isolated so a slow/failing page cannot delay first interaction.</summary>
    private async Task PreloadPagesAsync()
    {
        _preloading = true;
        try
        {
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var pages = NavigationItems.Select(item => item.Page).Distinct().ToArray();
            if (pages.Length == 0) return;

            // 预热只初始化数据，不打开或选中页面标签。
            await pages[0].ActivateAsync();
            foreach (var page in pages.Skip(1))
            {
                _preloadCancellation.Token.ThrowIfCancellationRequested();
                // Yield between pages so layout/input/rendering gets a chance to run. This is
                // intentionally a UI-idle continuation rather than a tight sequential loop.
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await page.ActivateAsync();
            }
        }
        catch (OperationCanceledException) when (_preloadCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // 预加载失败不影响启动;手动打开该页时仍可继续使用。
        }
        finally
        {
            _preloading = false;
        }
    }

    // Retained for the older view-model test harness and downstream add-ins.  The application
    // composition root uses the focused workbench constructor above.
    public MainViewModel(
        DashboardViewModel dashboard, RuntimeViewModel runtime, ToolsViewModel tools,
        PackagesViewModel packages, ProjectsViewModel projects, SettingsViewModel settings,
        IUiLogService logService, IDesktopNavigationService navigationService, IClipboardService clipboard)
    {
        _logService = logService;
        _clipboard = clipboard;
        _navigationService = navigationService;
        _settings = settings;
        Initialize([
            new NavigationItem("Legacy_Dashboard", Codicons.Dashboard, dashboard, NavigationTargets.Dashboard),
            new NavigationItem("Legacy_Runtime", Codicons.ServerProcess, runtime, NavigationTargets.Runtime),
            new NavigationItem("Legacy_Tools", Codicons.Tools, tools, NavigationTargets.Tools),
            new NavigationItem("Legacy_Packages", Codicons.Package, packages, NavigationTargets.Packages),
            new NavigationItem("Legacy_Projects", Codicons.FolderLibrary, projects, NavigationTargets.Projects),
            new NavigationItem("Nav_Settings", Codicons.Settings, settings, NavigationTargets.Settings)]);
    }

    private void Initialize(ObservableCollection<NavigationItem> items)
    {
        NavigationItems = items;
        OutputEntries = _logService.Entries;
        ProblemEntries = new ReadOnlyObservableCollection<UiLogEntry>(_problemEntries);
        SelectedProblemEntries.CollectionChanged += (_, _) => RefreshCopyCommands();
        SelectedOutputEntries.CollectionChanged += (_, _) => RefreshCopyCommands();
        OutputView = new ListCollectionView(OutputEntries);
        ProblemView = new ListCollectionView(ProblemEntries);
        OutputView.Filter = FilterPanelEntry;
        ProblemView.Filter = FilterPanelEntry;
        ((INotifyCollectionChanged)OutputEntries).CollectionChanged += OnOutputEntriesChanged;
        RefreshProblems();
        RefreshCopyCommands();
        RefreshActivityBadges();
        if (_explorer is not null && _git is not null && _projects is not null)
        {
            _git.PropertyChanged += OnGitPropertyChanged;
            _projects.PropertyChanged += OnProjectsPropertyChanged;
        }

        SelectedNavigationItem = NavigationItems[0];
        _navigationService.NavigationRequested += OnNavigationRequested;
        Loc.CultureChanged += (_, _) => RefreshLocalizedDisplayStrings();
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; private set; } = [];
    public TerminalViewModel? Terminal { get; private set; }

    /// <summary>The unified workbench tab strip that owns the main content region.</summary>
    public WorkbenchViewModel Workbench { get; private set; } = null!;
    public ReadOnlyObservableCollection<UiLogEntry> OutputEntries { get; private set; } = null!;
    public ReadOnlyObservableCollection<UiLogEntry> ProblemEntries { get; private set; } = null!;
    public ObservableCollection<UiLogEntry> SelectedProblemEntries { get; } = [];
    public ObservableCollection<UiLogEntry> SelectedOutputEntries { get; } = [];

    public int ProblemCount => _problemEntries.Count;
    public string ProblemsTabTitle => ProblemCount == 0
        ? Loc.GetString("ProblemsTabTitle")
        : $"{Loc.GetString("ProblemsTabTitle")} ({ProblemCount})";

    // Debounce log-search filter refresh so each keystroke does not O(n) re-evaluate
    // both ListCollectionView views (the terminal buffer can hold 200k+ characters).
    private DispatcherTimer? _searchDebounceTimer;
    private WorkbenchPanel _previousPanel = WorkbenchPanel.Output;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(180);

    [ObservableProperty]
    private NavigationItem? selectedNavigationItem;

    [ObservableProperty]
    private PageViewModel? currentPage;

    [ObservableProperty]
    private WorkbenchPanel selectedPanel = WorkbenchPanel.Output;

    [ObservableProperty]
    private bool isPanelExpanded;

    /// <summary>底部面板最大化:布局由 MainWindow 在编辑器/面板行之间切换(保留活动栏、侧栏与状态栏)。</summary>
    [ObservableProperty]
    private bool isPanelMaximized;

    [ObservableProperty]
    private string pendingChordText = string.Empty;

    [ObservableProperty]
    private GridLength panelHeight = new(0);

    /// <summary>Panel bounds are centralized in WorkbenchLayoutMetrics; these constants remain
    /// as compatibility aliases for settings/tests that use the old MainViewModel surface.</summary>
    public const double MinimumPanelHeight = WorkbenchLayoutMetrics.LogPanelMinimumHeight;
    public const double MaximumPanelHeight = WorkbenchLayoutMetrics.PanelAbsoluteMaximumHeight;
    public const double DefaultPanelHeight = 240;
    /// <summary>Compatibility diagnostic for callers that used the old threshold. The actual
    /// responsive decision is calculated from the current group count and sidebar width.</summary>
    public static double CompactLayoutThreshold =>
        WorkbenchLayoutMetrics.RequiredWorkbenchWidth(1, true);

    // Collapsing must never discard the splitter choice. This runtime value is restored from the
    // persisted PanelHeight and is intentionally independent of the 0-height collapsed state.
    private double _lastExpandedPanelHeight = DefaultPanelHeight;

    /// <summary>Last non-zero panel height; used when Ctrl+J or a panel tab reopens the panel.</summary>
    public double LastExpandedPanelHeight => _lastExpandedPanelHeight;

    public bool IsOutputSelected => SelectedPanel == WorkbenchPanel.Output;
    public bool IsProblemsSelected => SelectedPanel == WorkbenchPanel.Problems;
    public bool IsTerminalSelected => SelectedPanel == WorkbenchPanel.Terminal;

    // U7: panel search + level filter over the log/probelists
    [ObservableProperty]
    private string panelSearchText = string.Empty;

    [ObservableProperty]
    private LogPanelLevelFilter panelLevelFilter = LogPanelLevelFilter.All;

    public IReadOnlyList<LogPanelLevelFilter> PanelLevelOptions { get; } =
        [LogPanelLevelFilter.All, LogPanelLevelFilter.Info, LogPanelLevelFilter.Warning, LogPanelLevelFilter.Error];

    public ListCollectionView OutputView { get; private set; } = null!;
    public ListCollectionView ProblemView { get; private set; } = null!;

    public string EnvironmentHealthGlyph => ProblemCount == 0 ? Codicons.Check : Codicons.Warning;
    public string EnvironmentHealthText => ProblemCount == 0
        ? Loc.GetString("Status_Ready")
        : Loc.Format("Status_NProblems", ProblemCount);

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (_preloadEnabled && _preloading)
        {
            _preloadCancellation.Cancel();
        }
        CurrentPage = value?.Page;
        OnPropertyChanged(nameof(SidebarColumnWidth));
        OnPropertyChanged(nameof(IsSidebarColumnVisible));
        OnPropertyChanged(nameof(IsSettingsActive));
        OnPropertyChanged(nameof(ShowViewPageWelcome));
        OnPropertyChanged(nameof(OperationPage));
        // 标签→活动栏同步中不重开页面标签(它已打开,重复打开会循环)。
        if (CurrentPage is { } page && !_syncingActivitySelection)
        {
            _ = ActivatePageSafelyAsync(page);
            if (page is ExplorerPageViewModel or GitViewModel or SearchViewModel)
            {
                // 视图页永不产生页面标签;且视图页下页面标签不得处于选中态。
                Workbench?.ClearNonDocumentSelection();
            }
            else
            {
                Workbench?.OpenOrActivatePage(CurrentPage);
            }
        }
        RecordNavigation();
    }

    private async Task ActivatePageSafelyAsync(PageViewModel page)
    {
        try
        {
            await page.ActivateAsync();
        }
        catch (Exception ex)
        {
            _logService.WriteException("ERROR", $"页面“{page.Title}”初始化失败", ex);
        }
    }

    /// <summary>视图页欢迎层:资源管理器 / 源代码管理且无文档标签选中时显示各自欢迎页,
    /// 与文档选中互斥。</summary>
    public bool ShowViewPageWelcome => CurrentPage is ExplorerPageViewModel or GitViewModel
        && Workbench?.SelectedTab is not EditorWorkbenchTab;

    /// <summary>状态栏操作反馈源:选中的内容页(环境管理跟随其当前小节),
    /// 文档/空选中时为当前活动栏页。</summary>
    public PageViewModel? OperationPage => Workbench?.SelectedTab switch
    {
        PageWorkbenchTab { Content: PageViewModel page } => page is EnvironmentManagementViewModel env ? env.CurrentPage ?? env : page,
        _ => CurrentPage,
    };

    /// <summary>标签→活动栏联动:选中页面标签时高亮其所属活动项(受 _syncingActivitySelection
    /// 保护,避免回环);文档/空选中时活动栏不动。</summary>
    private void OnWorkbenchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkbenchViewModel.SelectedTab))
        {
            return;
        }

        OnPropertyChanged(nameof(ShowViewPageWelcome));
        OnPropertyChanged(nameof(OperationPage));

        if (Workbench.SelectedTab is PageWorkbenchTab { Content: PageViewModel page })
        {
            CurrentPage = page;
            var item = NavigationItems.FirstOrDefault(candidate => ReferenceEquals(candidate.Page, page));
            if (item is not null && !ReferenceEquals(item, SelectedNavigationItem))
            {
                _syncingActivitySelection = true;
                try
                {
                    SelectedNavigationItem = item;
                }
                finally
                {
                    _syncingActivitySelection = false;
                }
            }
        }
        RecordNavigation();
    }

    private void OnEnvironmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 环境页内部切换小节 → 状态栏反馈源跟随当前小节页。
        if (e.PropertyName == nameof(EnvironmentManagementViewModel.CurrentPage))
        {
            OnPropertyChanged(nameof(OperationPage));
            RecordNavigation();
        }
    }

    /// <summary>The editor group owned by the current page (Explorer / Git): drives Ctrl+W and
    /// Ctrl+PgUp/PgDn tab cycling when focus is not in the terminal panel.</summary>
    public EditorAreaViewModel? EditorFromPage => CurrentPage switch
    {
        ExplorerPageViewModel explorer => explorer.Editor,
        GitViewModel git => git.Editor,
        _ => null
    };

    // ===== Shared secondary sidebar (Ctrl+B) =====

    [ObservableProperty]
    private bool isSidebarVisible = true;

    /// <summary>Transient responsive state. It never changes the user's Ctrl+B preference, so a
    /// sidebar hidden only because the window is narrow returns when there is room again.</summary>
    [ObservableProperty]
    private bool isSidebarAutoCollapsed;

    [ObservableProperty]
    private bool isCompactActivityBar;

    private double _sidebarWidth = 300;

    /// <summary>Persisted sidebar width; the choice survives restarts (clamped to usable bounds).</summary>
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set
        {
            var clamped = Math.Clamp(value, WorkbenchLayoutMetrics.SidebarMinimumWidth,
                WorkbenchLayoutMetrics.SidebarMaximumWidth);
            if (Math.Abs(_sidebarWidth - clamped) < 0.5)
            {
                return;
            }

            _sidebarWidth = clamped;
            OnPropertyChanged(nameof(SidebarColumnWidth));
        }
    }

    /// <summary>Secondary left sidebar column width. The sidebar is shared across pages; Ctrl+B
    /// hides it for the whole session, independent of which page provides the sidebar content.</summary>
    public GridLength SidebarColumnWidth => CurrentPage?.Sidebar is null || !IsSidebarVisible || IsSidebarAutoCollapsed
        ? new GridLength(0)
        : new GridLength(SidebarWidth);

    /// <summary>Whether the sidebar column should be rendered (page has a sidebar AND it is not hidden).</summary>
    public bool IsSidebarColumnVisible => CurrentPage?.Sidebar is not null && IsSidebarVisible && !IsSidebarAutoCollapsed;

    /// <summary>Applies a splitter-driven panel height (clamped) and keeps the expanded flag in sync.</summary>
    public void SetPanelHeight(double height, WorkbenchPanel? panel = null)
    {
        var minimum = WorkbenchLayoutMetrics.PanelMinimumHeight(panel ?? SelectedPanel);
        var clamped = height <= 0 ? 0 : Math.Clamp(height, minimum, MaximumPanelHeight);
        if (clamped > 0)
        {
            _lastExpandedPanelHeight = clamped;
            OnPropertyChanged(nameof(LastExpandedPanelHeight));
        }

        PanelHeight = new GridLength(clamped);
        IsPanelExpanded = clamped > 0;
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        // 响应式自动收起只是“窗口太窄”时的临时隐藏。此时点击切换(顶栏按钮 / Ctrl+B)
        // 意味着用户要重新打开侧栏:清除自动收起并显式打开,避免窗口加宽后意外复活。
        if (IsSidebarAutoCollapsed)
        {
            IsSidebarAutoCollapsed = false;
            IsSidebarVisible = true;
            return;
        }

        IsSidebarVisible = !IsSidebarVisible;
    }

    /// <summary>Applies the fixed-desktop responsive rule. Called by the main window on resize;
    /// the user preference remains in <see cref="IsSidebarVisible"/>.</summary>
    public void UpdateResponsiveLayout(double windowWidth)
    {
        var groupCount = Workbench?.Editor?.Groups.GroupCount ?? 1;
        var shouldAutoCollapse = IsSidebarVisible
            && WorkbenchLayoutMetrics.ShouldAutoCollapseSidebar(windowWidth, groupCount, SidebarWidth);
        if (IsSidebarAutoCollapsed != shouldAutoCollapse)
        {
            IsSidebarAutoCollapsed = shouldAutoCollapse;
        }

        // Compact activity density is transient and does not alter the sidebar preference.
        var compact = windowWidth < WorkbenchLayoutMetrics.WindowMinimumWidth + 160;
        if (IsCompactActivityBar != compact)
        {
            IsCompactActivityBar = compact;
        }
    }

    partial void OnIsSidebarVisibleChanged(bool value)
    {
        if (!value && IsSidebarAutoCollapsed)
        {
            IsSidebarAutoCollapsed = false;
        }
        OnPropertyChanged(nameof(SidebarColumnWidth));
        OnPropertyChanged(nameof(IsSidebarColumnVisible));
    }

    partial void OnIsSidebarAutoCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarColumnWidth));
        OnPropertyChanged(nameof(IsSidebarColumnVisible));
    }

    [RelayCommand]
    private void ClearOutput() => _logService.Clear();

    /// <summary>Problems are the warning/error projection of the shared output log. Clearing them
    /// therefore clears the source messages as well, keeping panel counts and selections in sync.</summary>
    [RelayCommand]
    private void ClearProblems() => _logService.Clear();

    [RelayCommand]
    private void SelectPanel(WorkbenchPanel panel)
    {
        SelectedPanel = panel;
        if (PanelHeight.Value > 0)
        {
            SetPanelHeight(PanelHeight.Value, panel);
        }
        IsPanelExpanded = true;
    }

    [RelayCommand]
    private void TogglePanel() => IsPanelExpanded = !IsPanelExpanded;

    /// <summary>最大化 / 恢复底部面板(最大化时占满编辑器主区域)。</summary>
    [RelayCommand]
    private void TogglePanelMaximize() => IsPanelMaximized = !IsPanelMaximized;

    /// <summary>拆分活动编辑器组(命令路由:向右 / 向下)。</summary>
    public void SplitEditorGroup(EditorSplitOrientation orientation)
    {
        if (Workbench?.Editor is not { } editor)
        {
            return;
        }

        editor.Groups.SplitGroup(editor.Groups.ActiveGroup, orientation, editor.Groups.ActiveGroup.SelectedTab);
    }

    /// <summary>聚焦下一个/上一个编辑器组(命令路由)。</summary>
    public void FocusNextEditorGroup() => Workbench?.Editor?.Groups.FocusNextGroup();

    public void FocusPreviousEditorGroup() => Workbench?.Editor?.Groups.FocusPreviousGroup();

    /// <summary>“终端：新建终端”命令：显示底部面板、选中终端标签并用默认 Shell 与工作区目录
    /// 创建会话；键盘焦点由视图层监听 <see cref="TerminalViewModel.FocusRequested"/> 交给终端窗口。</summary>
    [RelayCommand]
    private async Task OpenNewTerminalAsync()
    {
        SelectPanel(WorkbenchPanel.Terminal);
        if (Terminal is { } terminal)
        {
            await terminal.NewTerminalCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private void NavigateByIndex(int index)
    {
        if (index >= 0 && index < NavigationItems.Count)
        {
            SelectedNavigationItem = NavigationItems[index];
        }
    }

    [RelayCommand]
    private void ShowOperationLog()
    {
        SelectedPanel = WorkbenchPanel.Output;
        IsPanelExpanded = true;
    }

    // Status-bar item commands (VS Code status-bar items are clickable)
    [RelayCommand]
    private void ShowProblems()
    {
        SelectedPanel = WorkbenchPanel.Problems;
        IsPanelExpanded = true;
    }

    // ===== Title-bar menu commands (VS Code menu bar) =====

    [RelayCommand]
    private static void SetTheme(AppTheme theme) => ThemeService.Apply(theme);

    [RelayCommand]
    private void OpenProjectDirectory() => _ = _explorer?.ChooseProjectCommand?.ExecuteAsync(null);

    [RelayCommand]
    private static void About() =>
        System.Windows.MessageBox.Show(
            "Nornia —— Windows 原生的精简开发工作台。\n基于 WPF/MVVM，外壳仿 VS Code（活动栏 / 侧栏 / 标签条 / 面板 / 状态栏）。",
            "关于 Nornia",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);

    // ===== QuickInput overlay: command palette (Ctrl+Shift+P) / quick open (Ctrl+P) =====

    [RelayCommand]
    private void ShowCommandPalette()
    {
        DetachQuickOpenPrefixMode();
        QuickInput.Open("输入命令", BuildPaletteItems());
    }

    [RelayCommand]
    private async Task ShowQuickOpenAsync()
    {
        // Quick open reads the workspace root, which is only populated once the explorer page has
        // activated (settings → OpenWorkspaceAsync). Idempotent, so this just guarantees it ran.
        if (_explorer is { } explorer)
        {
            await explorer.ActivateAsync();
        }

        var invocationGeneration = GetQuickOpenCacheGeneration();
        DetachQuickOpenPrefixMode();
        QuickInput.Open("打开文件", [new QuickPickItem("正在扫描工作区…", "文件索引准备中", Codicons.Loading, static () => { })]);
        AttachQuickOpenPrefixMode();
        var invocationSession = _quickOpenSession;

        var items = await BuildQuickOpenItemsAsync();
        if (!QuickInput.IsOpen || !IsQuickOpenGenerationCurrent(invocationGeneration) ||
            !ReferenceEquals(_quickOpenSession, invocationSession))
        {
            if (ReferenceEquals(_quickOpenSession, invocationSession))
            {
                DetachQuickOpenPrefixMode();
                QuickInput.Close();
            }
            return;
        }

        if (_quickOpenSession is { } session)
        {
            session.FileItems = items.ToArray();
            if (session.Mode == QuickOpenUiMode.File)
            {
                QuickInput.ReplaceItems(session.FileItems);
            }
        }
    }

    /// <summary>状态栏语言选择器(VS Code"更改语言模式"):QuickInput 枚举全部已注册语言,
    /// 选中即切换当前标签的解析/高亮类型。</summary>
    public void OpenLanguagePicker(EditorTabItem? tab)
    {
        if (tab is not FilePreviewTab preview)
        {
            return;
        }

        DetachQuickOpenPrefixMode();
        var items = CodeFileTypeRegistry.Instance.All.Select(type => new QuickPickItem(
            type.DisplayName,
            $"语言 ID: {type.LanguageId}",
            type.IconMonogram,
            () => preview.SetLanguage(type))).ToArray();
        QuickInput.Open("更改语言模式", items);
    }

    /// <summary>面包屑符号段点击 / Ctrl+Shift+O → 打开当前标签的符号选择器(QuickInput;
    /// 无大纲时无操作)。符号项以 '@' 前缀标识,与统一快速打开前缀模式一致。</summary>
    public void OpenSymbolPicker(EditorTabItem? tab)
    {
        if (tab is not FilePreviewTab preview || preview.OutlineEntries.Count == 0)
        {
            return;
        }

        var items = preview.OutlineEntries.Select(entry => new QuickPickItem(
            "@" + entry.DisplayName,
            $"第 {entry.Line} 行",
            Codicons.SymbolFile,
            () => JumpToSymbol(preview, entry))).ToArray();
        QuickInput.Open("转到符号", items);
        AttachQuickOpenPrefixMode();
        QuickInput.FilterText = "@";
    }

    /// <summary>快速打开的前缀模式(VS Code quickAccess):'@' 切到当前文件符号、':' 切到行号,
    /// 其余为文件。前缀保留在输入框,过滤随模式联动;关闭时自动解绑。</summary>
    private QuickOpenSession? _quickOpenSession;

    private sealed class QuickOpenSession
    {
        public required QuickPickItem[] FileItems { get; set; }
        public required QuickPickItem[] SymbolItems { get; init; }
        public required QuickPickItem[] LineItems { get; init; }
        public QuickOpenUiMode Mode { get; set; } = QuickOpenUiMode.File;
    }

    private enum QuickOpenUiMode { File, Symbol, Line }

    /// <summary>把当前已经打开的 QuickInput(文件/符号)接入前缀模式切换;关闭时自动解绑。</summary>
    private void AttachQuickOpenPrefixMode()
    {
        DetachQuickOpenPrefixMode();
        var tab = Workbench?.Editor.SelectedTab as FilePreviewTab;
        _quickOpenSession = new QuickOpenSession
        {
            FileItems = QuickInput.Items.ToArray(),
            SymbolItems = tab is null
                ? []
                : tab.OutlineEntries.Select(entry => new QuickPickItem(
                    "@" + entry.DisplayName,
                    $"第 {entry.Line} 行",
                    Codicons.SymbolFile,
                    () => JumpToSymbol(tab, entry))).ToArray(),
            LineItems =
            [
                new QuickPickItem("转到行…", "输入行号（如 42 或 42,16）· Enter 定位", Codicons.GoToFile,
                    () => ExecuteQuickOpenGotoLine()),
            ],
        };
        QuickInput.PropertyChanged += OnQuickInputForPrefixChanged;
    }

    private void DetachQuickOpenPrefixMode()
    {
        QuickInput.PropertyChanged -= OnQuickInputForPrefixChanged;
        _quickOpenSession = null;
    }

    private static void JumpToSymbol(FilePreviewTab tab, CodeOutlineEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Id) && tab.SymbolDocument.FindById(entry.Id) is { } symbol)
        {
            tab.JumpToSymbolCommand.Execute(symbol);
        }
        else
        {
            tab.JumpToOutlineCommand.Execute(entry);
        }
    }

    private void OnQuickInputForPrefixChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickInputViewModel.IsOpen) && !QuickInput.IsOpen)
        {
            DetachQuickOpenPrefixMode();
            return;
        }

        if (e.PropertyName != nameof(QuickInputViewModel.FilterText) || _quickOpenSession is not { } session)
        {
            return;
        }

        var trimmed = (QuickInput.FilterText ?? string.Empty).TrimStart();
        var mode = trimmed.Length > 0 && trimmed[0] == '@'
            ? QuickOpenUiMode.Symbol
            : trimmed.Length > 0 && trimmed[0] == ':' ? QuickOpenUiMode.Line : QuickOpenUiMode.File;
        if (mode == session.Mode)
        {
            return;
        }

        session.Mode = mode;
        QuickInput.ReplaceItems(mode switch
        {
            QuickOpenUiMode.Symbol => session.SymbolItems,
            QuickOpenUiMode.Line => session.LineItems,
            _ => session.FileItems,
        });
    }

    private void ExecuteQuickOpenGotoLine()
    {
        var tail = (QuickInput.FilterText ?? string.Empty).TrimStart(':').Trim();
        if (Workbench?.Editor.SelectedTab is not FilePreviewTab preview || tail.Length == 0)
        {
            return;
        }

        preview.GoToLineInput = tail;
        preview.GoToLineCommand.Execute(null);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab 最近编辑器切换器(VS Code openNextRecentlyUsedEditor /
    /// openPreviousRecentlyUsedEditor):QuickInput 列出工作台级 MRU 标签,回车即激活。
    /// next=false(Ctrl+Shift+Tab)时预选次新标签,配合 Shift 反向循环。</summary>
    public void ShowEditorMruSwitcher(bool next)
    {
        var editors = Workbench?.Groups.EditorsInMruOrder;
        if (editors is null || editors.Count == 0)
        {
            return;
        }

        DetachQuickOpenPrefixMode();
        var items = editors.Select(tab => new QuickPickItem(
            tab.Name,
            tab.Path,
            tab.IsDiff ? Codicons.SourceControl : Codicons.File,
            () => Workbench?.Editor.ActivateTab(tab))).ToArray();
        QuickInput.Open("最近编辑器", items);
        if (!next && items.Length > 1)
        {
            QuickInput.MoveSelection(1);
        }
    }

    /// <summary>Command palette rows: navigation, environment sections, themes, toggles, workspace
    /// actions (VS Code Ctrl+Shift+P). Pure building so the palette content stays unit-testable.</summary>
    public IReadOnlyList<QuickPickItem> BuildPaletteItems()
    {
        if (_commandRegistry is not null)
        {
            return _commandRegistry.Commands.Select(command => new QuickPickItem(
                command.Title,
                string.Join(" · ", new[] { command.Category, command.Id, _keybindings?.GetPrimaryBinding(command.Id) }
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
                command.Glyph,
                () => _ = _commandRegistry.ExecuteAsync(command.Id))).ToArray();
        }

        var items = new List<QuickPickItem>
        {
            new("打开项目目录…", "文件", Codicons.FolderOpened, () => _ = _explorer?.ChooseProjectCommand?.ExecuteAsync(null)),
            new("切换主题（深色）", "视图", Codicons.Gear, () => ThemeService.Apply(AppTheme.Dark)),
            new("切换主题（浅色）", "视图", Codicons.Gear, () => ThemeService.Apply(AppTheme.Light)),
            new("切换主题（高对比度）", "视图", Codicons.Gear, () => ThemeService.Apply(AppTheme.HighContrast)),
            new("切换侧栏", "视图 · Ctrl+B", Codicons.LayoutSidebarLeft, () => ToggleSidebarCommand.Execute(null)),
            new("切换底栏", "视图 · Ctrl+J", Codicons.LayoutPanel, () => TogglePanelCommand.Execute(null)),
            new("终端：新建终端", "视图 · Ctrl+`", Codicons.Terminal, () => _ = OpenNewTerminalCommand.ExecuteAsync(null)),
            new("显示输出面板", "视图", Codicons.Output, () => ShowOperationLogCommand.Execute(null)),
            new("显示问题面板", "视图", Codicons.Error, () => ShowProblemsCommand.Execute(null)),
            new("清除输出", "视图", Codicons.ClearAll, () => ClearOutputCommand.Execute(null)),
        };
        foreach (var nav in NavigationItems)
        {
            var destination = nav.NavigationIds.FirstOrDefault() ?? nav.Title;
            var glyph = nav.Glyph;
            items.Add(new QuickPickItem(nav.Title, "打开页面", glyph, () => SelectNavigationItem(destination)));
        }

        if (_environment is { } environment)
        {
            foreach (var section in environment.Items)
            {
                var key = section.Title;
                items.Add(new QuickPickItem(key, "环境管理", section.Glyph, () => OpenEnvironmentSection(key)));
            }
        }

        return items;
    }

    /// <summary>Attaches the production command registry after the shell view model is composed.
    /// Legacy constructors used by focused tests keep the deterministic built-in palette fallback.</summary>
    public void AttachCommandPlatform(ICommandRegistry commandRegistry, IKeybindingService keybindings)
    {
        _commandRegistry = commandRegistry;
        _keybindings = keybindings;
        Commands.Attach(commandRegistry);
    }

    public void OpenSettings(bool keyboardShortcuts)
    {
        NavigateToSettingsCommand.Execute(null);
        if (keyboardShortcuts) _settings?.Editor.ShowKeyboardShortcutsCommand.Execute(null);
        else _settings?.Editor.ShowSettingsCommand.Execute(null);
    }

    private void OpenEnvironmentSection(string title)
    {
        // 命令面板行:选中环境页(打开/激活其标签)并在页内切换小节——不产生分区标签。
        SelectNavigationItem(NavigationTargets.Dashboard);
        _environment?.SelectSection(title);
    }

    /// <summary>Quick-open rows: every file under the current workspace root (bounded, generated
    /// folders skipped) so Ctrl+P can jump straight to a file preview tab.</summary>
    public IReadOnlyList<QuickPickItem> BuildQuickOpenItems()
    {
        if (_explorer is not { } explorer)
        {
            return [];
        }

        var root = explorer.Explorer.WorkspacePath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var workspaceContext = explorer.CurrentWorkspaceContext;
        var fullRoot = Path.GetFullPath(root);
        var files = QuickOpenFiles(fullRoot).ToList();
        var items = new List<QuickPickItem>(files.Count);
        foreach (var path in files)
        {
            var name = System.IO.Path.GetFileName(path);
            var relative = QuickOpenRelativePath(fullRoot, path);
            var glyph = Codicons.File;
            items.Add(new QuickPickItem(name, relative, glyph,
                () => _ = explorer.Editor.OpenFileAsync(path, expectedWorkspaceContext: workspaceContext)));
        }

        return items;
    }

    private async Task<IReadOnlyList<QuickPickItem>> BuildQuickOpenItemsAsync()
    {
        if (_explorer is not { } explorer)
        {
            return [];
        }

        var root = explorer.Explorer.WorkspacePath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var fullRoot = Path.GetFullPath(root);
        var workspaceContext = explorer.CurrentWorkspaceContext;
        IReadOnlyList<string>? files = null;
        Task<IReadOnlyList<string>>? buildTask = null;
        long cacheGeneration;
        lock (_quickOpenCacheGate)
        {
            cacheGeneration = _quickOpenCacheGeneration;
            if (string.Equals(_quickOpenCacheRoot, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                _quickOpenFilesCache is not null)
            {
                files = _quickOpenFilesCache;
            }
            else
            {
                if (!string.Equals(_quickOpenBuildRoot, fullRoot, StringComparison.OrdinalIgnoreCase) || _quickOpenBuildTask is null)
                {
                    _quickOpenBuildRoot = fullRoot;
                    _quickOpenBuildTask = Task.Run<IReadOnlyList<string>>(() => QuickOpenFiles(fullRoot).ToArray());
                }
                buildTask = _quickOpenBuildTask;
            }
        }

        if (files is null)
        {
            files = await buildTask!.ConfigureAwait(true);
            var stale = false;
            lock (_quickOpenCacheGate)
            {
                if (cacheGeneration == _quickOpenCacheGeneration &&
                    string.Equals(_quickOpenBuildRoot, fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _quickOpenCacheRoot = fullRoot;
                    _quickOpenFilesCache = files;
                }
                else if (cacheGeneration != _quickOpenCacheGeneration)
                {
                    // A watcher event arrived while the index was being built. Do not publish a
                    // stale snapshot into the overlay; the next pass reuses the new root/task or
                    // starts a fresh scan as needed.
                    stale = true;
                }
            }

            if (stale)
            {
                return await BuildQuickOpenItemsAsync().ConfigureAwait(true);
            }
        }

        return BuildQuickOpenItems(explorer, fullRoot, files, workspaceContext);
    }

    private IReadOnlyList<QuickPickItem> BuildQuickOpenItems(
        ExplorerPageViewModel explorer,
        string root,
        IReadOnlyList<string> files,
        ProjectWorkspaceContext? workspaceContext = null)
    {
        var items = new List<QuickPickItem>(files.Count);
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var relative = QuickOpenRelativePath(root, path);
            items.Add(new QuickPickItem(name, relative, Codicons.File,
                () => _ = explorer.Editor.OpenFileAsync(path, expectedWorkspaceContext: workspaceContext)));
        }

        return items;
    }

    private static string QuickOpenRelativePath(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative == "." ? Path.GetFileName(path) : relative;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return Path.GetFileName(path);
        }
    }

    /// <summary>Bounded, generated-folder-aware file enumeration for quick open. Skips VCS and build
    /// output trees that would drown the list (VS Code hides these by default too).</summary>
    public static IEnumerable<string> QuickOpenFiles(string root, int cap = 5000)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".svn", ".hg", "bin", "obj", "node_modules", ".vs", "packages",
        };
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && count < cap)
        {
            var dir = pending.Pop();
            IReadOnlyList<string> directories = [];
            IReadOnlyList<string> files = [];
            try
            {
                // Quick open applies fuzzy ranking after the index is loaded, so sorting every
                // directory here only delays the first usable rows and can enumerate millions of
                // siblings before the global cap is reached. Preserve filesystem order and stop
                // asking the OS once the cap's remaining budget is exhausted.
                var remaining = Math.Max(0, cap - count);
                directories = Directory.EnumerateDirectories(dir)
                    .Where(d => !excluded.Contains(Path.GetFileName(d)))
                    .Take(remaining)
                    .ToArray();
                files = Directory.EnumerateFiles(dir)
                    .Take(Math.Max(0, remaining - directories.Count))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Skip unreadable directories (deep trees can hit ACL boundaries).
                continue;
            }

            foreach (var sub in directories)
            {
                pending.Push(sub);
            }

            foreach (var file in files)
            {
                if (count >= cap)
                {
                    yield break;
                }

                yield return file;
                count++;
            }
        }
    }

    // ===== Status bar: git branch / sync badges + problem counters =====

    /// <summary>The shared source-control view model (status-bar branch indicator, click opens Git).</summary>
    public GitViewModel? Git => _git;

    /// <summary>侧栏预建视图缓存的稳定 DataContext 来源(见 MainWindow.xaml 侧栏缓存区):
    /// 各侧栏视图在窗口创建时即实例化,切换页面只切 Visibility,不再重建视觉树。</summary>
    public EnvironmentManagementViewModel? Environment => _environment;
    public ExplorerPageViewModel? Explorer => _explorer;
    public SearchViewModel? Search => _search;
    public ProjectsViewModel? Projects => _projects;
    public SettingsEditorViewModel? SettingsEditor => _settings?.Editor;

    [RelayCommand]
    private void OpenGit() => SelectNavigationItem(NavigationTargets.Git);

    [RelayCommand]
    private void OpenSearch()
    {
        SelectNavigationItem(NavigationTargets.Search);
        _search?.FocusSearchInput();
    }

    public int ProblemErrorCount => _problemEntries.Count(entry => entry.Level == "ERROR");
    public int ProblemWarningCount => _problemEntries.Count(entry => entry.Level == "WARNING");

    /// <summary>True when the settings page is the active activity-bar item (pins the bottom gear).</summary>
    public bool IsSettingsActive => SelectedNavigationItem?.Page is SettingsViewModel;

    [RelayCommand]
    private void NavigateToSettings() => SelectNavigationItem(NavigationTargets.Settings);

    private void SelectNavigationItem(string destination)
    {
        var item = NavigationItems.FirstOrDefault(candidate => candidate.NavigationIds.Contains(destination));
        if (item is not null)
        {
            SelectedNavigationItem = item;
        }
    }

    // ===== 顶栏命令中心(工作区名称)与返回/前进内存导航历史 =====

    /// <summary>命令中心标题:当前工作区(项目)目录名,未打开项目时显示 “Nornia”。</summary>
    public string WorkspaceTitle
    {
        get
        {
            if (_explorer is not null && !string.IsNullOrWhiteSpace(_explorer.Explorer.WorkspacePath))
            {
                var trimmed = _explorer.Explorer.WorkspacePath.TrimEnd('\\', '/');
                var name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? _explorer.Explorer.WorkspacePath : name;
            }

            return "Nornia";
        }
    }

    private void OnWorkspaceSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.WorkspacePath))
        {
            // Navigation history stores document paths. A path from the previous project must not
            // be replayed after a project switch, where the same relative file name may resolve to
            // a different document (or the old absolute file may be opened outside the project).
            _backStack.Clear();
            _forwardStack.Clear();
            _historyAnchor = null;
            GoBackCommand.NotifyCanExecuteChanged();
            GoForwardCommand.NotifyCanExecuteChanged();
            // A quick-open item captures an absolute path and its background index may still be
            // running. Close the old overlay and invalidate its session before the new workspace
            // can publish another file list.
            DetachQuickOpenPrefixMode();
            QuickInput.Close();
            InvalidateQuickOpenCache();
            OnPropertyChanged(nameof(WorkspaceTitle));
        }
    }

    private void OnWorkspaceFilesChanged(object? sender, EventArgs e) => InvalidateQuickOpenCache();

    private void InvalidateQuickOpenCache()
    {
        lock (_quickOpenCacheGate)
        {
            _quickOpenCacheGeneration++;
            _quickOpenCacheRoot = null;
            _quickOpenFilesCache = null;
        }
    }

    private long GetQuickOpenCacheGeneration()
    {
        lock (_quickOpenCacheGate)
        {
            return _quickOpenCacheGeneration;
        }
    }

    private bool IsQuickOpenGenerationCurrent(long generation)
    {
        lock (_quickOpenCacheGate)
        {
            return generation == _quickOpenCacheGeneration;
        }
    }

    /// <summary>一段内存导航位置:活动栏目标(稳定 ID)+ 环境页小节 + 工作区标签键 +
    /// 编辑器文件路径(标签已关闭时按路径重开)。历史只存在于内存,不持久化。</summary>
    private sealed record NavLocation(string? PageDestination, string? SectionKind, string? TabKey, string? EditorFilePath);

    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    private NavLocation CurrentLocation()
    {
        string? sectionKind = null;
        if (SelectedNavigationItem?.Page is EnvironmentManagementViewModel environment)
        {
            sectionKind = environment.SelectedItem?.Kind.ToString();
        }

        return new NavLocation(
            SelectedNavigationItem?.NavigationIds.FirstOrDefault(),
            sectionKind,
            Workbench?.SelectedTab?.TabKey,
            (Workbench?.SelectedTab as EditorWorkbenchTab)?.EditorTab.Path);
    }

    /// <summary>页面/标签切换后记录一段历史(去重:同一位置不重复入栈;
    /// 程序化的历史回放期间由 <see cref="_suppressHistoryRecording"/> 屏蔽)。</summary>
    private void RecordNavigation()
    {
        if (_suppressHistoryRecording || Workbench is null) return;
        var location = CurrentLocation();
        if (_historyAnchor is null)
        {
            _historyAnchor = location;
            return;
        }

        if (location == _historyAnchor) return;
        _backStack.Add(_historyAnchor);
        if (_backStack.Count > HistoryCapacity)
        {
            _backStack.RemoveAt(0);
        }

        _forwardStack.Clear();
        _historyAnchor = location;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBackMethod))]
    private void GoBack()
    {
        if (_backStack.Count == 0 || _historyAnchor is null) return;
        var target = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        _forwardStack.Add(_historyAnchor);
        _historyAnchor = target;
        ApplyHistoryLocation(target);
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoForwardMethod))]
    private void GoForward()
    {
        if (_forwardStack.Count == 0 || _historyAnchor is null) return;
        var target = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);
        _backStack.Add(_historyAnchor);
        if (_backStack.Count > HistoryCapacity)
        {
            _backStack.RemoveAt(0);
        }

        _historyAnchor = target;
        ApplyHistoryLocation(target);
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    private bool CanGoBackMethod() => CanGoBack;
    private bool CanGoForwardMethod() => CanGoForward;

    /// <summary>把历史位置应用到当前状态:活动栏目标 → 环境页小节 → 工作区标签
    /// (文件标签已关闭则按路径重开)。全程屏蔽 <see cref="RecordNavigation"/>,
    /// 避免回放本身被当作一次新导航再次入栈。</summary>
    private void ApplyHistoryLocation(NavLocation location)
    {
        if (Workbench is null) return;
        _suppressHistoryRecording = true;
        try
        {
            if (location.PageDestination is { } destination)
            {
                SelectNavigationItem(destination);
            }

            if (location.SectionKind is { } sectionKind &&
                SelectedNavigationItem?.Page is EnvironmentManagementViewModel environment &&
                Enum.TryParse<EnvironmentSection>(sectionKind, out var section))
            {
                environment.SelectSection(section);
            }

            if (location.TabKey is { } tabKey)
            {
                var tab = Workbench.Tabs.FirstOrDefault(candidate => candidate.TabKey == tabKey);
                if (tab is not null)
                {
                    Workbench.OpenOrActivateTab(tab);
                }
                else if (location.EditorFilePath is { Length: > 0 } path && File.Exists(path) && _explorer is not null)
                {
                    _ = _explorer.Editor.OpenFileAsync(path);
                }
            }
            else
            {
                Workbench.ClearNonDocumentSelection();
            }
        }
        finally
        {
            _suppressHistoryRecording = false;
        }
    }

    // ===== Keyboard routing (terminal-focus exception) =====

    /// <summary>Ctrl+W routing (VS Code): a focused terminal closes its session; otherwise the unified
    /// workbench closes the active tab. Lives on the view model (not the window code-behind) so the
    /// terminal-focus exception is unit-testable. No-op when nothing is selected.</summary>
    public void RouteCloseShortcut(bool terminalFocused)
    {
        if (terminalFocused && Terminal is { } terminal)
        {
            terminal.CloseCommand.Execute(terminal.SelectedSession);
        }
        else if (Workbench is { } workbench)
        {
            workbench.CloseActiveTabCommand.Execute(null);
        }
    }

    /// <summary>Ctrl+PgUp/PgDn routing (VS Code): a focused terminal cycles its sessions (offset +1 =
    /// next, -1 = previous, wrapping); otherwise the unified workbench cycles its tabs.</summary>
    public void RouteAdjacentTabShortcut(bool terminalFocused, int offset)
    {
        if (terminalFocused && Terminal is { } terminal)
        {
            terminal.GoToAdjacentSessionCommand.Execute(offset);
        }
        else if (Workbench is { } workbench)
        {
            workbench.GoToAdjacentTabCommand.Execute(offset);
        }
    }

    partial void OnSelectedPanelChanged(WorkbenchPanel value)
    {
        // Clear the leaving panel's selections so a switch does not leave stale selections
        // or focus behind it (e.g. multi-selected output rows persisting when moving to terminal).
        if (value != _previousPanel)
        {
            switch (_previousPanel)
            {
                case WorkbenchPanel.Output:
                    SelectedOutputEntries.Clear();
                    break;
                case WorkbenchPanel.Problems:
                    SelectedProblemEntries.Clear();
                    break;
            }
            RefreshCopyCommands();
        }

        _previousPanel = value;
        if (PanelHeight.Value > 0)
        {
            SetPanelHeight(PanelHeight.Value, value);
        }
        OnPropertyChanged(nameof(IsOutputSelected));
        OnPropertyChanged(nameof(IsProblemsSelected));
        OnPropertyChanged(nameof(IsTerminalSelected));
    }

    partial void OnIsPanelExpandedChanged(bool value)
    {
        if (value)
        {
            if (PanelHeight.Value <= 0)
            {
                PanelHeight = new GridLength(_lastExpandedPanelHeight);
            }
            return;
        }

        if (PanelHeight.Value > 0)
        {
            _lastExpandedPanelHeight = PanelHeight.Value;
            OnPropertyChanged(nameof(LastExpandedPanelHeight));
        }
        PanelHeight = new GridLength(0);
    }

    partial void OnPanelSearchTextChanged(string value)
    {
        _searchDebounceTimer?.Stop();
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            if (_searchDebounceTimer is null)
            {
                _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
                {
                    Interval = SearchDebounceDelay
                };
                _searchDebounceTimer.Tick += (_, _) =>
                {
                    _searchDebounceTimer!.Stop();
                    RefreshPanelFilter();
                };
            }
            _searchDebounceTimer.Start();
        }
        else
        {
            RefreshPanelFilter();
        }
    }
    partial void OnPanelLevelFilterChanged(LogPanelLevelFilter value) => RefreshPanelFilter();

    private void RefreshPanelFilter()
    {
        OutputView.Refresh();
        ProblemView.Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanCopySelectedProblems))]
    private void CopySelectedProblems() =>
        _clipboard.SetText(LogPanelCopyFormatter.FormatRows(SelectedProblemEntries));

    [RelayCommand(CanExecute = nameof(CanCopySelectedProblems))]
    private void CopyProblemMessages() =>
        _clipboard.SetText(LogPanelCopyFormatter.FormatMessages(SelectedProblemEntries));

    [RelayCommand(CanExecute = nameof(CanCopyAllProblems))]
    private void CopyAllProblems() =>
        _clipboard.SetText(LogPanelCopyFormatter.FormatRows(ProblemEntries));

    [RelayCommand(CanExecute = nameof(CanCopySelectedOutput))]
    private void CopySelectedOutput() =>
        _clipboard.SetText(LogPanelCopyFormatter.FormatRows(SelectedOutputEntries));

    [RelayCommand(CanExecute = nameof(CanCopySelectedOutput))]
    private void CopyOutputMessages() =>
        _clipboard.SetText(LogPanelCopyFormatter.FormatMessages(SelectedOutputEntries));

    [RelayCommand(CanExecute = nameof(CanCopyAllOutput))]
    private void CopyAllOutput() =>
        _clipboard.SetText(LogPanelCopyFormatter.FormatRows(OutputEntries));

    private bool CanCopySelectedProblems() => SelectedProblemEntries.Count > 0;
    private bool CanCopySelectedOutput() => SelectedOutputEntries.Count > 0;
    private bool CanCopyAllProblems() => _problemEntries.Count > 0;
    private bool CanCopyAllOutput() => OutputEntries.Count > 0;

    private void RefreshCopyCommands()
    {
        CopySelectedProblemsCommand.NotifyCanExecuteChanged();
        CopyProblemMessagesCommand.NotifyCanExecuteChanged();
        CopyAllProblemsCommand.NotifyCanExecuteChanged();
        CopySelectedOutputCommand.NotifyCanExecuteChanged();
        CopyOutputMessagesCommand.NotifyCanExecuteChanged();
        CopyAllOutputCommand.NotifyCanExecuteChanged();
    }

    private bool FilterPanelEntry(object item)
    {
        if (item is not UiLogEntry entry) return false;
        if (PanelLevelFilter != LogPanelLevelFilter.All && !LevelMatches(entry.Level, PanelLevelFilter)) return false;
        if (!string.IsNullOrWhiteSpace(PanelSearchText))
        {
            var haystack = $"{entry.Level} {entry.Message}";
            if (haystack.IndexOf(PanelSearchText, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }

        return true;
    }

    private static bool LevelMatches(string level, LogPanelLevelFilter filter) => filter switch
    {
        LogPanelLevelFilter.Info => level == "INFO",
        LogPanelLevelFilter.Warning => level == "WARNING",
        LogPanelLevelFilter.Error => level == "ERROR",
        _ => true,
    };

    private void OnOutputEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 增量同步:逐条追加/移除问题条目,避免每次日志写入都全量重扫并重建 Problems 列表
        // (突发输出时一次重建 O(n) 的刷新既阻塞 UI 又让隐藏的 Problems 列表空转)。
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (var entry in e.NewItems!.Cast<UiLogEntry>())
                {
                    if (entry.Level is "WARNING" or "ERROR")
                    {
                        _problemEntries.Add(entry);
                    }
                }
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (var entry in e.OldItems!.Cast<UiLogEntry>())
                {
                    _problemEntries.Remove(entry);
                }
                break;
            default:
                RefreshProblems();
                break;
        }

        RaiseProblemSignals();
        RefreshCopyCommands();
        if (e.NewItems?.Cast<UiLogEntry>().Any(entry => entry.Level == "ERROR") == true)
        {
            SelectedPanel = WorkbenchPanel.Problems;
            IsPanelExpanded = true;
        }
        PruneStaleSelections(SelectedProblemEntries, ProblemEntries);
        PruneStaleSelections(SelectedOutputEntries, OutputEntries);
    }

    private void RaiseProblemSignals()
    {
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(ProblemErrorCount));
        OnPropertyChanged(nameof(ProblemWarningCount));
        OnPropertyChanged(nameof(ProblemsTabTitle));
        OnPropertyChanged(nameof(EnvironmentHealthGlyph));
        OnPropertyChanged(nameof(EnvironmentHealthText));
        RefreshActivityBadges();
    }

    private void RefreshProblems()
    {
        _problemEntries.ReplaceRange(OutputEntries.Where(entry => entry.Level is "WARNING" or "ERROR"));

        RaiseProblemSignals();
    }

    private static void PruneStaleSelections(ObservableCollection<UiLogEntry> selected, IReadOnlyCollection<UiLogEntry> source)
    {
        if (selected.Count == 0)
        {
            return;
        }

        // O(n + m) instead of O(n·m): the source is hashed once, evicted entries drop out.
        var live = new HashSet<UiLogEntry>(source);
        for (var i = selected.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(selected[i]))
            {
                selected.RemoveAt(i);
            }
        }
    }

    /// <summary>Activity-bar badges: environment problems (VS Code-style problem count), the git
    /// change count and the attention project count (VS Code-style activity badges). Matching is
    /// done by canonical <see cref="NavigationItem.NavigationIds"/>, never by localized title.</summary>
    private void RefreshActivityBadges()
    {
        var environment = NavigationItems.FirstOrDefault(item => item.NavigationIds.Contains(NavigationTargets.Dashboard));
        if (environment is not null)
        {
            environment.Badge = ProblemCount > 0 ? ProblemCount.ToString() : string.Empty;
        }

        var git = NavigationItems.FirstOrDefault(item => item.NavigationIds.Contains(NavigationTargets.Git));
        if (git is not null)
        {
            git.Badge = _git is { ChangeCount: > 0 } ? _git.ChangeCount.ToString() : string.Empty;
        }

        var project = NavigationItems.FirstOrDefault(item => item.NavigationIds.Contains(NavigationTargets.Projects));
        if (project is not null)
        {
            project.Badge = _projects is { AttentionBadge.Length: > 0 } ? _projects.AttentionBadge : string.Empty;
        }
    }

    private void OnGitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GitViewModel.ChangeCount) or nameof(GitViewModel.StagedCountLabel))
        {
            RefreshActivityBadges();
        }
    }

    private void OnProjectsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectsViewModel.AttentionBadge))
        {
            RefreshActivityBadges();
        }
    }

    private void OnNavigationRequested(object? sender, NavigationRequest request)
    {
        // Select the activity-bar item that owns this canonical destination id. Matching is by
        // stable <see cref="NavigationItem.NavigationIds"/> so it stays correct under any culture.
        var item = NavigationItems.FirstOrDefault(candidate => candidate.NavigationIds.Contains(request.Destination));
        if (item is not null)
        {
            SelectedNavigationItem = item;
            if (item.Page is INavigationTarget target) target.ApplyNavigationContext(request.Context);
        }

        // Environment section destinations switch the section inside the environment page content
        // area (no workbench tab) and apply the navigation context to its section page.
        if (EnvironmentSectionInfo.SectionForDestination(request.Destination) is { } section)
        {
            _environment?.SelectSection(section);
            if (_environment?.SectionPage(section) is INavigationTarget target)
            {
                target.ApplyNavigationContext(request.Context);
            }
        }
    }

    /// <summary>Re-raise every localized display property when the UI culture changes.</summary>
    private void RefreshLocalizedDisplayStrings()
    {
        foreach (var item in NavigationItems)
        {
            item.OnCultureChanged();
        }

        OnPropertyChanged(nameof(ProblemsTabTitle));
        OnPropertyChanged(nameof(EnvironmentHealthText));
    }
}

public enum WorkbenchPanel
{
    Output,
    Problems,
    Terminal
}

public enum LogPanelLevelFilter
{
    All,
    Info,
    Warning,
    Error
}

/// <summary>One activity-bar destination. A stable <see cref="NavigationIds"/> set is used for
/// all routing (navigation requests, badge refresh) so nothing depends on the localized display
/// title. <see cref="Badge"/> renders the VS Code-style count badge overlay (empty string hides it).</summary>
public sealed partial class NavigationItem : ObservableObject
{
    public NavigationItem(string titleResourceKey, string glyph, PageViewModel page, params string[] navigationIds)
    {
        TitleResourceKey = titleResourceKey;
        Glyph = glyph;
        Page = page;
        NavigationIds = navigationIds;
    }

    public string TitleResourceKey { get; }
    /// <summary>Localized display title; refreshed when the UI culture changes.</summary>
    public string Title => Loc.GetString(TitleResourceKey);
    public string Glyph { get; }
    public PageViewModel Page { get; }
    /// <summary>Canonical navigation destination ids this item owns (e.g. the environment item owns
    /// Dashboard, Runtime, Tools, Packages, Cache).</summary>
    public IReadOnlyList<string> NavigationIds { get; }

    internal void OnCultureChanged() => OnPropertyChanged(nameof(Title));

    [ObservableProperty]
    private string badge = string.Empty;
}
