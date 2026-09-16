using System.Collections.Concurrent;
using Nornia.Core.Interfaces;
using Serilog;

namespace Nornia.Desktop.Services;

/// <summary>Low-overhead metrics sink for startup and expensive background/UI publications.</summary>
public sealed class UiPerformanceMetrics : IUiPerformanceMetrics
{
    private const int FlushEverySamples = 100;
    private readonly ConcurrentDictionary<string, Aggregate> _aggregates = new(StringComparer.Ordinal);
    private int _samplesSinceFlush;
    private int _flushInProgress;

    public PerformanceScope Begin(string operation, int? itemCount = null, string? phase = null) =>
        new(duration => Record(new PerformanceSample(operation, duration, itemCount, Phase: phase)));

    public void Record(PerformanceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var aggregate = _aggregates.GetOrAdd(sample.Operation, static _ => new Aggregate());
        aggregate.Add(sample);

        // Keep the hot path allocation-free and avoid writing one log event per keystroke. A
        // structured summary is emitted often enough for diagnostics and remains visible under
        // the application's Information-level logger.
        if (Interlocked.Increment(ref _samplesSinceFlush) >= FlushEverySamples)
        {
            Flush();
        }
    }

    /// <summary>Publishes and resets the current aggregate window. App shutdown calls this once so
    /// a short session is not silently lost just because it produced fewer than 100 samples.</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _flushInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _samplesSinceFlush, 0);
            foreach (var (operation, aggregate) in _aggregates)
            {
                if (!aggregate.TryTakeSnapshot(out var snapshot))
                {
                    continue;
                }

                Log.Information(
                    "perf_summary operation={Operation} count={Count} average_ms={AverageMs} max_ms={MaxMs} item_count_avg={ItemCountAverage} estimated_bytes_avg={EstimatedBytesAverage} phase={Phase}",
                    operation, snapshot.Count, snapshot.AverageMs, snapshot.MaxMs,
                    snapshot.ItemCountAverage, snapshot.EstimatedBytesAverage, snapshot.Phase);
            }
        }
        finally
        {
            Volatile.Write(ref _flushInProgress, 0);
        }
    }

    private sealed class Aggregate
    {
        private readonly object _gate = new();
        private int _count;
        private double _durationMs;
        private double _maxMs;
        private long _itemCount;
        private int _itemCountSamples;
        private long _estimatedBytes;
        private int _estimatedBytesSamples;
        private string? _phase;

        public void Add(PerformanceSample sample)
        {
            lock (_gate)
            {
                _count++;
                var durationMs = sample.Duration.TotalMilliseconds;
                _durationMs += durationMs;
                _maxMs = Math.Max(_maxMs, durationMs);
                if (sample.ItemCount is { } itemCount)
                {
                    _itemCount += itemCount;
                    _itemCountSamples++;
                }

                if (sample.EstimatedBytes is { } estimatedBytes)
                {
                    _estimatedBytes += estimatedBytes;
                    _estimatedBytesSamples++;
                }

                _phase = sample.Phase;
            }
        }

        public bool TryTakeSnapshot(out AggregateSnapshot snapshot)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    snapshot = default;
                    return false;
                }

                snapshot = new AggregateSnapshot(
                    _count,
                    _durationMs / _count,
                    _maxMs,
                    _itemCountSamples == 0 ? null : (double)_itemCount / _itemCountSamples,
                    _estimatedBytesSamples == 0 ? null : (double)_estimatedBytes / _estimatedBytesSamples,
                    _phase);
                _count = 0;
                _durationMs = 0;
                _maxMs = 0;
                _itemCount = 0;
                _itemCountSamples = 0;
                _estimatedBytes = 0;
                _estimatedBytesSamples = 0;
                _phase = null;
                return true;
            }
        }
    }

    private readonly record struct AggregateSnapshot(
        int Count,
        double AverageMs,
        double MaxMs,
        double? ItemCountAverage,
        double? EstimatedBytesAverage,
        string? Phase);
}
