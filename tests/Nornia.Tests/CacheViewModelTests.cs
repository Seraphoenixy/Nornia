using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Package.Services;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class CacheViewModelTests
{
    private static (CacheViewModel ViewModel, FakeCacheInventory Inventory, FakeCacheCleanup Cleanup, FakeUiLogService Logs) Create(CacheCandidate[] candidates)
    {
        var logService = new FakeUiLogService();
        var cleanup = new FakeCacheCleanup();
        var packages = new FakePackageRepository
        {
            Packages =
            [
                new PackageInfo("OpenJS.NodeJS", "Node.js", "22.0", null, "winget", true),
                new PackageInfo("Microsoft.DotNet", ".NET", "10.0", null, "winget", true)
            ]
        };
        var inventory = new FakeCacheInventory(candidates);
        var viewModel = new CacheViewModel(
            inventory,
            cleanup,
            packages,
            new CachePackageAssociationService(),
            new FakeConfirmationService { Result = true },
            logService,
            new FakeUiDispatcher());
        return (viewModel, inventory, cleanup, logService);
    }

    private static CacheCandidate[] Fixture() =>
    [
        new("c1", "User", "C:\\Users\\Test\\.npm", 100, CacheConfidence.High, "npm cache", "OpenJS.NodeJS", "Node.js", "winget", "npm"),
        new("c2", "User", "C:\\Users\\Test\\.yarn\\cache", 200, CacheConfidence.Review, "yarn cache", "OpenJS.NodeJS", "Node.js", "winget", "yarn"),
        new("c3", "AppData", "C:\\Users\\Test\\.nuget\\packages", 300, CacheConfidence.High, "nuget cache", "Microsoft.DotNet", ".NET", "winget", "nuget"),
        new("c4", "AppData", "C:\\Users\\Test\\SomeApp\\Cache", 50, CacheConfidence.High, "app cache", null, null, null, "其他")
    ];

    [Fact]
    public async Task ActivateAsync_DoesNotScanCache_AfterStartup()
    {
        // 启动/首次激活不得触发缓存扫描(全盘目录遍历);数据只在“扫描缓存”按钮显式加载。
        var (viewModel, inventory, _, _) = Create(Fixture());

        await viewModel.ActivateAsync();

        Assert.Equal(0, inventory.ScanCalls);
        Assert.Empty(viewModel.Candidates);
        Assert.True(viewModel.IsCandidateListEmpty);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(1, inventory.ScanCalls);
        Assert.Equal(4, viewModel.Candidates.Count);
    }

    [Fact]
    public async Task CleanPackageAsync_GathersAllCandidateIdsOfThePackageIncludingReview()
    {
        var (viewModel, _, cleanup, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);

        var node = Assert.Single(viewModel.PackageSummaries, summary => summary.PackageId == "OpenJS.NodeJS");

        await viewModel.CleanPackageCommand.ExecuteAsync(node);

        Assert.Equal(2, cleanup.CleanedIds.Count);
        Assert.Contains("c1", cleanup.CleanedIds);
        Assert.Contains("c2", cleanup.CleanedIds);
        Assert.DoesNotContain("c3", cleanup.CleanedIds);
    }

    [Fact]
    public async Task TypeFilter_FiltersCandidatesByToolEcosystem()
    {
        var (viewModel, _, _, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.TypeFilter = "npm";

        var filtered = viewModel.FilteredCandidates.Cast<CacheCandidateItem>().ToArray();
        Assert.Single(filtered);
        Assert.Equal("c1", filtered[0].Id);
    }

    [Fact]
    public async Task SelectedPackageSummary_FiltersCandidatesToThatPackage()
    {
        var (viewModel, _, _, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.SelectedPackageSummary = Assert.Single(viewModel.PackageSummaries, summary => summary.PackageId == "Microsoft.DotNet");

        var filtered = viewModel.FilteredCandidates.Cast<CacheCandidateItem>().ToArray();
        var item = Assert.Single(filtered);
        Assert.Equal("c3", item.Id);
    }

    [Fact]
    public async Task PackageSummary_ExposesTypeBreakdown()
    {
        var (viewModel, _, _, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);

        var node = Assert.Single(viewModel.PackageSummaries, summary => summary.PackageId == "OpenJS.NodeJS");

        Assert.Equal("yarn(1) · npm(1)", node.TypeDisplay);
    }

    [Fact]
    public void UserDirectoryLabelFor_MapsKnownSpecialFoldersToFriendlyLabels()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal("AppData\\Local", CacheCandidateItem.UserDirectoryLabelFor(local));
        Assert.Equal("AppData\\Roaming", CacheCandidateItem.UserDirectoryLabelFor(roaming));
        Assert.Equal("用户目录", CacheCandidateItem.UserDirectoryLabelFor(profile));
        Assert.Equal(@"C:\custom\root", CacheCandidateItem.UserDirectoryLabelFor(@"C:\custom\root"));
    }

    // ===== Cache tables copy commands (复制选中 / 复制全部) =====

    private static (CacheViewModel ViewModel, FakeClipboardService Clipboard) CreateWithClipboard(CacheCandidate[] candidates)
    {
        var packages = new FakePackageRepository
        {
            Packages =
            [
                new PackageInfo("OpenJS.NodeJS", "Node.js", "22.0", null, "winget", true),
                new PackageInfo("Microsoft.DotNet", ".NET", "10.0", null, "winget", true)
            ]
        };
        var clipboard = new FakeClipboardService();
        var viewModel = new CacheViewModel(
            new FakeCacheInventory(candidates),
            new FakeCacheCleanup(),
            packages,
            new CachePackageAssociationService(),
            new FakeConfirmationService { Result = true },
            new FakeUiLogService(),
            new FakeUiDispatcher(),
            clipboard: clipboard);
        return (viewModel, clipboard);
    }

    [Fact]
    public async Task CopySelectedPackageSummaries_JoinsSelectedRows()
    {
        var (viewModel, clipboard) = CreateWithClipboard(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);
        Assert.False(viewModel.CopySelectedPackageSummariesCommand.CanExecute(null));

        viewModel.SelectedPackageSummaries.Add(viewModel.PackageSummaries[0]);
        viewModel.SelectedPackageSummaries.Add(viewModel.PackageSummaries[1]);
        Assert.True(viewModel.CopySelectedPackageSummariesCommand.CanExecute(null));

        viewModel.CopySelectedPackageSummariesCommand.Execute(null);

        Assert.Equal(
            string.Join(Environment.NewLine, viewModel.SelectedPackageSummaries.Select(FormatPackageSummaryRow)),
            clipboard.LastText);
    }

    [Fact]
    public async Task CopySelectedCandidates_JoinsSelectedRowsAndRequiresSelection()
    {
        var (viewModel, clipboard) = CreateWithClipboard(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);
        Assert.False(viewModel.CopySelectedCandidatesCommand.CanExecute(null));

        var item = viewModel.FilteredCandidates.Cast<CacheCandidateItem>().First();
        viewModel.SelectedCandidates.Add(item);
        Assert.True(viewModel.CopySelectedCandidatesCommand.CanExecute(null));

        viewModel.CopySelectedCandidatesCommand.Execute(null);

        Assert.Equal(FormatCandidateRow(item), clipboard.LastText);
    }

    private static string FormatPackageSummaryRow(CachePackageSummaryItem summary) =>
        $"{summary.PackageName}\t{summary.PackageId}\t{summary.Provider}\t{summary.CandidateCount}\t{summary.SizeDisplay}";

    private static string FormatCandidateRow(CacheCandidateItem candidate) =>
        $"{candidate.Confidence}\t{candidate.Source}\t{candidate.PackageName}\t{candidate.SizeDisplay}\t{candidate.Path}";
}
