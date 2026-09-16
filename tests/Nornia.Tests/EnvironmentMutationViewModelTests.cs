using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

public sealed class EnvironmentMutationViewModelTests
{
    [Fact]
    public async Task RuntimeUpgrade_RescansAndRebindsSelectedRowAfterWingetCompletes()
    {
        var before = Runtime("Visual C++ Redistributable", "14.0");
        var after = Runtime("Visual C++ Redistributable", "14.1");
        var runtimeInventory = new MutableRuntimeInventory([after]);
        var provider = new FakePackageProvider();
        var viewModel = new RuntimeViewModel(
            runtimeInventory,
            provider,
            new FakePackageInventory([]),
            new FakePackageResolver(),
            new FakeConfirmationService(),
            new FakeUiLogService());

        var selected = new ManagedComponentItem(before, "14.1");
        viewModel.Runtimes.Add(selected);
        viewModel.SelectedRuntime = selected;

        await viewModel.UpgradeCommand.ExecuteAsync(null);

        Assert.Single(provider.UpgradedPackages);
        Assert.Equal(1, runtimeInventory.ForcedRefreshCalls);
        Assert.Equal("14.1", viewModel.SelectedRuntime?.Version);
        Assert.Same(viewModel.Runtimes.Single(), viewModel.SelectedRuntime);
    }

    private static CoreRuntime Runtime(string name, string version) =>
        new(Guid.NewGuid(), name, version, $"C:\\{name}\\{version}", "X64", "Test", 0, RuntimeStatus.Installed);

    private sealed class MutableRuntimeInventory(IReadOnlyList<CoreRuntime> refreshResult) : IRuntimeInventoryService
    {
        public int ForcedRefreshCalls { get; private set; }

        public Task<IReadOnlyList<CoreRuntime>> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(refreshResult);

        public Task<IReadOnlyList<CoreRuntime>> RefreshForcedAsync(CancellationToken cancellationToken = default)
        {
            ForcedRefreshCalls++;
            return Task.FromResult(refreshResult);
        }

        public Task<IReadOnlyList<CoreRuntime>> GetPersistedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(refreshResult);
    }
}
