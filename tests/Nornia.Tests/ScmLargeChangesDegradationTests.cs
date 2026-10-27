using System.Collections.Specialized;
using Nornia.Core.Models;
using Nornia.Desktop;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Git;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>大规模变更下的降级与发布契约(G2/G5/G11 的行为证据,全部不依赖墙钟):
/// 条目上限必须真的传到服务并在界面可见;树状视图一次只发布预算内的行,但操作作用范围
/// 仍是完整列表;内容未变的刷新不得发出任何集合通知。</summary>
public sealed class ScmLargeChangesDegradationTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"nornia-scm-{Guid.NewGuid():N}");

    public ScmLargeChangesDegradationTests() => Directory.CreateDirectory(_repoPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repoPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp repository directory.
        }
    }

    private static GitFileChange Change(string path, bool staged) => new(
        path,
        staged ? GitChangeStatus.Modified : GitChangeStatus.Unmodified,
        staged ? GitChangeStatus.Unmodified : GitChangeStatus.Modified);

    private static GitRepositoryStatus StatusOf(
        IReadOnlyList<GitFileChange> unstaged,
        IReadOnlyList<GitFileChange> staged) =>
        new(true, "main", "origin/main", 0, 0, staged, unstaged);

    private static GitRepositoryStatus StatusWithUnstaged(int count) => StatusOf(
        Enumerable.Range(0, count).Select(i => Change($"u{i:D5}.cs", staged: false)).ToArray(),
        [Change("src/staged.cs", staged: true)]);

    private (GitViewModel ViewModel, FakeGitService Git, FakeUiLogService Log) Create(GitRepositoryStatus status)
    {
        var git = new FakeGitService { Status = status };
        var log = new FakeUiLogService();
        var viewModel = new GitViewModel(
            git,
            new FakeFolderPicker { Result = _repoPath },
            new FakeConfirmationService(),
            new FakeTextPromptService(),
            new FakeProjectCatalog(),
            new EditorAreaViewModel(git, log),
            log,
            new FakeClipboardService(),
            new FakeGitRepositoryWatcher(),
            new FakeProjectWorkspaceService(),
            new FakeApplicationStateStore(),
            new FakeSettingsService());
        viewModel.RepositoryPath = _repoPath;
        return (viewModel, git, log);
    }

    [Fact]
    public async Task Refresh_PassesStatusEntryLimitAndSurfacesTruncation()
    {
        var (viewModel, git, log) = Create(StatusWithUnstaged(5));
        viewModel.StatusEntryLimit = 3;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(3, Assert.Single(git.StatusMaximumEntriesRequests));
        Assert.True(viewModel.IsStatusTruncated);
        // 上限是「只列出前 N 条」:总数被裁到 N,提示必须写明 N,用户才不会把前 N 条当成全部变更。
        Assert.Equal(3, viewModel.StagedChanges.Count + viewModel.UnstagedChanges.Count);
        Assert.Contains("3", viewModel.StatusTruncationSummary);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("仅列出前 3 项", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_WithinLimit_KeepsTruncationHintHidden()
    {
        var (viewModel, git, _) = Create(StatusOf([Change("src/u0.cs", staged: false)], []));
        viewModel.StatusEntryLimit = 3;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsStatusTruncated);
        Assert.Equal(string.Empty, viewModel.StatusTruncationSummary);
        Assert.Equal(3, Assert.Single(git.StatusMaximumEntriesRequests));
    }

    [Fact]
    public async Task TreeRenderBudget_LimitsPublishedRowsAndShowMoreExpandsByChunk()
    {
        var (viewModel, _, _) = Create(StatusWithUnstaged(5_000));
        viewModel.ScmLayout = ScmLayout.Tree;
        viewModel.TreeRowRenderLimit = 64;
        viewModel.TreeRowRenderChunk = 32;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 未暂存:5000 个根级文件行 → 只发布 64 行;已暂存区段只有 1 行,不受预算影响。
        Assert.Equal(64, viewModel.UnstagedTreeRows.Count);
        Assert.Equal(4_936, viewModel.HiddenUnstagedRowCount);
        Assert.True(viewModel.ShowMoreUnstagedRowsFooter);
        Assert.Contains("4936", viewModel.MoreUnstagedRowsSummary);

        viewModel.ShowMoreUnstagedRowsCommand.Execute(null);

        Assert.Equal(96, viewModel.UnstagedTreeRows.Count);
        Assert.Equal(4_904, viewModel.HiddenUnstagedRowCount);
    }

    [Fact]
    public async Task TreeRenderBudget_KeepsCommandScopeOnTheFullChangeList()
    {
        // 渲染预算只限制「发布给视图的行数」:命令与选区作用域仍必须是完整集合,
        // 否则批量操作会在病态仓库里悄悄漏掉预算之外的变更。
        var (viewModel, _, _) = Create(StatusWithUnstaged(1_200));
        viewModel.ScmLayout = ScmLayout.Tree;
        viewModel.TreeRowRenderLimit = 64;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1_200, viewModel.UnstagedChanges.Count);
        Assert.Equal(64, viewModel.UnstagedTreeRows.Count);
        Assert.Same(
            Assert.Single(viewModel.UnstagedChanges, item => item.Path == "u00010.cs"),
            (viewModel.UnstagedTreeRows[10] as ScmFileNode)!.Change);

        // 树行预算之外(第 200 项)的变更仍在完整集合里,可被同一命令作用域选中。
        var beyondBudget = Assert.Single(viewModel.UnstagedChanges, item => item.Path == "u00200.cs");
        viewModel.SelectedUnstagedChanges.Add(beyondBudget);
        Assert.True(viewModel.CopyChangePathsCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnchangedRefresh_PublishesZeroCollectionNotifications()
    {
        // 内容相同的静默刷新必须一发都不发:这是「无变化」真的被判成无变化(而不是整表 Reset)的
        // 唯一可观测证据,也是更改栏在 watcher 静默刷新里不重绘的原因。
        var (viewModel, _, _) = Create(StatusWithUnstaged(800));
        viewModel.ScmLayout = ScmLayout.Tree;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        var notifications = 0;
        void Count(object? sender, NotifyCollectionChangedEventArgs e) => notifications++;
        viewModel.UnstagedChanges.CollectionChanged += Count;
        viewModel.StagedChanges.CollectionChanged += Count;
        viewModel.UnstagedTreeRows.CollectionChanged += Count;
        try
        {
            await viewModel.RefreshCommand.ExecuteAsync(null);
        }
        finally
        {
            viewModel.UnstagedChanges.CollectionChanged -= Count;
            viewModel.StagedChanges.CollectionChanged -= Count;
            viewModel.UnstagedTreeRows.CollectionChanged -= Count;
        }

        Assert.Equal(0, notifications);
        Assert.Equal(800, viewModel.UnstagedChanges.Count);
    }

    [Fact]
    public async Task StagedCountDropsToZero_CollapsesStagedSection()
    {
        var (viewModel, git, log) = Create(StatusWithUnstaged(2));

        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsStagedSectionExpanded);

        git.Status = StatusOf(
            [Change("u00000.cs", staged: false), Change("u00001.cs", staged: false)],
            []);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsStagedSectionExpanded,
            $"未折叠: staged={viewModel.StagedChanges.Count} unstaged={viewModel.UnstagedChanges.Count} "
            + $"cap={viewModel.StatusEntryLimit} caps=[{string.Join(",", git.StatusMaximumEntriesRequests)}] "
            + $"statusCalls={git.StatusRequests.Count} phase={viewModel.LastOperationResult?.Phase} "
            + $"log=[{string.Join(" | ", log.Entries.Select(e => e.Message))}]");
    }
}
