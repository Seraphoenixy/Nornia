using System.Collections.Concurrent;

namespace Nornia.Core.Coalescing;

/// <summary>
/// 一次性调度器(VS Code RunOnceScheduler 的 .NET 对应物):多次 <see cref="Schedule"/>
/// 合并为延迟到期后的**一次**执行,每次 Schedule 重置计时(尾部去抖)。线程安全。
/// 用于"事件风暴 → 每批一次处理"的路径(文件监视、滚动重绘、UI 进度)。
/// </summary>
public sealed class RunOnceScheduler : IDisposable
{
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private bool _disposed;

    /// <summary>到点后执行的处理;执行期间重入 <see cref="Schedule"/> 会安排下一次。</summary>
    public Action? Action { get; set; }

    /// <summary>处理抛出的异常(默认不吞:记录到 <see cref="LastException"/>)。可挂接日志。</summary>
    public Action<Exception>? OnError { get; set; }

    public Exception? LastException { get; private set; }

    public RunOnceScheduler(int delayMs)
    {
        if (delayMs < 0) throw new ArgumentOutOfRangeException(nameof(delayMs));
        DelayMs = delayMs;
    }

    public int DelayMs { get; set; }

    public bool IsScheduled { get; private set; }

    /// <summary>安排(或重置)一次延迟执行;已安排时重新计时。</summary>
    public void Schedule(int? delayMs = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (delayMs is { } delay && delay != DelayMs)
            {
                DelayMs = delay;
            }

            if (_timer is null)
            {
                IsScheduled = true;
                _timer = new System.Threading.Timer(OnTimer, null, DelayMs, Timeout.Infinite);
            }
            else
            {
                _timer.Change(DelayMs, Timeout.Infinite);
            }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_timer is not null)
            {
                _timer.Dispose();
                _timer = null;
                IsScheduled = false;
            }
        }
    }

    /// <summary>若已安排则立即执行(并取消排程)。</summary>
    public void Flush()
    {
        Action? action;
        lock (_gate)
        {
            if (_timer is null) return;
            _timer.Dispose();
            _timer = null;
            IsScheduled = false;
            action = Action;
        }

        Run(action);
    }

    private void OnTimer(object? state)
    {
        Action? action;
        lock (_gate)
        {
            if (_timer is not null)
            {
                _timer.Dispose();
                _timer = null;
                IsScheduled = false;
            }

            action = Action;
        }

        Run(action);
    }

    private void Run(Action? action)
    {
        if (action is null) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LastException = ex;
            OnError?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Action = null;
            OnError = null;
            if (_timer is not null)
            {
                _timer.Dispose();
                _timer = null;
                IsScheduled = false;
            }
        }
    }
}

/// <summary>
/// 工作聚合器(VS Code RunOnceWorker):<see cref="Work"/> 累积工作单元,
/// 延迟到期后把**整批**单元交给处理(批内只跑一次)。线程安全。
/// 典型用法:文件变更事件 75ms 聚合后一次性处理。
/// </summary>
public sealed class RunOnceWorker<T> : IDisposable
{
    private readonly RunOnceScheduler _scheduler;
    private readonly ConcurrentQueue<T> _units = new();
    private readonly Action<IReadOnlyList<T>> _handler;

    public RunOnceWorker(Action<IReadOnlyList<T>> handler, int delayMs)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _scheduler = new RunOnceScheduler(delayMs)
        {
            Action = () =>
            {
                var batch = _units.ToArray();
                while (_units.TryDequeue(out _)) { }
                if (batch.Length > 0)
                {
                    _handler(batch);
                }
            }
        };
        OnError = _scheduler.OnError;
        _scheduler.OnError = ex => OnError?.Invoke(ex);
    }

    /// <summary>处理抛出的异常钩子。</summary>
    public Action<Exception>? OnError { get; set; }

    public bool IsScheduled => _scheduler.IsScheduled;

    /// <summary>当前积压的单元数(用于背压判断)。</summary>
    public int PendingCount => _units.Count;

    public void Work(T unit)
    {
        _units.Enqueue(unit);
        _scheduler.Schedule();
    }

    /// <summary>立即处理积压(若排程中)。</summary>
    public void Flush() => _scheduler.Flush();

    public void Dispose() => _scheduler.Dispose();
}

/// <summary>
/// 带背压的分块消费者(VS Code ThrottledWorker):
/// 每批至多 <see cref="MaxChunkSize"/> 个单元,批间休息 <see cref="RestMs"/>;
/// 积压超过 <see cref="MaxBuffered"/> 时丢弃最旧单元(防止洪峰无限占内存)。线程安全。
/// 典型用法:watcher 事件"每批 ≤100 条、批间 200ms、缓冲上限 10000"。
/// </summary>
public sealed class ThrottledWorker<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<T> _pending = new();
    private readonly int _maxChunkSize;
    private readonly int _restMs;
    private readonly int _maxBuffered;
    private readonly Action<IReadOnlyList<T>> _handler;
    private RunOnceScheduler? _restScheduler;
    private volatile bool _processing;
    private bool _disposed;

    /// <summary>因缓冲上限被丢弃的单元计数(可观测性)。</summary>
    public long DroppedCount { get; private set; }

    public ThrottledWorker(int maxChunkSize, int restMs, int? maxBuffered, Action<IReadOnlyList<T>> handler)
    {
        if (maxChunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunkSize));
        if (restMs < 0) throw new ArgumentOutOfRangeException(nameof(restMs));
        if (maxBuffered is < 0) throw new ArgumentOutOfRangeException(nameof(maxBuffered));
        _maxChunkSize = maxChunkSize;
        _restMs = restMs;
        _maxBuffered = maxBuffered ?? int.MaxValue;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>处理抛出的异常钩子。</summary>
    public Action<Exception>? OnError { get; set; }

    public int PendingCount => _pending.Count;

    /// <summary>提交一个工作单元;必要时启动下一轮(批 + 休息)。</summary>
    public void Work(T unit)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.Enqueue(unit);
            // 背压:缓冲超限时丢弃最旧单元。
            while (_pending.Count > _maxBuffered && _pending.TryDequeue(out _))
            {
                DroppedCount++;
            }

            if (_processing) return;
            _processing = true;
        }

        ProcessNext();
    }

    private void ProcessNext()
    {
        // 取一个分块(不阻塞调用方)。
        var chunk = new T[_maxChunkSize];
        var count = 0;
        while (count < _maxChunkSize && _pending.TryDequeue(out var item))
        {
            chunk[count++] = item;
        }

        if (count > 0)
        {
            try
            {
                _handler(chunk.AsSpan(0, count).ToArray());
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
        }

        bool hasMore;
        lock (_gate)
        {
            hasMore = _pending.Count > 0;
            if (!hasMore)
            {
                _processing = false;
            }
        }

        if (!hasMore) return;

        // 批间休息后再取下一批;休息期间的新 Work 不会另起线程(仍在 _processing)。
        if (_restMs <= 0)
        {
            ProcessNext();
            return;
        }

        var rest = new RunOnceScheduler(_restMs) { Action = () => ProcessNext() };
        rest.OnError = ex => OnError?.Invoke(ex);
        lock (_gate)
        {
            if (_disposed || !_processing)
            {
                rest.Dispose();
                return;
            }

            _restScheduler = rest;
        }

        rest.Schedule();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _processing = false;
            _restScheduler?.Dispose();
            _restScheduler = null;
        }
    }
}
