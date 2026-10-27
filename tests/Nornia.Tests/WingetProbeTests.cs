using Nornia.Core.Models;
using Nornia.Package.Providers;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>
/// Winget 能力探测。Core 契约是「每进程最多探测一次」:原先的 once-guard 用
/// <see cref="System.Threading.Volatile"/> 读写做守卫且 none 更新是全过程的读-改-写,
/// 并发首调会同时进入并各自派生 winget 进程,观测结果也会互相覆盖。
/// </summary>
public sealed class WingetProbeTests
{
    [Fact]
    public async Task GetCapabilitiesAsync_ConcurrentCallersTriggerExactlyOneProbe()
    {
        // 用闸门让两个探测命令真正在飞行中:若 once-guard 失效,八个调用者会同时越过守卫。
        var gate = new TaskCompletionSource();
        var runner = new GatedProcessRunner(gate.Task);
        var probe = new WingetProbe(runner);

        var waiters = Enumerable.Range(0, 8).Select(_ => probe.GetCapabilitiesAsync()).ToArray();
        Assert.Equal(2, runner.CallCount);

        gate.SetResult();
        var results = await Task.WhenAll(waiters);

        Assert.Equal(2, runner.CallCount);
        Assert.All(results, capabilities => Assert.Equal(new Version(1, 10, 340), capabilities.Version));
        Assert.All(results, capabilities => Assert.True(capabilities.HasStructuredOutput));
    }

    [Fact]
    public async Task GetCapabilitiesAsync_SequentialCallsReuseTheSameResult()
    {
        var runner = new CountingProcessRunner();
        var probe = new WingetProbe(runner);

        var first = await probe.GetCapabilitiesAsync();
        var second = await probe.GetCapabilitiesAsync();

        Assert.Equal(2, runner.Calls.Count);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task ObservationsSurviveSubsequentProbeReads()
    {
        var runner = new CountingProcessRunner();
        var probe = new WingetProbe(runner);

        var probed = await probe.GetCapabilitiesAsync();
        Assert.Equal(new Version(1, 10, 340), probed.Version);
        Assert.True(probed.HasStructuredOutput);

        probe.ObserveNoMatchExitCode(WingetExitCodes.LegacyNoMatch);
        probe.ObserveHeaderLanguage("zh-Hans");

        // 观测由 Provider 在解析真实输出时上报,必须并入后续读取,
        // 既不能被探测写回覆盖,也不会让探测重跑一次。
        var capabilities = await probe.GetCapabilitiesAsync();

        Assert.Equal("zh-Hans", capabilities.OutputLanguage);
        Assert.Contains(WingetExitCodes.LegacyNoMatch, capabilities.NoMatchObservedCodes);
        Assert.Equal(new Version(1, 10, 340), capabilities.Version);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task ObserveNoMatchExitCode_ConcurrentWritesKeepEveryDistinctCode()
    {
        var runner = new CountingProcessRunner();
        var probe = new WingetProbe(runner);

        Parallel.For(0, 32, index => probe.ObserveNoMatchExitCode(WingetExitCodes.LegacyNoMatch + index));

        var capabilities = await probe.GetCapabilitiesAsync();

        Assert.Equal(32, capabilities.NoMatchObservedCodes.Count);
    }

    [Fact]
    public async Task CancellingTheWaiterDoesNotPoisonLaterCallers()
    {
        var runner = new CountingProcessRunner();
        var probe = new WingetProbe(runner);
        using var cancellation = new CancellationTokenSource();

        _ = probe.GetCapabilitiesAsync(cancellation.Token);
        cancellation.Cancel();

        // 第一个等待者被取消,但探测任务本身仍在跑,后续调用者必须仍能拿到完整结果。
        var capabilities = await probe.GetCapabilitiesAsync();

        Assert.Equal(new Version(1, 10, 340), capabilities.Version);
        Assert.True(capabilities.HasStructuredOutput);
    }

    [Fact]
    public async Task FallbackCapabilitiesStayAvailableWhenProbeCommandsFail()
    {
        var runner = new CountingProcessRunner(shouldSucceed: false);
        var probe = new WingetProbe(runner);

        var capabilities = await probe.GetCapabilitiesAsync();

        Assert.Null(capabilities.Version);
        Assert.False(capabilities.HasStructuredOutput);
    }

    private sealed class CountingProcessRunner(bool shouldSucceed = true) : global::Nornia.Core.Interfaces.IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IProgress<ProcessOutput>? progress = null,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            int? maximumOutputBytes = null)
        {
            lock (Calls)
            {
                Calls.Add(arguments);
            }

            if (!shouldSucceed)
            {
                return Task.FromResult(new ProcessResult(-1, string.Empty, "winget not found"));
            }

            var output = arguments.Contains("--version")
                ? "v1.10.340"
                : "--output  Enables support for output logic";
            return Task.FromResult(new ProcessResult(0, output, string.Empty));
        }

        public Task<ProcessRawResult> RunRawAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            var text = arguments.Contains("--version") ? "v1.10.340" : "--output";
            return Task.FromResult(new ProcessRawResult(0, System.Text.Encoding.UTF8.GetBytes(text), string.Empty));
        }

        public Task<ProcessResult> RunShellAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }

    /// <summary>探测命令挂在闸门上,用来让并发调用者真正同时处于飞行中 —— 同步返回的
    /// fake 会让 probe 在同一调用里跑完,那样根本验证不到 once-guard 的竞态。</summary>
    private sealed class GatedProcessRunner(Task gate) : global::Nornia.Core.Interfaces.IProcessRunner
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IProgress<ProcessOutput>? progress = null,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            int? maximumOutputBytes = null)
        {
            Interlocked.Increment(ref _callCount);
            await gate.ConfigureAwait(false);
            var output = arguments.Contains("--version") ? "v1.10.340" : "--output  Enables output logic";
            return new ProcessResult(0, output, string.Empty);
        }

        public async Task<ProcessRawResult> RunRawAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            await gate.ConfigureAwait(false);
            return new ProcessRawResult(0, System.Text.Encoding.UTF8.GetBytes("v1.10.340"), string.Empty);
        }

        public async Task<ProcessResult> RunShellAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            await gate.ConfigureAwait(false);
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }
}
