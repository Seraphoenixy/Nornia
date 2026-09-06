using Nornia.Desktop.Behaviors;
using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Serilog;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Nornia.Desktop.Views;

public partial class MainWindow : Window
{
    // WM_GETMINMAXINFO: keep the maximized rect inside the work area, so the custom-chrome window
    // (WindowStyle=None) never covers the taskbar — otherwise the bottom status bar ends up in the
    // taskbar-covered band and is invisible when maximized.
    private const int WmGetMinMaxInfo = 0x0024;
    // 原生标题栏拖动:把点击当作"按住了标题栏"交给系统拖动循环(Win32 移动循环),
    // 系统拖动天然支持跨显示器 / 跨 DPI(光标跟到哪里窗口跟到哪里)。
    // 相比 WPF 的 DragMove:后者在光标跨过 DPI 不同的显示器边界时会中断拖动,
    // 导致窗口无法从显示器 A 拖到显示器 B。
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private static readonly IntPtr HtCaption = (IntPtr)2;
    private bool _movingOrSizing;
    private bool _dpiChangedDuringMove;
    private HwndSource? _source;
    private HwndSourceHook? _windowProcHook;
    private readonly ISettingsService _settings;
    private readonly IApplicationStateStore _stateStore;
    private readonly IKeybindingService _keybindings;
    private readonly IContextKeyService _contextKeys;
    private readonly IShutdownCoordinator _shutdownCoordinator;
    private readonly RevisionGate _layoutSettingsRevisionGate = new();
    private ISettingsSession? _layoutSettingsSession;
    private bool _closingAfterSettingsFlush;
    private readonly DispatcherTimer _layoutSaveTimer;
    private bool _layoutDirty;

    // ===== 启动分阶段物化(壳先行、内容延后;VS Code 式)=====
    // 终端视图原为 XAML x:Name 生成字段,现改为首帧后由代码物化,字段在此声明。
    private TerminalView? PanelTerminalView;
    private bool _contentStaged;
    private readonly HashSet<object> _sidebarsMaterialized = [];

    private void ApplyDpi()
    {
        DpiHelper.ApplyWindowChrome(this);
        var scale = DpiHelper.Scale(this);
        foreach (var button in new[] { MinimizeButton, MaximizeButton, CloseButton })
        {
            if (button is null) continue;
            button.Width = DpiHelper.SystemButtonWidth(scale);
            button.Height = DpiHelper.SystemButtonHeight(scale);
        }
    }

    // ============================================================================
    // 启动分阶段物化(壳先行、内容延后;VS Code 式)
    // 窗口首建只构建壳(标题栏/活动栏/侧栏列/工作台外框/面板框架/状态栏),最重的
    // 重子树(EditorGroupsView、六个页面侧栏——GitView 近千行 XAML、终端视图)在
    // 首帧渲染后分阶段物化,各阶段计时写入会话日志([perf] 启动阶段)供性能回归追踪。
    // ============================================================================

    /// <summary>挂首帧钩子:壳绘制完成后开始物化内容。阶段 1(工作台 + 当前页侧栏)
    /// 在首帧回调内同步完成;阶段 2(其余侧栏)/阶段 3(终端)在 ApplicationIdle 补建。</summary>
    private void HookFirstFrameForContentStaging()
    {
        void OnFirstFrame(object? sender, EventArgs e)
        {
            // 自定义窗格(WindowStyle=None)的句柄在构造期即创建,Show 前就可能有渲染帧;
            // 一次性消费会被这些帧吃掉,故解绑必须在"已加载且完成物化"之后。
            if (_contentStaged || !IsLoaded) return;
            CompositionTarget.Rendering -= OnFirstFrame;
            _contentStaged = true;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                MaterializeEditorWorkbench();
                MaterializeCurrentSidebar();
            }
            catch (Exception ex)
            {
                // 阶段失败不阻断窗口:未建部分在后续阶段(空闲补建)重试,幂等。
                Log.Warning(ex, "[perf] 启动阶段1 物化失败");
            }

            Log.Information("[perf] 启动阶段1 首帧内容(工作台+当前侧栏) {Ms}ms", stopwatch.ElapsedMilliseconds);
            _ = ContinueStagingAsync();
        }

        CompositionTarget.Rendering += OnFirstFrame;
    }

    private async Task ContinueStagingAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!IsLoaded) return;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                MaterializeRemainingSidebars();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[perf] 启动阶段2 侧栏补建失败");
            }

            Log.Information("[perf] 启动阶段2 其余侧栏 {Ms}ms", stopwatch.ElapsedMilliseconds);

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!IsLoaded) return;
            stopwatch.Restart();
            try
            {
                MaterializeTerminal();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[perf] 启动阶段3 终端物化失败");
            }

            Log.Information("[perf] 启动阶段3 终端 {Ms}ms", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // 窗口已关闭/调度器已停泵:剩余阶段不再执行。
        }
    }

    private void MaterializeEditorWorkbench()
    {
        if (EditorWorkbenchHost is null || EditorWorkbenchHost.Children.Count > 0) return;
        if (DataContext is not MainViewModel { } viewModel) return;
        var view = new EditorGroupsView { DataContext = viewModel.Workbench };
        EditorWorkbenchHost.Children.Add(view);
    }

    private void MaterializeCurrentSidebar()
    {
        if (DataContext is MainViewModel { } viewModel)
            MaterializeSidebarFor(viewModel.CurrentPage);
    }

    private void MaterializeRemainingSidebars()
    {
        if (DataContext is not MainViewModel viewModel) return;
        object?[] pages = { viewModel.Environment, viewModel.Explorer, viewModel.Search,
                            viewModel.Git, viewModel.Projects, viewModel.SettingsEditor };
        foreach (var page in pages) MaterializeSidebarFor(page);
    }

    /// <summary>物化指定页面的侧栏视图并插入 <see cref="SidebarHost"/>(等效原 XAML 声明:
    /// 视图类型 + 页 VM DataContext + 以窗口为源的可见性绑定)。幂等——物化后视图常驻,
    /// 切换页面只切换 Visibility,滚动位置与展开状态得以保留。</summary>
    private void MaterializeSidebarFor(object? page)
    {
        if (SidebarHost is null || page is null) return;
        if (!_sidebarsMaterialized.Add(page)) return;

        // 可见性必须以窗口 DataContext(MainViewModel)解析 CurrentPage:侧栏视图的 DataContext
        // 是各页 VM,直接绑 CurrentPage.Sidebar 会解析不到、退回 Visible,五个侧栏全部叠在一起。
        var converter = FindResource("SidebarVisibilityConverter") as IValueConverter;
        Binding MakeBinding(Type parameter) => new("DataContext.CurrentPage.Sidebar")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
            Converter = converter,
            ConverterParameter = parameter,
        };

        FrameworkElement? view = null;
        if (page is EnvironmentManagementViewModel env) view = Build<EnvironmentSidebarView>(env, MakeBinding(typeof(EnvironmentManagementViewModel)));
        else if (page is ExplorerPageViewModel explorer) view = Build<ExplorerSidebarView>(explorer, MakeBinding(typeof(ExplorerPageViewModel)));
        else if (page is SearchViewModel search) view = Build<SearchSidebarView>(search, MakeBinding(typeof(SearchViewModel)));
        else if (page is GitViewModel git) view = Build<GitView>(git, MakeBinding(typeof(GitViewModel)));
        else if (page is ProjectsViewModel projects) view = Build<ProjectsSidebarView>(projects, MakeBinding(typeof(ProjectsViewModel)));
        else if (page is SettingsEditorViewModel settings) view = Build<SettingsSidebarView>(settings, MakeBinding(typeof(SettingsEditorViewModel)));

        if (view is null)
        {
            // 无侧栏的页面(或未知页型):不占位,允许后续阶段重试。
            _sidebarsMaterialized.Remove(page);
            return;
        }

        SidebarHost.Children.Add(view);

        static T Build<T>(object dataContext, Binding binding) where T : FrameworkElement, new()
        {
            var element = new T { DataContext = dataContext };
            element.SetBinding(VisibilityProperty, binding);
            return element;
        }
    }

    private void MaterializeTerminal()
    {
        if (PanelTerminalHost is null || PanelTerminalHost.Content is not null) return;
        if (DataContext is not MainViewModel { } viewModel || viewModel.Terminal is not { } terminalVm) return;
        PanelTerminalView = new TerminalView { DataContext = terminalVm, Margin = new Thickness(12, 4, 12, 4) };
        PanelTerminalHost.Content = PanelTerminalView;
        // 兜底聚焦:终端面板已展开且选中(用户在首秒内按过 Ctrl+`,FocusRequested 在视图
        // 物化前到达被双优先级重试空转)时,按同一模式补一次聚焦。
        if (viewModel.IsPanelExpanded && viewModel.IsTerminalSelected)
        {
            FocusTerminalSurfaceIfVisible();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, FocusTerminalSurfaceIfVisible);
        }
    }

    public MainWindow(MainViewModel viewModel, ISettingsService settings, IApplicationStateStore stateStore,
        IKeybindingService keybindings, IContextKeyService contextKeys, DesktopCommandBootstrapper commands,
        IShutdownCoordinator shutdownCoordinator)
    {
        InitializeComponent();
        MinWidth = WorkbenchLayoutMetrics.WindowMinimumWidth;
        MinHeight = WorkbenchLayoutMetrics.WindowMinimumHeight;
        _settings = settings;
        _stateStore = stateStore;
        _stateStore.Changed += (_, _) => QueueLayoutRestore();
        _keybindings = keybindings;
        _contextKeys = contextKeys;
        _shutdownCoordinator = shutdownCoordinator;
        commands.WindowAction = action => ExecuteWindowActionAsync(action, viewModel);
        commands.Register(viewModel);
        _ = ReloadKeybindingsSafelyAsync();
        _keybindings.PendingChordChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            viewModel.PendingChordText = _keybindings.PendingChord is { } chord
                ? $"正在等待第二个按键（{chord}）" : string.Empty);
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width - 24);
        Height = Math.Min(Height, workArea.Height - 24);
        StateChanged += OnWindowStateChanged;
        WindowState = WindowState.Maximized;
        DataContext = viewModel;
        // 新建会话后把键盘焦点交给终端窗口(仅当终端面板已展开并选中)。
        if (viewModel.Terminal is { } terminalVm)
        {
            terminalVm.FocusRequested += (_, _) =>
            {
                // 面板内容在下一布局周期才实体化,双优先级重试确保表面已加载。
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusTerminalSurfaceIfVisible);
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, FocusTerminalSurfaceIfVisible);
            };
        }
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage))
            {
                // A sidebar-bearing page can be selected after the window has already become
                // narrow; re-evaluate then rather than waiting for the next resize event.
                viewModel.UpdateResponsiveLayout(ActualWidth);
            }
            if (e.PropertyName is nameof(MainViewModel.SidebarColumnWidth) or nameof(MainViewModel.IsSidebarColumnVisible)
                or nameof(MainViewModel.PanelHeight) or nameof(MainViewModel.IsPanelMaximized))
            {
                ApplyLayout();
            }
            UpdateWorkbenchContext(viewModel);
        };
        PreviewGotKeyboardFocus += (_, _) => UpdateFocusContext();
        PreviewLostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, UpdateFocusContext);
        LocationChanged += (_, _) => MarkLayoutDirty();
        SizeChanged += (_, eventArgs) =>
        {
            viewModel.UpdateResponsiveLayout(eventArgs.NewSize.Width);
            ApplyLayout();
            MarkLayoutDirty();
        };
        _layoutSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _layoutSaveTimer.Tick += (_, _) =>
        {
            _layoutSaveTimer.Stop();
            if (_layoutDirty)
            {
                _layoutDirty = false;
                _ = SaveLayoutSafelyAsync();
            }
        };
        Loaded += (_, _) => _ = InitializeLoadedLayoutSafelyAsync(viewModel);
        Closing += MainWindow_Closing;
        ApplyDpi();
        // 壳先行:窗口壳(标题栏/活动栏/侧栏列/面板框架/状态栏)首建即渲染;
        // 重内容(工作台/侧栏/终端)在首帧后分阶段物化,见 HookFirstFrameForContentStaging。
        HookFirstFrameForContentStaging();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Mark the flush state and cancel *before* anything async: if the shutdown work completes
        // synchronously the event handler must not call Close() re-entrantly, otherwise WPF's
        // InternalClose sees the window still mid-close and throws VerifyNotClosing.
        if (_closingAfterSettingsFlush) return;
        _closingAfterSettingsFlush = true;
        e.Cancel = true;
        _ = CleanupAndCloseAsync();
    }

    private async Task CleanupAndCloseAsync()
    {
        try
        {
            await _shutdownCoordinator.FlushAsync();
        }
        catch (Exception exception)
        {
            // Shutdown must remain best-effort: a failed settings/database flush must not leave
            // the window permanently stuck in the cancelled Closing state.
            System.Diagnostics.Debug.WriteLine($"Shutdown flush failed: {exception}");
        }
        finally
        {
            _layoutSaveTimer.Stop();
            try
            {
                if (_layoutDirty)
                {
                    _layoutDirty = false;
                    await SaveLayoutAsync();
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Layout save during shutdown failed: {exception}");
            }
            // Defer to the dispatcher so Close() runs after the current Closing event returns; the
            // re-entered Closing then sees _closingAfterSettingsFlush and lets the close proceed.
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    /// <summary>Applies the sidebar column width and output-panel height the VM drives (the two
    /// splitters write back through <see cref="RestoreLayoutAsync"/> / drag handlers).</summary>
    private void ApplyLayout()
    {
        if (DataContext is not MainViewModel viewModel || SidebarColumn is null || SidebarSplitterColumn is null ||
            PanelRow is null || PanelSplitterRow is null)
        {
            return;
        }

        var sidebarVisible = viewModel.IsSidebarColumnVisible;
        SidebarColumn.MinWidth = sidebarVisible ? WorkbenchLayoutMetrics.SidebarMinimumWidth : 0;
        var availableSidebarWidth = ActualWidth - WorkbenchLayoutMetrics.ActivityBarWidth
            - WorkbenchLayoutMetrics.EditorHostMinimumWidth - WorkbenchLayoutMetrics.SashHitArea;
        SidebarColumn.MaxWidth = sidebarVisible
            ? WorkbenchLayoutMetrics.SidebarMaximumFor(availableSidebarWidth)
            : WorkbenchLayoutMetrics.SidebarMaximumWidth;
        SidebarColumn.Width = new GridLength(viewModel.SidebarColumnWidth.Value);
        SidebarSplitterColumn.Width = new GridLength(sidebarVisible ? WorkbenchLayoutMetrics.SashHitArea : 0);
        ApplyPanelLayout(viewModel);
    }

    private void ApplyPanelLayout(MainViewModel viewModel)
    {
        if (PanelRow is null || PanelSplitterRow is null || EditorHostRow is null)
        {
            return;
        }

        // 面板最大化:编辑器主区域让位给面板(保留活动栏、侧栏与状态栏)。
        var maximized = viewModel.IsPanelMaximized && viewModel.IsPanelExpanded;
        EditorHostRow.Height = new GridLength(maximized ? 0 : 1, GridUnitType.Star);
        PanelSplitterRow.Height = new GridLength(maximized || !viewModel.IsPanelExpanded ? 0 : 5);
        if (maximized)
        {
            PanelRow.Height = new GridLength(1, GridUnitType.Star);
            return;
        }

        var requestedPanelHeight = viewModel.PanelHeight.Value;
        var maximumPanelHeight = MaximumPanelHeightForCurrentWindow(viewModel.SelectedPanel);
        var effectivePanelHeight = requestedPanelHeight <= 0
            ? 0
            : Math.Min(requestedPanelHeight, maximumPanelHeight);
        if (Math.Abs(requestedPanelHeight - effectivePanelHeight) > 0.5)
        {
            viewModel.SetPanelHeight(effectivePanelHeight, viewModel.SelectedPanel);
            return;
        }

        PanelSplitterRow.Height = new GridLength(viewModel.IsPanelExpanded
            ? WorkbenchLayoutMetrics.SashHitArea : 0);
        PanelRow.Height = new GridLength(effectivePanelHeight);
    }

    private double MaximumPanelHeightForCurrentWindow(WorkbenchPanel panel)
    {
        // The panel may use at most 70% of the editor/panel host. Before the first Arrange pass,
        // use the model cap and let EditorPanelHost_SizeChanged tighten it afterwards.
        if (EditorPanelHost?.ActualHeight is not > 0)
        {
            return MainViewModel.MaximumPanelHeight;
        }

        return WorkbenchLayoutMetrics.PanelMaximumHeight(EditorPanelHost.ActualHeight, panel);
    }

    private void EditorPanelHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The editor host resizes continuously while the sidebar splitter is dragged. Reapplying
        // the persisted sidebar width here would overwrite GridSplitter's live column width and
        // make the sidebar appear impossible to resize.
        if (DataContext is MainViewModel viewModel)
        {
            ApplyPanelLayout(viewModel);
        }
    }

    private void QueueLayoutRestore()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() => _ = RestoreLayoutSafelyAsync(clampWindow: false)));
        }
        catch (InvalidOperationException)
        {
            // The state store can publish during dispatcher shutdown; there is no live window
            // left that could consume the late layout update.
        }
    }

    private async Task ReloadKeybindingsSafelyAsync()
    {
        try
        {
            await _keybindings.ReloadAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to reload keybindings during window construction.");
        }
    }

    private async Task RestoreLayoutSafelyAsync(bool clampWindow)
    {
        try
        {
            await RestoreLayoutAsync(clampWindow);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply a live window-layout update.");
        }
    }

    private async Task InitializeLoadedLayoutSafelyAsync(MainViewModel viewModel)
    {
        try
        {
            await RestoreLayoutAsync(clampWindow: true);
            await BindLayoutSettingsAsync();
            viewModel.UpdateResponsiveLayout(ActualWidth);
            ApplyLayout();
            UpdateFocusContext();
            UpdateWorkbenchContext(viewModel);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize the main window layout.");
        }
    }

    /// <summary>Restores the persisted sidebar/panel dimensions and (only on the initial restore)
    /// the window placement. 坏损的 settings.json(反序列化失败)不得让窗口恢复阶段崩溃:降级为默认布局。
    /// <para><paramref name="clampWindow"/> 只在启动恢复(<c>Loaded</c>)为 <c>true</c>:窗口位置只在此刻
    /// 从持久化状态恢复并做防丢失钳制。实时状态变更(<c>ApplicationStateStore.Changed</c>)只回放侧栏/面板,
    /// 绝不重设窗口位置——否则拖动(保存→变更→恢复)会把用户刻意摆出的半出屏 / 第二屏位置瞬间拽回。
    /// 钳制基准是虚拟屏(所有显示器并集)而非主屏工作区,只保证窗口至少一部分可到达
    /// (显示器被拔掉时窗口不会彻底消失),不强制整窗可见。</para></summary>
    private async Task RestoreLayoutAsync(bool clampWindow)
    {
        SettingsSnapshot current;
        ApplicationState state;
        try
        {
            current = await _settings.GetSnapshotAsync(new());
            state = await _stateStore.LoadAsync();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to load settings during window-layout restore; using defaults.");
            return;
        }
        if (clampWindow && state.WindowBounds is { } bounds)
        {
            // 虚拟屏范围(DIP):窗口可落在任意显示器,只确保左上角至少有一部分落在屏内。
            var screenLeft = SystemParameters.VirtualScreenLeft;
            var screenTop = SystemParameters.VirtualScreenTop;
            var screenWidth = SystemParameters.VirtualScreenWidth;
            var screenHeight = SystemParameters.VirtualScreenHeight;
            var width = Math.Min(bounds.Width, screenWidth);
            var height = Math.Min(bounds.Height, screenHeight);
            // 保留至少 160 DIP 的可见前缘(足够点到标题栏),并允许刻意半出屏的位置原样保留。
            var keep = Math.Min(160d, Math.Min(width, height));
            var left = Math.Clamp(bounds.Left, screenLeft, screenLeft + screenWidth - keep);
            var top = Math.Clamp(bounds.Top, screenTop, screenTop + screenHeight - keep);
            Left = (int)left;
            Top = (int)top;
            Width = width;
            Height = height;
            WindowState = bounds.Maximized ? WindowState.Maximized : WindowState.Normal;
        }

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SidebarWidth = state.SidebarWidth ?? current.Effective(BuiltInSettingsCatalog.SidebarDefaultWidth);
            viewModel.SetPanelHeight(state.PanelHeight ?? current.Effective(BuiltInSettingsCatalog.PanelDefaultHeight),
                viewModel.SelectedPanel);
            viewModel.IsPanelExpanded = state.PanelVisible ?? current.Effective(BuiltInSettingsCatalog.PanelStartExpanded);
            viewModel.IsPanelMaximized = state.PanelMaximized ?? false;
        }

        ApplyLayout();
    }

    /// <summary>布局设置会话:侧栏默认宽度 / 面板默认高度 / 面板启动展开。修改后立即应用到当前
    /// 窗口,并清除对应的机器状态(拖拽)覆盖 —— 用户修改默认值是明确意图,下一次启动按新默认值,
    /// 不再被旧的拖拽状态覆盖。</summary>
    private async Task BindLayoutSettingsAsync()
    {
        if (_layoutSettingsSession is not null) return;
        var session = await _settings.OpenSessionAsync(new(),
        [
            BuiltInSettingsCatalog.SidebarDefaultWidth.Id,
            BuiltInSettingsCatalog.PanelDefaultHeight.Id,
            BuiltInSettingsCatalog.PanelStartExpanded.Id,
        ]);
        _layoutSettingsSession = session;
        session.Changed += (_, _) => ApplyLayoutSettingsSafely(session);
        ApplyLayoutSettings(session.Current);
    }

    private void ApplyLayoutSettingsSafely(ISettingsSession session)
    {
        try
        {
            ApplyLayoutSettings(session.Current);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply live layout settings.");
        }
    }

    private void ApplyLayoutSettings(SettingsSnapshot? snapshot)
    {
        if (DataContext is not MainViewModel viewModel || snapshot is null ||
            !_layoutSettingsRevisionGate.TryAccept(snapshot))
        {
            return;
        }

        viewModel.SidebarWidth = snapshot.Effective(BuiltInSettingsCatalog.SidebarDefaultWidth);
        viewModel.SetPanelHeight(snapshot.Effective(BuiltInSettingsCatalog.PanelDefaultHeight), viewModel.SelectedPanel);
        viewModel.IsPanelExpanded = snapshot.Effective(BuiltInSettingsCatalog.PanelStartExpanded);
        // 清除对应机器状态覆盖;属性变更已触发 ApplyLayout 重排。
        _ = ResetLayoutOverridesSafelyAsync();
    }

    private async Task ResetLayoutOverridesSafelyAsync()
    {
        try
        {
            await _stateStore.CommitAsync(new([
                new(ApplicationStateField.SidebarWidth, null, Reset: true),
                new(ApplicationStateField.PanelHeight, null, Reset: true),
                new(ApplicationStateField.PanelVisible, null, Reset: true),
            ]));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to reset live layout overrides.");
        }
    }

    private void MarkLayoutDirty()
    {
        _layoutDirty = true;
        if (!_layoutSaveTimer.IsEnabled)
        {
            _layoutSaveTimer.Start();
        }
    }

    private async Task SaveLayoutAsync()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var bounds = new WindowBoundsInfo(
            RestoreBounds.Left,
            RestoreBounds.Top,
            RestoreBounds.Width,
            RestoreBounds.Height,
            WindowState == WindowState.Maximized);
        await _stateStore.CommitAsync(new([
            new(ApplicationStateField.WindowBounds, bounds),
            new(ApplicationStateField.SidebarWidth, viewModel.SidebarWidth),
            new(ApplicationStateField.PanelHeight, viewModel.LastExpandedPanelHeight),
            new(ApplicationStateField.PanelVisible, viewModel.IsPanelExpanded),
            new(ApplicationStateField.PanelMaximized, viewModel.IsPanelMaximized),
        ]));
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && SidebarColumn.ActualWidth > 0)
        {
            viewModel.SidebarWidth = SidebarColumn.ActualWidth;
        }

        _ = SaveLayoutSafelyAsync();
    }

    private async Task SaveLayoutSafelyAsync()
    {
        try
        {
            await SaveLayoutAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist window layout.");
        }
    }

    // ===== 活动栏:重复点击当前一级按钮切换左侧栏展开/折叠(VS Code 活动栏行为) =====

    private NavigationItem? _activitySelectionAtMouseDown;

    private void ActivityBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 记录按下瞬间的选中项(ListBox 在按下时更新选中),释放时据此判定
        // "点击的是按下前已选中的项"。
        _activitySelectionAtMouseDown = (sender as ListBox)?.SelectedItem as NavigationItem;
    }

    private void ActivityBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var previous = _activitySelectionAtMouseDown;
        _activitySelectionAtMouseDown = null;
        if (previous is null || sender is not ListBox { SelectedItem: NavigationItem current })
        {
            return;
        }

        // 选中项变了(点了其它一级按钮)→ 常规导航,不切换侧栏。
        if (!ReferenceEquals(previous, current))
        {
            return;
        }

        // 释放点不在该项上(拖到其它项后松开)→ 视为导航,不切换。
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (TryFindAncestorListBoxItem(source) is not { } item
            || !ReferenceEquals(item.DataContext, current))
        {
            return;
        }

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ToggleSidebarCommand.Execute(null);
        }
    }

    private static ListBoxItem? TryFindAncestorListBoxItem(DependencyObject source)
        => FindAncestor<ListBoxItem>(source);

    private void PanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && PanelRow.ActualHeight > 0)
        {
            viewModel.SetPanelHeight(Math.Min(PanelRow.ActualHeight,
                MaximumPanelHeightForCurrentWindow(viewModel.SelectedPanel)), viewModel.SelectedPanel);
        }

        _ = SaveLayoutSafelyAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        // Keep the delegate reference; AddHook does not return one (the hook stays for the window's lifetime).
        _windowProcHook = WindowProc;
        _source.AddHook(_windowProcHook);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 拖动/缩放期间(WM_ENTERSIZEMOVE → WM_EXITSIZEMOVE):DpiChanged 里重设 WindowChrome 会
        // 打断系统移动循环,故在进入时挂起 ApplyDpi,退出后再补。
        switch (msg)
        {
            case WmEnterSizeMove:
                _movingOrSizing = true;
                return IntPtr.Zero;
            case WmExitSizeMove:
                _movingOrSizing = false;
                if (_dpiChangedDuringMove)
                {
                    _dpiChangedDuringMove = false;
                    ApplyDpi();
                }
                return IntPtr.Zero;
        }

        if (msg != WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // MINMAXINFO 使用物理像素:把 SystemParameters.WorkArea(DIP) 按当前 DPI 换算。
        var dpi = VisualTreeHelper.GetDpi(this);
        var workArea = SystemParameters.WorkArea;
        var info = Marshal.PtrToStructure<NativeMinMaxInfo>(lParam);
        info.MaxPosition.X = (int)Math.Round(workArea.Left * dpi.DpiScaleX);
        info.MaxPosition.Y = (int)Math.Round(workArea.Top * dpi.DpiScaleY);
        info.MaxSize.X = (int)Math.Round(workArea.Width * dpi.DpiScaleX);
        info.MaxSize.Y = (int)Math.Round(workArea.Height * dpi.DpiScaleY);
        info.MaxTrackSize = info.MaxSize;
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    private void MainWindow_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        // 拖动/缩放期间挂起重设(否则打断正在进行的系统移动循环),退出移动时补做。
        if (_movingOrSizing)
        {
            _dpiChangedDuringMove = true;
            return;
        }
        ApplyDpi();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        // 用系统原生标题栏拖动替代 WPF DragMove:DragMove 在光标跨过 DPI 不同的显示器
        // 边界时会中断,窗口拖不到第二屏;原生拖动由 OS 移动循环接管,跨显示器/跨 DPI
        // 稳定跟随光标(最大化时拖动会自动还原并移动,与常规窗口行为一致)。
        if (Mouse.LeftButton != MouseButtonState.Pressed || _source is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = PointToScreen(e.GetPosition(this)); // WPF 给出的是 DIP,需换算为物理像素。
        var x = (int)Math.Round(screen.X * dpi.DpiScaleX);
        var y = (int)Math.Round(screen.Y * dpi.DpiScaleY);
        // LOWORD=屏幕 X,HIWORD=屏幕 Y(物理像素,可能为负 → 16 位补码表示)。
        var lParam = (y << 16) | (x & 0xFFFF);
        SendMessage(_source.Handle, WmNcLButtonDown, HtCaption, new IntPtr(lParam));
        e.Handled = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            MaximizeGlyph.Text = WindowState == WindowState.Maximized ? Codicons.ChromeRestore : Codicons.ChromeMaximize;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    // ===== QuickInput overlay (command palette / quick open) =====

    /// <summary>点击遮罩空白处关闭命令面板 / 快速打开(VS Code:点击外部即关闭)。
    /// 事件挂在全窗口遮罩 Border 上;点击调色板内部不会冒泡到这里(承载面板在其上方)。</summary>
    private void QuickInputBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.QuickInput.Close();
        }
    }

    /// <summary>Esc closes, Enter confirms, Up/Down move the selection (VS Code quick input).</summary>
    private void QuickInputOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                viewModel.QuickInput.Close();
                e.Handled = true;
                break;
            case Key.Enter:
                viewModel.QuickInput.ConfirmCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                viewModel.QuickInput.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                viewModel.QuickInput.MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Focus the input box as soon as the overlay becomes visible and select all text.</summary>
    private void QuickInputBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    // This WPF adapter only normalizes and dispatches. Business shortcuts live in the command registry.
    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        // Focus events are routed before WPF commits Keyboard.FocusedElement, and the deferred
        // focus refresh can still be pending when the user double-clicks a word and immediately
        // presses Ctrl+C. Refresh from the actual focused element at dispatch time so the global
        // list-copy binding cannot swallow a native text-editor copy.
        UpdateFocusContext();
        try
        {
            if (await _keybindings.DispatchAsync(NormalizeKeyStroke(e)))
            {
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            // PreviewKeyDown is an async void WPF event. Observe command-dispatch failures so a
            // malformed binding cannot escape through the dispatcher and take down the UI loop.
            Log.Error(ex, "全局快捷键处理失败");
        }
    }

    private void UpdateFocusContext()
    {
        var focused = Keyboard.FocusedElement;
        _contextKeys.Set("terminalFocus", IsInsideTerminalView(focused as DependencyObject));
        _contextKeys.Set("editorTextFocus", focused is ICSharpCode.AvalonEdit.TextEditor or ICSharpCode.AvalonEdit.Editing.TextArea);
        _contextKeys.Set("markdownPreviewFocus", focused is DependencyObject dependency
            && FindAncestor<MarkdownPreviewView>(dependency) is not null);
        _contextKeys.Set("inputFocus", IsTextInput(focused));
    }

    private void UpdateWorkbenchContext(MainViewModel viewModel)
    {
        _contextKeys.Set("settingsFocus", viewModel.IsSettingsActive);
        _contextKeys.Set("activeView", viewModel.SelectedNavigationItem?.Title ?? string.Empty);
        _contextKeys.Set("activeEditor", viewModel.Workbench?.SelectedTab is EditorWorkbenchTab { IsDiff: true }
            ? "diff" : viewModel.Workbench?.SelectedTab is null ? string.Empty : "code");
        _contextKeys.Set("panelVisible", viewModel.IsPanelExpanded);
        _contextKeys.Set("sidebarVisible", viewModel.IsSidebarColumnVisible);
        _contextKeys.Set("workspaceOpen", viewModel.Workbench?.Tabs.OfType<EditorWorkbenchTab>().Any() == true);
        _contextKeys.Set("gitRepositoryOpen", viewModel.Git?.IsRepository == true);
    }

    private async Task ExecuteWindowActionAsync(string action, MainViewModel viewModel)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        switch (action)
        {
            case "copyFocused" when focused is not null:
                CopyFromFocusedList(focused, viewModel);
                break;
            case "selectAllFocused" when focused is not null:
                SelectAllInFocusedList(focused);
                break;
            case "clearTerminal":
                viewModel.Terminal?.ClearCommand.Execute(viewModel.Terminal.SelectedSession);
                break;
            case "closeTerminal":
                if (viewModel.Terminal?.SelectedSession is { } terminal)
                    _ = viewModel.Terminal.CloseCommand.ExecuteAsync(terminal);
                break;
            case "focusTerminal":
                viewModel.SelectPanelCommand.Execute(WorkbenchPanel.Terminal);
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PanelTerminalView?.FocusSurface());
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => PanelTerminalView?.FocusSurface());
                break;
            case "refreshActiveView":
                if (viewModel.CurrentPage is RuntimeViewModel runtime) _ = runtime.ScanCommand.ExecuteAsync(null);
                else if (viewModel.CurrentPage is ToolsViewModel tools) _ = tools.ScanCommand.ExecuteAsync(null);
                else if (viewModel.CurrentPage is CacheViewModel cache) _ = cache.ScanCommand.ExecuteAsync(null);
                else if (viewModel.CurrentPage is ProjectsViewModel projects) _ = projects.CheckCommand.ExecuteAsync(null);
                break;
            case "theme.dark":
            case "theme.light":
            case "theme.highContrast":
                var snapshot = await _settings.GetSnapshotAsync(new());
                var value = action switch
                {
                    "theme.light" => "Light",
                    "theme.highContrast" => "HighContrast",
                    _ => "Dark",
                };
                await _settings.CommitAsync(SettingsTransaction.Set(snapshot, SettingScope.User,
                    BuiltInSettingsCatalog.Theme, value));
                break;
        }
    }

    private static string NormalizeKeyStroke(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var name = key switch
        {
            Key.OemTilde => "`",
            Key.OemComma => ",",
            Key.OemBackslash => "\\",
            Key.OemPipe => "\\",
            Key.PageDown => "pagedown",
            Key.PageUp => "pageup",
            Key.Return => "enter",
            Key.Escape => "escape",
            _ => key.ToString().ToLowerInvariant(),
        };
        var parts = new List<string>(5);
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("win");
        parts.Add(name);
        return string.Join('+', parts);
    }

    private static bool IsTextInput(object? element)
    {
        if (element is TextBox or PasswordBox or RichTextBox or ComboBox)
        {
            return true;
        }

        // Read-only AvalonEdit surfaces own native text-selection shortcuts (including Ctrl+C):
        // global list/tab shortcuts must not intercept keys while any editor surface is focused.
        if (element is ICSharpCode.AvalonEdit.TextEditor or ICSharpCode.AvalonEdit.Editing.TextArea)
        {
            return true;
        }

        if (element is not DependencyObject dependency)
        {
            return false;
        }

        // 交互式终端表面拥有全部按键(数字、F5、方向键等直达 shell),同文本输入豁免,
        // 否则数字 1-8 会被全局导航劫持、打字直接切走页面。
        if (FindAncestor<CodeDocumentView>(dependency) is not null
            || FindAncestor<DiffDocumentView>(dependency) is not null
            || FindAncestor<MarkdownPreviewView>(dependency) is not null
            || IsInsideTerminalView(dependency))
        {
            return true;
        }

        return false;
    }

    /// <summary>终端面板展开且选中时,把键盘焦点交给交互式终端表面。</summary>
    private void FocusTerminalSurfaceIfVisible()
    {
        if (DataContext is MainViewModel viewModel && viewModel.IsPanelExpanded && viewModel.IsTerminalSelected)
        {
            PanelTerminalView?.FocusSurface();
        }
    }

    private static bool IsInsideTerminalView(DependencyObject? focused) =>
        focused is not null && FindAncestor<TerminalView>(focused) is not null;

    private static bool SelectAllInFocusedList(DependencyObject focused)
    {
        if (FindAncestor<ListBox>(focused) is { Items.Count: > 0 } listBox)
        {
            listBox.SelectAll();
            return true;
        }

        if (FindAncestor<DataGrid>(focused) is { Items.Count: > 0 } dataGrid)
        {
            dataGrid.SelectAll();
            return true;
        }

        return false;
    }

    private bool CopyFromFocusedList(DependencyObject focused, MainViewModel viewModel)
    {
        if (FindAncestor<ListBox>(focused) is { } listBox)
        {
            // Output / Problems panels (split-mode copy with their own formatters)
            if (ReferenceEquals(listBox, OutputPanelList))
            {
                return TryExecute(viewModel.CopySelectedOutputCommand);
            }

            if (ReferenceEquals(listBox, ProblemsPanelList))
            {
                return TryExecute(viewModel.CopySelectedProblemsCommand);
            }

            return CopyFromList(listBox);
        }

        return FindAncestor<DataGrid>(focused) is { } dataGrid && CopyFromDataGrid(dataGrid);
    }

    private static bool CopyFromList(ListBox listBox)
    {
        var selected = (listBox.DataContext, listBox.ItemsSource);
        return selected switch
        {
            (GitViewModel git, var source) when ReferenceEquals(source, git.UnstagedChanges) || ReferenceEquals(source, git.StagedChanges)
                => TryExecute(git.CopyChangePathsCommand),
            (GitViewModel git, var source) when ReferenceEquals(source, git.Branches)
                => TryExecute(git.CopyBranchNamesCommand),
            (GitViewModel git, var source) when ReferenceEquals(source, git.LogRows)
                => TryExecute(git.CopyCommitHashesCommand),
            (DiffTab diff, var source) when ReferenceEquals(source, diff.DiffLines)
                => TryExecute(diff.CopySelectedDiffLinesCommand),
            (EditorAreaViewModel editor, var source) when ReferenceEquals(source, editor.OpenTabs)
                => TryExecute(editor.CopySelectedTabPathCommand),
            (ProjectsViewModel projects, var source) when ReferenceEquals(source, projects.Projects)
                => TryExecute(projects.CopySelectedProjectsCommand),
            (EnvironmentManagementViewModel environment, var source) when ReferenceEquals(source, environment.Items)
                => TryExecute(environment.CopySelectedSectionsCommand),
            _ => false
        };
    }

    private static bool CopyFromDataGrid(DataGrid dataGrid)
    {
        return dataGrid.DataContext switch
        {
            RuntimeViewModel runtime when ReferenceEquals(dataGrid.ItemsSource, runtime.FilteredRuntimes)
                => TryExecute(runtime.CopySelectedRuntimesCommand),
            ToolsViewModel tools when ReferenceEquals(dataGrid.ItemsSource, tools.FilteredTools)
                => TryExecute(tools.CopySelectedToolsCommand),
            PackagesViewModel packages when ReferenceEquals(dataGrid.ItemsSource, packages.FilteredPackages)
                || ReferenceEquals(dataGrid.ItemsSource, packages.SearchView)
                => TryExecute(packages.CopySelectedPackagesCommand),
            CacheViewModel cache when ReferenceEquals(dataGrid.ItemsSource, cache.CategorySummaries)
                => TryExecute(cache.CopySelectedCategorySummariesCommand),
            CacheViewModel cache when ReferenceEquals(dataGrid.ItemsSource, cache.FilteredCandidates)
                => TryExecute(cache.CopySelectedCandidatesCommand),
            _ => false
        };
    }

    private static bool TryExecute(ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            // Markdown inline elements (for example Hyperlink) are ContentElements rather
            // than Visuals. They can become the focused/event source, so walk the WPF content
            // tree first instead of passing them to VisualTreeHelper, which throws.
            current = current switch
            {
                FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
                ContentElement content => ContentOperations.GetParent(content),
                Visual or System.Windows.Media.Media3D.Visual3D =>
                    VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current),
            };
        }

        return null;
    }
}
