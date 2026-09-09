using Nornia.Core.Models;
using Nornia.Core.Interfaces;
using Nornia.Package.Providers;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class WingetProviderTests
{
    [Fact]
    public async Task IsAvailableAsync_ExecutesVersionInsteadOfUsingWhereAliasLookup()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.SequenceEqual(["--version"])
                ? new ProcessResult(0, "Windows Package Manager v1.10.340", "")
                : new ProcessResult(1, "", "unexpected command"));
        var provider = new WingetProvider(runner);

        Assert.True(await provider.IsAvailableAsync());
        var call = Assert.Single(runner.Calls);
        Assert.Equal("winget.exe", call.FileName);
        Assert.Equal(["--version"], call.Arguments);
    }

    [Fact]
    public async Task IsAvailableAsync_ReturnsFalseWhenAliasCannotStart()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(-1, "", "cannot start"));
        var provider = new WingetProvider(runner);

        Assert.False(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task SearchAsync_ParsesWingetTable()
    {
        const string output = "Name              Id                    Version  Source\n--------------------------------------------------------\nPython 3.13       Python.Python.3.13    3.13.2   winget\n";
        var provider = new WingetProvider(new FakeProcessRunner((_, _) => new ProcessResult(0, output, "")));

        var package = Assert.Single(await provider.SearchAsync("python"));

        Assert.Equal("Python.Python.3.13", package.Id);
        Assert.Equal("3.13.2", package.Version);
    }

    [Fact]
    public async Task InstallAsync_UsesExactIdAndVersion()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, "installed", ""));
        var provider = new WingetProvider(runner);

        await provider.InstallAsync("OpenJS.NodeJS", "22.14.0");

        var call = Assert.Single(runner.Calls);
        var arguments = call.Arguments.ToList();
        Assert.Equal("winget.exe", call.FileName);
        Assert.Contains("--exact", arguments);
        Assert.Equal("22.14.0", arguments[arguments.IndexOf("--version") + 1]);
    }

    [Theory]
    [InlineData("Name              Id                    Version  Available  Source\n-------------------------------------------------------------------\nGit               Git.Git               2.50.0   2.51.0     winget\n")]
    [InlineData("Id                    Name              Version  Available  Source\n-------------------------------------------------------------------\nGit.Git               Git               2.50.0   2.51.0     winget\n")]
    public async Task ListInstalledAsync_UsesHeaderToMapColumns(string output)
    {
        var provider = new WingetProvider(new FakeProcessRunner((_, _) => new ProcessResult(0, output, "")));

        var package = Assert.Single(await provider.ListInstalledAsync());

        Assert.Equal("Git.Git", package.Id);
        Assert.Equal("Git", package.Name);
        Assert.Equal("2.50.0", package.Version);
        Assert.Equal("2.51.0", package.AvailableVersion);
        Assert.True(package.IsInstalled);
    }

    [Fact]
    public async Task SearchAsync_ForwardsStandardOutputAndErrorToProgress()
    {
        var output = "Name  Id  Version\n-----------------\nGit  Git.Git  2.50.0\n";
        var provider = new WingetProvider(new FakeProcessRunner((_, _) => new ProcessResult(0, output, "source warning")));
        var progress = new CapturingProgress();

        await provider.SearchAsync("git", progress);

        Assert.Contains(progress.Entries, entry => entry.Text == "Git  Git.Git  2.50.0" && !entry.IsError);
        Assert.Contains(progress.Entries, entry => entry.Text == "source warning" && entry.IsError);
    }

    [Fact]
    public async Task InstallAsync_ForwardsWingetVirtualTerminalProgressFrame()
    {
        const string wingetProgress = "\u001b]9;4;1;42\u0007";
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, wingetProgress, ""));
        var provider = new WingetProvider(runner);
        var progress = new CapturingProgress();

        await provider.InstallAsync("Git.Git", progress: progress);

        Assert.Contains(progress.Entries, entry => entry.Text == wingetProgress && !entry.IsError);
    }

    [Fact]
    public async Task InstallAsync_UsesInteractiveRunnerWhenDesktopProvidesOne()
    {
        var standardRunner = new FakeProcessRunner((_, _) => new ProcessResult(0, "redirected", ""));
        var interactiveRunner = new CapturingInteractiveProcessRunner();
        var provider = new WingetProvider(standardRunner, interactiveProcessRunner: interactiveRunner);

        await provider.InstallAsync("Git.Git");

        Assert.True(interactiveRunner.Called);
        Assert.Empty(standardRunner.Calls);
    }

    [Fact]
    public async Task ListInstalledAsync_DeduplicatesExactRecordsButKeepsArchitectureVariants()
    {
        const string output = """
            Name                         Id                              Version  Source
            --------------------------------------------------------------------------
            Visual C++ Redistributable   Microsoft.VCRedist.2015+.x64    14.51    winget
            Visual C++ Redistributable   Microsoft.VCRedist.2015+.x64    14.51    winget
            Visual C++ Redistributable   Microsoft.VCRedist.2015+.x86    14.51    winget
            """;
        var provider = new WingetProvider(new FakeProcessRunner((_, _) => new ProcessResult(0, output, "")));

        var packages = await provider.ListInstalledAsync();

        Assert.Equal(2, packages.Count);
        Assert.Contains(packages, package => package.Architecture == "X64");
        Assert.Contains(packages, package => package.Architecture == "X86");
    }

    [Fact]
    public async Task ListInstalledAsync_UsesChineseFixedWidthHeadersWithoutSplittingName()
    {
        var header = PadDisplay("名称", 55) + PadDisplay("ID", 40) + PadDisplay("版本", 18) + PadDisplay("可用", 12) + "源";
        var separator = new string('-', header.Length);
        var row = PadDisplay("Microsoft Visual C++ 2010  x64 Redistributable", 55)
                  + PadDisplay("Microsoft.VCRedist.2010.x64", 40)
                  + PadDisplay("10.0.40219", 18)
                  + PadDisplay("", 12)
                  + "winget";
        var provider = new WingetProvider(new FakeProcessRunner((_, _) => new ProcessResult(0, $"{header}\n{separator}\n{row}\n", "")));

        var package = Assert.Single(await provider.ListInstalledAsync());

        Assert.Equal("Microsoft Visual C++ 2010  x64 Redistributable", package.Name);
        Assert.Equal("Microsoft.VCRedist.2010.x64", package.Id);
        Assert.Equal("X64", package.Architecture);
    }

    [Fact]
    public async Task ListInstalledAsync_ResolvesTruncatedWingetName()
    {
        var listOutput = "Name                                  Id                                      Version  Source\n----------------------------------------------------------------------------------------------------\n"
                         + "Microsoft Visual C++ v14 Redistribut…".PadRight(38)
                         + "Microsoft.VCRedist.2015+.x64".PadRight(40)
                         + "14.51    winget\n";
        var provider = new WingetProvider(new FakeProcessRunner((_, arguments) => arguments[0] == "show"
            ? new ProcessResult(0, "Found Microsoft Visual C++ v14 Redistributable (x64) [Microsoft.VCRedist.2015+.x64]", "")
            : new ProcessResult(0, listOutput, "")));

        var package = Assert.Single(await provider.ListInstalledAsync());

        Assert.Equal("Microsoft Visual C++ v14 Redistributable (x64)", package.Name);
    }

    private sealed class CapturingProgress : IProgress<ProcessOutput>
    {
        public List<ProcessOutput> Entries { get; } = [];
        public void Report(ProcessOutput value) => Entries.Add(value);
    }

    private sealed class CapturingInteractiveProcessRunner : IInteractiveProcessRunner
    {
        public bool Called { get; private set; }

        public Task<ProcessResult> RunInteractiveAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IProgress<ProcessOutput>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new ProcessResult(0, "installed", ""));
        }
    }

    private static string PadDisplay(string value, int width)
    {
        var displayWidth = value.Sum(character => character is >= '\u2E80' and <= '\uA4CF' ? 2 : 1);
        return value + new string(' ', width - displayWidth);
    }
}
