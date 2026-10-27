using System.Windows;
using System.Windows.Threading;
using Timer = System.Threading.Timer;

namespace Nornia.Desktop.Services;

/// <summary>UI 线程排队优先级。刻意不暴露 WPF 的 <c>DispatcherPriority</c>,避免抽象把 WPF
/// 类型泄露到 ViewModel 层(这正是本抽象存在的意义:VM 可以脱离 STA 宿主单测)。</summary>
public enum UiDispatcherPriority
{
    /// <summary>默认优先级:常规状态更新。</summary>
    Normal,

    /// <summary>输入优先级:去抖后的筛选/重排,保证在下一次输入被处理前完成。</summary>
    Input,

    /// <summary>空闲优先级:预热、后台推导结果回填等可延迟工作。</summary>
    ApplicationIdle,
}

/// <summary>UI 定时器抽象。<c>DispatcherTimer</c> 的 Start/Stop 语义是去抖实现的核心(每次
/// 击键都要重启计时),因此这里保留同样的操作面,而不是退化成一次性回调。</summary>
public interface IUiTimer : IDisposable
{
    void Start();
    void Stop();
}

/// <summary>UI 线程调度抽象。<b>ViewModel 一律经此回到 UI 线程,不要直接读
/// <c>Application.Current.Dispatcher</c> / <c>Dispatcher.CurrentDispatcher</c></b>——那条路径会
/// 把 VM 绑死在 STA 宿主上,且完全无法单测。该约束由架构守卫测试强制
/// (见 <c>ArchitectureGuardrailTests</c>)。</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void BeginInvoke(Action action);

    /// <summary>按 <paramref name="priority"/> 排队。优先级只是提示,简单实现可以忽略它
    /// (默认实现转成无优先级排队)。</summary>
    void BeginInvoke(Action action, UiDispatcherPriority priority) => BeginInvoke(action);

    /// <summary>在 UI 线程执行并等待完成;无 WPF 宿主时内联执行。</summary>
    Task InvokeAsync(Action action);

    /// <summary>按 <paramref name="priority"/> 在 UI 线程执行并等待完成。默认实现忽略优先级。</summary>
    Task InvokeAsync(Action action, UiDispatcherPriority priority) => InvokeAsync(action);

    /// <summary>是否存在真正的 UI 宿主(WPF 应用)。为 false 表示没有消息泵,调用方需要
    /// "提前返回/内联"的语义时据此判断,而不是依赖优先级。</summary>
    bool IsHostAvailable { get; }

    /// <summary>UI 定时器(去抖等)。每次触发走 UI 线程;无 WPF 宿主时退化为线程池定时器,
    /// 仍保留"延迟 + 可重启"的语义。</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick, UiDispatcherPriority priority = UiDispatcherPriority.Normal);
}

/// <summary>绑定到<b>创建线程</b>的调度器:排队回调回到构造它的那个线程执行。
///
/// <para>这是 ViewModel 应当使用的实现。原因是 <see cref="WpfUiDispatcher"/> 在
/// <c>Application.Current</c> 为 null 时会把 <see cref="CheckAccess"/> 判为 true 并内联执行——
/// 对"无宿主就内联"的服务(文件监视器等)是对的,但对持有 <c>CollectionView</c> 的 VM 是错的:
/// 那些绑定集合<b>只能在创建它们的调度器线程上</b>变更,内联到线程池线程会直接抛
/// <c>NotSupportedException</c>(历史上这里用的是 <c>Dispatcher.CurrentDispatcher</c>,
/// 语义正是"回到我自己那个 UI 线程")。</para>
///
/// <para>用 <see cref="Dispatcher.FromThread"/> 而不是 <c>Dispatcher.CurrentDispatcher</c>:
/// 后者会为一个本来没有消息泵的线程<b>凭空创建</b>调度器,接着 <c>InvokeAsync</c> 将永远等不到
/// 回调(测试必挂)。没有调度器的线程上退化为内联,与既有降级行为一致。</para></summary>
public sealed class ThreadAffineUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher? _dispatcher = Dispatcher.FromThread(Thread.CurrentThread);

    public bool IsHostAvailable => _dispatcher is not null;

    public bool CheckAccess() => _dispatcher?.CheckAccess() ?? true;

    public void BeginInvoke(Action action)
    {
        if (_dispatcher is null)
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }

    public void BeginInvoke(Action action, UiDispatcherPriority priority)
    {
        if (_dispatcher is null)
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(Translate(priority), action);
    }

    public Task InvokeAsync(Action action)
    {
        if (_dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    public Task InvokeAsync(Action action, UiDispatcherPriority priority)
    {
        if (_dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action, Translate(priority)).Task;
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action tick, UiDispatcherPriority priority = UiDispatcherPriority.Normal) =>
        _dispatcher is null
            ? new ThreadPoolUiTimer(interval, tick)
            : new WpfUiTimer(interval, tick, Translate(priority), _dispatcher);

    internal static DispatcherPriority Translate(UiDispatcherPriority priority) => priority switch
    {
        UiDispatcherPriority.Input => DispatcherPriority.Input,
        UiDispatcherPriority.ApplicationIdle => DispatcherPriority.ApplicationIdle,
        _ => DispatcherPriority.Normal,
    };
}

/// <summary>WPF 宿主下的真实定时器:在 UI 线程上按优先级触发。</summary>
internal sealed class WpfUiTimer : IUiTimer
{
    private readonly DispatcherTimer _timer;

    public WpfUiTimer(TimeSpan interval, Action tick, DispatcherPriority priority, Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(priority, dispatcher) { Interval = interval };
        _timer.Tick += (_, _) => tick();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose() => _timer.Stop();
}

/// <summary>无 WPF 宿主(单测)时的退化定时器:线程池一次性定时器,保留可重启语义。</summary>
internal sealed class ThreadPoolUiTimer : IUiTimer
{
    private readonly TimeSpan _interval;
    private readonly Action _tick;
    private Timer? _timer;

    public ThreadPoolUiTimer(TimeSpan interval, Action tick)
    {
        _interval = interval;
        _tick = tick;
    }

    public void Start()
    {
        _timer ??= new Timer(_ => _tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(_interval, Timeout.InfiniteTimeSpan);
    }

    public void Stop() => _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Dispose() => Interlocked.Exchange(ref _timer, null)?.Dispose();
}

/// <summary>Default dispatcher backed by the WPF application dispatcher; degrades to direct
/// invocation when no application is running (e.g. under unit tests). 适用于"没有宿主就内联即可"
/// 的无状态服务;持有绑定集合的 ViewModel 请用 <see cref="ThreadAffineUiDispatcher"/>。</summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public bool IsHostAvailable => Application.Current is not null;

    public bool CheckAccess() => Application.Current?.Dispatcher.CheckAccess() ?? true;

    public void BeginInvoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    public void BeginInvoke(Action action, UiDispatcherPriority priority)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(ThreadAffineUiDispatcher.Translate(priority), action);
    }

    public Task InvokeAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    public Task InvokeAsync(Action action, UiDispatcherPriority priority)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, ThreadAffineUiDispatcher.Translate(priority)).Task;
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action tick, UiDispatcherPriority priority = UiDispatcherPriority.Normal)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null
            ? new ThreadPoolUiTimer(interval, tick)
            : new WpfUiTimer(interval, tick, ThreadAffineUiDispatcher.Translate(priority), dispatcher);
    }
}
