using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Package.Services;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class CacheViewModelTests
{
    private static (CacheViewModel ViewModel, FakeCacheInventory Inventory, FakeCacheCleanup Cleanup, FakeUiLogService Logs) Create(CacheCandidate[] candidates)
    {
        var logs = new FakeUiLogService();
        var cleanup = new FakeCacheCleanup();
        var inventory = new FakeCacheInventory(candidates);
        var viewModel = new CacheViewModel(
            inventory, cleanup, new CacheClassificationService(),
            new FakeConfirmationService { Result = true }, logs, new FakeUiDispatcher());
        return (viewModel, inventory, cleanup, logs);
    }

    private static CacheCandidate[] Fixture() =>
    [
        Candidate("c1", @"C:\Users\Test\AppData\Local\Google\Chrome\User Data\Default\Cache", 100, CacheConfidence.High, @"C:\Users\Test\AppData\Local"),
        Candidate("c2", @"C:\Users\Test\AppData\Local\Google\Chrome\User Data\Profile 1\Code Cache", 200, CacheConfidence.Review, @"C:\Users\Test\AppData\Local"),
        Candidate("c3", @"C:\Users\Test\.nuget\packages", 300, CacheConfidence.High, @"C:\Users\Test"),
        Candidate("c4", @"C:\Users\Test\AppData\Roaming\SomeApp\Cache", 50, CacheConfidence.High, @"C:\Users\Test\AppData\Roaming")
    ];

    [Fact]
    public async Task ActivateAsync_DoesNotScanCache_AfterStartup()
    {
        var (viewModel, inventory, _, _) = Create(Fixture());

        await viewModel.ActivateAsync();
        Assert.Equal(0, inventory.ScanCalls);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(1, inventory.ScanCalls);
        Assert.Equal(4, viewModel.Candidates.Count);
    }

    [Fact]
    public async Task Scan_WritesOneTerminalInfoLogWithoutDirectoryDetails()
    {
        var (viewModel, _, _, logs) = Create(Fixture());

        await viewModel.ScanCommand.ExecuteAsync(null);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal("INFO", entry.Level);
        Assert.Contains("4 个候选", entry.Message);
        Assert.Contains("3 个分类", entry.Message);
        Assert.DoesNotContain(@"C:\Users", entry.Message);
    }

    [Fact]
    public async Task CleanCategory_CleansOnlyCheckedCandidatesInTheCategory()
    {
        var (viewModel, _, cleanup, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);
        var chrome = Assert.Single(viewModel.CategorySummaries, summary => summary.CategoryName == "Google Chrome");

        await viewModel.CleanCategoryCommand.ExecuteAsync(chrome);

        Assert.Contains("c1", cleanup.CleanedIds);
        Assert.DoesNotContain("c2", cleanup.CleanedIds);
        Assert.DoesNotContain("c3", cleanup.CleanedIds);
    }

    [Fact]
    public async Task Scan_AutoSelectsLargestCategoryAndShowsOnlyItsCandidates()
    {
        var (viewModel, _, _, _) = Create(Fixture());

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedCategorySummary);
        Assert.Same(viewModel.CategorySummaries[0], viewModel.SelectedCategorySummary);
        Assert.All(viewModel.FilteredCandidates.Cast<CacheCandidateItem>(), candidate =>
            Assert.Equal(viewModel.SelectedCategorySummary.CategoryKey, candidate.CategoryKey));

        viewModel.SelectedCategorySummary = null;
        Assert.Same(viewModel.CategorySummaries[0], viewModel.SelectedCategorySummary);
    }

    [Fact]
    public async Task CandidateCheck_UpdatesCategoryScopeAndCommandAvailability()
    {
        var (viewModel, _, cleanup, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);
        var chrome = Assert.Single(viewModel.CategorySummaries, summary => summary.CategoryName == "Google Chrome");
        var high = Assert.Single(viewModel.Candidates, candidate => candidate.Id == "c1");
        var review = Assert.Single(viewModel.Candidates, candidate => candidate.Id == "c2");

        Assert.Equal("1/2", chrome.SelectionCountDisplay);
        Assert.Equal("100 B / 300 B", chrome.SelectionSizeDisplay);
        Assert.True(viewModel.CleanCategoryCommand.CanExecute(chrome));

        high.IsSelected = false;
        Assert.Equal("0/2", chrome.SelectionCountDisplay);
        Assert.False(viewModel.CleanCategoryCommand.CanExecute(chrome));

        review.IsSelected = true;
        Assert.Equal("1/2", chrome.SelectionCountDisplay);
        Assert.Equal("200 B / 300 B", chrome.SelectionSizeDisplay);
        await viewModel.CleanCategoryCommand.ExecuteAsync(chrome);

        Assert.Equal(["c2"], cleanup.CleanedIds);
    }

    [Fact]
    public async Task CleanCategory_WritesOneSuccessSummaryWithoutPerDirectoryLogs()
    {
        var (viewModel, _, _, logs) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);
        logs.Clear();
        var chrome = Assert.Single(viewModel.CategorySummaries, summary => summary.CategoryName == "Google Chrome");

        await viewModel.CleanCategoryCommand.ExecuteAsync(chrome);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal("INFO", entry.Level);
        Assert.Contains("分类“Google Chrome”清理完成", entry.Message);
        Assert.DoesNotContain(@"C:\Users", entry.Message);
    }

    [Fact]
    public async Task TypeFilter_FiltersCandidatesByCacheKind()
    {
        var (viewModel, _, _, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.TypeFilter = "代码缓存";

        Assert.Equal("c2", Assert.Single(viewModel.FilteredCandidates.Cast<CacheCandidateItem>()).Id);
    }

    [Fact]
    public async Task SelectedCategorySummary_FiltersCandidatesToThatCategory()
    {
        var (viewModel, _, _, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.SelectedCategorySummary = Assert.Single(viewModel.CategorySummaries, summary => summary.CategoryName == "NuGet");

        Assert.Equal("c3", Assert.Single(viewModel.FilteredCandidates.Cast<CacheCandidateItem>()).Id);
    }

    [Fact]
    public async Task SwitchingCategory_ResetsSecondaryFiltersAndNeverLeavesDetailsBlank()
    {
        var (viewModel, _, _, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);
        var nuget = Assert.Single(viewModel.CategorySummaries, summary => summary.CategoryName == "NuGet");
        var chrome = Assert.Single(viewModel.CategorySummaries, summary => summary.CategoryName == "Google Chrome");

        viewModel.SelectedCategorySummary = nuget;
        viewModel.TypeFilter = "nuget";
        Assert.Equal("c3", Assert.Single(viewModel.FilteredCandidates.Cast<CacheCandidateItem>()).Id);

        viewModel.SelectedCategorySummary = chrome;

        Assert.Equal("全部", viewModel.TypeFilter);
        Assert.Equal("全部", viewModel.ConfidenceFilter);
        Assert.Equal("全部", viewModel.SourceFilter);
        Assert.Equal(2, viewModel.FilteredCandidates.Cast<CacheCandidateItem>().Count());
    }

    [Fact]
    public async Task CategorySummary_ExposesTypeBreakdown()
    {
        var (viewModel, _, _, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);

        var chrome = Assert.Single(viewModel.CategorySummaries, summary => summary.CategoryName == "Google Chrome");

        Assert.Contains("代码缓存(1)", chrome.TypeDisplay);
        Assert.Contains("其他(1)", chrome.TypeDisplay);
    }

    [Fact]
    public async Task BulkSelectionCommands_RefreshEveryCategoryScopeOnceCompleted()
    {
        var (viewModel, _, _, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.All(viewModel.Candidates.Where(candidate => candidate.Confidence == CacheConfidence.High), candidate => Assert.True(candidate.IsSelected));
        Assert.False(Assert.Single(viewModel.Candidates, candidate => candidate.Confidence == CacheConfidence.Review).IsSelected);

        viewModel.ClearSelectionCommand.Execute(null);
        Assert.All(viewModel.Candidates, candidate => Assert.False(candidate.IsSelected));
        Assert.All(viewModel.CategorySummaries, summary => Assert.Equal(0, summary.SelectedCandidateCount));

        viewModel.SelectHighConfidenceCommand.Execute(null);
        Assert.All(viewModel.CategorySummaries, summary =>
            Assert.Equal(
                viewModel.Candidates.Count(candidate => candidate.CategoryKey == summary.CategoryKey && candidate.Confidence == CacheConfidence.High),
                summary.SelectedCandidateCount));
    }

    [Fact]
    public async Task CurrentCategorySelectionCommands_OnlyChangeTheDisplayedCategory()
    {
        var (viewModel, _, _, _) = Create(Fixture());
        await viewModel.ScanCommand.ExecuteAsync(null);
        var chrome = Assert.Single(viewModel.CategorySummaries, summary => summary.CategoryName == "Google Chrome");
        var nuget = Assert.Single(viewModel.Candidates, candidate => candidate.CategoryName == "NuGet");
        viewModel.SelectedCategorySummary = chrome;

        viewModel.ClearCategorySelectionCommand.Execute(null);

        Assert.All(viewModel.Candidates.Where(candidate => candidate.CategoryName == "Google Chrome"), candidate => Assert.False(candidate.IsSelected));
        Assert.True(nuget.IsSelected);
        Assert.Equal(0, chrome.SelectedCandidateCount);

        viewModel.SelectAllInCategoryCommand.Execute(null);

        Assert.All(viewModel.Candidates.Where(candidate => candidate.CategoryName == "Google Chrome"), candidate => Assert.True(candidate.IsSelected));
        Assert.True(nuget.IsSelected);
        Assert.Equal(2, chrome.SelectedCandidateCount);
        Assert.Equal(300, chrome.SelectedSizeBytes);
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

    [Fact]
    public async Task CopySelectedCategorySummaries_JoinsSelectedRows()
    {
        var clipboard = new FakeClipboardService();
        var viewModel = CreateWithClipboard(clipboard);
        await viewModel.ScanCommand.ExecuteAsync(null);
        Assert.True(viewModel.CopySelectedCategorySummariesCommand.CanExecute(null));
        viewModel.CopySelectedCategorySummariesCommand.Execute(null);

        var summary = viewModel.SelectedCategorySummary!;
        Assert.Equal($"{summary.CategoryName}\t{summary.TypeDisplay}\t{summary.SelectionCountDisplay}\t{summary.SelectionSizeDisplay}", clipboard.LastText);
    }

    [Fact]
    public async Task CopySelectedCandidates_JoinsSelectedRowsAndRequiresSelection()
    {
        var clipboard = new FakeClipboardService();
        var viewModel = CreateWithClipboard(clipboard);
        await viewModel.ScanCommand.ExecuteAsync(null);
        var item = viewModel.FilteredCandidates.Cast<CacheCandidateItem>().First();

        viewModel.SelectedCandidates.Add(item);
        viewModel.CopySelectedCandidatesCommand.Execute(null);

        Assert.Equal($"{(item.IsSelected ? "已勾选" : "未勾选")}\t{item.CacheType}\t{item.Confidence}\t{item.Path}\t{item.SizeDisplay}", clipboard.LastText);
    }

    private static CacheViewModel CreateWithClipboard(FakeClipboardService clipboard) => new(
        new FakeCacheInventory(Fixture()), new FakeCacheCleanup(), new CacheClassificationService(),
        new FakeConfirmationService { Result = true }, new FakeUiLogService(), new FakeUiDispatcher(), clipboard);

    private static CacheCandidate Candidate(string id, string path, long size, CacheConfidence confidence, string root) =>
        new(id, "User", path, size, confidence, "cache directory", UserDirectory: root);
}
