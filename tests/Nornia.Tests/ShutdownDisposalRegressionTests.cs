using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;

namespace Nornia.Tests;

/// <summary>关停释放回归:OnExit 期间 WPF 的 UI 调度器已停止泵帧,而设置/键位的后台
/// watch 任务在 UI 线程启动、async 续延绑定该调度器——任何"取消后 join 任务"的
/// Dispose 都会永远等不到续延,实测吞掉整个 5s 关停看门狗预算
/// ("service disposal cut off after 4995ms")。
/// 每个测试用**专用 STA 线程**复现生产条件(安装 DispatcherSynchronizationContext、
/// 任务在该线程启动、随后停止泵帧),并以看门超时断言 Dispose 必须快速返回——
/// 回归(重新加入 join)只挂住该测试自己的后台线程,不会拖垮共享的 WpfStaContext 测试套件。</summary>
public sealed class ShutdownDisposalRegressionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-shutdown-dispose-{Guid.NewGuid():N}");

    public ShutdownDisposalRegressionTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    /// <summary>专用 STA 线程上执行 <paramref name="startWork"/>(此时调度器正常泵帧,模拟启动期),
    /// 然后停止泵帧(模拟 OnExit),在停泵状态下执行 <paramref name="disposeWork"/>;
    /// 若 disposeWork 超过 <paramref name="budget"/> 未返回,以失败而非挂起报告。</summary>
    private static void RunOnDeadDispatcher(
        Action<Dispatcher> startWork,
        Action<Dispatcher> disposeWork,
        TimeSpan budget,
        out TimeSpan disposeElapsed)
    {
        var started = new ManualResetEventSlim(false);
        var disposeDone = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            // 与生产 UI 线程一致:async 续延经 DispatcherSynchronizationContext 回排到调度器。
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            startWork(dispatcher);
            started.Set();
            // —— 此处停止泵帧:调度器不再处理任何排队的操作(= OnExit 之后的状态)。——
            disposeWork(dispatcher);
            disposeDone.Set();
        })
        {
            IsBackground = true,
            Name = "shutdown-dispose-probe",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "启动阶段未在专用线程完成");
        var stopwatch = Stopwatch.StartNew();
        Assert.True(disposeDone.Wait(budget),
            "Dispose 在停止泵帧的调度器上超时——是否在等待绑定 UI 调度器的 watch 任务?(回归:重新 join 了 dispatcher-bound 任务)");
        disposeElapsed = stopwatch.Elapsed;
    }

    private static void PumpOnce(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
        Dispatcher.PushFrame(frame); // 静态方法;必须在目标线程上调用(本方法即于该线程执行)。
    }

    private static void PumpUntil(Dispatcher dispatcher, Task task, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            PumpOnce(dispatcher);
        }

        Assert.True(task.IsCompleted, $"启动期任务未在 {budget} 内完成");
    }

    [Fact]
    public void SettingsSession_DisposeAsync_ReturnsPromptly_WhenDispatcherStopsPumping()
    {
        var service = new ScopedSettingsService(new BuiltInSettingsCatalog(), TempPath("settings.jsonc"));
        ISettingsSession? session = null;
        Task<ISettingsSession>? sessionTask = null;

        RunOnDeadDispatcher(
            dispatcher =>
            {
                sessionTask = service.OpenSessionAsync(new());
                PumpUntil(dispatcher, sessionTask, TimeSpan.FromSeconds(5));
                session = sessionTask.GetAwaiter().GetResult(); // PumpUntil 已保证完成
            },
            dispatcher =>
            {
                Assert.NotNull(session);
                session!.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            },
            TimeSpan.FromSeconds(4),
            out var elapsed);

        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"DisposeAsync 耗时 {elapsed.TotalMilliseconds:0}ms");
    }

    [Fact]
    public void KeybindingService_Dispose_ReturnsPromptly_WhenDispatcherStopsPumping()
    {
        var coordinator = new SettingsChangeCoordinator();
        var keybindingsPath = TempPath("keybindings.json");
        KeybindingService? service = null;

        RunOnDeadDispatcher(
            dispatcher =>
            {
                service = new KeybindingService(new CommandRegistry(), new ContextKeyService(), coordinator);
                // 让 watch 任务走到 ReadAllAsync 挂起点(续延已绑定该调度器)。
                PumpOnce(dispatcher);
            },
            dispatcher =>
            {
                Assert.NotNull(service);
                service!.Dispose();
            },
            TimeSpan.FromSeconds(4),
            out var elapsed);

        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Dispose 耗时 {elapsed.TotalMilliseconds:0}ms");
        coordinator.Dispose();
    }

    [Fact]
    public void ScopedSettingsService_DisposeAsync_ReturnsPromptly_WhenDispatcherStopsPumping()
    {
        var service = new ScopedSettingsService(new BuiltInSettingsCatalog(), TempPath("settings-outer.jsonc"));

        RunOnDeadDispatcher(
            dispatcher =>
            {
                // 构造函数启动 _externalChanges(绑定该调度器);泵一次使其进入挂起点。
                PumpOnce(dispatcher);
            },
            dispatcher => service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(),
            TimeSpan.FromSeconds(4),
            out var elapsed);

        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"DisposeAsync 耗时 {elapsed.TotalMilliseconds:0}ms");
    }
}
