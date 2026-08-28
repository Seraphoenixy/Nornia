using Nornia.Core.Interfaces;
using Serilog;

namespace Nornia.Desktop.Services;

/// <summary>Low-overhead metrics sink for startup and expensive background/UI publications.</summary>
public sealed class UiPerformanceMetrics : IUiPerformanceMetrics
{
    public PerformanceScope Begin(string operation, int? itemCount = null, string? phase = null) =>
        new(duration => Record(new PerformanceSample(operation, duration, itemCount, Phase: phase)));

    public void Record(PerformanceSample sample)
    {
        Log.Debug("perf operation={Operation} duration_ms={DurationMs} item_count={ItemCount} estimated_bytes={EstimatedBytes} phase={Phase}",
            sample.Operation, sample.Duration.TotalMilliseconds, sample.ItemCount,
            sample.EstimatedBytes, sample.Phase);
    }

}
