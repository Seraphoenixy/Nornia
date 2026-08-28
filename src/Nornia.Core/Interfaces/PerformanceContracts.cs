using System.Diagnostics;

namespace Nornia.Core.Interfaces;

/// <summary>A low-frequency structured measurement emitted by UI-facing background work.</summary>
public sealed record PerformanceSample(
    string Operation,
    TimeSpan Duration,
    int? ItemCount = null,
    long? EstimatedBytes = null,
    string? Phase = null);

/// <summary>Disposable timing scope used by the shared performance metrics contract.</summary>
public sealed class PerformanceScope : IDisposable
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private Action<TimeSpan>? _onCompleted;

    public PerformanceScope(Action<TimeSpan> onCompleted) => _onCompleted = onCompleted;

    public void Dispose()
    {
        var callback = Interlocked.Exchange(ref _onCompleted, null);
        if (callback is null) return;
        _stopwatch.Stop();
        callback(_stopwatch.Elapsed);
    }
}

/// <summary>Shared performance sink. Implementations must not publish measurements to the UI log
/// collection; measurements are intended for structured diagnostics only.</summary>
public interface IUiPerformanceMetrics
{
    PerformanceScope Begin(string operation, int? itemCount = null, string? phase = null);
    void Record(PerformanceSample sample);
}
