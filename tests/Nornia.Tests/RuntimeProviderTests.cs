using Nornia.Core.Models;
using Nornia.Runtime.Providers;
using Nornia.Tests.Fakes;
using Nornia.Desktop.ViewModels;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

public sealed class RuntimeProviderTests
{
    [Fact]
    public async Task DotnetProvider_ParsesAllInstalledSdks()
    {
        // ParseSdk marks a runtime Broken unless its directory exists on disk, so create
        // real temp SDK directories for the mocked `dotnet --list-sdks` output to resolve.
        using var dir = new TempDirectory();
        var sdk1 = Path.Combine(dir.Path, "sdk", "8.0.408");
        var sdk2 = Path.Combine(dir.Path, "sdk", "10.0.100");
        Directory.CreateDirectory(sdk1);
        Directory.CreateDirectory(sdk2);

        var runner = new FakeProcessRunner((file, _) => file == "dotnet"
            ? new ProcessResult(0, $"8.0.408 [{Path.Combine(dir.Path, "sdk")}]\n10.0.100 [{Path.Combine(dir.Path, "sdk")}]\n", "")
            : new ProcessResult(1, "", "not found"));

        var result = await new DotnetRuntimeProvider(runner).DetectAsync();
        var runtimes = result.InstalledRuntimes;

        Assert.Equal(2, runtimes.Count);
        Assert.Contains(runtimes, runtime => runtime.Version == "10.0.100");
        Assert.All(runtimes, runtime => Assert.Equal(RuntimeStatus.Installed, runtime.Status));
    }

    [Theory]
    [InlineData("node", "v22.14.0", "22.14.0")]
    [InlineData("python", "Python 3.13.2", "3.13.2")]
    [InlineData("git", "git version 2.50.1.windows.1", "2.50.1.windows.1")]
    public async Task CommandProviders_ParseVersion(string executable, string output, string expected)
    {
        // LocateExecutable validates the reported path exists on disk, so create a real
        // temp executable for where.exe to resolve before it falls back to the system PATH.
        using var dir = new TempDirectory();
        var exePath = Path.Combine(dir.Path, $"{executable}.exe");
        File.WriteAllText(exePath, string.Empty);

        var runner = new FakeProcessRunner((file, _) => file == "where.exe"
            ? new ProcessResult(0, $"{exePath}\n", "")
            : new ProcessResult(0, output, ""));
        RuntimeProviderBase provider = executable switch
        {
            "node" => new NodeRuntimeProvider(runner),
            "python" => new PythonRuntimeProvider(runner),
            _ => new GitRuntimeProvider(runner)
        };

        var runtime = Assert.Single((await provider.DetectAsync()).InstalledRuntimes);

        Assert.Equal(expected, runtime.Version);
        Assert.Equal(exePath, runtime.InstallPath);
    }

    [Fact]
    public async Task MissingExecutable_ReturnsEmptyCollection()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(-1, "", "not found"));
        Assert.Empty((await new NodeRuntimeProvider(runner).DetectAsync()).InstalledRuntimes);
    }

    [Theory]
    [InlineData("x64", "System32")]
    [InlineData("arm64", "System32")]
    public void VisualCppRedistributable_UsesWindowsSystemDirectory(string architecture, string directoryName)
    {
        var expected = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), directoryName);

        Assert.Equal(expected, VisualCppRedistributableProvider.ResolveInstallPath(architecture));
    }

    [Fact]
    public void VisualCppRedistributable_UsesSyswow64ForX86On64BitWindows()
    {
        var expectedDirectory = Environment.Is64BitOperatingSystem ? "SysWOW64" : "System32";
        var expected = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), expectedDirectory);

        Assert.Equal(expected, VisualCppRedistributableProvider.ResolveInstallPath("x86"));
    }

    [Fact]
    public void VisualCppUpgrade_SelectsOnlyTheSelectedArchitecture()
    {
        var packages = new RuntimePackage[]
        {
            new("Microsoft.VCRedist.2015+.x86", null),
            new("Microsoft.VCRedist.2015+.x64", null)
        };

        var selected = RuntimeViewModel.SelectUpgradePackages("Visual C++ Redistributable", "X64", packages);

        var package = Assert.Single(selected);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", package.PackageId);
    }

    [Fact]
    public void VisualCppAvailableVersion_MatchesTheRuntimeArchitecture()
    {
        var runtime = new CoreRuntime(
            Guid.NewGuid(), "Visual C++ Redistributable", "14.50.35719.00", "C:\\Windows\\System32",
            "X64", "VisualCppRedistributableProvider", 0, RuntimeStatus.Installed);
        var resolved = new RuntimePackage[]
        {
            new("Microsoft.VCRedist.2015+.x86", null),
            new("Microsoft.VCRedist.2015+.x64", null)
        };
        var packages = new PackageInfo[]
        {
            new("Microsoft.VCRedist.2015+.x86", "VC++ x86", "14.51.36247.0", null, "winget", true, "X86"),
            new("Microsoft.VCRedist.2015+.x64", "VC++ x64", "14.50.35719.00", "14.51.36247.0", "winget", true, "X64")
        };

        Assert.Equal("14.51.36247.0", RuntimeViewModel.SelectAvailableVersion(runtime, packages, resolved));
    }

    [Fact]
    public void RestrictToLatestInstalls_KeepsAvailableOnlyOnNewestSideBySideInstance()
    {
        // Windows App Runtime 同包多实例(6000.318~6000.457 全解析到 Microsoft.WindowsAppRuntime.1.6):
        // 只有最新构建(x86/x64 并列)保留可用更新,旧 side-by-side 构建不显示虚假可用更新。
        var newestX64 = new CoreRuntime(
            Guid.NewGuid(), "Windows App Runtime", "6000.457.2140.0", @"C:\Program Files\WindowsApps\A", "X64", "WindowsAppRuntimeProvider", 0, RuntimeStatus.Installed);
        var newestX86 = new CoreRuntime(
            Guid.NewGuid(), "Windows App Runtime", "6000.457.2140.0", @"C:\Program Files\WindowsApps\B", "X86", "WindowsAppRuntimeProvider", 0, RuntimeStatus.Installed);
        var older = new CoreRuntime(
            Guid.NewGuid(), "Windows App Runtime", "6000.318.2304.0", @"C:\Program Files\WindowsApps\C", "X64", "WindowsAppRuntimeProvider", 0, RuntimeStatus.Installed);
        var annotated = new[]
        {
            (Runtime: newestX64, GroupKey: "Microsoft.WindowsAppRuntime.1.6", Available: (string?)"1.6.9"),
            (Runtime: newestX86, GroupKey: "Microsoft.WindowsAppRuntime.1.6", Available: (string?)"1.6.9"),
            (Runtime: older, GroupKey: "Microsoft.WindowsAppRuntime.1.6", Available: (string?)"1.6.9"),
        };

        var result = RuntimeViewModel.RestrictToLatestInstalls(annotated);

        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Equal("6000.457.2140.0", item.Runtime.Version));
        Assert.All(result, item => Assert.Equal("1.6.9", item.Available));
    }

    [Fact]
    public void RestrictToLatestInstalls_DifferentMajorPackagesKeepTheirOwnUpdate()
    {
        // .NET 等组件不同 major 解析为不同 winget 包:各自组内都是最新,互不吞掉可用更新。
        var net8 = new CoreRuntime(
            Guid.NewGuid(), ".NET Runtime", "8.0.11", "C:\\Program Files\\dotnet", "X64", "DotNetRuntimeProvider", 0, RuntimeStatus.Installed);
        var net9 = new CoreRuntime(
            Guid.NewGuid(), ".NET Runtime", "9.0.0", "C:\\Program Files\\dotnet", "X64", "DotNetRuntimeProvider", 0, RuntimeStatus.Installed);
        var annotated = new[]
        {
            (Runtime: net8, GroupKey: "Microsoft.DotNet.Runtime.8", Available: (string?)"8.0.12"),
            (Runtime: net9, GroupKey: "Microsoft.DotNet.Runtime.9", Available: (string?)"9.0.1"),
        };

        var result = RuntimeViewModel.RestrictToLatestInstalls(annotated);

        Assert.Equal(2, result.Count);
        Assert.Equal("8.0.12", result.Single(item => item.Runtime.Version == "8.0.11").Available);
        Assert.Equal("9.0.1", result.Single(item => item.Runtime.Version == "9.0.0").Available);
    }
}

/// <summary>Creates a unique temp directory and removes it (recursively) on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nornia-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
