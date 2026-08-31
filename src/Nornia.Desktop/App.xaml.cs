using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Nornia.Composition;
using Nornia.Core;
using Nornia.Core.Interfaces;
using Nornia.Desktop.Code;
using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;
using Nornia.Storage;
using Nornia.Storage.Database;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Nornia.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;
    private InputLatencyTracker? _inputLatencyTracker;

    /// <summary>Shutdown watchdog window: the maximum time the process may spend on cleanup work
    /// (killing child processes, terminal teardown, database drain, service disposal) before exiting
    /// anyway. Every cleanup phase honors this single shared deadline so their latencies never sum
    /// past the window.</summary>
    private const int ShutdownCleanupSeconds = 5;

    private static readonly TimeSpan ShutdownCleanupDeadline = TimeSpan.FromSeconds(ShutdownCleanupSeconds);

    /// <summary>Extra wait past <see cref="ShutdownCleanupDeadline"/> granted to the cleanup task so
    /// it can finish writing its abandonment report (exactly which phase was dropped) before the
    /// process exits.</summary>
    private static readonly TimeSpan ShutdownCleanupGrace = TimeSpan.FromSeconds(2);

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        // Windows 右键菜单"Nornia 打开"传入的路径(目录或文件,取首个确实存在的参数)。
        var contextArg = e.Args.FirstOrDefault(arg => Directory.Exists(arg) || File.Exists(arg));
        // 数据根目录(Roaming):最先创建,后续设置/状态/数据库/日志直接落此目录。
        Directory.CreateDirectory(NorniaPaths.DataDirectory);
        var logDirectory = Path.Combine(NorniaPaths.DataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var sessionLogPath = CreateSessionLogPath(logDirectory);
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File(
                sessionLogPath,
                rollingInterval: RollingInterval.Infinite,
                shared: true)
            .CreateLogger();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Service-layer registrations come from the shared composition root; the desktop only adds
        // its WPF-specific services here.
        _services = new ServiceCollection()
            .AddNorniaServices()
            .AddSingleton<BuiltInSettingsCatalog>()
            .AddSingleton<ISettingsDocumentStore, SettingsDocumentStore>()
            .AddSingleton<ISettingsResolver, SettingsResolver>()
            .AddSingleton<ISettingsChangeCoordinator, SettingsChangeCoordinator>()
            .AddSingleton<ScopedSettingsService>()
            .AddSingleton<ISettingsService>(provider => provider.GetRequiredService<ScopedSettingsService>())
            .AddSingleton<ApplicationStateStore>()
            .AddSingleton<IApplicationStateStore>(provider => provider.GetRequiredService<ApplicationStateStore>())
            .AddSingleton<LegacySettingsMigrator>()
            .AddSingleton<ICommandRegistry, CommandRegistry>()
            .AddSingleton<IContextKeyService, ContextKeyService>()
            .AddSingleton<IKeybindingService, KeybindingService>()
            .AddSingleton<DesktopCommandBootstrapper>()
            .AddSingleton<IUiLogService, UiLogService>()
            .AddSingleton<IUiPerformanceMetrics, UiPerformanceMetrics>()
            .AddSingleton<InputLatencyTracker>()
            .AddSingleton<IClipboardService, WpfClipboardService>()
            .AddSingleton<IFolderPickerService, FolderPickerService>()
            .AddSingleton<IExecutablePickerService, ExecutablePickerService>()
            .AddSingleton<IConfirmationService, ConfirmationService>()
            .AddSingleton<IDesktopNavigationService, DesktopNavigationService>()
            .AddSingleton<IProjectWorkspaceService, ProjectWorkspaceService>()
            // Silent SCM auto-refresh: working-tree watcher feeds GitViewModel (VS Code-style).
            .AddSingleton<IGitRepositoryWatcher, GitRepositoryWatcher>()
            // 源码视图自动刷新:已打开文件的外部变更监听(EditorAreaViewModel 统一 Watch/Unwatch)。
            .AddSingleton<IFileContentWatcher, FileContentWatcher>()
            // 资源管理器树自动刷新:工作区根目录的结构性监听(文件/目录的增删改,VS Code explorer 语义)。
            .AddSingleton<IWorkspaceFileWatcher, WorkspaceFileWatcher>()
            // Read-only code workbench services (file type, decoding, outlines, search).
            .AddSingleton<ICodeFileTypeRegistry, CodeFileTypeRegistry>()
            .AddSingleton<ITextDocumentDecoder, TextDocumentDecoder>()
            .AddSingleton<ICodeOutlineParser, CodeOutlineParser>()
            .AddSingleton<ITextSearchService, TextSearchService>()
            .AddSingleton<IWorkspaceSearchService, WorkspaceSearchService>()
            .AddSingleton<IMarkdownPreviewService>(MarkdownPreviewService.Instance)
            .AddSingleton<DashboardViewModel>()
            .AddSingleton<RuntimeViewModel>()
            .AddSingleton<ToolsViewModel>()
            .AddSingleton<PackagesViewModel>()
            .AddSingleton<CacheViewModel>()
            .AddSingleton<ProjectsViewModel>()
            .AddSingleton<ITerminalService, TerminalService>()
            .AddSingleton<WorkspaceViewModel>()
            .AddSingleton<EditorAreaViewModel>()
            .AddSingleton<IShutdownParticipant>(provider => provider.GetRequiredService<EditorAreaViewModel>())
            .AddSingleton<TerminalViewModel>()
            .AddSingleton<EnvironmentManagementViewModel>()
            .AddSingleton<GitViewModel>()
            .AddSingleton<ExplorerPageViewModel>()
            .AddSingleton<SearchViewModel>()
            .AddSingleton<SettingsEditorViewModel>()
            .AddSingleton<IShutdownParticipant>(provider => provider.GetRequiredService<SettingsEditorViewModel>())
            .AddSingleton<IShutdownCoordinator, ShutdownCoordinator>()
            .AddSingleton<AppearanceSettingsController>()
            .AddSingleton<SettingsViewModel>()
            // MainViewModel retains a legacy constructor for its existing test harness. Resolve
            // the production pages explicitly so DI never has to choose between constructors.
            .AddSingleton<MainViewModel>(provider => new MainViewModel(
                provider.GetRequiredService<EnvironmentManagementViewModel>(),
                provider.GetRequiredService<ExplorerPageViewModel>(),
                provider.GetRequiredService<GitViewModel>(),
                provider.GetRequiredService<ProjectsViewModel>(),
                provider.GetRequiredService<SettingsViewModel>(),
                provider.GetRequiredService<TerminalViewModel>(),
                provider.GetRequiredService<IUiLogService>(),
                provider.GetRequiredService<IDesktopNavigationService>(),
                provider.GetRequiredService<IClipboardService>(),
                provider.GetRequiredService<SearchViewModel>()))
            .AddSingleton<MainWindow>()
            .BuildServiceProvider();

        // Apply the persisted theme and UI font scale before any window is created so the shell
        // resolves correct colors and text sizes from the first frame.
        await _services.GetRequiredService<LegacySettingsMigrator>().MigrateAsync();
        var effectiveSettings = await _services.GetRequiredService<ISettingsService>().GetSnapshotAsync(new());
        var themeName = effectiveSettings.Effective(BuiltInSettingsCatalog.Theme);
        var theme = Enum.TryParse<AppTheme>(themeName, true, out var parsedTheme) ? parsedTheme : AppTheme.Dark;
        var accent = effectiveSettings.Effective(BuiltInSettingsCatalog.Accent);
        ThemeService.Apply(theme);
        ThemeFactory.ApplyAccent(accent);
        UiFontService.Apply(effectiveSettings.Effective(BuiltInSettingsCatalog.UiScale));
        // 系统"减少动画"偏好(VS Code monaco-reduce-motion):关闭时全部微交互动画置零。
        MotionService.ApplySystemMotionPreference();

        // 启动优化:数据库初始化(原生库 + WAL + 迁移)与日志清理移出 Show 关键路径,
        // 窗口先显示、后台继续;首屏读库方(任务中心)以 NorniaDatabase.Initialization 为闸门。
        var databaseReady = InitializeDatabaseAsync();

        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        // Show() normally activates a first window, but the asynchronous startup path and the
        // restored layout can leave it behind the launcher's foreground window. Request
        // activation explicitly while this user-initiated startup still owns foreground rights.
        mainWindow.Activate();
        mainWindow.Focus();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            // Loaded layout restoration can run after Show and change WindowState/placement.
            // A final, non-intrusive activation keeps the initial shell in front without trying
            // to steal focus later in the application's lifetime.
            if (mainWindow.IsVisible && !mainWindow.IsActive)
            {
                mainWindow.Activate();
                mainWindow.Focus();
            }
        }));
        base.OnStartup(e);

        // M9: 窗口已显示,才做旧日志保留清理(枚举+删除 I/O 不再占用启动关键路径)。
        _ = CleanupExpiredLogsAsync(logDirectory);

        // 输入延迟采样(VS Code inputLatency 思路):键/鼠标按下 → 下一渲染帧的延迟持续
        // 采样,让流畅度成为可测量、可回归的指标。空闲时零开销。
        _inputLatencyTracker = _services.GetRequiredService<InputLatencyTracker>();
        _inputLatencyTracker.Attach();

        // 窗口显示后的收尾:外观设置会话(订阅主题/配色的后续实时变更)与数据库初始化。
        // 数据库失败已在 InitializeDatabaseAsync 内处理(写日志 + 弹错 + 退出)。
        await _services.GetRequiredService<AppearanceSettingsController>().StartAsync();
        await databaseReady;

        // 右键菜单"Nornia 打开"传入的路径(窗口与数据库就绪后执行):目录作为项目打开
        // (加载资源管理器/终端/Git,并在项目目录登记);文件不打开项目,仅在编辑器视图打开该文件。
        if (contextArg is not null)
        {
            try
            {
                if (Directory.Exists(contextArg))
                {
                    var explorer = _services.GetRequiredService<ExplorerPageViewModel>();
                    await explorer.OpenProjectPathAsync(contextArg);
                }
                else
                {
                    var editor = _services.GetRequiredService<EditorAreaViewModel>();
                    await editor.OpenFileAsync(contextArg, permanent: true);
                }
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Failed to open path from command line: {Path}", contextArg);
            }
        }
    }

    /// <summary>每次进程启动创建独立日志文件，避免同一天的多次启动继续追加到同一文件。
    /// 旧日志的保留清理(枚举 + 删除,I/O)延后到窗口显示之后(M9:启动关键路径只做必需目录
    /// 与日志文件创建);清理失败不应阻止应用启动。</summary>
    private static string CreateSessionLogPath(string logDirectory)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        return Path.Combine(logDirectory, $"nornia-{stamp}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
    }

    /// <summary>按保留天数清理过期会话日志(窗口显示后 fire-and-forget)。</summary>
    private static Task CleanupExpiredLogsAsync(string logDirectory)
    {
        return Task.Run(() =>
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-NorniaSettings.SerilogFileRetentionDays);
            try
            {
                foreach (var path in Directory.EnumerateFiles(logDirectory, "nornia-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
                        {
                            File.Delete(path);
                        }
                    }
                    catch (IOException)
                    {
                        // An old log may still be held by another process; leave it for a later startup.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Retention cleanup is best-effort and must not block the UI shell.
                    }
                }
            }
            catch (IOException)
            {
                // The logger can still create the new session file if enumeration is temporarily blocked.
            }
            catch (UnauthorizedAccessException)
            {
                // The logger will report any actual file creation failure after configuration.
            }
        });
    }

    /// <summary>后台执行数据库初始化(不阻塞窗口显示)。失败语义与原来一致:
    /// 写日志、UI 输出错误并弹窗提示后退出。</summary>
    private Task InitializeDatabaseAsync()
    {
        var services = _services!; // 调用前刚在 OnStartup 赋值,仅用于消除后台 lambda 内的空值分析告警。
        return Task.Run(async () =>
        {
            using var performance = services.GetRequiredService<IUiPerformanceMetrics>()
                .Begin("database.initialize", phase: "startup");
            try
            {
                await services.GetRequiredService<NorniaDatabase>().InitializeAsync();
            }
            catch (Exception exception)
            {
                Log.Fatal(exception, "Failed to initialize the Nornia database.");
                services.GetService<IUiLogService>()?.Write("ERROR", $"数据库初始化失败：{exception.Message}");
                // 弹错并退出(后台线程 → 投递回 UI 线程)。_ = 与 MainWindow 中 Dispatcher 的
                // fire-and-forget 写法一致,显式丢弃结果以免 CS4014。
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    MessageBox.Show(
                        "数据库初始化失败，应用将退出。详情见日志。",
                        "Nornia",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown(1);
                }));
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _inputLatencyTracker?.Dispose();
        _inputLatencyTracker = null;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        // Watchdog: bound shutdown cleanup to a few seconds. Service disposal and the Serilog
        // flush can block on files locked by another process, which used to leave
        // Nornia.Desktop.exe alive after the window closed — and that residual process then held
        // locks on bin/obj files, breaking subsequent builds/tests. All cleanup phases share this
        // single <see cref="ShutdownCleanupDeadline"/>: terminal teardown and the database drain
        // run in parallel, and both plus the container disposal are cut short at the same deadline,
        // so their latencies never *sum* past the watchdog window. The cleanup task is given a
        // small grace past the deadline so it can always report exactly which phase had to be
        // abandoned before the process exits.
        //
        // The deadline CTS is intentionally not disposed: after the watchdog gives up (or the
        // cleanup completes) the process is about to terminate, and a still-running abandoned
        // phase must never touch a disposed token.
        var services = _services;
        var performanceMetrics = services?.GetService<IUiPerformanceMetrics>() as UiPerformanceMetrics;
        _services = null;
        var deadline = new CancellationTokenSource(ShutdownCleanupDeadline);
        var cleanup = RunShutdownCleanupAsync(services, deadline.Token);
        if (!cleanup.Wait(ShutdownCleanupDeadline + ShutdownCleanupGrace))
        {
            // Only a pathological synchronous hang inside a phase reaches this point; the process
            // exits regardless. Normally the cleanup task aborts its phases at the deadline and
            // reports the abandonment itself.
            Log.Warning("Shutdown cleanup exceeded {BudgetSeconds} seconds; exiting without waiting for the remaining work.",
                ShutdownCleanupSeconds);
        }
        else if (cleanup.Result.Abandoned.Count > 0)
        {
            // The phase summary carries each phase's elapsed time, so the log line itself reveals
            // which phase actually consumed the budget.
            Log.Warning("Shutdown cleanup exceeded {BudgetSeconds} seconds; abandoned: {Phases}. {PhasesSummary}",
                ShutdownCleanupSeconds, string.Join(", ", cleanup.Result.Abandoned), cleanup.Result.PhasesSummary);
        }

        performanceMetrics?.Flush();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>Runs the shutdown phases (kill child processes, dispose terminals, drain the
    /// database, dispose the service container) against the shared deadline and returns the names of
    /// the phases that had to be abandoned when the budget ran out (empty list = clean shutdown) plus
    /// a per-phase timing summary. The summary exists so a log line can tell a genuinely slow
    /// disposal apart from a budget that was already spent by an earlier phase (e.g. a slow
    /// process-tree kill) before disposal even started. Every phase is best-effort: an abandonment
    /// must never keep the process alive.</summary>
    private static async Task<(IReadOnlyList<string> Abandoned, string PhasesSummary)> RunShutdownCleanupAsync(
        ServiceProvider? services, CancellationToken deadline)
    {
        var abandoned = new List<string>();
        var phases = new List<string>(4);
        var stopwatch = Stopwatch.StartNew();
        var afterProcessKill = TimeSpan.Zero;
        var afterParallel = TimeSpan.Zero;

        try
        {
            // Cancel child package/runtime processes before releasing services. This prevents a
            // closing window from leaving a Winget or discovery operation alive in the background.
            // The (potentially multi-second) process-tree kill is confirmed off this thread by
            // ProcessRunner, so it cannot consume the shared cleanup budget.
            services?.GetService<IProcessRunnerShutdown>()?.StopActiveProcesses();
            afterProcessKill = stopwatch.Elapsed;
            phases.Add($"process kill {afterProcessKill.TotalMilliseconds:0.#}ms");

            // Terminal process teardown and database draining do not share state. Running them
            // together keeps the shutdown budget bounded by the slower operation instead of their
            // sum (the old sequential path could spend 1.5s+3s before container disposal began).
            var terminalCleanup = DisposeTerminalAsync(services);
            var databaseCleanup = DrainDatabaseAsync(services, deadline);
            var parallelCutShort = false;
            try
            {
                await Task.WhenAll(terminalCleanup, databaseCleanup).WaitAsync(deadline).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                parallelCutShort = true;
                if (!terminalCleanup.IsCompleted) abandoned.Add("terminal teardown");
                if (!databaseCleanup.IsCompleted) abandoned.Add("database drain");
            }
            afterParallel = stopwatch.Elapsed;
            phases.Add(parallelCutShort
                ? $"terminal teardown + database drain cut off at deadline ({(afterParallel - afterProcessKill).TotalMilliseconds:0.#}ms in)"
                : $"terminal teardown + database drain {(afterParallel - afterProcessKill).TotalMilliseconds:0.#}ms");

            // Container disposal only runs while budget remains. When the deadline expired before
            // it started, report that explicitly instead of blaming the disposal for a pre-spent
            // budget.
            if (deadline.IsCancellationRequested)
            {
                abandoned.Add("service disposal (not started)");
                phases.Add("service disposal not started (budget exhausted by earlier phases)");
            }
            else if (services is not null)
            {
                try
                {
                    // DisposeAsync, not Dispose: IAsyncDisposable singletons (appearance controller,
                    // terminal service, settings sessions) then release without occupying a
                    // thread-pool worker with blocking GetAwaiter().GetResult() waits.
                    await services.DisposeAsync().AsTask().WaitAsync(deadline).ConfigureAwait(false);
                    phases.Add($"service disposal {(stopwatch.Elapsed - afterParallel).TotalMilliseconds:0.#}ms");
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    abandoned.Add("service disposal");
                    phases.Add($"service disposal cut off after {(stopwatch.Elapsed - afterParallel).TotalMilliseconds:0.#}ms");
                }
                catch (Exception exception)
                {
                    // A single service's synchronous Dispose must not prevent log flushing.
                    Log.Warning(exception, "Failed to dispose services during shutdown.");
                }
            }
        }
        catch (Exception exception)
        {
            // Last-resort guard: an unexpected failure inside a phase must still let the process
            // exit and must not corrupt the report returned to the watchdog.
            Log.Warning(exception, "Unexpected failure during shutdown cleanup.");
            abandoned.Add("cleanup");
        }

        return (abandoned, string.Join("; ", phases));
    }

    private static async Task DisposeTerminalAsync(IServiceProvider? services)
    {
        try
        {
            var terminal = services?.GetService<ITerminalService>();
            if (terminal is not null) await terminal.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 终端释放失败不能阻断数据库收尾与容器释放。
            Log.Warning(exception, "Failed to dispose the terminal service during shutdown.");
        }
    }

    private static async Task DrainDatabaseAsync(IServiceProvider? services, CancellationToken deadline)
    {
        // 数据库退出收尾(best-effort,不卡退出):先带超时等在途操作落库,避免把写了一半的
        // 事务直接强杀回滚;再干净关闭连接池——强制 WAL checkpoint,退出后的 nornia.db
        // 单文件即为完整快照,不残留 -wal/-shm 待下次启动恢复。共享的关停预算到期时提前放弃。
        var database = services?.GetService<NorniaDatabase>();
        if (database is null) return;

        try
        {
            if (!await database.DrainAsync(TimeSpan.FromSeconds(3), deadline).ConfigureAwait(false))
            {
                Log.Warning("Database drain timed out after 3 seconds; in-flight operations may roll back.");
            }
            SqliteConnection.ClearAllPools();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // The shared shutdown budget expired while draining; the process is exiting anyway.
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to finalize the database during shutdown.");
        }
    }
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        _services?.GetService<IUiLogService>()?.Write("ERROR", $"未处理的界面异常：{e.Exception.Message}");
        MessageBox.Show(
            "发生未处理的错误。详细信息已写入 Output 面板。",
            "Nornia",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e) =>
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled application exception. IsTerminating: {IsTerminating}", e.IsTerminating);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
