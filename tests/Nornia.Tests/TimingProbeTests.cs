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

public sealed class TimingProbeTests
{
    private static void Step(string name, Stopwatch sw)
    {
        var ms = sw.ElapsedMilliseconds;
        Console.WriteLine($"PROBE {name} {ms}ms");
        sw.Restart();
    }

    [Fact]
    public void ProbeCreationCost()
    {
        var sw = Stopwatch.StartNew();
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

        var dashboard = new DashboardViewModel(runtimeInventory, new FakeSummaryReader(new DashboardSummary(0, 0, 0)),
            new FakeCacheInventory([]), navigation, logService);
        Step("dashboard", sw);
        var runtime = new RuntimeViewModel(runtimeInventory, packageProvider, packageInventory, resolver, confirmation, logService);
        Step("runtime", sw);
        var tools = new ToolsViewModel(runtimeInventory, packageProvider, packageInventory, resolver, confirmation, logService);
        Step("tools", sw);
        var cache = new CacheViewModel(new FakeCacheInventory([]), new FakeCacheCleanup(),
            new CacheClassificationService(), confirmation, logService, new FakeUiDispatcher());
        Step("cache", sw);
        var packages = new PackagesViewModel(packageProvider, packageInventory, confirmation, logService);
        Step("packages", sw);
        var settingsService = new FakeSettingsService();
        var workspaceService = new FakeProjectWorkspaceService();
        var projects = new ProjectsViewModel(new FakeProfileService(), new FakeCheckEngine([]),
            new FakeRepairPlanner(new EnvironmentRepairPlan([])), new FakeRepairExecutor(), new FakeProjectLauncher(),
            runtimeInventory, new FakeProjectCatalog(), new FakeFolderPicker(), confirmation,
            settingsService, navigation, logService);
        Step("projects", sw);
        var terminalService = Substitute.For<ITerminalService>();
        terminalService.DiscoverProfiles().Returns([]);
        var settingsEditor = new SettingsEditorViewModel(settingsService, new BuiltInSettingsCatalog(), workspaceService,
            new KeybindingService(new CommandRegistry(), new ContextKeyService(),
                TestTempRoot.NewFile("keybindings", ".json")),
            new CommandRegistry(), new FakeApplicationStateStore());
        var settings = new SettingsViewModel(settingsEditor, logService);
        Step("settings", sw);
        var main = new MainViewModel(dashboard, runtime, tools, packages, projects, settings, logService, navigation, clipboard);
        Step("main", sw);
        Console.WriteLine("PROBE DONE");
    }

    [Fact]
    public void TrivialNoop()
    {
        Console.WriteLine("NOOP");
    }
}
