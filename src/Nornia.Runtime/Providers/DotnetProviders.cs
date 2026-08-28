using CoreRuntime = Nornia.Core.Models.Runtime;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Nornia.Runtime.Providers;

public sealed partial class DotnetRuntimeProvider(IProcessRunner processRunner) : RuntimeProviderBase(".NET", processRunner)
{
    public override async Task<RuntimeDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var installed = new List<CoreRuntime>();
        var broken = new List<RuntimeBrokenInfo>();

        var result = await ProcessRunner.RunAsync("dotnet", ["--list-sdks"], cancellationToken: cancellationToken);
        if (!result.IsSuccess)
        {
            var dotnetPath = await LocateDotnetExecutableAsync(cancellationToken);
            if (dotnetPath is not null)
            {
                var brokenRuntime = new CoreRuntime(
                    RuntimeIdentity.Create(Name, "0.0.0-broken", dotnetPath), Name, "unknown", dotnetPath,
                    RuntimeInformation.OSArchitecture.ToString(), GetType().Name,
                    RuntimeIdentity.GetInstallTimestamp(dotnetPath), RuntimeStatus.Error,
                    DetectionStatus.Broken, $"dotnet --list-sdks failed: exit code {result.ExitCode}");
                broken.Add(CreateBroken(brokenRuntime,
                    $"Found 'dotnet' at '{dotnetPath}' but --list-sdks returned exit code {result.ExitCode}. Output: {Truncate(result.StandardError ?? result.StandardOutput)}"));
            }
            return RuntimeDetectionResult.Create(installed, broken);
        }

        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var runtime = ParseSdk(line);
            if (runtime is not null) installed.Add(runtime);
        }
        return RuntimeDetectionResult.Create(installed, broken);
    }

    private CoreRuntime? ParseSdk(string line)
    {
        var match = SdkLineRegex().Match(line.Trim());
        if (!match.Success)
        {
            return null;
        }

        var version = match.Groups["version"].Value;
        var path = Path.Combine(match.Groups["path"].Value, version);
        var detection = Directory.Exists(path) ? DetectionStatus.Installed : DetectionStatus.Broken;
        var runtime = new CoreRuntime(
            RuntimeIdentity.Create(Name, version, path), Name, version, path,
            RuntimeInformation.OSArchitecture.ToString(), GetType().Name,
            RuntimeIdentity.GetInstallTimestamp(path),
            detection == DetectionStatus.Broken ? RuntimeStatus.Error : RuntimeStatus.Installed,
            detection,
            detection == DetectionStatus.Broken ? $"Install directory '{path}' does not exist" : null);
        return runtime;
    }

    private async Task<string?> LocateDotnetExecutableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var whereResult = await ProcessRunner.RunAsync("where.exe", ["dotnet"], cancellationToken: cancellationToken);
            if (whereResult.IsSuccess)
            {
                var first = whereResult.StandardOutput
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(first) && File.Exists(first)) return first;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static string Truncate(string value, int maxLen = 200) =>
        value.Length <= maxLen ? value : string.Concat(value.AsSpan(0, maxLen - 3), "...");

    [GeneratedRegex("^(?<version>\\S+)\\s+\\[(?<path>.+)\\]$")]
    private static partial Regex SdkLineRegex();
}

public sealed partial class DotnetDesktopRuntimeProvider(IProcessRunner processRunner) : RuntimeProviderBase(".NET Desktop Runtime", processRunner)
{
    public override async Task<RuntimeDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync("dotnet", ["--list-runtimes"], cancellationToken: cancellationToken);
        var installed = result.IsSuccess ? Parse(result.StandardOutput) : [];
        return RuntimeDetectionResult.Create(installed, []);
    }

    internal IReadOnlyList<CoreRuntime> Parse(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => RuntimeLineRegex().Match(line.Trim()))
        .Where(match => match.Success)
        .Select(match =>
        {
            var version = match.Groups["version"].Value;
            var path = Path.Combine(match.Groups["path"].Value, version);
            var exists = Directory.Exists(path);
            return new CoreRuntime(RuntimeIdentity.Create(Name, version, path), Name, version, path,
                RuntimeInformation.OSArchitecture.ToString(), GetType().Name, RuntimeIdentity.GetInstallTimestamp(path),
                exists ? RuntimeStatus.Installed : RuntimeStatus.Error,
                exists ? DetectionStatus.Installed : DetectionStatus.Broken,
                exists ? null : $"Install directory '{path}' does not exist");
        }).ToArray();

    [GeneratedRegex("^Microsoft\\.WindowsDesktop\\.App\\s+(?<version>\\S+)\\s+\\[(?<path>.+)\\]$")]
    private static partial Regex RuntimeLineRegex();
}