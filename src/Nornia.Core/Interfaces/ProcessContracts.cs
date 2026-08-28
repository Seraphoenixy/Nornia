using Nornia.Core.Models;

namespace Nornia.Core.Interfaces;

public interface IProcessRunner
{
    /// <summary>Runs the process to completion and buffers its output.
    /// <paramref name="environmentVariables"/> are added to the child's environment (use for
    /// per-invocation git configuration such as <c>GIT_OPTIONAL_LOCKS=0</c>).
    /// <paramref name="maximumOutputBytes"/> caps the bytes retained per stream: reading continues
    /// (the child must never block on a full pipe) but excess output is dropped and
    /// <see cref="ProcessResult.OutputTruncated"/> is set.</summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        int? maximumOutputBytes = null);

    /// <summary>Runs the process to completion and returns stdout as <em>raw bytes</em> (no
    /// decoding). This compatibility body re-encodes the decoded output — lossless for the
    /// UTF-8/ASCII payloads the test doubles produce; the production
    /// <see cref="Nornia.Core.Services.ProcessRunner"/> reads the pipe bytes directly.</summary>
    async Task<ProcessRawResult> RunRawAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(fileName, arguments, cancellationToken: cancellationToken, environmentVariables: environmentVariables).ConfigureAwait(false);
        return new ProcessRawResult(result.ExitCode, new System.Text.UTF8Encoding(false).GetBytes(result.StandardOutput), result.StandardError);
    }

    /// <summary>Streams stdout/stderr without retaining complete output. Implementers should emit a
    /// final event with ExitCode; this compatibility body keeps existing test doubles source-safe.</summary>
    async IAsyncEnumerable<ProcessStreamEvent> StreamLinesAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int maximumLines = 1_000_000,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(fileName, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        var count = 0;
        using var output = new StringReader(result.StandardOutput);
        while (output.ReadLine() is { } line)
        {
            if (++count > maximumLines) break;
            yield return new ProcessStreamEvent(line, false);
        }

        using var error = new StringReader(result.StandardError);
        while (error.ReadLine() is { } line)
        {
            if (++count > maximumLines) break;
            yield return new ProcessStreamEvent(line, true);
        }

        yield return new ProcessStreamEvent(null, false, result.ExitCode, count > maximumLines);
    }

    /// <summary>Like <see cref="StreamLinesAsync"/> but for text output that may carry a legacy
    /// Chinese encoding (git diff content lines carry the file's raw bytes): the output is
    /// validated as strict UTF-8 over the whole payload and decoded as GB18030 when it is not
    /// clean, so a GBK/ANSI file does not garble under a fixed UTF-8 decode. This compatibility
    /// body delegates to <see cref="StreamLinesAsync"/> to keep existing test doubles source-safe;
    /// the production <see cref="Nornia.Core.Services.ProcessRunner"/> implements the fallback.</summary>
    async IAsyncEnumerable<ProcessStreamEvent> StreamLinesWithTextFallbackAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int maximumLines = 1_000_000,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamLinesAsync(fileName, arguments, maximumLines, environmentVariables, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }
}

/// <summary>Allows a host to stop processes it started during application shutdown.</summary>
public interface IProcessRunnerShutdown
{
    /// <summary>Requests termination of every active process. Returns immediately: the
    /// potentially multi-second process-tree kill is confirmed in the background so the caller's
    /// shutdown budget is not consumed by per-tree waits.</summary>
    void StopActiveProcesses();
}

/// <summary>环境修复步骤明细的持久化存储(实现为数据目录下的 JSON Lines 文件,不入数据库)。
/// 每个修复步骤随执行实时追加;回滚引擎按 correlation_id 读回步骤序列做逆向还原。</summary>
public interface IEnvironmentRepairLogRepository
{
    Task AppendAsync(EnvironmentRepairLogEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EnvironmentRepairLogEntry>> GetByCorrelationAsync(Guid correlationId, CancellationToken cancellationToken = default);
}

public interface IEnvironmentRollbackService
{
    Task<EnvironmentRollbackPlan?> BuildPlanAsync(Guid correlationId, CancellationToken cancellationToken = default);
    Task RollbackAsync(EnvironmentRollbackPlan plan, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
}
