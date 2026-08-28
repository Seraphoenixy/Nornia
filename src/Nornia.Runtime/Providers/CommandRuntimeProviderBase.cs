using CoreRuntime = Nornia.Core.Models.Runtime;
using Microsoft.Win32;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Nornia.Runtime.Providers;

public abstract class CommandRuntimeProviderBase(
    string name,
    string executable,
    IReadOnlyList<string> versionArguments,
    IProcessRunner processRunner,
    IVersionParser versionParser) : RuntimeProviderBase(name, processRunner)
{
    public override async Task<RuntimeDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var installed = new List<CoreRuntime>();
        var broken = new List<RuntimeBrokenInfo>();

        var candidatePath = await LocateExecutableAsync(executable, cancellationToken);

        if (candidatePath is null)
        {
            return RuntimeDetectionResult.Create(installed, broken);
        }

        var versionResult = await ProcessRunner.RunAsync(candidatePath, versionArguments, cancellationToken: cancellationToken);
        if (!versionResult.IsSuccess)
        {
            var brokenRuntime = new CoreRuntime(
                RuntimeIdentity.Create(Name, "0.0.0-broken", candidatePath),
                Name,
                "unknown",
                candidatePath,
                RuntimeInformation.OSArchitecture.ToString(),
                GetType().Name,
                RuntimeIdentity.GetInstallTimestamp(candidatePath),
                RuntimeStatus.Error,
                DetectionStatus.Broken,
                $"Version command failed: exit code {versionResult.ExitCode}");
            broken.Add(CreateBroken(brokenRuntime, $"Executable exists at '{candidatePath}' but version command returned exit code {versionResult.ExitCode}."));
            return RuntimeDetectionResult.Create(installed, broken);
        }

        var combinedOutput = $"{versionResult.StandardOutput}\n{versionResult.StandardError}".Trim();
        var version = versionParser.Parse(combinedOutput);
        if (string.IsNullOrWhiteSpace(version))
        {
            var brokenRuntime = new CoreRuntime(
                RuntimeIdentity.Create(Name, "0.0.0-broken", candidatePath),
                Name,
                "unknown",
                candidatePath,
                RuntimeInformation.OSArchitecture.ToString(),
                GetType().Name,
                RuntimeIdentity.GetInstallTimestamp(candidatePath),
                RuntimeStatus.Error,
                DetectionStatus.Broken,
                "Version output could not be parsed");
            broken.Add(CreateBroken(brokenRuntime, $"Executable at '{candidatePath}' produced output that could not be parsed as a version: {Truncate(combinedOutput)}"));
            return RuntimeDetectionResult.Create(installed, broken);
        }

        var finalPath = candidatePath;
        if (!File.Exists(finalPath))
        {
            var brokenRuntime = new CoreRuntime(
                RuntimeIdentity.Create(Name, version, finalPath),
                Name,
                version,
                finalPath,
                RuntimeInformation.OSArchitecture.ToString(),
                GetType().Name,
                RuntimeIdentity.GetInstallTimestamp(finalPath),
                RuntimeStatus.Error,
                DetectionStatus.Broken,
                "Install path points to a missing file");
            broken.Add(CreateBroken(brokenRuntime, $"Reported install path '{finalPath}' does not exist on disk."));
            return RuntimeDetectionResult.Create(installed, broken);
        }

        installed.Add(new CoreRuntime(
            RuntimeIdentity.Create(Name, version, finalPath),
            Name,
            version,
            finalPath,
            RuntimeInformation.OSArchitecture.ToString(),
            GetType().Name,
            RuntimeIdentity.GetInstallTimestamp(finalPath),
            RuntimeStatus.Installed,
            DetectionStatus.Installed));

        return RuntimeDetectionResult.Create(installed, broken);
    }

    // Async (not the synchronous GetAwaiter().GetResult() form) so the whole detection chain stays
    // awaitable. A sync-over-async call here deadlocks the dispatcher: Dashboard activates on startup,
    // runs the environment scan on the UI thread, and a blocking wait on where.exe never lets its
    // ProcessRunner continuation return to the dispatcher.
    private async Task<string?> LocateExecutableAsync(string exeName, CancellationToken cancellationToken = default)
    {
        var whereResult = await ProcessRunner.RunAsync("where.exe", [exeName], cancellationToken: cancellationToken);
        if (whereResult.IsSuccess)
        {
            var first = whereResult.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(first) && File.Exists(first))
            {
                return first;
            }
        }

        var fromPath = ResolveFromPath(exeName);
        if (fromPath is not null) return fromPath;

        return ResolveWindowsAppPath(exeName);
    }

    private static string Truncate(string value, int maxLen = 200) =>
        value.Length <= maxLen ? value : string.Concat(value.AsSpan(0, maxLen - 3), "...");

    private static string? ResolveFromPath(string executable)
    {
        var executableName = Path.HasExtension(executable) ? executable : $"{executable}.exe";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore invalid PATH entries and continue discovery.
            }
        }

        return null;
    }

    private static string? ResolveWindowsAppPath(string executable)
    {
        var executableName = Path.HasExtension(executable) ? executable : $"{executable}.exe";
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($"{appPaths}\\{executableName}");
            if (key?.GetValue(null) is string path && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

public sealed class NodeRuntimeProvider(IProcessRunner processRunner)
    : CommandRuntimeProviderBase("Node.js", "node", ["--version"], processRunner, new PlainVersionParser());

public sealed partial class GitRuntimeProvider(IProcessRunner processRunner)
    : CommandRuntimeProviderBase("Git", "git", ["--version"], processRunner, new RegexVersionParser(GitVersionRegex()))
{
    [GeneratedRegex("git version\\s+(?<version>\\d+(?:\\.\\d+)+(?:\\.windows\\.\\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex GitVersionRegex();
}

public sealed partial class PythonRuntimeProvider(IProcessRunner processRunner)
    : CommandRuntimeProviderBase("Python", "python", ["--version"], processRunner, new RegexVersionParser(PythonVersionRegex()))
{
    [GeneratedRegex("Python\\s+(?<version>\\d+(?:\\.\\d+)+)", RegexOptions.IgnoreCase)]
    private static partial Regex PythonVersionRegex();
}

public sealed partial class JavaRuntimeProvider(IProcessRunner processRunner)
    : CommandRuntimeProviderBase("Java", "java", ["-version"], processRunner, new RegexVersionParser(JavaVersionRegex()))
{
    [GeneratedRegex("version\\s+\"(?<version>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex JavaVersionRegex();
}