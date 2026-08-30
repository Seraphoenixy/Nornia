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

public sealed class MainViewModelTests
{
    private sealed record Fixture(
        MainViewModel Main,
        FakeUiLogService LogService,
        DesktopNavigationService Navigation,
        FakeClipboardService Clipboard,
        PackagesViewModel Packages);

    private static Fixture Create()
    {
        var logService = new FakeUiLogService();
        var navigation = new DesktopNavigationService();
        var runtimeInventory = new FakeRuntimeInventory(
        [
            FakeRuntimes.Runtime("Visual C++ Redistributable", "14.51"),
            FakeRuntimes.Runtime(".NET", "10.0.100")
        ]);
        var packageProvider = new FakePackageProvider();
        var packageInventory = new FakePackageInventory(
        [
            new PackageInfo("Git.Git", "Git", "2.55.0", "2.56.0", "winget", true)
        ]);
        var resolver = new FakePackageResolver();
        var confirmation = new FakeConfirmationService();
        var clipboard = new FakeClipboardService();

        var dashboard = new DashboardViewModel(
            runtimeInventory,
            new FakeSummaryReader(new DashboardSummary(0, 0, 0)),
            new FakeCacheInventory([]),
            navigation,
            logService);
        var runtime = new RuntimeViewModel(runtimeInventory, packageProvider, packageInventory, resolver, confirmation, logService);
        var tools = new ToolsViewModel(runtimeInventory, packageProvider, packageInventory, resolver, confirmation, logService);
        var cache = new CacheViewModel(
            new FakeCacheInventory([]),
            new FakeCacheCleanup(),
            new CacheClassificationService(),
            confirmation,
            logService,
            new FakeUiDispatcher());
        var packages = new PackagesViewModel(packageProvider, packageInventory, confirmation, logService);
        var settingsService = new FakeSettingsService();
        var workspaceService = new FakeProjectWorkspaceService();
        var projects = new ProjectsViewModel(
            new FakeProfileService(),
            new FakeCheckEngine([]),
            new FakeRepairPlanner(new EnvironmentRepairPlan([])),
            new FakeRepairExecutor(),
            new FakeProjectLauncher(),
            runtimeInventory,
            new FakeProjectCatalog(),
            new FakeFolderPicker(),
            confirmation,
            settingsService,
            navigation,
            logService);
        var terminalService = Substitute.For<ITerminalService>();
        terminalService.DiscoverProfiles().Returns([]);
        var settingsEditor = new SettingsEditorViewModel(settingsService, new BuiltInSettingsCatalog(), workspaceService,
            new KeybindingService(new CommandRegistry(), new ContextKeyService(),
                TestTempRoot.NewFile("keybindings", ".json")),
            new CommandRegistry(), new FakeApplicationStateStore());
        var settings = new SettingsViewModel(settingsEditor, logService);
        var main = new MainViewModel(dashboard, runtime, tools, packages, projects, settings, logService, navigation, clipboard);
        return new Fixture(main, logService, navigation, clipboard, packages);
    }

    [Fact]
    public void Constructor_SelectsFirstNavigationItem()
    {
        var fixture = Create();

        Assert.Same(fixture.Main.NavigationItems[0], fixture.Main.SelectedNavigationItem);
        Assert.Same(fixture.Main.NavigationItems[0].Page, fixture.Main.CurrentPage);
    }

    [Fact]
    public void SidebarWidth_ClampsToUsableRange()
    {
        var fixture = Create();

        fixture.Main.SidebarWidth = 80;
        Assert.Equal(170, fixture.Main.SidebarWidth);

        fixture.Main.SidebarWidth = 9999;
        Assert.Equal(WorkbenchLayoutMetrics.SidebarMaximumWidth, fixture.Main.SidebarWidth);
    }

    [Fact]
    public void SetPanelHeight_ClampsAndSyncsExpandedFlag()
    {
        var fixture = Create();

        fixture.Main.SetPanelHeight(240);
        Assert.True(fixture.Main.IsPanelExpanded);
        Assert.Equal(240, fixture.Main.PanelHeight.Value);

        fixture.Main.SetPanelHeight(0);
        Assert.False(fixture.Main.IsPanelExpanded);
        Assert.Equal(0, fixture.Main.PanelHeight.Value);
    }

    [Fact]
    public void PanelHeight_SurvivesCollapseReopenAndPanelSwitch()
    {
        var fixture = Create();
        fixture.Main.SetPanelHeight(320);

        fixture.Main.TogglePanelCommand.Execute(null);
        Assert.False(fixture.Main.IsPanelExpanded);
        Assert.Equal(0, fixture.Main.PanelHeight.Value);
        Assert.Equal(320, fixture.Main.LastExpandedPanelHeight);

        fixture.Main.SelectPanelCommand.Execute(WorkbenchPanel.Terminal);
        Assert.True(fixture.Main.IsPanelExpanded);
        Assert.Equal(320, fixture.Main.PanelHeight.Value);

        fixture.Main.TogglePanelCommand.Execute(null);
        fixture.Main.TogglePanelCommand.Execute(null);
        Assert.Equal(320, fixture.Main.PanelHeight.Value);
    }

    [Fact]
    public void NavigateByIndex_SelectsMatchingPage()
    {
        var fixture = Create();

        fixture.Main.NavigateByIndexCommand.Execute(2);

        Assert.Equal(fixture.Main.NavigationItems[2].Title, fixture.Main.SelectedNavigationItem!.Title);
    }

    [Fact]
    public void NavigationRequest_SelectsMatchingPageByDestination()
    {
        var fixture = Create();

        fixture.Navigation.Navigate(NavigationTargets.Packages, new NavigationContext.Updates());

        Assert.Equal("软件包", fixture.Main.SelectedNavigationItem!.Title);
    }

    [Fact]
    public void NavigationRequest_UnknownDestinationIsIgnored()
    {
        var fixture = Create();

        fixture.Navigation.Navigate("Not-A-Page");

        Assert.Equal(fixture.Main.NavigationItems[0], fixture.Main.SelectedNavigationItem);
    }

    [Fact]
    public void ErrorEntry_AddsProblemAndAutoSelectsProblemsPanel()
    {
        var fixture = Create();

        fixture.LogService.Write("ERROR", "boom");

        Assert.Equal(1, fixture.Main.ProblemCount);
        Assert.Equal(WorkbenchPanel.Problems, fixture.Main.SelectedPanel);
        Assert.True(fixture.Main.IsPanelExpanded);
    }

    [Fact]
    public void WarningEntries_AreCollectedIntoProblems()
    {
        var fixture = Create();

        fixture.LogService.Write("WARNING", "watch-out");
        fixture.LogService.Write("INFO", "noise");

        Assert.Equal(1, fixture.Main.ProblemCount);
        Assert.Equal("问题 (1)", fixture.Main.ProblemsTabTitle);
    }

    [Fact]
    public void ClearOutput_ClearsLogEntries()
    {
        var fixture = Create();

        fixture.LogService.Write("ERROR", "boom");
        fixture.Main.ClearOutputCommand.Execute(null);

        Assert.Empty(fixture.Main.OutputEntries);
        Assert.Equal(0, fixture.Main.ProblemCount);
    }

    [Fact]
    public void ClearProblems_ClearsProblemMessagesAndStaleSelection()
    {
        var fixture = Create();
        fixture.LogService.Write("ERROR", "boom");
        fixture.Main.SelectedProblemEntries.Add(fixture.Main.ProblemEntries[0]);

        fixture.Main.ClearProblemsCommand.Execute(null);

        Assert.Empty(fixture.Main.ProblemEntries);
        Assert.Empty(fixture.Main.SelectedProblemEntries);
        Assert.Empty(fixture.Main.OutputEntries);
    }

    [Fact]
    public void CopySelectedOutput_WritesFullRowsForMultiSelection()
    {
        var fixture = Create();
        fixture.LogService.Clear();
        fixture.LogService.Write("ERROR", "boom");
        fixture.LogService.Write("INFO", "done");
        fixture.Main.SelectedOutputEntries.Add(fixture.LogService.Entries[0]);
        fixture.Main.SelectedOutputEntries.Add(fixture.LogService.Entries[1]);

        fixture.Main.CopySelectedOutputCommand.Execute(null);

        Assert.Equal(
            string.Join(Environment.NewLine, fixture.LogService.Entries.Select(entry => entry.DisplayText)),
            fixture.Clipboard.LastText);
    }

    [Fact]
    public void CopyOutputMessages_WritesMessageOnly()
    {
        var fixture = Create();
        fixture.LogService.Clear();
        fixture.LogService.Write("ERROR", "boom");
        fixture.Main.SelectedOutputEntries.Add(fixture.LogService.Entries[0]);

        fixture.Main.CopyOutputMessagesCommand.Execute(null);

        Assert.Equal("boom", fixture.Clipboard.LastText);
    }

    [Fact]
    public void CopySelectedProblems_WritesSelectedProblemRows()
    {
        var fixture = Create();
        fixture.LogService.Clear();
        fixture.LogService.Write("WARNING", "watch-out");
        fixture.LogService.Write("ERROR", "boom");
        fixture.Main.SelectedProblemEntries.Add(fixture.LogService.Entries[0]);
        fixture.Main.SelectedProblemEntries.Add(fixture.LogService.Entries[1]);

        fixture.Main.CopySelectedProblemsCommand.Execute(null);

        Assert.Equal(
            string.Join(Environment.NewLine, fixture.Main.ProblemEntries.Select(entry => entry.DisplayText)),
            fixture.Clipboard.LastText);
    }

    [Fact]
    public void CopyAllOutput_IgnoresSearchAndLevelFilters()
    {
        var fixture = Create();
        fixture.LogService.Clear();
        fixture.LogService.Write("ERROR", "boom");
        fixture.LogService.Write("WARNING", "watch-out");
        fixture.LogService.Write("INFO", "noise");
        fixture.Main.PanelSearchText = "missing";
        fixture.Main.PanelLevelFilter = LogPanelLevelFilter.Error;

        fixture.Main.CopyAllOutputCommand.Execute(null);

        Assert.Equal(
            string.Join(Environment.NewLine, fixture.LogService.Entries.Select(entry => entry.DisplayText)),
            fixture.Clipboard.LastText);
    }

    [Fact]
    public void CopyAllProblems_IgnoresSearchAndLevelFilters()
    {
        var fixture = Create();
        fixture.LogService.Clear();
        fixture.LogService.Write("ERROR", "boom");
        fixture.LogService.Write("WARNING", "watch-out");
        fixture.Main.PanelSearchText = "missing";
        fixture.Main.PanelLevelFilter = LogPanelLevelFilter.Error;

        fixture.Main.CopyAllProblemsCommand.Execute(null);

        Assert.Equal(
            string.Join(Environment.NewLine, fixture.Main.ProblemEntries.Select(entry => entry.DisplayText)),
            fixture.Clipboard.LastText);
    }

    [Fact]
    public void CopyCommands_AreDisabledWithoutSelectionOrEntries()
    {
        var fixture = Create();
        fixture.LogService.Clear();

        Assert.False(fixture.Main.CopySelectedOutputCommand.CanExecute(null));
        Assert.False(fixture.Main.CopyOutputMessagesCommand.CanExecute(null));
        Assert.False(fixture.Main.CopySelectedProblemsCommand.CanExecute(null));
        Assert.False(fixture.Main.CopyAllOutputCommand.CanExecute(null));
        Assert.False(fixture.Main.CopyAllProblemsCommand.CanExecute(null));
    }

    [Fact]
    public void ClearOutput_DisablesCopyAll()
    {
        var fixture = Create();
        fixture.LogService.Clear();
        fixture.LogService.Write("ERROR", "boom");
        Assert.True(fixture.Main.CopyAllOutputCommand.CanExecute(null));
        Assert.True(fixture.Main.CopyAllProblemsCommand.CanExecute(null));

        fixture.Main.ClearOutputCommand.Execute(null);

        Assert.False(fixture.Main.CopyAllOutputCommand.CanExecute(null));
        Assert.False(fixture.Main.CopyAllProblemsCommand.CanExecute(null));
    }

    [Fact]
    public void ClearOutput_PrunesStaleSelections()
    {
        var fixture = Create();
        fixture.LogService.Clear();
        fixture.LogService.Write("ERROR", "boom");
        fixture.Main.SelectedOutputEntries.Add(fixture.LogService.Entries[0]);
        fixture.Main.SelectedProblemEntries.Add(fixture.LogService.Entries[0]);

        fixture.Main.ClearOutputCommand.Execute(null);

        Assert.Empty(fixture.Main.SelectedOutputEntries);
        Assert.Empty(fixture.Main.SelectedProblemEntries);
        Assert.False(fixture.Main.CopySelectedOutputCommand.CanExecute(null));
        Assert.False(fixture.Main.CopySelectedProblemsCommand.CanExecute(null));
    }

    // ===== Shared secondary sidebar (Ctrl+B) =====

    [Fact]
    public void SidebarColumn_FollowsPageAndCtrlBToggle()
    {
        var fixture = Create();

        // The first page (任务中心/dashboard) has no sidebar.
        Assert.False(fixture.Main.IsSidebarColumnVisible);
        Assert.Equal(new System.Windows.GridLength(0), fixture.Main.SidebarColumnWidth);

        fixture.Main.NavigateByIndexCommand.Execute(4); // 项目 (has a sidebar)
        Assert.True(fixture.Main.IsSidebarColumnVisible);
        Assert.Equal(new System.Windows.GridLength(300), fixture.Main.SidebarColumnWidth);

        fixture.Main.ToggleSidebarCommand.Execute(null); // Ctrl+B hides it
        Assert.False(fixture.Main.IsSidebarColumnVisible);
        Assert.Equal(new System.Windows.GridLength(0), fixture.Main.SidebarColumnWidth);

        fixture.Main.ToggleSidebarCommand.Execute(null); // Ctrl+B shows it again
        Assert.True(fixture.Main.IsSidebarColumnVisible);
        Assert.Equal(new System.Windows.GridLength(300), fixture.Main.SidebarColumnWidth);
    }

    [Fact]
    public void SidebarColumn_CtrlBHidePersistsAcrossPageSwitches()
    {
        var fixture = Create();
        fixture.Main.NavigateByIndexCommand.Execute(4); // 项目
        fixture.Main.ToggleSidebarCommand.Execute(null); // hidden for the session

        fixture.Main.NavigateByIndexCommand.Execute(0); // 任务中心
        Assert.False(fixture.Main.IsSidebarColumnVisible);

        fixture.Main.NavigateByIndexCommand.Execute(4); // back to 项目: still hidden
        Assert.False(fixture.Main.IsSidebarColumnVisible);
        Assert.Equal(new System.Windows.GridLength(0), fixture.Main.SidebarColumnWidth);
    }

    [Fact]
    public void SidebarColumn_AutoCollapseRestoresOnlyTheResponsiveHide()
    {
        var fixture = Create();
        fixture.Main.NavigateByIndexCommand.Execute(4); // 项目 (has a sidebar)

        fixture.Main.UpdateResponsiveLayout(MainViewModel.CompactLayoutThreshold - 1);
        Assert.True(fixture.Main.IsSidebarAutoCollapsed);
        Assert.False(fixture.Main.IsSidebarColumnVisible);

        fixture.Main.UpdateResponsiveLayout(MainViewModel.CompactLayoutThreshold);
        Assert.False(fixture.Main.IsSidebarAutoCollapsed);
        Assert.True(fixture.Main.IsSidebarColumnVisible);

        // 窄窗口下点击切换(顶栏按钮 / Ctrl+B)应直接打开侧栏:清除自动收起并显式显示。
        fixture.Main.UpdateResponsiveLayout(MainViewModel.CompactLayoutThreshold - 1);
        fixture.Main.ToggleSidebarCommand.Execute(null); // explicit toggle while auto-hidden
        Assert.False(fixture.Main.IsSidebarAutoCollapsed);
        Assert.True(fixture.Main.IsSidebarVisible);
        Assert.True(fixture.Main.IsSidebarColumnVisible);

        // 再次点击 → 显式隐藏;窗口加宽后不复活。
        fixture.Main.ToggleSidebarCommand.Execute(null);
        Assert.False(fixture.Main.IsSidebarVisible);
        fixture.Main.UpdateResponsiveLayout(MainViewModel.CompactLayoutThreshold + 100);
        Assert.False(fixture.Main.IsSidebarAutoCollapsed);
        Assert.False(fixture.Main.IsSidebarColumnVisible);
    }
}
