using Nornia.Core.Interfaces;
using Nornia.Core.Services;
using Nornia.Package.Providers;

namespace Nornia.Tests;

/// <summary>
/// Winget integration tests that only run when <c>NORNIA_WINGET_INTEGRATION=1</c> is set (e.g. in a
/// dedicated CI job or an isolated Windows VM). They verify Nornia's provider, probe and error
/// semantics against the real tool instead of fixtures — including the live no-match exit code
/// (which has drifted across winget versions) and the header language of the localized output.
/// Disable these in normal unit-test runs: <c>dotnet test --filter Category!=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WingetIntegrationTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("NORNIA_WINGET_INTEGRATION") is "1" or "true";
    private readonly IProcessRunner _runner = new ProcessRunner();

    [Fact]
    public async Task Winget_VersionReportsStableVersionLine()
    {
        if (!Enabled)
        {
            return; // Gated: only exercised with NORNIA_WINGET_INTEGRATION=1.
        }

        var result = await _runner.RunAsync("winget", ["--version"]);

        Assert.True(result.IsSuccess, $"winget --version failed: {result.StandardError}");
        Assert.Matches(@"v?\d+\.\d+\.\d+", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task Winget_ListParsesEndToEndAndProbesCapabilities()
    {
        if (!Enabled)
        {
            return; // Gated: only exercised with NORNIA_WINGET_INTEGRATION=1.
        }

        var probe = new WingetProbe(_runner);
        var provider = new WingetProvider(_runner, probe);
        var packages = await provider.ListInstalledAsync();

        Assert.NotEmpty(packages);
        Assert.All(packages, package => Assert.False(string.IsNullOrWhiteSpace(package.Id)));
        Assert.All(packages, package => Assert.False(string.IsNullOrWhiteSpace(package.Name)));

        var capabilities = await probe.GetCapabilitiesAsync();
        Assert.NotNull(capabilities.Version);
        Assert.Contains(capabilities.OutputLanguage, new[] { "en", "zh-Hans" });
    }

    [Fact]
    public async Task Winget_SearchNoMatchReturnsEmptyAndRecordsObservedExitCode()
    {
        if (!Enabled)
        {
            return; // Gated: only exercised with NORNIA_WINGET_INTEGRATION=1.
        }

        var probe = new WingetProbe(_runner);
        var provider = new WingetProvider(_runner, probe);
        var packages = await provider.SearchAsync("nornia-no-such-package-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(packages);

        var capabilities = await probe.GetCapabilitiesAsync();
        Assert.Contains(capabilities.NoMatchObservedCodes, WingetExitCodes.IsNoMatch);
    }
}