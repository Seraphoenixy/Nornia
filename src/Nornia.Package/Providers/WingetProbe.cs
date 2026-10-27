using Nornia.Core.Interfaces;
using System.Text.RegularExpressions;

namespace Nornia.Package.Providers;

/// <summary>Snapshot of the installed winget CLI's observable capabilities.</summary>
public sealed record WingetCapabilities(
    Version? Version,
    bool HasStructuredOutput,
    string? OutputLanguage,
    IReadOnlySet<int> NoMatchObservedCodes)
{
    public static WingetCapabilities Default { get; } = new(null, false, null, new HashSet<int>());
}

/// <summary>
/// Lazily probes the winget CLI at most once per process and accumulates observations from real
/// invocations. The fast probes (version, structured-output support) run once; the output language
/// and the live no-match exit code are recorded from the provider's actual search/list calls so the
/// hot path never starts extra winget processes.
/// </summary>
public sealed class WingetProbe(IProcessRunner processRunner)
{
    private static readonly Regex VersionRegex = new(@"v?(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    private readonly object _sync = new();

    private WingetCapabilities? _cached;
    private Task<WingetCapabilities>? _probeTask;

    public async Task<WingetCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        Task<WingetCapabilities> probe;
        lock (_sync)
        {
            // 探测任务只创建一次并共享:并发首调不会各自派生 winget 进程。
            probe = _probeTask ??= RunProbeAsync();
        }

        // WaitAsync 只放弃等待,不会取消正在跑的探测任务 —— 后续调用者仍能复用它的结果。
        if (cancellationToken.CanBeCanceled)
        {
            await probe.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await probe.ConfigureAwait(false);
        }

        // 返回当前快照,而不是探测任务的结果:探测完成与本次调用之间存在时间差,
        // 期间由 Provider 并发上报的 Observe* 观测不能被丢掉。
        return Snapshot();
    }

    private async Task<WingetCapabilities> RunProbeAsync()
    {
        // 探测归 probe 自身所有,因此不受任一调用方的 CancellationToken 影响;
        // 否则首个调用者取消会让所有后续调用者拿到同一个已取消的任务。
        var versionTask = ProbeVersionAsync(CancellationToken.None);
        var structuredTask = ProbeStructuredOutputAsync(CancellationToken.None);
        await Task.WhenAll(versionTask, structuredTask).ConfigureAwait(false);

        lock (_sync)
        {
            _cached = (_cached ?? WingetCapabilities.Default) with
            {
                Version = versionTask.Result,
                HasStructuredOutput = structuredTask.Result
            };
        }

        return Snapshot();
    }

    /// <summary>Records the header language observed in a real list/search output.</summary>
    public void ObserveHeaderLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        Update(current => current.OutputLanguage == language ? current : current with { OutputLanguage = language });
    }

    /// <summary>Records an exit code the real CLI returned for a no-match query. Observations made
    /// before the first probe run are merged into the base capabilities, never dropped.</summary>
    public void ObserveNoMatchExitCode(int exitCode)
    {
        Update(current => current.NoMatchObservedCodes.Contains(exitCode)
            ? current
            : current with { NoMatchObservedCodes = new HashSet<int>(current.NoMatchObservedCodes) { exitCode } });
    }

    private WingetCapabilities Snapshot()
    {
        lock (_sync)
        {
            return _cached ?? WingetCapabilities.Default;
        }
    }

    private void Update(Func<WingetCapabilities, WingetCapabilities> update)
    {
        // 与 Snapshot / RunProbeAsync 共用同一把锁:并发观测不会再互相覆盖。
        lock (_sync)
        {
            _cached = update(_cached ?? WingetCapabilities.Default);
        }
    }

    private async Task<Version?> ProbeVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunAsync("winget.exe", ["--version"], cancellationToken: cancellationToken);
            if (!result.IsSuccess)
            {
                return null;
            }

            var match = VersionRegex.Match(result.StandardOutput.Trim());
            return match.Success ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)) : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<bool> ProbeStructuredOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunAsync("winget.exe", ["list", "--help"], cancellationToken: cancellationToken);
            return result.IsSuccess && result.StandardOutput.Contains("--output", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}