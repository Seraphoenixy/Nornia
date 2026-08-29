using System.Diagnostics;
using Nornia.Core.Models;
using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Package.Services;
using Nornia.Project.Models;
using Nornia.Project.Services;
using Nornia.Tests.Fakes;
using NSubstitute;

namespace Nornia.Tests;

/// <summary>Covers the workbench-integrated MainViewModel in the unified mixed-strip model:
/// (1) content pages (环境管理 / 项目管理 / 设置) open as page tabs that mix with document tabs,
/// view pages (资源管理器 / 源代码管理) never create tabs and clear any page-tab selection, and
/// tab ↔ activity-bar selection stays in sync (guarded against loops);
/// (2) OperationPage drives the status-bar operation feedback;
/// (3) the Ctrl+W / Ctrl+PgUp/PgDn routing with the terminal-focus exception.</summary>
public sealed class WorkbenchMainViewModelTests
{
    internal sealed record Fixture(
        MainViewModel Main,
        FakeUiLogService Logs,
        DesktopNavigationService Navigation,
        TerminalViewModel Terminal,
        ITerminalService TerminalService,
        EditorAreaViewModel Editor);

    internal static Fixture Create()
    {
        var logs = new FakeUiLogService();
        var navigation = new DesktopNavigationService();
        var clipboard = new FakeClipboardService();

        var runtimeInventory = new FakeRuntimeInventory([]);
        var packageProvider = new FakePackageProvider();
        var packageInventory = new FakePackageInventory([]);
        var resolver = new FakePackageResolver();
        var confirmation = new FakeConfirmationService();

        var dashboard = new DashboardViewModel(runtimeInventory,
            new FakeSummaryReader(new DashboardSummary(0, 0, 0)),
            new FakeCacheInventory([]), navigation, logs);
        var runtime = new RuntimeViewModel(runtimeInventory, packageProvider, packageInventory, resolver, confirmation, logs);
        var tools = new ToolsViewModel(runtimeInventory, packageProvider, packageInventory, resolver, confirmation, logs);
        var cache = new CacheViewModel(new FakeCacheInventory([]), new FakeCacheCleanup(),
            new FakePackageRepository(), new CachePackageAssociationService(), confirmation, logs, new FakeUiDispatcher());
        var packages = new PackagesViewModel(packageProvider, packageInventory, confirmation, logs, cache);
        var environment = new EnvironmentManagementViewModel(dashboard, runtime, tools, packages, cache, logs, clipboard);

        var settingsService = new FakeSettingsService();
        var workspaceService = new FakeProjectWorkspaceService();
        var terminalService = Substitute.For<ITerminalService>();
        terminalService.DiscoverProfiles().Returns([new ShellProfile("pwsh", "PowerShell 7", "pwsh.exe")]);
        var terminal = new TerminalViewModel(terminalService, settingsService, workspaceService, logs, clipboard);
        var settingsEditor = new SettingsEditorViewModel(settingsService, new BuiltInSettingsCatalog(),
            workspaceService, new KeybindingService(new CommandRegistry(), new ContextKeyService(),
                TestTempRoot.NewFile("keybindings", ".json")),
            new CommandRegistry(), new FakeApplicationStateStore());
        var settings = new SettingsViewModel(settingsEditor, logs);

        var folder = new FakeFolderPicker();
        var catalog = new FakeProjectCatalog();
        var gitService = new FakeGitService();
        var workspace = new WorkspaceViewModel(folder, settingsService, workspaceService, logs, clipboard);
        var editor = new EditorAreaViewModel(gitService, logs);
        var projects = new ProjectsViewModel(
            new FakeProfileService(),
            new FakeCheckEngine([]),
            new FakeRepairPlanner(new EnvironmentRepairPlan([])),
            new FakeRepairExecutor(),
            new FakeProjectLauncher(),
            runtimeInventory,
            catalog,
            folder,
            confirmation,
            settingsService,
            navigation,
            logs,
            clipboard,
            workspaceService);
        var git = new GitViewModel(gitService, folder, confirmation, catalog, editor, logs, clipboard,
            new FakeGitRepositoryWatcher(), workspaceService, new FakeApplicationStateStore(), settingsService);
        var explorer = new ExplorerPageViewModel(workspace, editor, terminal, git, projects, catalog, folder, navigation, logs);

        var main = new MainViewModel(environment, explorer, git, projects, settings, terminal, logs, navigation, clipboard);
        return new Fixture(main, logs, navigation, terminal, terminalService, editor);
    }

    // ===== Startup: construction opens no page tab; the idle preload opens the default =====

    [Fact]
    public void Startup_OpensNoPageTab_ActivityBarStillDefaultsToEnvironment()
    {
        var fixture = Create();

        // 构造期不打开页面标签(壳先行,条带为空);默认页(环境管理)的标签由 PreloadPagesAsync
        // 在首帧后的 ApplicationIdle 续延中补开——测试进程不泵空闲优先级,该续延不执行,
        // 因此这里断言的仍是构造后的即时状态。
        Assert.Empty(fixture.Main.Workbench.Tabs);
        Assert.False(fixture.Main.Workbench.HasTabs);
        Assert.Null(fixture.Main.Workbench.SelectedTab);
        // 活动栏/二级左侧栏仍默认定位环境管理。
        Assert.Same(fixture.Main.NavigationItems[0], fixture.Main.SelectedNavigationItem);
    }

    // ===== Content pages open as tabs; view pages never do =====

    [Fact]
    public void NavigateContentPage_ActivatesExistingTab_NoDuplicates()
    {
        var fixture = Create();

        fixture.Main.NavigateByIndexCommand.Execute(3); // 项目管理
        fixture.Main.NavigateByIndexCommand.Execute(4); // 设置
        fixture.Main.NavigateByIndexCommand.Execute(3); // 项目管理(重复导航)

        // 启动不再自动打开环境管理页:导航仅产生 项目管理 + 设置 两个标签。
        Assert.Equal(2, fixture.Main.Workbench.Tabs.Count);
        Assert.Same(fixture.Main.NavigationItems[3].Page, fixture.Main.CurrentPage);
        var projectsTab = fixture.Main.Workbench.Tabs.Single(tab => ReferenceEquals(tab.Content, fixture.Main.NavigationItems[3].Page));
        Assert.Same(projectsTab, fixture.Main.Workbench.SelectedTab);
    }

    [Fact]
    public void NavigateViewPage_AddsNoTab_ClearsNonDocumentSelection()
    {
        var fixture = Create();
        var tabsBefore = fixture.Main.Workbench.Tabs.Count;

        fixture.Main.NavigateByIndexCommand.Execute(1); // 资源管理器
        fixture.Main.NavigateByIndexCommand.Execute(2); // 源代码管理

        // 视图页永不产生标签。
        Assert.Equal(tabsBefore, fixture.Main.Workbench.Tabs.Count);
        // 页面标签不得处于选中态;无文档时选中清空 → 欢迎层。
        Assert.Null(fixture.Main.Workbench.SelectedTab);
        Assert.True(fixture.Main.ShowViewPageWelcome);
    }

    [Fact]
    public async Task NavigateViewPage_WithDocument_KeepsDocumentSelected()
    {
        var fixture = Create();
        await OpenEditorTabsAsync(fixture, "a.cs");
        var document = Assert.IsType<EditorWorkbenchTab>(fixture.Main.Workbench.SelectedTab);

        fixture.Main.NavigateByIndexCommand.Execute(2); // 源代码管理

        // 活动文档保持选中 → 欢迎层不出现。
        Assert.Same(document, fixture.Main.Workbench.SelectedTab);
        Assert.False(fixture.Main.ShowViewPageWelcome);
    }

    // ===== Tab → activity-bar sync =====

    [Fact]
    public void SelectingPageTab_SyncsActivityBar_WithoutReopening()
    {
        var fixture = Create();
        // 启动不再自动打开环境管理页标签,先显式打开(重复导航不复制)。
        fixture.Main.Workbench.OpenOrActivatePage(fixture.Main.NavigationItems[0].Page);
        fixture.Main.NavigateByIndexCommand.Execute(3); // 项目管理标签打开并选中
        var environmentTab = fixture.Main.Workbench.Tabs.Single(tab => ReferenceEquals(tab.Content, fixture.Main.NavigationItems[0].Page));

        fixture.Main.Workbench.OpenOrActivateTab(environmentTab);

        // 标签→活动栏:高亮所属活动项,页面标签不重复打开。
        Assert.Same(fixture.Main.NavigationItems[0], fixture.Main.SelectedNavigationItem);
        Assert.Same(fixture.Main.NavigationItems[0].Page, fixture.Main.CurrentPage);
        Assert.Equal(2, fixture.Main.Workbench.Tabs.Count);
        Assert.Same(environmentTab, fixture.Main.Workbench.SelectedTab);
    }

    // ===== OperationPage: status-bar feedback source =====

    [Fact]
    public void OperationPage_FollowsSelectionAndEnvironmentSection()
    {
        var fixture = Create();
        var environment = Assert.IsType<EnvironmentManagementViewModel>(fixture.Main.NavigationItems[0].Page);

        // 启动不再自动打开环境管理页标签,先显式打开(反馈源跟随其当前小节)。
        fixture.Main.Workbench.OpenOrActivatePage(environment);

        // 环境页标签选中 → 反馈源是其当前小节页(默认概览)。
        Assert.Same(environment.CurrentPage, fixture.Main.OperationPage);
        Assert.IsType<DashboardViewModel>(fixture.Main.OperationPage);

        // 页内切小节 → 反馈源跟随。
        environment.SelectSection(EnvironmentSection.Runtime);
        Assert.IsType<RuntimeViewModel>(fixture.Main.OperationPage);

        // 其它内容页标签选中 → 反馈源是该页本身。
        fixture.Main.NavigateByIndexCommand.Execute(3); // 项目管理
        Assert.Same(fixture.Main.NavigationItems[3].Page, fixture.Main.OperationPage);

        // 视图页 → 反馈源回落到当前活动栏页。
        fixture.Main.NavigateByIndexCommand.Execute(1); // 资源管理器
        Assert.Same(fixture.Main.CurrentPage, fixture.Main.OperationPage);
    }

    // ===== Ctrl+W routing (terminal-focus exception) =====

    [Fact]
    public async Task CloseShortcut_TerminalFocused_ClosesTerminalSessionOnly()
    {
        var fixture = Create();
        await AddSessionAsync(fixture, "Terminal-A");
        Assert.Single(fixture.Terminal.Sessions);
        var workbenchTabs = fixture.Main.Workbench.Tabs.ToArray();
        var workbenchSelection = fixture.Main.Workbench.SelectedTab;

        fixture.Main.RouteCloseShortcut(terminalFocused: true);

        Assert.Empty(fixture.Terminal.Sessions);
        Assert.Null(fixture.Terminal.SelectedSession);
        // The workbench strip is untouched: same tabs, same selection.
        Assert.Equal(workbenchTabs, fixture.Main.Workbench.Tabs);
        Assert.Same(workbenchSelection, fixture.Main.Workbench.SelectedTab);
    }

    [Fact]
    public async Task CloseShortcut_NotTerminalFocused_ClosesActiveWorkbenchTab()
    {
        var fixture = Create();
        await OpenEditorTabsAsync(fixture, "a.cs", "b.cs", "c.cs"); // c.cs active (取消预览态后共存)

        fixture.Main.RouteCloseShortcut(terminalFocused: false);

        Assert.Empty(fixture.Terminal.Sessions);
        Assert.DoesNotContain(fixture.Main.Workbench.Tabs, tab => tab.Title == "c.cs");
        // Right neighbour absent → left neighbour selected.
        Assert.Equal("b.cs", fixture.Main.Workbench.SelectedTab?.Title);
    }

    // ===== Ctrl+PgUp/PgDn routing (terminal-focus exception) =====

    [Fact]
    public async Task AdjacentShortcut_TerminalFocused_CyclesTerminalSessions()
    {
        var fixture = Create();
        var first = await AddSessionAsync(fixture, "Terminal-A");
        var second = await AddSessionAsync(fixture, "Terminal-B");
        Assert.Same(second, fixture.Terminal.SelectedSession);
        var workbenchSelection = fixture.Main.Workbench.SelectedTab;

        fixture.Main.RouteAdjacentTabShortcut(terminalFocused: true, offset: -1); // B → A

        Assert.Same(first, fixture.Terminal.SelectedSession);
        // The workbench strip is untouched.
        Assert.Same(workbenchSelection, fixture.Main.Workbench.SelectedTab);
    }

    [Fact]
    public async Task AdjacentShortcut_NotTerminalFocused_CyclesWorkbenchTabs()
    {
        var fixture = Create();
        await OpenEditorTabsAsync(fixture, "a.cs", "b.cs", "c.cs");
        var firstDocument = fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().First();
        fixture.Main.Workbench.OpenOrActivateTab(firstDocument); // a.cs active

        fixture.Main.RouteAdjacentTabShortcut(terminalFocused: false, offset: 1);

        Assert.Equal("b.cs", fixture.Main.Workbench.SelectedTab?.Title);
        Assert.Empty(fixture.Terminal.Sessions); // terminal untouched
    }

    // ===== 面板最大化 / 编辑器组命令路由 =====

    [Fact]
    public void TogglePanelMaximize_FlipsMaximizedState()
    {
        var fixture = Create();

        Assert.False(fixture.Main.IsPanelMaximized);
        fixture.Main.TogglePanelMaximizeCommand.Execute(null);
        Assert.True(fixture.Main.IsPanelMaximized);
        fixture.Main.TogglePanelMaximizeCommand.Execute(null);
        Assert.False(fixture.Main.IsPanelMaximized);
    }

    [Fact]
    public async Task SplitEditorGroup_RoutesToGroups_MovesActiveTab()
    {
        var fixture = Create();
        await OpenEditorTabsAsync(fixture, "a.cs", "b.cs"); // b.cs active
        var editor = fixture.Main.Workbench.Editor;
        Assert.Single(editor.Groups.Groups);

        fixture.Main.SplitEditorGroup(EditorSplitOrientation.Vertical);

        Assert.Equal(2, editor.Groups.GroupCount);
        Assert.Equal(EditorSplitOrientation.Vertical, ((EditorSplitNode)editor.Groups.Root).Orientation);
        Assert.Equal("b.cs", editor.Groups.ActiveGroup.SelectedTab?.Name);
        Assert.Same(editor.Groups.ActiveGroup, editor.Groups.GroupsInLayoutOrder[^1]);
    }

    [Fact]
    public async Task FocusNextEditorGroup_RoutesToGroups()
    {
        var fixture = Create();
        await OpenEditorTabsAsync(fixture, "a.cs");
        fixture.Main.Workbench.Editor.Groups.SplitActiveGroup(EditorSplitOrientation.Vertical);
        var first = fixture.Main.Workbench.Editor.Groups.GroupsInLayoutOrder[0];
        fixture.Main.Workbench.Editor.Groups.ActiveGroup = first;

        fixture.Main.FocusNextEditorGroup();

        Assert.Same(fixture.Main.Workbench.Editor.Groups.GroupsInLayoutOrder[1],
            fixture.Main.Workbench.Editor.Groups.ActiveGroup);
    }

    // ===== “终端：新建终端”命令 (Ctrl+`) =====

    [Fact]
    public async Task OpenNewTerminal_ExpandsAndSelectsTerminalPanel_CreatesDefaultSession()
    {
        var fixture = Create();
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"nornia-term-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceDir);
        try
        {
            var session = new TerminalSession(
                new ShellProfile("pwsh", "PowerShell 7", "pwsh.exe"), workspaceDir, new Process());
            fixture.TerminalService
                .StartAsync(Arg.Any<ShellProfile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(session));
            fixture.Terminal.WorkingDirectory = workspaceDir;
            fixture.Main.IsPanelExpanded = false;

            await fixture.Main.OpenNewTerminalCommand.ExecuteAsync(null);

            // 显示底部面板并选中终端标签。
            Assert.True(fixture.Main.IsPanelExpanded);
            Assert.Equal(WorkbenchPanel.Terminal, fixture.Main.SelectedPanel);
            // 用默认 Shell 与工作区目录创建会话并选中。
            var tab = Assert.Single(fixture.Terminal.Sessions);
            Assert.Same(tab, fixture.Terminal.SelectedSession);
            Assert.Equal("pwsh", fixture.Terminal.SelectedProfile?.Id);
            await fixture.TerminalService.Received(1)
                .StartAsync(fixture.Terminal.SelectedProfile!, workspaceDir, Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(workspaceDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    [Fact]
    public async Task OpenNewTerminal_RequestsKeyboardFocusForTheTerminalSurface()
    {
        var fixture = Create();
        var session = new TerminalSession(
            new ShellProfile("pwsh", "PowerShell 7", "pwsh.exe"), Environment.CurrentDirectory, new Process());
        fixture.TerminalService
            .StartAsync(Arg.Any<ShellProfile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));
        var focusRequested = false;
        fixture.Terminal.FocusRequested += (_, _) => focusRequested = true;

        await fixture.Main.OpenNewTerminalCommand.ExecuteAsync(null);

        // 创建会话后视图层收到焦点请求(面板可见时把键盘焦点交给终端窗口)。
        Assert.True(focusRequested);
    }

    // ===== 会话侧栏:状态展示、关闭后相邻选择、空状态 =====

    [Fact]
    public async Task SessionSidebar_ShowsStableTitleAndStatus()
    {
        var fixture = Create();
        var tab = await AddSessionAsync(fixture, "Terminal-A");

        Assert.Equal("Terminal-A", tab.Title);
        Assert.Equal("运行中", tab.StatusText); // 会话运行态 → 可绑定状态文本。
        Assert.False(string.IsNullOrEmpty(tab.StatusGlyph));
    }

    [Fact]
    public async Task CloseMiddleSession_SelectsAdjacentSession()
    {
        var fixture = Create();
        _ = await AddSessionAsync(fixture, "Terminal-A");
        var second = await AddSessionAsync(fixture, "Terminal-B");
        var third = await AddSessionAsync(fixture, "Terminal-C");
        fixture.Terminal.SelectedSession = second;

        await fixture.Terminal.CloseCommand.ExecuteAsync(second);

        Assert.Equal(2, fixture.Terminal.Sessions.Count);
        // 原位置仍占用 → 选中同一位置的相邻会话。
        Assert.Same(third, fixture.Terminal.SelectedSession);
    }

    [Fact]
    public async Task CloseLastSession_SelectsLeftNeighbour()
    {
        var fixture = Create();
        var first = await AddSessionAsync(fixture, "Terminal-A");
        var second = await AddSessionAsync(fixture, "Terminal-B");
        Assert.Same(second, fixture.Terminal.SelectedSession);

        await fixture.Terminal.CloseCommand.ExecuteAsync(second);

        Assert.Same(first, fixture.Terminal.SelectedSession);
    }

    [Fact]
    public async Task CloseAllSessions_LeavesEmptyTerminalState()
    {
        var fixture = Create();
        var only = await AddSessionAsync(fixture, "Terminal-A");

        await fixture.Terminal.CloseCommand.ExecuteAsync(only);

        // 空状态:无会话、无选中,视图显示“新建终端”引导。
        Assert.False(fixture.Terminal.HasSessions);
        Assert.Null(fixture.Terminal.SelectedSession);
    }

    // ===== Shell 下拉切换默认配置文件(已有会话不受影响) =====

    [Fact]
    public async Task ProfileSwitch_PersistsDefaultShell_WithoutTouchingRunningSessions()
    {
        var settingsService = new FakeSettingsService();
        var terminalService = Substitute.For<ITerminalService>();
        terminalService.DiscoverProfiles().Returns([
            new ShellProfile("pwsh", "PowerShell 7", "pwsh.exe"),
            new ShellProfile("cmd", "命令提示符", "cmd.exe")]);
        var logService = new FakeUiLogService();
        var terminal = new TerminalViewModel(terminalService, settingsService, new FakeProjectWorkspaceService(),
            logService, new FakeClipboardService());
        await terminal.ActivateAsync();
        var session = await AddSessionToAsync(terminal, terminalService, "Terminal-A");

        var cmdProfile = terminal.Profiles.ToArray().Single(profile => profile.Id == "cmd");
        terminal.SelectedProfile = cmdProfile;

        // 默认配置已切换并持久化(写入用户作用域);已有会话保持不变(仍用创建时的 Shell)。
        // PersistDefaultShellAsync 在后台异步写入(真实 jsonc 文件读改写);轮询直到设置落盘。
        // 预算给到 5 秒:并行测试负载下本地文件 IO 偶发超过 1 秒(此前因此偶发 flake)。
        var persisted = false;
        for (var i = 0; i < 100; i++)
        {
            var snapshot = await settingsService.GetSnapshotAsync(new SettingsContext());
            if (string.Equals("cmd", snapshot.Effective(BuiltInSettingsCatalog.TerminalDefaultProfile), StringComparison.Ordinal))
            {
                persisted = true;
                break;
            }
            await Task.Delay(50);
        }
        if (!persisted)
        {
            var final = await settingsService.GetSnapshotAsync(new SettingsContext());
            persisted = string.Equals("cmd", final.Effective(BuiltInSettingsCatalog.TerminalDefaultProfile), StringComparison.Ordinal);
        }
        Assert.True(persisted, "Default shell was not persisted within the expected time.");
        Assert.Same(session, Assert.Single(terminal.Sessions));
        Assert.Equal("test-Terminal-A", session.Session.Profile.Id);
    }

    // ===== Helpers =====

    private static async Task OpenEditorTabsAsync(Fixture fixture, params string[] names)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nornia-shortcut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                await File.WriteAllTextAsync(path, "content");
                await fixture.Editor.OpenFileAsync(path);
                // 取消预览态,避免下一个文件打开替换掉它(预览替换语义)。
                fixture.Editor.SelectedTab!.IsPreview = false;
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    private static async Task<TerminalTab> AddSessionAsync(Fixture fixture, string name)
    {
        var session = new TerminalSession(
            new ShellProfile($"test-{name}", name, "pwsh.exe"),
            Environment.CurrentDirectory,
            new Process());
        fixture.TerminalService
            .StartAsync(Arg.Any<ShellProfile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        if (fixture.Terminal.SelectedProfile is null)
        {
            fixture.Terminal.SelectedProfile = new ShellProfile("pwsh", "PowerShell 7", "pwsh.exe");
        }

        await fixture.Terminal.NewTerminalCommand.ExecuteAsync(null);
        return fixture.Terminal.Sessions.Single(tab => tab.Session.Id == session.Id);
    }

    private static async Task<TerminalTab> AddSessionToAsync(TerminalViewModel terminal, ITerminalService terminalService, string name)
    {
        var session = new TerminalSession(
            new ShellProfile($"test-{name}", name, "pwsh.exe"),
            Environment.CurrentDirectory,
            new Process());
        terminalService
            .StartAsync(Arg.Any<ShellProfile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        await terminal.NewTerminalCommand.ExecuteAsync(null);
        return terminal.Sessions.Single(tab => tab.Session.Id == session.Id);
    }
}
