using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class ToolsViewModelTests
{
    private static ManagedComponentItem Python => new(FakeRuntimes.Runtime("Python", "3.13.2"), null);

    private static ManagedComponentItem Git => new(FakeRuntimes.Runtime("Git", "2.50.1"), null);

    private static ToolExtension Extension(string name, string version, string? latest = null) =>
        new(ToolExtensionEcosystem.Pip, name, version, latest);

    private static (ToolsViewModel ViewModel, FakeToolExtensionInventoryService Extensions, FakeToolExtensionProvider Provider, FakeConfirmationService Confirmation, FakeClipboardService Clipboard) Create(
        params ToolExtension[] extensions)
    {
        var provider = new FakeToolExtensionProvider(ToolExtensionEcosystem.Pip, "pip");
        var service = new FakeToolExtensionInventoryService(provider) { RefreshResult = extensions };
        var confirmation = new FakeConfirmationService();
        var clipboard = new FakeClipboardService();
        var viewModel = new ToolsViewModel(
            new FakeRuntimeInventory([]),
            new FakePackageProvider(),
            new FakePackageInventory([]),
            new FakePackageResolver(),
            confirmation,
            new FakeUiLogService(),
            service,
            clipboard);
        return (viewModel, service, provider, confirmation, clipboard);
    }

    [Fact]
    public void SelectingToolWithEcosystem_ShowsPanelAndPublishesExtensions()
    {
        var (viewModel, service, _, _, _) = Create(Extension("requests", "2.32.3"));

        viewModel.SelectedTool = Python;

        Assert.True(viewModel.ExtensionPanelVisible);
        Assert.Equal("Python 的扩展依赖（pip）", viewModel.ExtensionPanelTitle);
        var extension = Assert.Single(viewModel.Extensions);
        Assert.Equal("requests", extension.Name);
        Assert.Equal("2.32.3", extension.Version);
        Assert.Equal((ToolExtensionEcosystem.Pip, false), Assert.Single(service.RefreshRequests));
    }

    [Fact]
    public void SelectingToolWithoutEcosystem_HidesPanelAndClearsList()
    {
        var (viewModel, service, _, _, _) = Create(Extension("requests", "2.32.3"));

        viewModel.SelectedTool = Python;
        Assert.True(viewModel.ExtensionPanelVisible);

        viewModel.SelectedTool = Git;

        Assert.False(viewModel.ExtensionPanelVisible);
        Assert.Empty(viewModel.Extensions);
        Assert.Single(service.RefreshRequests); // Git 无生态,不再触发加载
    }

    [Fact]
    public void SelectingNoTool_HidesPanel()
    {
        var (viewModel, _, _, _, _) = Create(Extension("requests", "2.32.3"));

        viewModel.SelectedTool = Python;
        Assert.True(viewModel.ExtensionPanelVisible);

        viewModel.SelectedTool = null;

        Assert.False(viewModel.ExtensionPanelVisible);
        Assert.Empty(viewModel.Extensions);
    }

    [Fact]
    public async Task InstallExtensionCommand_InstallsByNameAndForcedRefreshes()
    {
        var (viewModel, service, provider, _, _) = Create(Extension("requests", "2.32.3"));
        viewModel.SelectedTool = Python;

        viewModel.ExtensionSearchName = "flask";
        Assert.True(viewModel.InstallExtensionCommand.CanExecute(null));
        await viewModel.InstallExtensionCommand.ExecuteAsync(null);

        Assert.Equal(("flask", (string?)null), Assert.Single(provider.InstallRequests));
        Assert.Equal((ToolExtensionEcosystem.Pip, true), service.RefreshRequests[^1]);
        Assert.Equal(string.Empty, viewModel.ExtensionSearchName);
    }

    [Fact]
    public void InstallExtensionCommand_DisabledWithoutName()
    {
        var (viewModel, _, _, _, _) = Create(Extension("requests", "2.32.3"));
        viewModel.SelectedTool = Python;

        Assert.False(viewModel.InstallExtensionCommand.CanExecute(null));
    }

    [Fact]
    public async Task UninstallExtensionsCommand_ConfirmsAndUninstallsSelected()
    {
        var requests = Extension("requests", "2.32.3");
        var (viewModel, service, provider, confirmation, _) = Create(requests);
        viewModel.SelectedTool = Python;
        viewModel.SelectedExtensions.Add(requests);

        await viewModel.UninstallExtensionsCommand.ExecuteAsync(null);

        Assert.Equal(["requests"], provider.UninstalledPackages);
        Assert.Equal(1, confirmation.Calls);
        Assert.Equal((ToolExtensionEcosystem.Pip, true), service.RefreshRequests[^1]);
    }

    [Fact]
    public async Task UninstallExtensionsCommand_DeclinedConfirmation_DoesNotUninstall()
    {
        var requests = Extension("requests", "2.32.3");
        var (viewModel, service, provider, confirmation, _) = Create(requests);
        confirmation.Result = false;
        viewModel.SelectedTool = Python;
        viewModel.SelectedExtensions.Add(requests);

        await viewModel.UninstallExtensionsCommand.ExecuteAsync(null);

        Assert.Empty(provider.UninstalledPackages);
        Assert.Equal(1, confirmation.Calls);
        Assert.Single(service.RefreshRequests); // 仅选中工具的初始加载
    }

    [Fact]
    public async Task UpgradeExtensionsCommand_UpgradesSelectedUpdatableExtensions()
    {
        var flask = Extension("flask", "3.0.3", latest: "3.1.0");
        var (viewModel, _, provider, _, _) = Create(flask);
        viewModel.SelectedTool = Python;
        viewModel.SelectedExtensions.Add(flask);

        await viewModel.UpgradeExtensionsCommand.ExecuteAsync(null);

        Assert.Equal(["flask"], provider.UpgradedPackages);
    }

    [Fact]
    public async Task UpgradeAllExtensionsCommand_UpgradesEveryUpdatableExtension()
    {
        var (viewModel, _, provider, _, _) = Create(
            Extension("flask", "3.0.3", latest: "3.1.0"),
            Extension("requests", "2.32.3"));
        viewModel.SelectedTool = Python;

        Assert.True(viewModel.UpgradeAllExtensionsCommand.CanExecute(null));
        await viewModel.UpgradeAllExtensionsCommand.ExecuteAsync(null);

        Assert.Equal(["flask"], provider.UpgradedPackages);
    }

    [Fact]
    public async Task RefreshExtensionsCommand_ForcedRefresh()
    {
        var (viewModel, service, _, _, _) = Create(Extension("requests", "2.32.3"));
        viewModel.SelectedTool = Python;

        await viewModel.RefreshExtensionsCommand.ExecuteAsync(null);

        Assert.Equal((ToolExtensionEcosystem.Pip, true), service.RefreshRequests[^1]);
    }

    [Fact]
    public void ExtensionCommands_DisabledWhilePanelHidden()
    {
        var (viewModel, _, _, _, _) = Create(Extension("requests", "2.32.3"));

        Assert.False(viewModel.RefreshExtensionsCommand.CanExecute(null));
        Assert.False(viewModel.InstallExtensionCommand.CanExecute(null));
        Assert.False(viewModel.UninstallExtensionsCommand.CanExecute(null));
        Assert.False(viewModel.UpgradeExtensionsCommand.CanExecute(null));
        Assert.False(viewModel.UpgradeAllExtensionsCommand.CanExecute(null));
    }

    [Fact]
    public void CopyAllExtensionsCommand_CopiesTabSeparatedRows()
    {
        var (viewModel, _, _, _, clipboard) = Create(
            Extension("requests", "2.32.3"),
            Extension("flask", "3.0.3", latest: "3.1.0"));
        viewModel.SelectedTool = Python;

        viewModel.CopyAllExtensionsCommand.Execute(null);

        Assert.True(viewModel.HasExtensionUpdates);
        Assert.Contains("requests\t2.32.3\t", clipboard.LastText);
        Assert.Contains("flask\t3.0.3\t3.1.0", clipboard.LastText);
    }

    // ===== 依赖关系树 =====

    private static ToolExtensionDependency Dependency(string name, string? constraint = null, bool isLeaf = false) =>
        new(name, null, constraint, isLeaf);

    [Fact]
    public void SelectingExtension_LoadsDependencyRoots()
    {
        var (viewModel, _, provider, _, _) = Create(Extension("requests", "2.32.3"));
        provider.Dependencies["requests"] = [Dependency("certifi"), Dependency("idna", ">=2.5")];
        viewModel.SelectedTool = Python;

        viewModel.SelectedExtension = viewModel.Extensions[0];

        Assert.Equal("requests 的依赖", viewModel.DependencyPanelTitle);
        Assert.Equal(["certifi", "idna"], viewModel.DependencyRoots.Select(node => node.Name));
        Assert.Equal(">=2.5", viewModel.DependencyRoots[1].Constraint);
        Assert.Equal(["requests"], provider.GetDependenciesCalls);
    }

    [Fact]
    public async Task ExpandingDependencyNode_LazilyLoadsChildren()
    {
        var (viewModel, _, provider, _, _) = Create(Extension("requests", "2.32.3"));
        provider.Dependencies["requests"] = [Dependency("certifi")];
        provider.Dependencies["certifi"] = [Dependency("m2crypto", isLeaf: true)];
        viewModel.SelectedTool = Python;
        viewModel.SelectedExtension = viewModel.Extensions[0];

        var certifi = Assert.Single(viewModel.DependencyRoots);
        await viewModel.LoadDependencyNodeAsync(certifi);

        Assert.True(certifi.IsLoaded);
        Assert.Equal("m2crypto", Assert.Single(certifi.Children).Name);
        Assert.Equal(["requests", "certifi"], provider.GetDependenciesCalls);
    }

    [Fact]
    public async Task ExpandingDependencyNode_IsSingleFlight()
    {
        var (viewModel, _, provider, _, _) = Create(Extension("requests", "2.32.3"));
        provider.Dependencies["requests"] = [Dependency("certifi")];
        viewModel.SelectedTool = Python;
        viewModel.SelectedExtension = viewModel.Extensions[0];
        var certifi = Assert.Single(viewModel.DependencyRoots);

        await viewModel.LoadDependencyNodeAsync(certifi);
        await viewModel.LoadDependencyNodeAsync(certifi); // IsLoaded 命中,不再派生

        Assert.Equal(["requests", "certifi"], provider.GetDependenciesCalls);
    }

    [Fact]
    public void SwitchingTool_ClearsDependencyTree()
    {
        var (viewModel, _, provider, _, _) = Create(Extension("requests", "2.32.3"));
        provider.Dependencies["requests"] = [Dependency("certifi")];
        viewModel.SelectedTool = Python;
        viewModel.SelectedExtension = viewModel.Extensions[0];
        Assert.NotEmpty(viewModel.DependencyRoots);

        viewModel.SelectedTool = Git; // 无生态 → 面板与树一起清空

        Assert.False(viewModel.ExtensionPanelVisible);
        Assert.Empty(viewModel.DependencyRoots);
        Assert.Equal("依赖关系", viewModel.DependencyPanelTitle);
    }

    [Fact]
    public void DependencyLoadError_DoesNotClearExtensionList()
    {
        var (viewModel, _, provider, _, _) = Create(Extension("requests", "2.32.3"));
        provider.GetDependenciesException = new InvalidOperationException("boom");
        viewModel.SelectedTool = Python;
        viewModel.SelectedExtension = viewModel.Extensions[0];

        Assert.NotEmpty(viewModel.Extensions);           // 扩展列表不受依赖树错误影响
        Assert.Contains("boom", viewModel.DependencyErrorText);
        Assert.Equal(System.Windows.Visibility.Visible, viewModel.DependencyErrorVisibility);
    }

    [Fact]
    public void DotnetEcosystem_SelectedExtension_ShowsEmptyDependencyTree()
    {
        var provider = new FakeToolExtensionProvider(ToolExtensionEcosystem.DotnetTool, "dotnet-tool");
        var service = new FakeToolExtensionInventoryService(provider)
        {
            RefreshResult = [new ToolExtension(ToolExtensionEcosystem.DotnetTool, "dotnet-ef", "8.0.11", null)],
        };
        var viewModel = new ToolsViewModel(
            new FakeRuntimeInventory([]),
            new FakePackageProvider(),
            new FakePackageInventory([]),
            new FakePackageResolver(),
            new FakeConfirmationService(),
            new FakeUiLogService(),
            service,
            new FakeClipboardService());
        viewModel.SelectedTool = new ManagedComponentItem(FakeRuntimes.Runtime("dotnet", "9.0.1"), null);
        Assert.True(viewModel.ExtensionPanelVisible);

        viewModel.SelectedExtension = viewModel.Extensions[0];

        Assert.Equal("dotnet-ef 的依赖", viewModel.DependencyPanelTitle);
        Assert.Empty(viewModel.DependencyRoots); // dotnet tool 无依赖概念 → 空树
    }
}
