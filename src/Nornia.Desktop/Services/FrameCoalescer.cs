using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nornia.Desktop.Services;

/// <summary>
/// 每帧合并调度器(WPF 的 requestAnimationFrame 等价物,对照 VS Code 的
/// dom.scheduleAtNextAnimationFrame / AnimationFrameScheduler):
/// 多次 <see cref="Schedule"/> 在下一个渲染帧**合并为一次**批量执行。
/// 空闲时不订阅任何事件(零开销);有积压时才挂 CompositionTarget.Rendering。
/// 动作在 UI 线程执行。用于滚动驱动的概览标尺/sticky/minimap 等
/// "高频事件 → 每帧一份"路径。
/// </summary>
public sealed class FrameCoalescer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly object _gate = new();
    private bool _subscribed;
    private bool _disposed;

    public FrameCoalescer(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher
            ?? Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>安排一个动作在下一渲染帧执行;同帧内的多次安排只产生一次批量执行。</summary>
    public void Schedule(Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queue.Enqueue(action);
            if (!_subscribed)
            {
                _subscribed = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }
    }

    public int PendingCount => _queue.Count;

    private void OnRendering(object? sender, EventArgs e)
    {
        // 取走本帧前已排入的全部动作(执行中新增的留到下一帧,避免无界循环)。
        var batch = new List<Action>();
        while (_queue.TryDequeue(out var action))
        {
            batch.Add(action);
        }

        foreach (var action in batch)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
        }

        lock (_gate)
        {
            if (_queue.IsEmpty && _subscribed)
            {
                _subscribed = false;
                CompositionTarget.Rendering -= OnRendering;
            }
        }
    }

    /// <summary>动作抛出的异常钩子(默认不吞,仅记录,避免单个动作炸掉整帧)。</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>立即执行积压(测试 / 窗口关闭前刷帧用)。</summary>
    public void Pump()
    {
        OnRendering(null, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_subscribed)
            {
                _subscribed = false;
                CompositionTarget.Rendering -= OnRendering;
            }
        }
    }
}
