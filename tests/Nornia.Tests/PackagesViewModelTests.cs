using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class PackagesViewModelTests
{
    [Fact]
    public async Task UninstallCommand_ConfirmsAndUninstallsSelectedPackage()
    {
        var package = InstalledPackage("Git.Git", "Git");
        var provider = new FakePackageProvider();
        var inventory = new FakePackageInventory([package]);
        var confirmation = new FakeConfirmationService();
        var viewModel = new PackagesViewModel(provider, inventory, confirmation, new FakeUiLogService());

        viewModel.SelectedPackage = package;

        Assert.True(viewModel.UninstallCommand.CanExecute(null));
        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.Equal(["Git.Git"], provider.UninstalledPackages);
        Assert.Equal(1, confirmation.Calls);
        Assert.Equal(1, inventory.ForcedRefreshCalls);
    }

    [Fact]
    public async Task UninstallCommand_DoesNotCallProviderWhenConfirmationIsDeclined()
    {
        var package = InstalledPackage("OpenJS.NodeJS", "Node.js");
        var provider = new FakePackageProvider();
        var inventory = new FakePackageInventory([package]);
        var confirmation = new FakeConfirmationService { Result = false };
        var viewModel = new PackagesViewModel(provider, inventory, confirmation, new FakeUiLogService());

        viewModel.SelectedPackage = package;
        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.Empty(provider.UninstalledPackages);
        Assert.Equal(1, confirmation.Calls);
        Assert.Equal(0, inventory.ForcedRefreshCalls);
    }

    private static PackageInfo InstalledPackage(string id, string name) =>
        new(id, name, "1.0.0", null, "winget", true);
}
