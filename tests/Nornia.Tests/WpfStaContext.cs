using System.Collections.Concurrent;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Nornia.Tests;

/// <summary>进程级共享的 WPF STA 上下文:WPF 只允许同一进程存在一个
/// <see cref="Application"/> 实例,而 xUnit 默认在同进程内并行跑测试类——因此所有
/// STA 控件测试(SectionCollapse / MarkdownPreviewView 同步 / 大纲跳转集成)共用
/// 这一条 STA 线程与同一个 <c>Nornia.Desktop.App</c>(完整 App.xaml 资源字典;
/// 只构造 + InitializeComponent,不 Run,OnStartup 不执行)。
/// 用法:<c>WpfStaContext.Run(() => { ...控件操作...; WpfStaContext.PumpQueue(); });</c></summary>
public static class WpfStaContext
{
    private static readonly Lazy<Pump> _pump = new(() => new Pump());

    /// <summary>把控件操作排队到共享 STA 线程执行;工作线程内异常会重新抛出。</summary>
    public static void Run(Action work) => _pump.Value.Run(work);

    /// <summary>在共享 STA 线程上泵 Dispatcher 到 Background 优先级(让绑定/延迟滚动/
    /// 节流回调落地)。必须在 <see cref="Run"/> 的闭包内调用。</summary>
    public static void PumpQueue()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
        Dispatcher.PushFrame(frame);
    }

    private sealed class Pump
    {
        private readonly BlockingCollection<(Action Work, ManualResetEventSlim Done, List<Exception> Errors)> _queue = [];

        public Pump()
        {
            var thread = new Thread(() =>
            {
                // ApplicationDefinition 的 InitializeComponent 由 Main() 显式调用(构造函数不调),
                // 手动触发以载入 App.xaml 完整资源字典。
                var app = new Nornia.Desktop.App();
                app.InitializeComponent();
                // 测试 App 不执行 OnStartup(其中才设 OnMainWindowClose):默认
                // OnLastWindowClose 下,任一测试窗口 Show+Close(如侧栏模板测试)会关闭整个
                // App,其后所有 STA 测试全部命中"应用程序对象正在关闭"。测试 App 随进程退出,
                // 显式关闭语义即可。
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // 与真实 UI 线程一致:async 续帖经 DispatcherSynchronizationContext 回排到
                // Dispatcher 队列(由 PumpQueue 驱动),避免 VM 属性变更/视图操作跨线程。
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                foreach (var (work, done, errors) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        work();
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }

                    done.Set();
                }
            })
            {
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Run(Action work)
        {
            using var done = new ManualResetEventSlim(false);
            var errors = new List<Exception>();
            _queue.Add((work, done, errors));
            done.Wait();
            if (errors.Count > 0)
            {
                throw new AggregateException(errors);
            }
        }
    }
}
