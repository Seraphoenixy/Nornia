using Nornia.Core.Models;
using Nornia.Desktop;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Git;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class GitViewModelTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"nornia-gitvm-{Guid.NewGuid():N}");

    /// <summary>Log fake shared with <see cref="Create"/> so silent-refresh tests can assert
    /// that auto-refreshes stay out of the operation log.</summary>
    private FakeUiLogService Log { get; } = new();

    /// <summary>Watcher fake shared with <see cref="Create"/>.</summary>
    private FakeGitRepositoryWatcher Watcher { get; } = new();

    public GitViewModelTests()
    {
        Directory.CreateDirectory(_repoPath);
    }

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

    private static GitRepositoryStatus SampleStatus() => new(
        IsRepository: true,
        Branch: "main",
        Upstream: "origin/main",
        AheadCount: 2,
        BehindCount: 1,
        StagedChanges:
        [
            new GitFileChange("src/B.cs", GitChangeStatus.Added, GitChangeStatus.Unmodified)
        ],
        UnstagedChanges:
        [
            new GitFileChange("src/A.cs", GitChangeStatus.Unmodified, GitChangeStatus.Modified),
            new GitFileChange("notes/todo.txt", GitChangeStatus.Unmodified, GitChangeStatus.Untracked)
        ]);

    private static GitFileDiff SampleDiff() => new(
        "src/A.cs",
        null,
        false,
        false,
        false,
        [
            new GitDiffHunk(1, 2, 1, 2, "@@ -1,2 +1,2 @@",
            [
                new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep"),
                new GitDiffLine(GitDiffLineKind.Added, null, 2, "added")
            ])
        ]);

    private (GitViewModel ViewModel, FakeGitService Git, FakeSettingsService Settings, FakeConfirmationService Confirmation, FakeClipboardService Clipboard) Create(FakeGitService? gitOverride = null)
    {
        var git = gitOverride ?? new FakeGitService { Status = SampleStatus(), DiffResult = SampleDiff() };
        var settings = new FakeSettingsService();
        var confirmation = new FakeConfirmationService();
        var clipboard = new FakeClipboardService();
        var viewModel = new GitViewModel(
            git,
            new FakeFolderPicker { Result = _repoPath },
            confirmation,
            new FakeProjectCatalog(),
            new EditorAreaViewModel(git, new FakeUiLogService()),
            Log,
            clipboard,
            Watcher,
            new FakeProjectWorkspaceService(),
            new FakeApplicationStateStore(),
            settings);
        return (viewModel, git, settings, confirmation, clipboard);
    }

    [Fact]
    public async Task Refresh_PopulatesChangesBranchesAndLog()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        git.Logs = [new GitCommitInfo("a".PadRight(40, '0'), "aaaaaaa", "initial", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.Branches = [new GitBranchInfo("main", true, "origin/main", 2, 1)];

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsRepository);
        Assert.Single(viewModel.StagedChanges);
        Assert.Equal(2, viewModel.UnstagedChanges.Count);
        Assert.Contains("main", viewModel.RepositorySummary);
        Assert.Contains("领先 2", viewModel.RepositorySummary);
        Assert.Contains("落后 1", viewModel.RepositorySummary);
        Assert.Single(viewModel.Branches);
        Assert.Single(viewModel.Logs);
        Assert.Equal(_repoPath, Assert.Single(git.StatusRequests));
    }

    [Fact]
    public async Task Refresh_EmptyRepositoryWithoutCommits_DegradesToEmptyHistoryInsteadOfFailing()
    {
        // A freshly `git init`'d repo whose branch has no commits yet: `git log` fails with
        // "does not have any commits yet". Previously this bubbled up through
        // ProjectWorkspaceService.PublishAsync → activation as an unobserved Task exception.
        var git = new FakeGitService
        {
            Status = new GitRepositoryStatus(
                IsRepository: true,
                Branch: "main",
                Upstream: null,
                AheadCount: 0,
                BehindCount: 0,
                StagedChanges: [],
                UnstagedChanges: []),
            Branches = [new GitBranchInfo("main", true, null, 0, 0)],
            Logs = [],
            LogException = new GitOperationException(
                "git log failed: fatal: your current branch 'main' does not have any commits yet"),
        };
        var (viewModel, _, _, _, _) = Create(git);
        viewModel.RepositoryPath = _repoPath;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsRepository);
        Assert.Empty(viewModel.Logs);
        Assert.NotNull(viewModel.LastOperationResult);
        Assert.Equal(OperationPhase.Succeeded, viewModel.LastOperationResult!.Phase);
        // The git log failure is surfaced as a warning, not an unhandled/unobserved exception.
        Assert.Contains(Log.Entries, entry => entry.Level == "WARNING" &&
            entry.Message.Contains("加载提交历史失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_NonRepositoryShowsHint()
    {
        var (viewModel, _, _, _, _) = Create(new FakeGitService());
        viewModel.RepositoryPath = _repoPath;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRepository);
        Assert.Empty(viewModel.UnstagedChanges);
        Assert.Equal("未检测到 Git 仓库。", viewModel.RepositorySummary);
    }

    [Fact]
    public async Task InitializeRepository_ReactivatesCurrentProjectAndLoadsGitState()
    {
        var git = new FakeGitService
        {
            Status = GitRepositoryStatus.NotARepository,
            StatusAfterInitialization = SampleStatus(),
        };
        var workspace = new FakeProjectWorkspaceService();
        workspace.ActivateResultFactory = path => new ProjectWorkspaceContext(
            new ProjectAsset(Guid.NewGuid(), "Test", path, ProjectPathStatus.Available, 0, null, null, EnvironmentHealthStatus.Unknown),
            path,
            git.InitializedRepositoryPaths.Count > 0 ? path : null);
        var viewModel = new GitViewModel(
            git,
            new FakeFolderPicker { Result = _repoPath },
            new FakeConfirmationService(),
            new FakeProjectCatalog(),
            new EditorAreaViewModel(git, new FakeUiLogService()),
            Log,
            new FakeClipboardService(),
            Watcher,
            workspace,
            new FakeApplicationStateStore(),
            new FakeSettingsService());

        await workspace.ActivateAsync(_repoPath);
        Assert.True(viewModel.InitializeRepositoryCommand.CanExecute(null));

        await viewModel.InitializeRepositoryCommand.ExecuteAsync(null);

        Assert.Equal(_repoPath, Assert.Single(git.InitializedRepositoryPaths));
        Assert.Equal(new[] { _repoPath, _repoPath }, workspace.ActivatedPaths);
        Assert.True(viewModel.IsRepository);
        Assert.Equal(_repoPath, viewModel.RepositoryPath);
    }

    [Fact]
    public void InitializeRepository_IsUnavailableWithoutAnOpenProject()
    {
        var (viewModel, _, _, _, _) = Create(new FakeGitService());

        Assert.False(viewModel.InitializeRepositoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task InitializeRepository_FailureLeavesWorkspaceAsNonRepository()
    {
        var git = new FakeGitService { InitializeException = new InvalidOperationException("git missing") };
        var workspace = new FakeProjectWorkspaceService();
        workspace.ActivateResultFactory = path => new ProjectWorkspaceContext(
            new ProjectAsset(Guid.NewGuid(), "Test", path, ProjectPathStatus.Available, 0, null, null, EnvironmentHealthStatus.Unknown),
            path,
            null);
        var viewModel = new GitViewModel(
            git,
            new FakeFolderPicker(),
            new FakeConfirmationService(),
            new FakeProjectCatalog(),
            new EditorAreaViewModel(git, new FakeUiLogService()),
            Log,
            new FakeClipboardService(),
            Watcher,
            workspace,
            new FakeApplicationStateStore(),
            new FakeSettingsService());

        await workspace.ActivateAsync(_repoPath);
        await viewModel.InitializeRepositoryCommand.ExecuteAsync(null);

        Assert.Equal(_repoPath, Assert.Single(git.InitializedRepositoryPaths));
        Assert.Single(workspace.ActivatedPaths);
        Assert.False(viewModel.IsRepository);
        Assert.Equal(OperationPhase.Failed, viewModel.LastOperationResult?.Phase);
    }

    // ===== Silent watcher-driven auto-refresh (VS Code SCM) =====

    [Fact]
    public async Task Refresh_AttachesWatcherForHealthyRepository()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(_repoPath, Watcher.AttachedPaths);
    }

    [Fact]
    public async Task Refresh_DetachesWatcherWhenNotARepository()
    {
        var (viewModel, _, _, _, _) = Create(new FakeGitService());
        viewModel.RepositoryPath = _repoPath;
        var detachesBefore = Watcher.DetachCalls;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(Watcher.DetachCalls > detachesBefore);
        Assert.Empty(Watcher.AttachedPaths);
    }

    [Fact]
    public void ChangingRepositoryPath_DetachesWatcher()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        var detachesBefore = Watcher.DetachCalls;

        viewModel.RepositoryPath = Path.Combine(_repoPath, "other");

        Assert.Equal(detachesBefore + 1, Watcher.DetachCalls);
    }

    [Fact]
    public async Task WatcherChange_TriggersExactlyOneSilentRefresh()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        GitRepositoryStatus? refreshed = null;
        viewModel.StatusRefreshed += (_, status) => refreshed = status;
        var statusCallsBefore = git.StatusRequests.Count;
        var logEntriesBefore = Log.Entries.Count;
        var statusMessageBefore = viewModel.StatusMessage;
        var operationBefore = viewModel.CurrentOperation; // the manual refresh's terminal state

        Watcher.RaiseChangesDetected();

        Assert.Equal(statusCallsBefore + 1, git.StatusRequests.Count);
        Assert.NotNull(refreshed);
        Assert.Same(operationBefore, viewModel.CurrentOperation); // silent: no new operation banner
        Assert.Equal(statusMessageBefore, viewModel.StatusMessage);
        Assert.Equal(logEntriesBefore, Log.Entries.Count);
        Assert.True(Watcher.SuppressionWindows > 0);
    }

    [Fact]
    public async Task SilentRefresh_WithIdenticalData_RaisesNoCollectionEvents()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        git.Logs = [new GitCommitInfo("a".PadRight(40, '0'), "aaaaaaa", "initial", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.Branches = [new GitBranchInfo("main", true, "origin/main", 2, 1)];
        await viewModel.RefreshCommand.ExecuteAsync(null);

        // After the silent refresh returns identical data no list may repaint (flicker guard):
        // zero collection events and the same graph row instances.
        var collectionEvents = 0;
        viewModel.StagedChanges.CollectionChanged += (_, _) => collectionEvents++;
        viewModel.UnstagedChanges.CollectionChanged += (_, _) => collectionEvents++;
        viewModel.Logs.CollectionChanged += (_, _) => collectionEvents++;
        viewModel.LogRows.CollectionChanged += (_, _) => collectionEvents++;
        viewModel.Branches.CollectionChanged += (_, _) => collectionEvents++;
        viewModel.Stashes.CollectionChanged += (_, _) => collectionEvents++;
        viewModel.OutgoingCommits.CollectionChanged += (_, _) => collectionEvents++;
        viewModel.IncomingCommits.CollectionChanged += (_, _) => collectionEvents++;
        var rowsBefore = viewModel.LogRows.ToArray();
        var statusCallsBefore = git.StatusRequests.Count;

        Watcher.RaiseChangesDetected();

        Assert.Equal(statusCallsBefore + 1, git.StatusRequests.Count); // the refresh did run
        Assert.Equal(0, collectionEvents);
        Assert.Equal(rowsBefore, viewModel.LogRows);
    }

    [Fact]
    public async Task SilentRefresh_HeadUnchanged_RunsStatusOnly()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        git.Logs = [new GitCommitInfo("a".PadRight(40, '0'), "aaaaaaa", "initial", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.Branches = [new GitBranchInfo("main", true, "origin/main", 2, 1)];
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, git.LogCalls); // the manual refresh ran one full load
        Assert.Equal(1, git.BranchCalls);
        Assert.Equal(1, git.StashListCalls);
        var statusCallsBefore = git.StatusRequests.Count;

        Watcher.RaiseChangesDetected();

        // HEAD did not move (no .git in the temp tree → signature stays null): the quiet
        // refresh stays status-only and skips branches / history / stashes entirely.
        Assert.Equal(statusCallsBefore + 1, git.StatusRequests.Count);
        Assert.Equal(1, git.LogCalls);
        Assert.Equal(1, git.BranchCalls);
        Assert.Equal(1, git.StashListCalls);
    }

    [Fact]
    public async Task SilentRefresh_HeadMoved_EscalatesToFullReload()
    {
        // Real .git/HEAD + ref files inside the temp worktree drive the HEAD signature.
        var gitDirectory = Path.Combine(_repoPath, ".git");
        Directory.CreateDirectory(Path.Combine(gitDirectory, "refs", "heads"));
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), "ref: refs/heads/main");
        var referenceFile = Path.Combine(gitDirectory, "refs", "heads", "main");
        File.WriteAllText(referenceFile, new string('a', 40));

        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        git.Logs = [new GitCommitInfo("a".PadRight(40, '0'), "aaaaaaa", "initial", null, "A", "a@x", DateTimeOffset.UtcNow)];
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, git.LogCalls);

        File.WriteAllText(referenceFile, new string('b', 40)); // an external commit moved HEAD

        Watcher.RaiseChangesDetected();

        Assert.Equal(2, git.LogCalls); // escalated to a full reload
        Assert.Equal(2, git.BranchCalls);
    }

    // ===== G7: 折叠区段不发 git 进程,展开后懒加载补齐 =====

    private static async Task WaitUntilAsync(Func<bool> condition, string failure, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(condition(), failure);
    }

    [Fact]
    public async Task CollapsedGraphSection_SkipsLogAndRemoteProcesses_LazyLoadsOnExpand()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        git.Logs = [new GitCommitInfo("a".PadRight(40, '0'), "aaaaaaa", "initial", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.Branches = [new GitBranchInfo("main", true, "origin/main", 2, 1)];
        // 首次打开:图视图默认展开 → 历史照常加载(打开仓库时 VM 会强制展开图分区)。
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, git.LogCalls);
        Assert.Equal(2, git.OutgoingCommitsCalls + git.IncomingCommitsCalls); // 上游存在 → 传入/传出照常拉取

        // 用户折叠图分区后再次全量重载:log / 传入传出 子进程整段跳过,结果保留旧数据。
        viewModel.IsGraphViewExpanded = false;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, git.LogCalls);
        Assert.Equal(2, git.OutgoingCommitsCalls + git.IncomingCommitsCalls);
        Assert.Single(viewModel.Logs); // 折叠期间列表保留上次内容

        // 展开 → 懒加载补齐历史。
        viewModel.IsGraphViewExpanded = true;

        await WaitUntilAsync(() => git.LogCalls == 2, "展开图分区后应懒加载一次提交历史");
        Assert.Single(viewModel.Logs);
        Assert.Equal(4, git.OutgoingCommitsCalls + git.IncomingCommitsCalls);
    }

    [Fact]
    public async Task CollapsedChangesView_SkipsStashProcess_LazyLoadsOnExpand()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        git.Logs = [new GitCommitInfo("a".PadRight(40, '0'), "aaaaaaa", "initial", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.Stashes = [new GitStashInfo(0, "wip")];
        viewModel.IsChangesViewExpanded = false;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, git.StashListCalls); // 更改视图折叠 → stash 子进程跳过
        Assert.Equal(1, git.LogCalls); // 图视图仍展开 → 历史照常加载

        viewModel.IsChangesViewExpanded = true;

        await WaitUntilAsync(() => git.StashListCalls == 1, "展开更改视图后应懒加载一次贮藏列表");
        Assert.Single(viewModel.Stashes);
    }

    [Fact]
    public void WatcherChange_WhileBusy_DefersUntilOperationCompletes()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        viewModel.IsBusy = true;
        var statusCallsBefore = git.StatusRequests.Count;

        Watcher.RaiseChangesDetected();

        Assert.Equal(statusCallsBefore, git.StatusRequests.Count);

        viewModel.IsBusy = false;

        Assert.Equal(statusCallsBefore + 1, git.StatusRequests.Count);
    }

    [Fact]
    public async Task WatcherChanges_WhileRefreshInFlight_MergeIntoOneFollowUp()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var git = new FakeGitService { Status = SampleStatus(), DiffResult = SampleDiff(), StatusGate = gate.Task };
        var (viewModel, _, _, _, _) = Create(git);
        viewModel.RepositoryPath = _repoPath;

        Watcher.RaiseChangesDetected(); // starts the in-flight refresh (blocked on the gate)
        Watcher.RaiseChangesDetected(); // merges into the pending slot
        Watcher.RaiseChangesDetected(); // still merges (no second flight)

        Assert.Single(git.StatusRequests);

        gate.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (git.StatusRequests.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(2, git.StatusRequests.Count); // in-flight + exactly one merged follow-up
    }

    [Fact]
    public async Task WatcherChanges_MidFlightCatchUp_LandsWithinSnappyWindow()
    {
        // 更改栏"未及时监听到更改"的回归防线:落在静默刷新在途期间(或刚结束后的冷却窗内)的
        // 变更,其补刷必须在"一个最小间隔 + 一次状态读取"内落地。旧的 2000ms 最小间隔让这条链
        // 最长达到 3-4s;上限取 1900ms —— 新值(500ms)下最坏路径约 1.5s,旧值下最短也要 2000ms,
        // 二者可区分。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var git = new FakeGitService { Status = SampleStatus(), DiffResult = SampleDiff(), StatusGate = gate.Task };
        var (viewModel, _, _, _, _) = Create(git);
        viewModel.RepositoryPath = _repoPath;

        Watcher.RaiseChangesDetected(); // in-flight, blocked on the gate
        Watcher.RaiseChangesDetected(); // merges into the pending slot
        Assert.Single(git.StatusRequests);

        gate.SetResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (git.StatusRequests.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        stopwatch.Stop();

        Assert.Equal(2, git.StatusRequests.Count);
        Assert.True(stopwatch.ElapsedMilliseconds < 1900,
            $"mid-flight catch-up took {stopwatch.ElapsedMilliseconds}ms; expected < 1900ms (one minimum interval + one status)");
    }

    [Fact]
    public async Task WatcherBurstsInsideMinimumInterval_CoalesceToOneDeferredRefresh()
    {
        // G1 (VS Code throttle + post-completion cooldown): a burst that lands inside the minimum
        // interval after the last silent refresh must not start a fresh status; it is recorded as
        // pending and run exactly once, when the interval elapses.
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        var before = git.StatusRequests.Count;

        Watcher.RaiseChangesDetected(); // first burst: no prior silent refresh → runs immediately
        Assert.Equal(before + 1, git.StatusRequests.Count);

        Watcher.RaiseChangesDetected(); // inside the cooldown window
        Watcher.RaiseChangesDetected(); // still inside the window — merges into the same pending slot
        Assert.Equal(before + 1, git.StatusRequests.Count);

        var deadline = DateTime.UtcNow.AddSeconds(GitViewModel.QuietRefreshMinimumIntervalMs / 1000 + 3);
        while (git.StatusRequests.Count < before + 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(before + 2, git.StatusRequests.Count); // exactly one merged catch-up refresh
    }

    [Fact]
    public void PostOperationCatchUp_BypassesMinimumInterval()
    {
        // A silent refresh just completed (cooldown active). A watcher burst that arrives while a
        // user operation is busy must catch up the moment the operation finishes — not after the
        // cooldown, so the UI never lags a completed user operation by the throttle window.
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        var before = git.StatusRequests.Count;

        Watcher.RaiseChangesDetected(); // silent refresh #1 (immediate: none before it)
        Assert.Equal(before + 1, git.StatusRequests.Count);

        viewModel.IsBusy = true;
        Watcher.RaiseChangesDetected(); // recorded as pending while busy
        Assert.Equal(before + 1, git.StatusRequests.Count);

        viewModel.IsBusy = false; // operation completes → catch-up refresh bypasses the cooldown
        Assert.Equal(before + 2, git.StatusRequests.Count);
    }

    [Fact]
    public async Task ConfiguredConstructor_RefreshesRepository()
    {
        var git = new FakeGitService { Status = SampleStatus(), DiffResult = SampleDiff() };
        var viewModel = new GitViewModel(
            git,
            new FakeFolderPicker { Result = _repoPath },
            new FakeConfirmationService(),
            new FakeProjectCatalog(),
            new EditorAreaViewModel(git, new FakeUiLogService()),
            new FakeUiLogService(),
            new FakeClipboardService(),
            new FakeGitRepositoryWatcher(),
            new FakeProjectWorkspaceService(),
            new FakeApplicationStateStore(),
            new FakeSettingsService());
        viewModel.RepositoryPath = _repoPath;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsRepository);
        Assert.Equal(2, viewModel.UnstagedChanges.Count);
    }

    [Fact]
    public async Task SelectingChange_RaisesDiffOpenRequested()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        GitDiffRequest? requested = null;
        viewModel.DiffOpenRequested += (_, request) => requested = request;
        viewModel.SelectedUnstagedChange = viewModel.UnstagedChanges.First(change => change.Path == "src/A.cs");

        Assert.NotNull(requested);
        Assert.Equal("src/A.cs", requested!.Path);
        Assert.Equal(_repoPath, requested.RepositoryPath);
        Assert.False(requested.IsStaged);
        Assert.False(requested.IsUntracked);
        Assert.True(requested.IsPreview); // 更改页单击 = 预览 diff(VS Code SCM 语义)
    }

    [Fact]
    public async Task Commit_RequiresStagedChangesAndMessage()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.CanCommit); // no message yet
        viewModel.CommitMessage = "fix: something";
        Assert.True(viewModel.CanCommit);

        await viewModel.CommitCommand.ExecuteAsync(null);

        Assert.Equal("fix: something", Assert.Single(git.CommitMessages));
        Assert.Equal(string.Empty, viewModel.CommitMessage);
    }

    [Fact]
    public async Task DiscardChange_RequiresConfirmation()
    {
        var (viewModel, git, _, confirmation, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var change = viewModel.UnstagedChanges[0];
        confirmation.Result = false;
        viewModel.DiscardChangeCommand.Execute(change);
        await WaitUntilAsync(() => viewModel.LastOperationResult is not null);
        Assert.Empty(git.DiscardedPaths);

        confirmation.Result = true;
        await viewModel.DiscardChangeCommand.ExecuteAsync(change);
        Assert.Contains(change.Path, git.DiscardedPaths);
    }

    [Fact]
    public async Task StageAndUnstageAll_RouteToService()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.StageAllCommand.CanExecute(null));
        await viewModel.StageAllCommand.ExecuteAsync(null);
        Assert.Contains("*", git.StagedPaths);

        Assert.True(viewModel.UnstageAllCommand.CanExecute(null));
        await viewModel.UnstageAllCommand.ExecuteAsync(null);
        Assert.Contains("*", git.UnstagedPaths);
    }

    [Fact]
    public async Task StageChange_RefreshesStatusOnlyAndKeepsMetadataCached()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        var statusRequests = git.StatusRequests.Count;
        var branchCalls = git.BranchCalls;
        var logCalls = git.LogCalls;
        var stashListCalls = git.StashListCalls;
        var change = viewModel.UnstagedChanges[0];

        await viewModel.StageChangeCommand.ExecuteAsync(change);

        Assert.Equal(statusRequests + 1, git.StatusRequests.Count);
        Assert.Equal(branchCalls, git.BranchCalls);
        Assert.Equal(logCalls, git.LogCalls);
        Assert.Equal(stashListCalls, git.StashListCalls);
        Assert.True(Watcher.OperationSuppressions > 0);
    }

    [Fact]
    public async Task PerRowButtons_EnableByTheirOwnItemNotSelection()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var staged = viewModel.StagedChanges[0];
        var unstaged = viewModel.UnstagedChanges[0];
        viewModel.SelectedUnstagedChange = null; // regression: buttons must not depend on the selection

        Assert.True(viewModel.UnstageChangeCommand.CanExecute(staged));
        Assert.False(viewModel.UnstageChangeCommand.CanExecute(unstaged));
        Assert.True(viewModel.StageChangeCommand.CanExecute(unstaged));
        Assert.False(viewModel.StageChangeCommand.CanExecute(staged));
        // 丢弃只允许未暂存内容(已暂存需先取消暂存,不能直接丢弃)
        Assert.False(viewModel.DiscardChangeCommand.CanExecute(staged));
        Assert.True(viewModel.DiscardChangeCommand.CanExecute(unstaged));
    }

    [Fact]
    public async Task UnstageChange_ExecutesWithoutPriorSelection()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        var staged = viewModel.StagedChanges[0];
        viewModel.SelectedStagedChange = null;

        await viewModel.UnstageChangeCommand.ExecuteAsync(staged);

        Assert.Contains(staged.Path, git.UnstagedPaths);
    }

    [Fact]
    public async Task OpenRepository_PicksFolderAndRefreshesStatus()
    {
        var (viewModel, git, _, _, _) = Create();

        // 目录选择器返回仓库路径后激活项目工作区;工作区上下文把仓库路径供给 SCM 面板(等价于 ApplyWorkspaceContextAsync)。
        viewModel.OpenRepositoryCommand.Execute(null);
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(_repoPath, viewModel.RepositoryPath);
        Assert.Contains(_repoPath, git.StatusRequests);
    }

    [Fact]
    public async Task ExpandingLogRow_LoadsFilesOnceAndFileClickRaisesDiffOpenRequested()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        git.Logs = [new GitCommitInfo("b".PadRight(40, '0'), "bbbbbbb", "docs", null, "A", "a@x", DateTimeOffset.UtcNow)];
        await viewModel.RefreshCommand.ExecuteAsync(null);
        git.CommitFiles = [new GitFileChange("docs/readme.md", GitChangeStatus.Modified, GitChangeStatus.Unmodified)];

        var row = viewModel.LogRows[^1];
        Assert.False(row.IsExpanded);
        Assert.Equal(0, git.CommitFileListCalls); // 未展开不请求文件清单(懒加载)

        viewModel.ToggleLogRowCommand.Execute(row);

        await WaitUntilAsync(() => row.IsLoaded);
        Assert.True(row.IsExpanded);
        Assert.Single(row.Files);
        Assert.Equal(1, git.CommitFileListCalls); // 同一提交只加载一次
        Assert.Equal([row.Commit.Hash], git.CommitFileListRequests);

        // 折叠再展开:不再请求(缓存)
        viewModel.ToggleLogRowCommand.Execute(row);
        Assert.False(row.IsExpanded);
        viewModel.ToggleLogRowCommand.Execute(row);
        Assert.True(row.IsExpanded);
        Assert.Equal(1, git.CommitFileListCalls);

        GitDiffRequest? requested = null;
        viewModel.DiffOpenRequested += (_, request) => requested = request;
        viewModel.OpenLogFileDiffCommand.Execute(row.Files[0]);

        Assert.NotNull(requested);
        Assert.Equal("docs/readme.md", requested!.Path);
        Assert.Equal(row.Commit.Hash, requested.CommitHash);
        Assert.False(requested.IsPreview); // 历史页的提交文件 diff 非预览(与更改页单击区分)
    }

    [Fact]
    public async Task SelectingInOneChangeList_KeepsSelectionsIndependent()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var unstaged = viewModel.UnstagedChanges.First(change => change.Path == "src/A.cs");
        viewModel.SelectedUnstagedChange = unstaged;

        // The two change lists must keep independent selections (a single shared TwoWay SelectedItem
        // would have the staged list push null back and clear the unstaged row highlight).
        Assert.Same(unstaged, viewModel.SelectedUnstagedChange);
        Assert.Null(viewModel.SelectedStagedChange);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }

    // ===== Collapsible-section state (session-only, mirrors the change counts until the user toggles) =====

    [Fact]
    public async Task SectionState_InitialExpansionFollowsChangeCounts()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsUnstagedSectionExpanded);   // 2 unstaged changes
        Assert.True(viewModel.IsStagedSectionExpanded);     // 1 staged change
        Assert.False(viewModel.IsBranchesSectionExpanded);  // branches start collapsed
        Assert.True(viewModel.IsLogSectionExpanded);        // log starts expanded
    }

    [Fact]
    public async Task ToggleSection_KeepsUserChoiceAcrossRefresh()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsUnstagedSectionExpanded);

        viewModel.ToggleSectionCommand.Execute("unstaged");
        Assert.False(viewModel.IsUnstagedSectionExpanded);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsUnstagedSectionExpanded); // user collapse survives refresh
    }

    [Fact]
    public async Task StagedSection_AutoCollapsesWhenStagedCountDropsToZero()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsStagedSectionExpanded);

        git.Status = SampleStatus() with { StagedChanges = [] };
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsStagedSectionExpanded);
    }

    [Fact]
    public async Task ToggleLogRow_EmptyCommitShowsNoFilesPlaceholder()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        git.Logs = [new GitCommitInfo("a".PadRight(40, '0'), "aaaaaaa", "root", null, "A", "a@x", DateTimeOffset.UtcNow)];
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var row = viewModel.LogRows[^1];
        Assert.False(row.HasNoFiles);
        viewModel.ToggleLogRowCommand.Execute(row);
        await WaitUntilAsync(() => row.IsLoaded);

        Assert.True(row.IsExpanded);
        Assert.True(row.IsLoaded);
        Assert.True(row.HasNoFiles); // 空提交(根提交)展开后显示"无文件更改"占位
        Assert.Empty(row.Files);
    }

    [Fact]
    public void LogRowToolTipText_ContainsAuthorTimeAndFullMessage()
    {
        var commit = new GitCommitInfo(
            "a".PadRight(40, '0'), "aaaaaaa", "fix: subject", "body line 1\nbody line 2",
            "张三", "zhang@example.com", new DateTimeOffset(2024, 6, 1, 12, 30, 0, TimeSpan.FromHours(8)));
        var row = new GitLogRow(commit, null);

        var text = row.ToolTipText;
        Assert.Contains("提交: aaaaaaa", text);
        Assert.Contains("提交人: 张三 <zhang@example.com>", text);
        Assert.Contains("提交时间: 2024-06-01 12:30", text);
        Assert.Contains("fix: subject", text);
        Assert.Contains("body line 1", text);
        Assert.Contains("body line 2", text);

        // %s 会把没有空行分隔的标题和列表折叠为一行；悬浮窗应优先显示 %B 原文。
        var rawMessage = "fix: subject\n- body line 1\n- body line 2";
        var rawRow = new GitLogRow(commit with { Message = rawMessage }, null);
        Assert.Equal(rawMessage, rawRow.FullMessage);
        Assert.EndsWith(rawMessage, rawRow.ToolTipText);

        // 无 body 时提示不追加空行
        var withoutBody = new GitLogRow(commit with { Body = null }, null).ToolTipText;
        Assert.DoesNotContain("\n\n", withoutBody.TrimEnd('\n'));
    }

    [Fact]
    public void LogRow_FormatsCommitAgeAndTagBadge()
    {
        var now = new DateTimeOffset(2025, 1, 10, 12, 0, 0, TimeSpan.FromHours(8));
        var commit = new GitCommitInfo(
            "a".PadRight(40, '0'), "aaaaaaa", "release", null, "张三", "zhang@example.com",
            now.AddDays(-2), Refs: [new GitRefInfo("v1.2.0", GitRefKind.Tag)]);
        var row = new GitLogRow(commit, null);

        Assert.Equal("2 天前", GitLogRow.FormatCommitAge(commit.AuthorDate, now));
        Assert.Equal("v1.2.0", Assert.Single(row.RefBadges).Name);
        Assert.Equal(Codicons.Tag, row.RefBadges[0].Glyph);
        Assert.Equal("InfoAccentBrush", row.RefBadges[0].ColorKey);
        Assert.Contains("距今", row.ToolTipText);
    }

    // ===== Multi-select + clipboard copy commands =====

    [Fact]
    public async Task CopyChangePaths_JoinsMultiSelectionWithoutTrailingNewline()
    {
        var (viewModel, _, _, _, clipboard) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.False(viewModel.CopyChangePathsCommand.CanExecute(null));

        viewModel.SelectedUnstagedChanges.Add(viewModel.UnstagedChanges[0]);
        viewModel.SelectedUnstagedChanges.Add(viewModel.UnstagedChanges[1]);
        Assert.True(viewModel.CopyChangePathsCommand.CanExecute(null));

        viewModel.CopyChangePathsCommand.Execute(null);
        Assert.Equal(string.Join(Environment.NewLine, viewModel.UnstagedChanges.Select(item => item.Path)), clipboard.LastText);
    }

    [Fact]
    public async Task CopyCommands_ShareThePlainTextRowFormattingRules()
    {
        var (viewModel, git, _, _, clipboard) = Create();
        viewModel.RepositoryPath = _repoPath;
        git.Logs = [new GitCommitInfo("a".PadRight(40, '0'), "aaaaaaa", "initial", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.Branches = [new GitBranchInfo("main", true, "origin/main", 2, 1)];
        git.CommitFiles = [new GitFileChange("src/A.cs", GitChangeStatus.Added, GitChangeStatus.Unmodified)];
        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.SelectedBranches.Add(viewModel.Branches[0]);
        viewModel.CopyBranchNamesCommand.Execute(null);
        Assert.Equal("main", clipboard.LastText);

        // LogRows 顶部还有 传入/传出的更改 两行同步折叠栏;提交行取最后一行
        viewModel.SelectedCommits.Add(viewModel.LogRows[^1]);
        viewModel.CopyCommitHashesCommand.Execute(null);
        Assert.Equal("a".PadRight(40, '0'), clipboard.LastText);

        viewModel.CopyCommitSubjectsCommand.Execute(null);
        Assert.Equal("initial", clipboard.LastText);

        // 展开行内文件:复制单个文件路径
        viewModel.CopyLogFilePathCommand.Execute(new LogFileRow(viewModel.LogRows[^1], git.CommitFiles[0]));
        Assert.Equal("src/A.cs", clipboard.LastText);
    }

    // ===== Selected-rows git operations: ONE git call, CanExecute bound to the selection =====

    [Fact]
    public async Task StageSelectedChanges_StagesAllPathsInOneCall()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.SelectedUnstagedChanges.Add(viewModel.UnstagedChanges[0]);
        viewModel.SelectedUnstagedChanges.Add(viewModel.UnstagedChanges[1]);
        Assert.True(viewModel.StageSelectedChangesCommand.CanExecute(null));

        await viewModel.StageSelectedChangesCommand.ExecuteAsync(null);
        Assert.Equal(1, git.StageCalls);
        Assert.Equal(viewModel.UnstagedChanges.Select(item => item.Path).ToArray(), git.StagedPaths);
    }

    [Fact]
    public async Task UnstageSelectedChanges_OneCallForSelectedStagedRows()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.False(viewModel.UnstageSelectedChangesCommand.CanExecute(null));

        viewModel.SelectedStagedChanges.Add(viewModel.StagedChanges[0]);
        Assert.True(viewModel.UnstageSelectedChangesCommand.CanExecute(null));

        await viewModel.UnstageSelectedChangesCommand.ExecuteAsync(null);
        Assert.Equal(1, git.UnstageCalls);
        Assert.Equal(["src/B.cs"], git.UnstagedPaths);
    }

    [Fact]
    public async Task DiscardSelectedChanges_ConfirmsOnceAndCallsGitOnce()
    {
        var (viewModel, git, _, confirmation, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.False(viewModel.DiscardSelectedChangesCommand.CanExecute(null));

        // 丢弃只作用于未暂存选中;单独选中已暂存项时命令不可用。
        viewModel.SelectedStagedChanges.Add(viewModel.StagedChanges[0]);
        Assert.False(viewModel.DiscardSelectedChangesCommand.CanExecute(null));
        viewModel.SelectedStagedChanges.Clear();

        viewModel.SelectedUnstagedChanges.Add(viewModel.UnstagedChanges[0]);

        confirmation.Result = false;
        await viewModel.DiscardSelectedChangesCommand.ExecuteAsync(null);
        Assert.Equal(0, git.DiscardCalls);

        confirmation.Result = true;
        await viewModel.DiscardSelectedChangesCommand.ExecuteAsync(null);
        Assert.Equal(1, git.DiscardCalls);
        Assert.Equal([viewModel.UnstagedChanges[0].Path], git.DiscardedPaths);
    }

    // ===== Change-row display derivations (VS Code resource label: name + directory + status) =====

    [Fact]
    public void ChangeItem_ExposesFileNameAndRelativeDirectoryForRowLayout()
    {
        var item = new GitChangeItem(new GitFileChange("src/deep/A.cs", GitChangeStatus.Unmodified, GitChangeStatus.Modified));

        Assert.Equal("A.cs", item.FileName);
        Assert.Equal("src/deep", item.RelativeDirectory);
    }

    [Fact]
    public void ChangeItem_RootLevelFileHasEmptyRelativeDirectory()
    {
        var item = new GitChangeItem(new GitFileChange("README.md", GitChangeStatus.Added, GitChangeStatus.Unmodified));

        Assert.Equal("README.md", item.FileName);
        Assert.Equal(string.Empty, item.RelativeDirectory);
        Assert.Equal("A", item.StatusLetter);
    }

    [Fact]
    public void ChangeItem_PartiallyStagedDeletionShowsTheCorrectStatusPerSection()
    {
        var change = new GitFileChange("docs/guide.md", GitChangeStatus.Added, GitChangeStatus.Deleted);
        var staged = new GitChangeItem(change, IsStagedSection: true);
        var unstaged = new GitChangeItem(change, IsStagedSection: false);

        Assert.Equal("A", staged.StatusLetter);
        Assert.True(staged.IsStaged);
        Assert.Equal("D", unstaged.StatusLetter);
        Assert.False(unstaged.IsStaged);
    }

    [Fact]
    public void ChangeItem_RenameExposesSuffixAndKeepsDisplayPath()
    {
        var item = new GitChangeItem(new GitFileChange("src/New.cs", GitChangeStatus.Renamed, GitChangeStatus.Unmodified, "src/Old.cs"));

        Assert.Equal("New.cs", item.FileName);
        Assert.Equal("src", item.RelativeDirectory);
        Assert.Equal("src/Old.cs", item.RenameSuffix);
        Assert.Equal(Codicons.ArrowLeft, item.RenameSuffixGlyph);
        Assert.Equal("src/New.cs（原路径：src/Old.cs）", item.DisplayPath);
    }

    // ===== Branch / sync status next to the commit box (VS Code SCM) =====

    [Fact]
    public async Task Refresh_PopulatesBranchSyncStatusFromStatus()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("main", viewModel.CurrentBranch);
        Assert.Equal(2, viewModel.AheadCount);
        Assert.Equal(1, viewModel.BehindCount);
        Assert.True(viewModel.NeedsSync);
    }

    [Fact]
    public async Task Refresh_NonRepositoryResetsBranchSyncStatus()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("main", viewModel.CurrentBranch);

        var (empty, _, _, _, _) = Create(new FakeGitService());
        empty.RepositoryPath = _repoPath;
        await empty.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, empty.CurrentBranch);
        Assert.Equal(0, empty.AheadCount);
        Assert.Equal(0, empty.BehindCount);
        Assert.False(empty.NeedsSync);
    }

    [Fact]
    public async Task Fetch_RefreshesTheGraphStateWithoutChangingTheWorktree()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        await viewModel.FetchCommand.ExecuteAsync(null);

        Assert.Equal(1, git.FetchCalls);
        Assert.Empty(git.StagedPaths);
        Assert.Empty(git.DiscardedPaths);
    }

    [Fact]
    public async Task NonRepository_CollapsesGraphViewUntilARepositoryIsLoaded()
    {
        var (viewModel, _, _, _, _) = Create(new FakeGitService());
        viewModel.RepositoryPath = _repoPath;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsGraphViewExpanded);
        Assert.True(viewModel.IsChangesViewExpanded);
    }

    // ===== Commit split menu: 提交并推送 / 提交并同步 =====

    [Fact]
    public async Task CommitAndPush_CommitsThenPushesAndClearsMessage()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.CommitMessage = "release: v1";

        Assert.True(viewModel.CommitAndPushCommand.CanExecute(null));
        await viewModel.CommitAndPushCommand.ExecuteAsync(null);

        Assert.Equal("release: v1", Assert.Single(git.CommitMessages));
        Assert.Equal(1, git.PushCalls);
        Assert.Equal(0, git.PullCalls);
        Assert.Equal(string.Empty, viewModel.CommitMessage);
    }

    [Fact]
    public async Task CommitAndSync_CommitsPullsThenPushes()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.CommitMessage = "feat: sync";

        await viewModel.CommitAndSyncCommand.ExecuteAsync(null);

        Assert.Equal("feat: sync", Assert.Single(git.CommitMessages));
        Assert.Equal(1, git.PullCalls);
        Assert.Equal(1, git.PushCalls);
        Assert.Equal(string.Empty, viewModel.CommitMessage);
    }

    [Fact]
    public async Task CommitVariants_FollowCommitCanExecute()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.CommitAndPushCommand.CanExecute(null)); // 没有消息
        viewModel.CommitMessage = "msg";
        Assert.True(viewModel.CommitAndPushCommand.CanExecute(null));
        Assert.True(viewModel.CommitAndSyncCommand.CanExecute(null));
    }

    // ===== Tree layout (VS Code "view as tree") =====

    [Fact]
    public void BuildTreeRows_GroupsByDirectoryWithSortedIndentedRows()
    {
        GitChangeItem Change(string path) =>
            new(new GitFileChange(path, GitChangeStatus.Unmodified, GitChangeStatus.Modified));

        var rows = GitViewModel.BuildTreeRows(
        [
            Change("src/A.cs"),
            Change("notes/todo.txt"),
            Change("README.md"),
        ]).ToArray();

        // 根级文件与目录按名称混排(OrdinalIgnoreCase):notes < README.md < src
        var notes = Assert.IsType<ScmFolderNode>(rows[0]);
        Assert.Equal("notes", notes.Name);
        Assert.Equal(0, notes.Depth);
        Assert.Equal("notes/todo.txt", Assert.IsType<ScmFileNode>(rows[1]).Change.Path);
        Assert.Equal(1, rows[1].Depth);
        Assert.Equal("README.md", Assert.IsType<ScmFileNode>(rows[2]).Change.Path);
        Assert.Equal(0, rows[2].Depth);
        Assert.Equal("src", Assert.IsType<ScmFolderNode>(rows[3]).FolderPath);
        Assert.Equal("src/A.cs", Assert.IsType<ScmFileNode>(rows[4]).Change.Path);
        Assert.Equal(1, rows[4].Depth);
    }

    [Fact]
    public void BuildTreeRows_CollapsedFolderHidesChildren()
    {
        GitChangeItem Change(string path) =>
            new(new GitFileChange(path, GitChangeStatus.Unmodified, GitChangeStatus.Modified));

        var rows = GitViewModel.BuildTreeRows([Change("src/deep/A.cs")], new HashSet<string> { "src" }).ToArray();

        var folder = Assert.Single(rows.OfType<ScmFolderNode>());
        Assert.Equal("src", folder.FolderPath);
        Assert.True(folder.IsCollapsed);
        Assert.Empty(rows.OfType<ScmFileNode>());
    }

    [Fact]
    public async Task Refresh_BuildsTreeRowsForBothChangeLists()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 未暂存:notes/ + src/ 两个目录、两个文件
        Assert.Equal(["notes", "src"], viewModel.UnstagedTreeRows.OfType<ScmFolderNode>().Select(node => node.Name));
        Assert.Equal(["notes/todo.txt", "src/A.cs"], viewModel.UnstagedTreeRows.OfType<ScmFileNode>().Select(node => node.Change.Path));
        // 已暂存:src/ 一个目录、一个文件
        Assert.Equal(["src"], viewModel.StagedTreeRows.OfType<ScmFolderNode>().Select(node => node.Name));
        Assert.Equal(["src/B.cs"], viewModel.StagedTreeRows.OfType<ScmFileNode>().Select(node => node.Change.Path));
    }

    [Fact]
    public async Task ToggleScmLayout_SwitchesLayoutFlags()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsListLayout);
        viewModel.ToggleScmLayoutCommand.Execute(null);
        Assert.True(viewModel.IsTreeLayout);
        Assert.False(viewModel.IsListLayout);
    }

    [Fact]
    public async Task ViewExpansion_DefaultsToExpandedForBothViews()
    {
        var (viewModel, _, _, _, _) = Create();
        Assert.True(viewModel.IsChangesViewExpanded);
        Assert.True(viewModel.IsGraphViewExpanded);
    }

    [Fact]
    public async Task ToggleSection_ChangesView_TogglesIsChangesViewExpanded()
    {
        var (viewModel, _, _, _, _) = Create();

        viewModel.ToggleSectionCommand.Execute("changesView");
        Assert.False(viewModel.IsChangesViewExpanded);
        Assert.True(viewModel.IsGraphViewExpanded); // 两视图独立折叠,互不影响

        viewModel.ToggleSectionCommand.Execute("changesView");
        Assert.True(viewModel.IsChangesViewExpanded);
    }

    [Fact]
    public async Task ToggleSection_GraphView_TogglesIsGraphViewExpanded()
    {
        var (viewModel, _, _, _, _) = Create();

        viewModel.ToggleSectionCommand.Execute("graphView");
        Assert.False(viewModel.IsGraphViewExpanded);
        Assert.True(viewModel.IsChangesViewExpanded);

        viewModel.ToggleSectionCommand.Execute("graphView");
        Assert.True(viewModel.IsGraphViewExpanded);
    }

    [Fact]
    public async Task ViewBadgeCounts_MirrorChangesAndLogs()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(viewModel.StagedChanges.Count + viewModel.UnstagedChanges.Count, viewModel.ChangesViewBadgeCount);
        Assert.Equal(viewModel.Logs.Count, viewModel.GraphViewBadgeCount);
    }

    [Fact]
    public async Task ToggleFolder_CollapsesAndExpandsTreeDirectory()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var src = viewModel.UnstagedTreeRows.OfType<ScmFolderNode>().First(node => node.Name == "src");
        viewModel.ToggleFolderCommand.Execute(src);
        Assert.DoesNotContain(viewModel.UnstagedTreeRows, row => row is ScmFileNode file && file.Change.Path == "src/A.cs");

        viewModel.ToggleFolderCommand.Execute(src);
        Assert.Contains(viewModel.UnstagedTreeRows, row => row is ScmFileNode file && file.Change.Path == "src/A.cs");
    }

    [Fact]
    public async Task FolderCommands_RejectTreeFileNodesWithoutThrowing()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var file = viewModel.UnstagedTreeRows.OfType<ScmFileNode>()
            .First(row => row.Change.Path == "src/A.cs");

        Assert.False(viewModel.StageFolderCommand.CanExecute(file));
        Assert.False(viewModel.DiscardFolderCommand.CanExecute(file));
        await viewModel.StageFolderCommand.ExecuteAsync(file);
        await viewModel.DiscardFolderCommand.ExecuteAsync(file);
    }

    [Fact]
    public async Task TreeRowSelection_RaisesDiffOpenRequested()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        GitDiffRequest? requested = null;
        viewModel.DiffOpenRequested += (_, request) => requested = request;
        viewModel.SelectedUnstagedTreeRow = viewModel.UnstagedTreeRows
            .OfType<ScmFileNode>()
            .First(row => row.Change.Path == "src/A.cs");

        Assert.NotNull(requested);
        Assert.Equal("src/A.cs", requested!.Path);
        Assert.True(requested.IsPreview); // 树状行单击同样是预览 diff
    }

    [Fact]
    public async Task OpenChangeFilePermanent_FlatRow_OpensWorkTreeFileAsPermanentTab()
    {
        // 双击平铺行:工作区文件以常驻标签打开(不进预览槽)。
        var editor = new EditorAreaViewModel(new FakeGitService { Status = SampleStatus(), DiffResult = SampleDiff() }, Log);
        var viewModel = CreateGitViewModel(editor);
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 与 OpenChangeFilePermanent 内部 Path.Combine(RepositoryPath, change.Path) 完全同构,
        // 保证 TabKey("file:" + 路径) 一致,不引入分隔符差异。
        var filePath = Path.Combine(_repoPath, "src/A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "class A { }");

        var change = viewModel.UnstagedChanges.First(change => change.Path == "src/A.cs");
        viewModel.OpenChangeFilePermanentCommand.Execute(change);
        await WaitUntilAsync(() => editor.OpenTabs.Count == 1);

        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal(filePath, tab.Path);
        Assert.False(tab.IsPreview);
    }

    [Fact]
    public async Task OpenChangeFilePermanent_TreeRow_OpensWorkTreeFileAsPermanentTab()
    {
        // 双击树状文件行(DataContext 为 ScmFileNode)解析出同一工作区文件。
        var editor = new EditorAreaViewModel(new FakeGitService { Status = SampleStatus(), DiffResult = SampleDiff() }, Log);
        var viewModel = CreateGitViewModel(editor);
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 与 OpenChangeFilePermanent 内部 Path.Combine(RepositoryPath, change.Path) 完全同构,
        // 保证 TabKey("file:" + 路径) 一致,不引入分隔符差异。
        var filePath = Path.Combine(_repoPath, "src/A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "class A { }");

        var row = viewModel.UnstagedTreeRows.OfType<ScmFileNode>().First(row => row.Change.Path == "src/A.cs");
        viewModel.OpenChangeFilePermanentCommand.Execute(row);
        await WaitUntilAsync(() => editor.OpenTabs.Count == 1);

        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal(filePath, tab.Path);
        Assert.False(tab.IsPreview);
    }

    [Fact]
    public async Task OpenChangeFilePermanent_PromotesAlreadyOpenFilePreview()
    {
        // 先单击资源管理器式预览、再双击变更行:同一标签就地转正,不新增标签。
        var editor = new EditorAreaViewModel(new FakeGitService { Status = SampleStatus(), DiffResult = SampleDiff() }, Log);
        var viewModel = CreateGitViewModel(editor);
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 与 OpenChangeFilePermanent 内部 Path.Combine(RepositoryPath, change.Path) 完全同构,
        // 保证 TabKey("file:" + 路径) 一致,不引入分隔符差异。
        var filePath = Path.Combine(_repoPath, "src/A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "class A { }");

        await editor.OpenFileAsync(filePath);
        var preview = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.True(preview.IsPreview);

        var change = viewModel.UnstagedChanges.First(change => change.Path == "src/A.cs");
        viewModel.OpenChangeFilePermanentCommand.Execute(change);
        await WaitUntilAsync(() => !preview.IsPreview);

        Assert.Same(preview, Assert.Single(editor.OpenTabs));
        Assert.False(preview.IsPreview);
    }

    private GitViewModel CreateGitViewModel(EditorAreaViewModel editor) => new(
        new FakeGitService { Status = SampleStatus(), DiffResult = SampleDiff() },
        new FakeFolderPicker { Result = _repoPath },
        new FakeConfirmationService(),
        new FakeProjectCatalog(),
        editor,
        Log,
        new FakeClipboardService(),
        Watcher,
        new FakeProjectWorkspaceService(),
        new FakeApplicationStateStore(),
        new FakeSettingsService());

    [Fact]
    public async Task TreeMultiSelection_FeedsBatchCommands()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var file = viewModel.UnstagedTreeRows.OfType<ScmFileNode>().First(row => row.Change.Path == "src/A.cs");
        viewModel.SelectedUnstagedTreeRows.Add(file);
        Assert.True(viewModel.StageSelectedChangesCommand.CanExecute(null));

        await viewModel.StageSelectedChangesCommand.ExecuteAsync(null);
        Assert.Contains("src/A.cs", git.StagedPaths);
    }

    [Fact]
    public async Task TreeFolderSelection_ExpandsToAllFolderChanges()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var folder = viewModel.UnstagedTreeRows.OfType<ScmFolderNode>()
            .Single(row => row.FolderPath == "src");
        viewModel.SelectedUnstagedTreeRows.Add(folder);

        Assert.True(viewModel.StageSelectedChangesCommand.CanExecute(null));
        await viewModel.StageSelectedChangesCommand.ExecuteAsync(null);

        Assert.Equal(["src/A.cs"], git.StagedPaths);
    }
    // ===== 回归:筛选视图与集合条数一致(看不到更改类问题的护栏) =====

    [Fact]
    public async Task Refresh_ChangeViewsExposeEveryItem()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(viewModel.UnstagedChanges.Count, viewModel.UnstagedChangesView.OfType<GitChangeItem>().Count());
        Assert.Equal(viewModel.StagedChanges.Count, viewModel.StagedChangesView.OfType<GitChangeItem>().Count());
    }

    // ===== 阶段二:拖放暂存 / 提交消息历史 =====

    [Fact]
    public async Task DroppedChanges_RouteToStageAndUnstage()
    {
        var (viewModel, git, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        await viewModel.StageDroppedChangesCommand.ExecuteAsync(new[] { "src/A.cs", "notes/todo.txt" });
        Assert.Equal(["src/A.cs", "notes/todo.txt"], git.StagedPaths);

        await viewModel.UnstageDroppedChangesCommand.ExecuteAsync(new[] { "src/B.cs" });
        Assert.Equal(["src/B.cs"], git.UnstagedPaths);
    }

    [Fact]
    public async Task Commit_RecordsMessageHistoryAndApplyReusesIt()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.CommitMessage = "fix: first";
        await viewModel.CommitCommand.ExecuteAsync(null);
        viewModel.CommitMessage = "feat: second";
        await viewModel.CommitCommand.ExecuteAsync(null);

        Assert.Equal(["feat: second", "fix: first"], viewModel.RecentCommitMessages);

        viewModel.CommitMessage = string.Empty;
        viewModel.ApplyCommitMessageCommand.Execute("fix: first");
        Assert.Equal("fix: first", viewModel.CommitMessage);
    }

    // ===== 阶段三:提交图形 / 分页 / 传入传出 / 贮藏 =====

    [Fact]
    public void BuildCommitGraph_LinearChainUsesSingleLane()
    {
        var commits = new[]
        {
            new GitCommitInfo("c2", "c2", "t2", null, "A", "a@x", DateTimeOffset.UtcNow, ["c1"]),
            new GitCommitInfo("c1", "c1", "t1", null, "A", "a@x", DateTimeOffset.UtcNow, ["c0"]),
            new GitCommitInfo("c0", "c0", "t0", null, "A", "a@x", DateTimeOffset.UtcNow),
        };

        var rows = GitViewModel.BuildCommitGraph(commits);

        Assert.All(rows, row => Assert.Equal(0, row.DotLane));
        Assert.All(rows, row => Assert.Empty(row.PassLanes));
        Assert.All(rows.Take(2), row => Assert.Equal([new GitGraphLink(0, 0)], row.Links));
        Assert.Empty(rows[2].Links);

        // 一条分支一条线:只有最顶部的提交是新泳道起点,其余行的泳道线从上方延续。
        Assert.False(rows[0].DotLaneContinuesFromAbove);
        Assert.True(rows[1].DotLaneContinuesFromAbove);
        Assert.True(rows[2].DotLaneContinuesFromAbove);
    }

    [Fact]
    public void BuildCommitGraph_BranchAndMergeAssignLanes()
    {
        // m 合并 b、c 两条分支;b、c 都基于 a。
        var commits = new[]
        {
            new GitCommitInfo("m", "m", "merge", null, "A", "a@x", DateTimeOffset.UtcNow, ["b", "c"]),
            new GitCommitInfo("b", "b", "branch b", null, "A", "a@x", DateTimeOffset.UtcNow, ["a"]),
            new GitCommitInfo("c", "c", "branch c", null, "A", "a@x", DateTimeOffset.UtcNow, ["a"]),
            new GitCommitInfo("a", "a", "root", null, "A", "a@x", DateTimeOffset.UtcNow),
        };

        var rows = GitViewModel.BuildCommitGraph(commits);

        // m:圆点泳道 0,首父 b 延续泳道 0,c 分叉到泳道 1
        Assert.Equal(0, rows[0].DotLane);
        Assert.Equal([new GitGraphLink(0, 0), new GitGraphLink(0, 1)], rows[0].Links);
        Assert.False(rows[0].DotLaneContinuesFromAbove); // 顶部合并提交:泳道从本点才开始
        // b:泳道 0 圆点,泳道 1(c)竖线穿过;泳道 0 从 m 延续
        Assert.Equal(0, rows[1].DotLane);
        Assert.Equal([1], rows[1].PassLanes);
        Assert.True(rows[1].DotLaneContinuesFromAbove);
        // c:泳道 1 圆点,父 a 已在泳道 0 → 合并曲线 (1→0);泳道 1 从 m 的分叉延续
        Assert.Equal(1, rows[2].DotLane);
        Assert.Equal([new GitGraphLink(1, 0)], rows[2].Links);
        Assert.True(rows[2].DotLaneContinuesFromAbove);
        // a:回到泳道 0,无父;泳道 0 从 b/c 延续到根部
        Assert.Equal(0, rows[3].DotLane);
        Assert.Empty(rows[3].Links);
        Assert.True(rows[3].DotLaneContinuesFromAbove);
        Assert.Equal(2, rows[0].LaneCount);
    }

    [Fact]
    public void BuildCommitGraph_InterleavedSiblingKeepsMainLineOnOneLane()
    {
        // 分支上的提交按拓扑序插入主线显示之间(E,D,F,C,B,A):主线 E→D→C→B→A 必须始终
        // 一条线(泳道 0),分叉提交 F 独立一条线并在其行内以合并曲线(1→0)汇回主线。
        var commits = new[]
        {
            new GitCommitInfo("e", "e", "E", null, "A", "a@x", DateTimeOffset.UtcNow, ["d"]),
            new GitCommitInfo("d", "d", "D", null, "A", "a@x", DateTimeOffset.UtcNow, ["c"]),
            new GitCommitInfo("f", "f", "F", null, "A", "a@x", DateTimeOffset.UtcNow, ["c"]),
            new GitCommitInfo("c", "c", "C", null, "A", "a@x", DateTimeOffset.UtcNow, ["b"]),
            new GitCommitInfo("b", "b", "B", null, "A", "a@x", DateTimeOffset.UtcNow, ["a"]),
            new GitCommitInfo("a", "a", "A", null, "A", "a@x", DateTimeOffset.UtcNow),
        };

        var rows = GitViewModel.BuildCommitGraph(commits);

        // 主线 E,D,C,B,A 全部落在泳道 0(不是每提交一个新泳道的"阶梯")。
        Assert.All(new[] { 0, 1, 3, 4, 5 }, i => Assert.Equal(0, rows[i].DotLane));
        Assert.False(rows[0].DotLaneContinuesFromAbove); // 只有顶部 E 是新泳道起点
        Assert.All(new[] { 1, 3, 4, 5 }, i => Assert.True(rows[i].DotLaneContinuesFromAbove, $"row {i} 应延续泳道 0"));

        // F 独立泳道 1,行内含主线贯穿 + 合并曲线 (1→0) 汇回主线。
        Assert.Equal(1, rows[2].DotLane);
        Assert.False(rows[2].DotLaneContinuesFromAbove);
        Assert.Contains(new GitGraphLink(1, 0), rows[2].Links);
        Assert.Contains(0, rows[2].PassLanes);
    }

    [Fact]
    public void BuildCommitGraph_SecondParentDisplayedFirst_KeepsMergeBaseOnFirstParentLane()
    {
        // git log --topo-order 显示顺序为 m, c, b, a(二父链先显示):
        // 首父 b 处理时发现其父 a 被二父 c 的侧线占位,必须把 a 接回主线泳道 0,
        // 原侧线(泳道 1)在 b 的行内以 MergeFromLanes 汇入 —— 否则合并基 a 会掉到侧线,
        // 主线 m→b→a 被画成两段("一条分支多根线")。
        var commits = new[]
        {
            new GitCommitInfo("m", "m", "merge", null, "A", "a@x", DateTimeOffset.UtcNow, ["b", "c"]),
            new GitCommitInfo("c", "c", "branch c", null, "A", "a@x", DateTimeOffset.UtcNow, ["a"]),
            new GitCommitInfo("b", "b", "branch b", null, "A", "a@x", DateTimeOffset.UtcNow, ["a"]),
            new GitCommitInfo("a", "a", "root", null, "A", "a@x", DateTimeOffset.UtcNow),
        };

        var rows = GitViewModel.BuildCommitGraph(commits);

        // m:泳道 0 圆点,分叉到泳道 1(c)。
        Assert.Equal(0, rows[0].DotLane);
        Assert.Equal([new GitGraphLink(0, 0), new GitGraphLink(0, 1)], rows[0].Links);
        // c:泳道 1(侧线),主线 0 贯穿,首父 a 暂放泳道 1(同线延续)。
        Assert.Equal(1, rows[1].DotLane);
        Assert.Equal([0], rows[1].PassLanes);
        Assert.Equal([new GitGraphLink(1, 1)], rows[1].Links);
        Assert.Empty(rows[1].MergeFromLanes ?? []);
        // b:主线 0 圆点,把 a 接回收缩泳道 0,原侧线 1 在本行汇入主线。
        Assert.Equal(0, rows[2].DotLane);
        Assert.Equal([new GitGraphLink(0, 0)], rows[2].Links);
        Assert.Equal([1], rows[2].MergeFromLanes ?? []);
        Assert.Empty(rows[2].PassLanes);
        // a(合并基):回到主线 0,整条首父链 m→b→a 一条线。
        Assert.Equal(0, rows[3].DotLane);
        Assert.True(rows[3].DotLaneContinuesFromAbove);
        Assert.Empty(rows[3].Links);
    }

    [Fact]
    public async Task LoadMoreCommits_AppendsNextPageAndStopsWhenExhausted()
    {
        var commits = Enumerable.Range(0, 40).Select(i =>
            new GitCommitInfo($"c{i}", $"c{i}", $"t{i}", null, "A", "a@x", DateTimeOffset.UtcNow, i > 0 ? [$"c{i - 1}"] : null)).ToArray();
        var (viewModel, git, _, _, _) = Create();
        git.Logs = commits;
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(30, viewModel.Logs.Count);
        // 顶部两行是 传入/传出的更改 同步折叠栏(SampleStatus: ahead 2 / behind 1)
        Assert.Equal(32, viewModel.LogRows.Count);
        Assert.True(viewModel.CanLoadMoreCommits);

        await viewModel.LoadMoreCommitsCommand.ExecuteAsync(null);
        Assert.Equal(40, viewModel.Logs.Count);
        Assert.Equal(42, viewModel.LogRows.Count);
        Assert.False(viewModel.CanLoadMoreCommits);
    }

    [Fact]
    public async Task Refresh_LoadsRemoteSectionsAndOutgoingRowsExpandInlineFiles()
    {
        var outgoing = new GitCommitInfo("o1".PadRight(40, '0'), "o1", "outgoing", null, "A", "a@x", DateTimeOffset.UtcNow);
        var incoming = new GitCommitInfo("i1".PadRight(40, '0'), "i1", "incoming", null, "A", "a@x", DateTimeOffset.UtcNow);
        var (viewModel, git, _, _, _) = Create();
        git.OutgoingCommits = [outgoing];
        git.IncomingCommits = [incoming];
        git.CommitFiles = [new GitFileChange("src/A.cs", GitChangeStatus.Added, GitChangeStatus.Unmodified)];
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasOutgoing);
        Assert.True(viewModel.HasIncoming);
        Assert.Same(outgoing, Assert.Single(viewModel.OutgoingCommits).Commit);
        Assert.Same(incoming, Assert.Single(viewModel.IncomingCommits).Commit);

        // 传出/传入行不带泳道图,但同样是可折叠提交栏
        var outgoingRow = Assert.Single(viewModel.OutgoingCommits);
        Assert.Null(outgoingRow.Graph);

        viewModel.ToggleLogRowCommand.Execute(outgoingRow);
        await WaitUntilAsync(() => outgoingRow.IsLoaded);
        Assert.Single(outgoingRow.Files);
        Assert.Equal("src/A.cs", outgoingRow.Files[0].Path);
    }

    [Fact]
    public async Task Refresh_BuildsSyncGroupRowsAtBranchTop()
    {
        var (viewModel, git, _, _, _) = Create();
        git.Logs = [new GitCommitInfo("h1".PadRight(40, '0'), "h1", "local tip", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.OutgoingCommits = [new GitCommitInfo("o1".PadRight(40, '0'), "o1", "outgoing", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.IncomingCommits = [new GitCommitInfo("i1".PadRight(40, '0'), "i1", "incoming", null, "A", "a@x", DateTimeOffset.UtcNow)];
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 顶部两行与提交行同集合同模板:传入的更改(远端色空心圆点,分支顶端)、传出的更改(本地色)
        Assert.Equal(3, viewModel.LogRows.Count);
        Assert.Equal("传入的更改 1", viewModel.LogRows[0].Commit.Subject);
        Assert.Equal("HEAD...origin/main", viewModel.LogRows[0].Commit.Hash);
        Assert.True(viewModel.LogRows[0].Graph!.DotHollow);
        Assert.False(viewModel.LogRows[0].Graph!.DotLaneContinuesFromAbove);

        Assert.Equal("传出的更改 2", viewModel.LogRows[1].Commit.Subject);
        Assert.Equal("origin/main...HEAD", viewModel.LogRows[1].Commit.Hash);
        Assert.True(viewModel.LogRows[1].Graph!.DotHollow);
        Assert.True(viewModel.LogRows[1].Graph!.DotLaneContinuesFromAbove);
        Assert.Equal("GraphCurrentBranchBrush", viewModel.LogRows[1].Graph!.LaneColorKeys![0]);

        // 首提交从上方接入同步行:上段竖线入色取最下方同步行的色键(本地色),连线全程单色
        var firstCommit = viewModel.LogRows[2];
        Assert.Equal("local tip", firstCommit.Commit.Subject);
        Assert.True(firstCommit.Graph!.DotLaneContinuesFromAbove);
        Assert.Equal(["GraphCurrentBranchBrush"], firstCommit.Graph.LaneIncomingColorKeys!);
    }

    [Fact]
    public void UpstreamColorKey_MapsRemoteUpstreamToRemotePalette()
    {
        var branches = new[]
        {
            new GitBranchInfo("origin/main", IsCurrent: false, IsRemote: true, TipHash: "c1"),
        };

        Assert.StartsWith("GraphRemote", GitViewModel.UpstreamColorKey("origin/main", branches), StringComparison.Ordinal);
        Assert.Equal("GraphCurrentBranchBrush", GitViewModel.UpstreamColorKey("unknown/branch", branches));
        Assert.Equal("GraphCurrentBranchBrush", GitViewModel.UpstreamColorKey(null, branches));
    }

    [Fact]
    public async Task SyncGroupRows_ExpandMergedFileImpactWithSingleClick()
    {
        var (viewModel, git, _, _, _) = Create();
        git.Logs = [new GitCommitInfo("h1".PadRight(40, '0'), "h1", "tip", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.IncomingCommits = [new GitCommitInfo("i1".PadRight(40, '0'), "i1", "incoming", null, "A", "a@x", DateTimeOffset.UtcNow)];
        git.CommitFiles =
        [
            new GitFileChange("src/A.cs", GitChangeStatus.Modified, GitChangeStatus.Unmodified),
            new GitFileChange("src/B.cs", GitChangeStatus.Added, GitChangeStatus.Unmodified),
        ];
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 单击「传入的更改」行:与提交行同一展开机制,懒加载 diff 范围(HEAD...origin/main)
        // 的合并文件影响;收起再展开不重复请求
        var incomingRow = viewModel.LogRows[0];
        Assert.Equal("传入的更改 1", incomingRow.Commit.Subject);

        viewModel.ToggleLogRowCommand.Execute(incomingRow);
        Assert.True(incomingRow.IsExpanded);
        await WaitUntilAsync(() => incomingRow.IsLoaded);
        Assert.Equal(2, incomingRow.Files.Count);
        Assert.Equal("HEAD...origin/main", Assert.Single(git.CommitFileListRequests));

        viewModel.ToggleLogRowCommand.Execute(incomingRow);
        viewModel.ToggleLogRowCommand.Execute(incomingRow);
        Assert.True(incomingRow.IsExpanded);
        Assert.Equal(1, git.CommitFileListCalls);
    }

    [Fact]
    public async Task StashCommands_RouteToService()
    {
        var (viewModel, git, _, confirmation, _) = Create();
        git.Stashes = [new GitStashInfo(0, "wip: spike"), new GitStashInfo(1, "wip: docs")];
        viewModel.RepositoryPath = _repoPath;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.Stashes.Count);

        await viewModel.PopStashCommand.ExecuteAsync(viewModel.Stashes[0]);
        Assert.Equal([0], git.PoppedStashes);

        confirmation.Result = false;
        await viewModel.DropStashCommand.ExecuteAsync(viewModel.Stashes[1]);
        Assert.Empty(git.DroppedStashes);

        confirmation.Result = true;
        await viewModel.DropStashCommand.ExecuteAsync(viewModel.Stashes[1]);
        Assert.Equal([1], git.DroppedStashes);

        Assert.True(viewModel.StashAllChangesCommand.CanExecute(null));
        await viewModel.StashAllChangesCommand.ExecuteAsync(null);
        Assert.Equal(1, git.StashPushCalls);
    }
}
