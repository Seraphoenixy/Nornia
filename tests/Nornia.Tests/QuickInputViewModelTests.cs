using Nornia.Core.Models;
using Nornia.Desktop;
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

/// <summary>Covers the phase-2 QuickInput overlay: fuzzy filtering, selection movement, confirm
/// execution and open/close semantics — all pure logic, no WPF UI required.</summary>
public sealed class QuickInputViewModelTests
{
    private static QuickInputViewModel Create(params QuickPickItem[] items)
    {
        var vm = new QuickInputViewModel();
        vm.Open("测试", items);
        return vm;
    }

    [Fact]
    public void Open_PopulatesAndSelectsFirst()
    {
        var vm = Create(
            new QuickPickItem("刷新", "视图", Codicons.Refresh, () => { }),
            new QuickPickItem("暂存全部", "源代码管理", Codicons.Add, () => { }));

        Assert.True(vm.IsOpen);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("刷新", vm.SelectedItem?.Title);
        Assert.Equal("测试", vm.Title);
    }

    [Fact]
    public void Filter_MatchesSubstringOfTitleOrDetail()
    {
        var vm = Create(
            new QuickPickItem("显示问题面板", "视图", "", () => { }),
            new QuickPickItem("打开项目目录", "文件", "", () => { }));

        vm.FilterText = "项目";
        Assert.Single(vm.View.Cast<QuickPickItem>());
        Assert.Equal("打开项目目录", vm.View.Cast<QuickPickItem>().Single().Title);

        // Detail 匹配:视图
        vm.FilterText = "视图";
        Assert.Single(vm.View.Cast<QuickPickItem>());
        Assert.Equal("显示问题面板", vm.View.Cast<QuickPickItem>().Single().Title);
    }

    [Fact]
    public void Filter_FuzzySubsequenceMatches()
    {
        var vm = Create(new QuickPickItem("源代码管理", "页面", Codicons.SourceControl, () => { }));

        // 子序列 "代码管" -> 源[代]码[管]理
        vm.FilterText = "代码管";
        Assert.Single(vm.View.Cast<QuickPickItem>());

        // 乱序则过滤掉
        vm.FilterText = "管代码";
        Assert.Empty(vm.View.Cast<QuickPickItem>());
    }

    [Fact]
    public void Confirm_ExecutesSelectedAndCloses()
    {
        var executed = false;
        var vm = Create(new QuickPickItem("命令", "测试", "", () => executed = true));

        vm.ConfirmCommand.Execute(null);

        Assert.True(executed);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void Confirm_WithoutSelectionClosesQuietly()
    {
        var vm = Create();
        vm.ConfirmCommand.Execute(null);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void MoveSelection_WrapsAroundVisibleRows()
    {
        var vm = Create(
            new QuickPickItem("A", "测试", "", () => { }),
            new QuickPickItem("B", "测试", "", () => { }),
            new QuickPickItem("C", "测试", "", () => { }));

        vm.MoveSelection(1);
        Assert.Equal("B", vm.SelectedItem?.Title);
        vm.MoveSelection(1);
        Assert.Equal("C", vm.SelectedItem?.Title);
        vm.MoveSelection(1); // wrap -> A
        Assert.Equal("A", vm.SelectedItem?.Title);
        vm.MoveSelection(-1); // wrap -> C
        Assert.Equal("C", vm.SelectedItem?.Title);
    }

    [Fact]
    public void Filter_KeepsSelectionOnVisibleRow()
    {
        var vm = Create(
            new QuickPickItem("打开项目", "文件", "", () => { }),
            new QuickPickItem("切换主题", "视图", "", () => { }));

        vm.FilterText = "主题";
        Assert.Equal("切换主题", vm.SelectedItem?.Title);
    }

    [Fact]
    public void Close_TogglesIsOpen()
    {
        var vm = Create(new QuickPickItem("A", "测试", "", () => { }));
        vm.Close();
        Assert.False(vm.IsOpen);
    }
}

/// <summary>Covers the phase-2 MainViewModel additions: command-palette content, quick-open file
/// enumeration and status-bar counters. Uses the same lightweight fixture style as MainViewModelTests.</summary>
public sealed partial class MainViewModelQuickInputTests
{
    private static Nornia.Desktop.ViewModels.MainViewModel Create(out FakeUiLogService logs)
    {
        logs = new FakeUiLogService();
        var terminalService = Substitute.For<ITerminalService>();
        terminalService.DiscoverProfiles().Returns([]);
        var settingsService = new FakeSettingsService();
        var workspaceService = new FakeProjectWorkspaceService();
        var settingsEditor = new SettingsEditorViewModel(settingsService, new BuiltInSettingsCatalog(), workspaceService,
            new KeybindingService(new CommandRegistry(), new ContextKeyService(),
                Path.Combine(Path.GetTempPath(), $"nornia-keybindings-{Guid.NewGuid():N}.json")),
            new CommandRegistry(), new FakeApplicationStateStore());
        var settings = new Nornia.Desktop.ViewModels.SettingsViewModel(settingsEditor, logs);
        var navigation = new DesktopNavigationService();
        var clipboard = new FakeClipboardService();
        var runtimeInventory = new FakeRuntimeInventory([]);
        var packageProvider = new FakePackageProvider();
        var packageInventory = new FakePackageInventory([]);
        var resolver = new FakePackageResolver();
        var confirmation = new FakeConfirmationService();
        var dashboard = new DashboardViewModel(runtimeInventory, new FakeSummaryReader(new DashboardSummary(0, 0, 0)),
            new FakeCacheInventory([]), navigation, logs);
        var runtime = new RuntimeViewModel(runtimeInventory, packageProvider, packageInventory, resolver, confirmation, logs);
        var tools = new ToolsViewModel(runtimeInventory, packageProvider, packageInventory, resolver, confirmation, logs);
        var cache = new CacheViewModel(new FakeCacheInventory([]), new FakeCacheCleanup(),
            new CacheClassificationService(), confirmation, logs, new FakeUiDispatcher());
        var packages = new PackagesViewModel(packageProvider, packageInventory, confirmation, logs);
        return new Nornia.Desktop.ViewModels.MainViewModel(
            dashboard, runtime, tools, packages, new ProjectsViewModel(
                new FakeProfileService(), new FakeCheckEngine([]), new FakeRepairPlanner(new EnvironmentRepairPlan([])),
                new FakeRepairExecutor(), new FakeProjectLauncher(), runtimeInventory,
                new FakeProjectCatalog(), new FakeFolderPicker(), confirmation, settingsService, navigation, logs),
            settings, logs, navigation, clipboard);
    }

    [Fact]
    public void BuildPaletteItems_ContainsNavigationThemeAndViewCommands()
    {
        var main = Create(out _);
        var items = main.BuildPaletteItems();

        Assert.Contains(items, i => i.Title == "设置" && i.Detail == "打开页面");
        Assert.Contains(items, i => i.Title.Contains("切换主题（深色）", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Title == "切换侧栏");
        Assert.Contains(items, i => i.Title == "打开项目目录…");
    }

    [Fact]
    public void ProblemCounters_ReflectLoggedErrorsAndWarnings()
    {
        var main = Create(out var logs);
        logs.Write("WARNING", "第一个警告");
        logs.Write("WARNING", "第二个警告");
        logs.Write("ERROR", "一个错误");

        Assert.Equal(1, main.ProblemErrorCount);
        Assert.Equal(2, main.ProblemWarningCount);
    }

    [Fact]
    public void QuickOpenFiles_SkipsBuildArtifactsAndBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "nornia-qopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "a");
        File.WriteAllText(Path.Combine(root, "bin", "out.dll"), "o");
        try
        {
            var files = Nornia.Desktop.ViewModels.MainViewModel.QuickOpenFiles(root).ToList();
            Assert.Contains(files, f => f.EndsWith("A.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(files, f => f.EndsWith("out.dll", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
