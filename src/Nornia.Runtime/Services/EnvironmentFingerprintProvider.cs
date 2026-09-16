using Microsoft.Win32;
using Nornia.Core.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace Nornia.Runtime.Services;

/// <summary>
/// Computes a cheap, process-spawn-free fingerprint of the machine environment: the raw PATH
/// string, the resolved locations of the command-based tool candidates (dotnet/node/python/git/java,
/// the same inputs <see cref="Providers.CommandRuntimeProviderBase"/> detects against) and the Visual
/// C++ Redistributable registry versions. A scan-persisted fingerprint that still matches therefore
/// means the structural environment is unchanged, and the persisted inventory snapshot can be served
/// without spawning a single process. Uncovered changes (e.g. a dotnet SDK upgrade) are bounded by
/// the persisted-snapshot TTL and by explicit refreshes. Any failure returns null, which callers
/// must treat as "environment changed" so a broken probe can only ever cause extra scans, never
/// stale data being presented as fresh.
/// </summary>
public sealed class EnvironmentFingerprintProvider : IEnvironmentFingerprintProvider
{
    private static readonly string[] ProbedExecutables = ["dotnet", "node", "python", "git", "java"];

    private const string VisualCppRegistryPath =
        @"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes";

    public Task<string?> ComputeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult<string?>(Compute());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail open: an unusable fingerprint must trigger a re-scan, never serve stale data.
            return Task.FromResult<string?>(null);
        }
    }

    private static string? Compute()
    {
        var parts = new List<string>(9)
        {
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty
        };

        foreach (var executable in ProbedExecutables)
        {
            var resolved = WindowsPathLocator.ResolveFromPath(executable)
                           ?? WindowsPathLocator.ResolveWindowsAppPath(executable);
            parts.Add(resolved ?? "<absent>");
        }

        foreach (var architecture in new[] { "x86", "x64", "arm64" })
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{VisualCppRegistryPath}\{architecture}");
            parts.Add(key?.GetValue("Version") as string ?? "<absent>");
        }

        var joined = string.Join('\u001F', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
