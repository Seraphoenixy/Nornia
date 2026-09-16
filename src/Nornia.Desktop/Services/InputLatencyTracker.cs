using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Nornia.Core.Interfaces;

namespace Nornia.Desktop.Services;

/// <summary>
/// 输入延迟采样器(对照 VS Code base/browser/performance.ts 的 inputLatency 测量器):
/// 每次键按下/鼠标按下记录时间戳,下一个渲染帧完成时计算 交互→渲染 的延迟并经
/// <see cref="IUiPerformanceMetrics"/> 上报(逐样本 Debug 日志 + 每 100 样本一次均值/峰值汇总)。
///
/// 空闲时零开销:仅在有待测样本期间订阅 CompositionTarget.Rendering。
/// 让"流畅"成为可测量、可回归的一阶指标,而不是感觉。
/// </summary>
public sealed class InputLatencyTracker : IDisposable
{
    private const int SummaryEvery = 100;

    private readonly IUiPerformanceMetrics _metrics;
    private readonly object _gate = new();
    private long _pendingTicks;
    private bool _hooked;
    private bool _disposed;
    private double _windowSumMs;
    private double _windowMaxMs;
    private int _windowCount;

    public InputLatencyTracker(IUiPerformanceMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <summary>在 WPF 输入管线挂接(<c>InputManager.PreProcessInput</c>,应用级、全部窗口、
    /// 无需逐窗口接线);无 WPF 环境时为空操作。只计"离散"交互:按键与鼠标按键按下
    /// (不含移动/滚轮),与 VS Code 采样口径一致。</summary>
    public void Attach()
    {
        if (Application.Current is null)
        {
            return;
        }

        System.Windows.Input.InputManager.Current.PreProcessInput += OnPreProcessInput;
    }

    private void OnPreProcessInput(object? sender, System.Windows.Input.PreProcessInputEventArgs args)
    {
        var routed = args.StagingItem?.Input?.RoutedEvent;
        if (routed is not null &&
            (routed == System.Windows.UIElement.PreviewKeyDownEvent ||
             routed == System.Windows.UIElement.KeyDownEvent ||
             routed == System.Windows.UIElement.PreviewMouseDownEvent ||
             routed == System.Windows.UIElement.MouseDownEvent))
        {
            NoteInteraction();
        }
    }

    /// <summary>记录一次交互时间戳(UI 线程调用)。</summary>
    public void NoteInteraction()
    {
        if (_disposed) return;
        // One render frame represents the current interaction burst. Keep the first timestamp in
        // that burst so rapid key presses do not overwrite the oldest latency sample.
        Interlocked.CompareExchange(ref _pendingTicks, Stopwatch.GetTimestamp(), 0);
        HookFrame();
    }

    private void HookFrame()
    {
        lock (_gate)
        {
            if (_hooked || _disposed) return;
            _hooked = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var ticks = Interlocked.Exchange(ref _pendingTicks, 0);
        if (ticks != 0)
        {
            RecordLatency((Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency);
        }

        // 无后续样本 → 退订渲染回调,回到零开销状态。
        if (Volatile.Read(ref _pendingTicks) == 0)
        {
            lock (_gate)
            {
                if (_hooked && Volatile.Read(ref _pendingTicks) == 0)
                {
                    _hooked = false;
                    CompositionTarget.Rendering -= OnRendering;
                }
            }
        }
    }

    private void RecordLatency(double milliseconds)
    {
        _metrics.Record(new PerformanceSample("input_latency", TimeSpan.FromMilliseconds(milliseconds), Phase: "input"));

        _windowSumMs += milliseconds;
        _windowMaxMs = Math.Max(_windowMaxMs, milliseconds);
        _windowCount++;
        if (_windowCount >= SummaryEvery)
        {
            _metrics.Record(new PerformanceSample(
                "input_latency_summary",
                TimeSpan.FromMilliseconds(_windowSumMs / _windowCount),
                _windowCount,
                Phase: $"max={_windowMaxMs:F1}ms"));
            _windowSumMs = 0;
            _windowMaxMs = 0;
            _windowCount = 0;
        }
    }

    public void Dispose()
    {
        if (Application.Current is not null)
        {
            System.Windows.Input.InputManager.Current.PreProcessInput -= OnPreProcessInput;
        }

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_hooked)
            {
                _hooked = false;
                CompositionTarget.Rendering -= OnRendering;
            }
        }
    }
}
