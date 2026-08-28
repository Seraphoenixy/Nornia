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

    private WingetCapabilities? _cached;
    private int _probesRun;

    public async Task<WingetCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _probesRun) == 0)
        {
            var versionTask = ProbeVersionAsync(cancellationToken);
            var structuredTask = ProbeStructuredOutputAsync(cancellationToken);
            var version = await versionTask;
            var hasStructuredOutput = await structuredTask;
            Update(current => new WingetCapabilities(version, hasStructuredOutput, current.OutputLanguage, current.NoMatchObservedCodes));
            Volatile.Write(ref _probesRun, 1);
        }

        return _cached ?? WingetCapabilities.Default;
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

    private void Update(Func<WingetCapabilities, WingetCapabilities> update)
    {
        var current = _cached ?? WingetCapabilities.Default;
        Volatile.Write(ref _cached, update(current));
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