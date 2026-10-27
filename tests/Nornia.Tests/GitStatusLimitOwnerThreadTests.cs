using System.Windows.Threading;
using Nornia.Core.Models;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>git.statusLimit 的实时生效必须经过所有权线程。变更列表与其 CollectionView 只能由构造
/// ViewModel 的那条线程发布;而设置会话的 Changed 事件并不保证落在所有权线程上——会话可能在没有同步
/// 上下文的线程上打开(激活链 fire-and-forget,设置页就出过这个事故),watch 续延随之落回线程池。
/// 跨线程发布会被 NotSupportedException 从中间打断:实测 20 条移除通知只发出了第 1 条,列表停在
/// 119 条,整轮刷新被异常吞掉,降级提示与实际条数从此互相矛盾。
///
/// <para>测试用专用 STA 线程 + <see cref="DispatcherSynchronizationContext"/> 复现生产 UI 线程,
/// 再把 Changed 重投到线程池线程上(OffThreadChangedSession),于是"必须回排"成为可断言的红线。</para></summary>
public sealed class GitStatusLimitOwnerThreadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-status-limit-owner-{Guid.NewGuid():N}");

    public GitStatusLimitOwnerThreadTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void GitStatusLimit_ChangedOnBackgroundThread_RepublishesOnTheOwningThread()
    {
        var changes = Enumerable.Range(0, 120)
            .Select(index => new GitFileChange(
                $"src/f{index:D3}.cs", GitChangeStatus.Unmodified, GitChangeStatus.Modified))
            .ToArray();
        var git = new FakeGitService
        {
            Status = new GitRepositoryStatus(true, "main", null, 0, 0, [], changes),
            DiffResult = new GitFileDiff("src/f000.cs", null, false, false, false, []),
        };
        var settings = new FakeSettingsService();
        var logs = new FakeUiLogService();

        var ownerThreadId = 0;
        var publishThreadId = 0;
        var changedThreadId = 0;
        Exception? failure = null;
        var initialCount = 0;
        var finalCount = 0;
        var truncationSummary = string.Empty;
        var finished = new ManualResetEventSlim(false);
        GitViewModel? viewModel = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            // 与生产 UI 线程一致:构造期捕获的同步上下文就是这条线程的调度器,await 续延排回这里。
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                ownerThreadId = Environment.CurrentManagedThreadId;
                viewModel = new GitViewModel(
                    git, new FakeFolderPicker { Result = _tempDir }, new FakeConfirmationService(),
                    new FakeTextPromptService(), new FakeProjectCatalog(), new EditorAreaViewModel(git, new FakeUiLogService()),
                    logs, new FakeClipboardService(), new FakeGitRepositoryWatcher(),
                    new FakeProjectWorkspaceService(), new FakeApplicationStateStore(),
                    new OffThreadChangedSettingsService(settings, () => changedThreadId = Environment.CurrentManagedThreadId));
                viewModel.UnstagedChanges.CollectionChanged += (_, _) => publishThreadId = Environment.CurrentManagedThreadId;

                var activate = viewModel.ActivateAsync();
                PumpUntil(dispatcher, () => activate.IsCompleted, TimeSpan.FromSeconds(30));
                activate.GetAwaiter().GetResult();

                viewModel.RepositoryPath = _tempDir;
                var refresh = viewModel.RefreshCommand.ExecuteAsync(null);
                PumpUntil(dispatcher, () => refresh.IsCompleted && viewModel.UnstagedChanges.Count == 120,
                    TimeSpan.FromSeconds(30));
                refresh.GetAwaiter().GetResult();
                initialCount = viewModel.UnstagedChanges.Count;
                Assert.Equal(10_000, viewModel.StatusEntryLimit);

                var commit = SetStatusLimitAsync(settings, 100);
                PumpUntil(dispatcher, () => commit.IsCompleted, TimeSpan.FromSeconds(60));
                commit.GetAwaiter().GetResult();

                PumpUntil(dispatcher, () => viewModel.UnstagedChanges.Count == 100, TimeSpan.FromSeconds(60));
                finalCount = viewModel.UnstagedChanges.Count;
                truncationSummary = viewModel.StatusTruncationSummary;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "git-status-limit-owner",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(150)), "所有权线程宿主测试未在预算内结束。");
        thread.Join(TimeSpan.FromSeconds(10));
        Assert.Null(failure);
        Assert.NotNull(viewModel);
        Assert.Equal(120, initialCount);
        Assert.Equal(100, finalCount);
        Assert.Contains("100", truncationSummary);
        Assert.Equal(100, viewModel!.StatusEntryLimit);
        Assert.Contains(100, git.StatusMaximumEntriesRequests);
        Assert.True(viewModel.IsStatusTruncated, "达到上限后没有出现截断降级提示");

        // 事件确实来自另一条线程(否则本测试什么都没证),而每次发布都落在所有权线程上。
        Assert.NotEqual(ownerThreadId, changedThreadId);
        Assert.Equal(ownerThreadId, publishThreadId);
        Assert.DoesNotContain(logs.Entries, entry => entry.Level == "WARNING" && !entry.Message.Contains("仅列出前"));
    }

    private static async Task SetStatusLimitAsync(FakeSettingsService settings, int value)
    {
        var baseline = await settings.GetSnapshotAsync(new SettingsContext());
        var result = await settings.CommitAsync(
            SettingsTransaction.Set(baseline, SettingScope.User, BuiltInSettingsCatalog.GitStatusLimit, value));
        Assert.True(result.Status == SettingsCommitStatus.Success,
            $"设置提交失败: {result.Status} {result.ErrorMessage}");
    }

    /// <summary>在所有权线程上泵调度器直到条件成立(排队的续延与 Post 回调据此落地)。</summary>
    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
            Dispatcher.PushFrame(frame);
        }

        Assert.True(condition(), $"所有权线程在 {budget.TotalSeconds:0}s 内没有完成这一步。");
    }

    /// <summary>透传真实设置服务,只把会话的 Changed 事件改在线程池线程上重投——复现"会话由没有同步
    /// 上下文的线程打开、watch 续延落回线程池"的生产投递形态。</summary>
    private sealed class OffThreadChangedSettingsService(
        FakeSettingsService inner,
        Action onStateChanged) : ISettingsService
    {
        public Task<SettingsSnapshot> GetSnapshotAsync(SettingsContext context, CancellationToken cancellationToken = default) =>
            inner.GetSnapshotAsync(context, cancellationToken);

        public Task<SettingsCommitResult> CommitAsync(SettingsTransaction transaction, CancellationToken cancellationToken = default) =>
            inner.CommitAsync(transaction, cancellationToken);

        public Task<SettingsCommitResult> ResetAsync(string key, SettingScope scope, SettingsContext context,
            string? languageId = null, CancellationToken cancellationToken = default) =>
            inner.ResetAsync(key, scope, context, languageId, cancellationToken);

        public IAsyncEnumerable<SettingsChangeSet> WatchAsync(SettingsContext context, CancellationToken cancellationToken = default) =>
            inner.WatchAsync(context, cancellationToken);

        public async Task<ISettingsSession> OpenSessionAsync(SettingsContext context,
            IReadOnlyCollection<string>? keys = null, CancellationToken cancellationToken = default) =>
            new OffThreadChangedSession(await inner.OpenSessionAsync(context, keys, cancellationToken), onStateChanged);
    }

    private sealed class OffThreadChangedSession(
        ISettingsSession inner,
        Action onStateChanged) : ISettingsSession
    {
        private event EventHandler<SettingsChangeSet>? ChangedInternal;

        public event EventHandler<SettingsChangeSet>? Changed
        {
            add
            {
                if (value is null) return;
                ChangedInternal += value;
                inner.Changed += Forward;
            }
            remove
            {
                if (value is null) return;
                ChangedInternal -= value;
                inner.Changed -= Forward;
            }
        }

        public SettingsContext Context => inner.Context;
        public SettingsSnapshot? Current => inner.Current;
        public IReadOnlyCollection<string>? SubscribedKeys => inner.SubscribedKeys;

        public Task<SettingsSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            inner.RefreshAsync(cancellationToken);

        public Task<SettingsCommitResult> CommitAsync(SettingScope scope, IReadOnlyList<SettingOperation> operations,
            CancellationToken cancellationToken = default) =>
            inner.CommitAsync(scope, operations, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private void Forward(object? sender, SettingsChangeSet changes)
        {
            // 线程池线程:没有同步上下文,VM 若直接在这里发布绑定集合就撞上 CollectionView 的线程亲和。
            ThreadPool.QueueUserWorkItem(_ =>
            {
                onStateChanged();
                ChangedInternal?.Invoke(sender, changes);
            });
        }
    }
}
