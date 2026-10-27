using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Nornia.Core.Collections;
using Nornia.Core.Coalescing;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>Covers the coalescing primitives (VS Code async.ts/event.ts equivalents):
/// RunOnceScheduler/RunOnceWorker/ThrottledWorker, value latch + debounced coalescer and the
/// collection middle-operation differ used by the incremental row projections.</summary>
public sealed class CoalescingPrimitivesTests
{
    [Fact]
    public void RunOnceScheduler_CoalescesMultipleSchedulesIntoOneRun()
    {
        var runs = 0;
        using var scheduler = new RunOnceScheduler(30) { Action = () => Interlocked.Increment(ref runs) };
        scheduler.Schedule();
        scheduler.Schedule();
        scheduler.Schedule();

        // IsScheduled 归位后不会再有待执行的回调,因此这是"只跑一次"的确定性证据,
        // 不需要再 sleep 一段时间去"确认没有第二次"。
        Assert.True(SpinWait.SpinUntil(() => runs == 1 && !scheduler.IsScheduled, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void RunOnceScheduler_RescheduleKeepsExactlyOnePendingRun()
    {
        // 原实现用 Thread.Sleep(35) 去观察 60ms 计时器"尚未触发"——既没验证到重置
        // (35ms 本来就早于 60ms),又在负载稍高的 CI 上会因为 sleep 超时误报。
        // 改为断言真正有价值的性质:重复 Schedule 不叠加排程,最终恰好跑一次。
        var runs = 0;
        using var scheduler = new RunOnceScheduler(60) { Action = () => Interlocked.Increment(ref runs) };
        scheduler.Schedule();
        Assert.True(scheduler.IsScheduled);

        scheduler.Schedule(); // 尾部去抖:重新计时
        Assert.True(scheduler.IsScheduled);
        Assert.Equal(0, runs); // 重复 Schedule 不得立刻执行

        Assert.True(SpinWait.SpinUntil(() => runs == 1 && !scheduler.IsScheduled, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void RunOnceScheduler_FlushRunsImmediatelyAndClears()
    {
        var ran = false;
        using var scheduler = new RunOnceScheduler(60_000) { Action = () => ran = true };
        scheduler.Schedule();
        scheduler.Flush();

        Assert.True(ran);
        Assert.False(scheduler.IsScheduled);
    }

    [Fact]
    public void RunOnceScheduler_CapturesHandlerException()
    {
        using var scheduler = new RunOnceScheduler(10) { Action = () => throw new InvalidOperationException("boom") };
        scheduler.Schedule();

        SpinWait.SpinUntil(() => scheduler.LastException is not null, TimeSpan.FromSeconds(2));
        Assert.True(scheduler.LastException is InvalidOperationException);
    }

    [Fact]
    public void RunOnceWorker_AccumulatesUnitsIntoOneBatch()
    {
        List<int>? batch = null;
        using var worker = new RunOnceWorker<int>(list => batch = list.ToList(), 30);
        worker.Work(1);
        worker.Work(2);
        worker.Work(3);

        SpinWait.SpinUntil(() => batch is not null, TimeSpan.FromSeconds(2));
        Assert.Equal([1, 2, 3], batch);
        Assert.Equal(0, worker.PendingCount);
    }

    [Fact]
    public void ThrottledWorker_ProcessesAllUnitsInOrderAcrossChunks()
    {
        var received = new List<int>();
        var gate = new object();
        using var worker = new ThrottledWorker<int>(2, 20, null, chunk =>
        {
            lock (gate) received.AddRange(chunk);
        });

        for (var i = 0; i < 5; i++)
        {
            worker.Work(i);
        }

        SpinWait.SpinUntil(() =>
        {
            lock (gate) return received.Count == 5;
        }, TimeSpan.FromSeconds(3));

        Assert.Equal([0, 1, 2, 3, 4], received);
    }

    [Fact]
    public async Task ThrottledWorker_DropsOldestBeyondBufferCap()
    {
        var release = new ManualResetEventSlim(false);
        var handlerEntered = new ManualResetEventSlim(false);
        var received = new List<int>();
        var gate = new object();
        using var worker = new ThrottledWorker<int>(1, 3, 3, chunk =>
        {
            handlerEntered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            lock (gate) received.AddRange(chunk);
        });

        var pumping = Task.Run(() => worker.Work(1)); // handler blocks inside this call
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(2)));
        worker.Work(2);
        worker.Work(3);
        worker.Work(4); // pending = 2,3,4 → at cap
        worker.Work(5); // pending would reach 5 > cap 3 → oldest dropped

        Assert.Equal(1, worker.DroppedCount);
        release.Set();
        await pumping.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DebouncedCoalescer_MergesValuesAcrossTheQuietWindow()
    {
        string? result = null;
        using var coalescer = new DebouncedCoalescer<string>(30, (previous, current) => (previous ?? string.Empty) + current);
        coalescer.Debounced += value => result = value;
        coalescer.Raise("a");
        coalescer.Raise("b");
        coalescer.Raise("c");

        SpinWait.SpinUntil(() => result is not null, TimeSpan.FromSeconds(2));
        Assert.Equal("abc", result);
    }

    [Fact]
    public void ValueLatch_FiresOnlyWhenValueChanges()
    {
        var latch = new ValueLatch<int>();
        var calls = new List<int>();
        latch.Changed += calls.Add;

        Assert.True(latch.Push(1));
        Assert.False(latch.Push(1));
        Assert.True(latch.Push(2));

        Assert.Equal([1, 2], calls);
    }

    // ===== CollectionDiffer =====

    [Fact]
    public void Compute_FindsMinimalMiddleOperation()
    {
        var current = new List<object> { "a", "b", "c", "d", "e" };
        var desired = new List<object> { "a", "x", "y", "d", "e" };

        var op = CollectionDiffer.Compute(current, desired);

        Assert.Equal(1, op.RemoveStart);
        Assert.Equal(2, op.RemoveCount);
        Assert.Equal(1, op.InsertAt);
        Assert.Equal(2, op.InsertCount);
        Assert.False(op.IsNoOp);
    }

    [Fact]
    public void Compute_IsNoOpForIdenticalSequences()
    {
        var current = new List<object> { "a", "b", "c" };

        var op = CollectionDiffer.Compute(current, new List<object> { "a", "b", "c" });

        Assert.True(op.IsNoOp);
        Assert.Equal(0, op.RemoveCount);
        Assert.Equal(0, op.InsertCount);
    }

    [Fact]
    public void Apply_WithPrecomputedOperation_InsertsTheDesiredSlice()
    {
        var collection = new ObservableCollection<string> { "a", "b", "c", "d" };
        var desired = new List<string> { "a", "x", "y", "d" };
        var op = CollectionDiffer.Compute(collection, desired);

        var removed = CollectionDiffer.Apply(collection, desired, op);

        Assert.Equal(new HashSet<string> { "b", "c" }, removed.ToHashSet());
        Assert.Equal(["a", "x", "y", "d"], collection);
    }

    [Fact]
    public void Apply_WithPrecomputedNoOp_EmitsNoNotifications()
    {
        var collection = new ObservableCollection<string> { "a", "b" };
        var desired = new List<string> { "a", "b" };
        var notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;

        CollectionDiffer.Apply(collection, desired, CollectionDiffer.Compute(collection, desired));

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void Apply_ReplacesTheMiddleRegionAndReturnsRemovedItems()
    {
        var collection = new ObservableCollection<string> { "a", "b", "c", "d" };

        var removed = CollectionDiffer.Apply(collection, new List<string> { "a", "x", "y", "d" });

        Assert.Equal(new HashSet<string> { "b", "c" }, removed.ToHashSet());
        Assert.Equal(["a", "x", "y", "d"], collection);
    }

    [Fact]
    public void Apply_IsNoOpWhenSequencesMatch()
    {
        var collection = new ObservableCollection<string> { "a", "b" };
        var notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;

        CollectionDiffer.Apply(collection, new List<string> { "a", "b" });

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void Apply_HandlesGrowthAtTheTailAndShrinkAtTheHead()
    {
        var collection = new ObservableCollection<string> { "a", "b", "c" };

        CollectionDiffer.Apply(collection, new List<string> { "c", "d", "e" });

        Assert.Equal(["c", "d", "e"], collection);
    }

    [Fact]
    public void ReplaceRange_WithEqualityCheck_SkipsTheResetWhenNothingChanged()
    {
        var collection = new BulkObservableCollection<string> { "a", "b" };
        var notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;

        collection.ReplaceRange(new[] { "a", "b" });

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void ReplaceRange_WithoutEqualityCheck_AlwaysPublishesOneReset()
    {
        var collection = new BulkObservableCollection<string> { "a", "b" };
        var notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;

        // 调用方已经用一次差分确认过有变化时,跳过逐项 SequenceEqual 的第二遍全表比较。
        collection.ReplaceRange(new[] { "a", "b" }, checkEquality: false);

        Assert.Equal(1, notifications);
    }

    [Fact]
    public void SyncCollection_TailShrinkToNewEntryLimit_PublishesRemovesOnly()
    {
        // git.statusLimit 生效就是这一形状:同一列表从 120 条缩到 100 条(尾部纯删除)。
        // 必须走差分而不是快照:Reset 会重建每一行的容器与选中态,而逐条 Remove 让前 100 行
        // 实例存活;同时列表必须精确落在目标条数上——发布中途被打断(例如跨线程通知抛异常)
        // 会留下 119 条这种"提示说 100、列表不是 100"的半状态。
        var current = Enumerable.Range(0, 120).Select(index => index.ToString("D3")).ToArray();
        var desired = current[..100];
        var target = new BulkObservableCollection<string>();
        target.ReplaceRange(current);
        var removes = 0;
        var resets = 0;
        target.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Remove) removes += args.OldItems?.Count ?? 0;
            if (args.Action == NotifyCollectionChangedAction.Reset) resets++;
        };

        var changed = GitViewModel.SyncCollection(target, desired);

        Assert.True(changed);
        Assert.Equal(100, target.Count);
        Assert.Equal(20, removes);
        Assert.Equal(0, resets);
    }
}
