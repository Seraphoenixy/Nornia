namespace Nornia.Core.Coalescing;

/// <summary>
/// 值去重器(VS Code Event.latch 的对应物):仅当值与上次发布不同才触发
/// <see cref="Changed"/>。用于"高频同值事件"(滚动位置、进度百分比)的降噪。
/// </summary>
public sealed class ValueLatch<T>
{
    private T? _last;
    private bool _hasLast;
    private readonly EqualityComparer<T> _comparer;

    public ValueLatch(EqualityComparer<T>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public event Action<T>? Changed;

    public T? Last { get; private set; }

    public bool HasValue { get; private set; }

    /// <summary>提交候选值;与上次不同时触发 Changed 并返回 true。</summary>
    public bool Push(T value)
    {
        lock (this)
        {
            if (_hasLast && _comparer.Equals(_last, value))
            {
                return false;
            }

            _last = value;
            _hasLast = true;
            Last = value;
            HasValue = true;
        }

        Changed?.Invoke(value);
        return true;
    }

    public void Reset()
    {
        lock (this)
        {
            _hasLast = false;
            _last = default;
            HasValue = false;
            Last = default;
        }
    }
}

/// <summary>
/// 尾部去抖合并器(VS Code Event.debounce 的对应物):每次 <see cref="Raise"/>
/// 重置计时;静默 <paramref name="delayMs"/> 后,用 <paramref name="merge"/>
/// 把批内所有值合并成一个,触发 <see cref="Debounced"/>。线程安全。
/// 典型用法:搜索输入 100ms 去抖;进度 250ms 合并。
/// </summary>
public sealed class DebouncedCoalescer<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly RunOnceScheduler _scheduler;
    private T? _pending;
    private bool _hasPending;
    private readonly Func<T?, T, T> _merge;
    private readonly int _delayMs;

    public DebouncedCoalescer(int delayMs, Func<T?, T, T>? merge = null)
    {
        _delayMs = delayMs;
        _merge = merge ?? ((_, current) => current!);
        _scheduler = new RunOnceScheduler(delayMs)
        {
            Action = () =>
            {
                T value;
                lock (_gate)
                {
                    value = _pending!;
                    _hasPending = false;
                    _pending = default;
                }

                Debounced?.Invoke(value);
            }
        };
        OnError = _scheduler.OnError;
        _scheduler.OnError = ex => OnError?.Invoke(ex);
    }

    /// <summary>处理抛出的异常钩子。</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>静默期结束后触发,参数为批内合并后的值。</summary>
    public event Action<T>? Debounced;

    /// <summary>记录一次事件并重置静默计时。</summary>
    public void Raise(T value)
    {
        lock (_gate)
        {
            _pending = _hasPending ? _merge(_pending!, value) : value;
            _hasPending = true;
        }

        _scheduler.Schedule(_delayMs);
    }

    /// <summary>立即发布积压(若排程中)。</summary>
    public void Flush() => _scheduler.Flush();

    public void Dispose() => _scheduler.Dispose();
}
