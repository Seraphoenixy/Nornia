using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.Tests.Fakes;

internal sealed class FakeProcessRunner(
    Func<string, IReadOnlyList<string>, ProcessResult> handler,
    Func<string, IReadOnlyList<string>, byte[]>? rawHandler = null) : IProcessRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment)> Calls { get; } = [];

    /// <summary>Raw-bytes path: uses <c>rawHandler</c> when provided (legacy-encoded payloads can
    /// only be faked at the byte level), otherwise re-encodes the text handler's output (lossless
    /// for the ASCII/UTF-8 fixtures the tests produce).</summary>
    public Task<ProcessRawResult> RunRawAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((fileName, arguments, environmentVariables));
        var result = handler(fileName, arguments);
        var bytes = rawHandler is null
            ? new System.Text.UTF8Encoding(false).GetBytes(result.StandardOutput)
            : rawHandler(fileName, arguments);
        return Task.FromResult(new ProcessRawResult(result.ExitCode, bytes, result.StandardError));
    }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        int? maximumOutputBytes = null)
    {
        Calls.Add((fileName, arguments, environmentVariables));
        var result = handler(fileName, arguments);
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            progress?.Report(new ProcessOutput(line, false));
        }

        foreach (var line in result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            progress?.Report(new ProcessOutput(line, true));
        }

        return Task.FromResult(result);
    }
}
