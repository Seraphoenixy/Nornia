namespace Nornia.Core.Models;

public sealed record ProcessOutput(string Text, bool IsError);

/// <summary>One streamed process line, or the terminal event carrying the exit code.</summary>
public sealed record ProcessStreamEvent(string? Text, bool IsError, int? ExitCode = null, bool OutputLimitReached = false)
{
    public bool IsCompleted => ExitCode.HasValue;
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool OutputTruncated = false)
{
    public bool IsSuccess => ExitCode == 0;
}

/// <summary>Result of a process whose stdout must not be decoded: the raw bytes are returned
/// untouched so legacy-encoded payloads (e.g. a GBK file's diff content) can be passed through
/// byte-identical instead of round-tripping through a fixed text encoding.</summary>
public sealed record ProcessRawResult(int ExitCode, byte[] StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}
