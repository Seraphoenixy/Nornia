using CoreRuntime = Nornia.Core.Models.Runtime;
using Microsoft.Win32;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.Runtime.Providers;

public sealed class VisualCppRedistributableProvider(IProcessRunner processRunner) : RuntimeProviderBase("Visual C++ Redistributable", processRunner)
{
    private const string RegistryPath = @"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes";

    /// <summary>
    /// Gets the directory where the shared VC++ runtime DLLs are deployed.
    /// The redistributable's uninstall registry entry does not have an
    /// InstallLocation; its registry key (or Package Cache path) is metadata,
    /// not the runtime installation directory.
    /// </summary>
    internal static string ResolveInstallPath(string architecture)
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDirectory = architecture.Equals("x86", StringComparison.OrdinalIgnoreCase)
            && Environment.Is64BitOperatingSystem
            ? "SysWOW64"
            : "System32";

        return Path.Combine(windowsDirectory, systemDirectory);
    }

    public override Task<RuntimeDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var installed = new List<CoreRuntime>();
        var broken = new List<RuntimeBrokenInfo>();

        using var root = Registry.LocalMachine.OpenSubKey(RegistryPath);
        foreach (var architecture in new[] { "x86", "x64", "arm64" })
        {
            using var key = root?.OpenSubKey(architecture);
            var version = key?.GetValue("Version") as string;
            if (key is null || string.IsNullOrWhiteSpace(version)) continue;

            var installedFlag = Convert.ToInt32(key.GetValue("Installed", 1));
            // Keep the registry location as the stable identity input, but expose
            // the actual shared-DLL directory as Runtime.InstallPath.
            var registryLocation = $"HKLM\\{RegistryPath}\\{architecture}";
            var installPath = ResolveInstallPath(architecture);
            if (installedFlag == 0)
            {
                var brokenRuntime = new CoreRuntime(
                    RuntimeIdentity.Create(Name, version, registryLocation), Name, version, installPath,
                    architecture.ToUpperInvariant(), GetType().Name, 0, RuntimeStatus.Error,
                    DetectionStatus.Broken, "Registry Installed flag is 0");
                broken.Add(CreateBroken(brokenRuntime,
                    $"Registry entry for Visual C++ {architecture} exists (version {version}) but Installed=0 indicates a broken install."));
                continue;
            }
            installed.Add(new CoreRuntime(RuntimeIdentity.Create(Name, version, registryLocation), Name, version, installPath,
                architecture.ToUpperInvariant(), GetType().Name, 0, RuntimeStatus.Installed, DetectionStatus.Installed));
        }
        return Task.FromResult(RuntimeDetectionResult.Create(installed, broken));
    }
}

public sealed class WindowsAppRuntimeProvider(IProcessRunner processRunner) : RuntimeProviderBase("Windows App Runtime", processRunner)
{
    public override async Task<RuntimeDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync("powershell",
            ["-NoProfile", "-NonInteractive", "-Command",
             "Get-AppxPackage -Name Microsoft.WindowsAppRuntime.* | ForEach-Object { \"$($_.Version)|$($_.InstallLocation)|$($_.Architecture)\" }"],
            cancellationToken: cancellationToken);

        IReadOnlyList<CoreRuntime> installed;
        IReadOnlyList<RuntimeBrokenInfo> broken;
        if (result.IsSuccess)
        {
            installed = Parse(result.StandardOutput, out broken);
        }
        else
        {
            installed = [];
            broken = [];
        }
        return RuntimeDetectionResult.Create(installed, broken);
    }

    internal IReadOnlyList<CoreRuntime> Parse(string output, out IReadOnlyList<RuntimeBrokenInfo> broken)
    {
        var installedList = new List<CoreRuntime>();
        var brokenList = new List<RuntimeBrokenInfo>();

        foreach (var parts in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                     .Select(line => line.Split('|'))
                     .Where(parts => parts.Length == 3 && Version.TryParse(parts[0], out _)))
        {
            var version = parts[0];
            var installLocation = parts[1];
            var architecture = parts[2];
            var pathExists = Directory.Exists(installLocation);
            var runtime = new CoreRuntime(RuntimeIdentity.Create(Name, version, installLocation),
                Name, version, installLocation, architecture, GetType().Name,
                RuntimeIdentity.GetInstallTimestamp(installLocation),
                pathExists ? RuntimeStatus.Installed : RuntimeStatus.Error,
                pathExists ? DetectionStatus.Installed : DetectionStatus.Broken,
                pathExists ? null : $"Appx install location '{installLocation}' does not exist");
            if (pathExists) installedList.Add(runtime);
            else brokenList.Add(CreateBroken(runtime, runtime.DetectionError!));
        }

        broken = brokenList;
        return installedList;
    }
}
