using System.Collections.ObjectModel;
using Nornia.Core.Collections;
using Nornia.Core.Coalescing;

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

        SpinWait.SpinUntil(() => runs == 1, TimeSpan.FromSeconds(2));
        Assert.False(scheduler.IsScheduled);
        Thread.Sleep(80);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void RunOnceScheduler_RescheduleResetsTheTimer()
    {
        var runs = 0;
        using var scheduler = new RunOnceScheduler(60) { Action = () => Interlocked.Increment(ref runs) };
        scheduler.Schedule();
        Thread.Sleep(35);
        scheduler.Schedule(); // trailing debounce: timer restarts

        Assert.Equal(0, runs);
        SpinWait.SpinUntil(() => runs == 1, TimeSpan.FromSeconds(2));
        Thread.Sleep(80);
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
        Assert.Equal([1, 2], op.Items);
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
}
