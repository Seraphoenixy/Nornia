using Nornia.Core.Models;
using Nornia.Runtime.Extensions;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class DotnetToolExtensionProviderTests
{
    private const string TableOutput = """
        Package Id      Version      Commands
        -------------------------------  ---------
        dotnet-ef       8.0.11       dotnet-ef
        csharpier       0.29.2       dotnet-csharpier
        """;

    private static (FakeProcessRunner Runner, DotnetToolExtensionProvider Provider, TempDirectory Dir) CreateProvider(
        string output = TableOutput)
    {
        var dir = new TempDirectory();
        var dotnet = Path.Combine(dir.Path, "dotnet.exe");
        File.WriteAllText(dotnet, string.Empty);
        var runner = new FakeProcessRunner((fileName, _) => fileName == "where.exe"
            ? new ProcessResult(0, $"{dotnet}\n", string.Empty)
            : new ProcessResult(0, output, string.Empty));
        return (runner, new DotnetToolExtensionProvider(runner), dir);
    }

    [Fact]
    public async Task ListInstalledAsync_ParsesTableRows()
    {
        var (_, provider, _) = CreateProvider();

        var tools = await provider.ListInstalledAsync();

        Assert.Equal(["dotnet-ef", "csharpier"], tools.Select(tool => tool.Name));
        Assert.Equal(["8.0.11", "0.29.2"], tools.Select(tool => tool.Version));
        Assert.All(tools, tool =>
        {
            Assert.Equal(ToolExtensionEcosystem.DotnetTool, tool.Ecosystem);
            Assert.Null(tool.AvailableVersion);
        });
    }

    [Fact]
    public async Task ListInstalledAsync_UsesToolListCommand()
    {
        // 回归:曾写成 ["list","-g"],dotnet list 要求 package/reference 子命令,
        // 真实环境报「未提供必需的命令。」(exit 1)。
        var (runner, provider, _) = CreateProvider();

        await provider.ListInstalledAsync();

        Assert.Equal(["tool", "list", "-g"], runner.Calls[^1].Arguments);
        Assert.EndsWith("dotnet.exe", runner.Calls[^1].FileName);
    }

    [Fact]
    public async Task ListInstalledAsync_SkipsHeaderAndSeparatorRows()
    {
        // 表头("Package Id")与全横线分隔行不得被解析为包。
        var (_, provider, _) = CreateProvider("Package Id      Version      Commands\n-------------------------------  ---------\n");

        var tools = await provider.ListInstalledAsync();

        Assert.Empty(tools);
    }

    [Fact]
    public async Task ListOutdatedAsync_ReturnsEmptyWithoutSpawning()
    {
        var (runner, provider, _) = CreateProvider();

        var outdated = await provider.ListOutdatedAsync();

        Assert.Empty(outdated);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task InstallAsync_WithoutVersion_OmitsVersionFlag()
    {
        var (runner, provider, _) = CreateProvider();

        await provider.InstallAsync("dotnet-ef");

        Assert.Equal(["tool", "install", "-g", "dotnet-ef"], runner.Calls[^1].Arguments);
    }

    [Fact]
    public async Task InstallAsync_WithVersion_AddsVersionFlag()
    {
        var (runner, provider, _) = CreateProvider();

        await provider.InstallAsync("dotnet-ef", "8.0.11");

        Assert.Equal(["tool", "install", "-g", "dotnet-ef", "--version", "8.0.11"], runner.Calls[^1].Arguments);
    }

    [Fact]
    public async Task UninstallAsync_UsesUninstallCommand()
    {
        var (runner, provider, _) = CreateProvider();

        await provider.UninstallAsync("dotnet-ef");

        Assert.Equal(["tool", "uninstall", "-g", "dotnet-ef"], runner.Calls[^1].Arguments);
    }

    [Fact]
    public async Task UpgradeAsync_UsesUpdateCommand()
    {
        var (runner, provider, _) = CreateProvider();

        await provider.UpgradeAsync("dotnet-ef");

        Assert.Equal(["tool", "update", "-g", "dotnet-ef"], runner.Calls[^1].Arguments);
    }

    [Fact]
    public async Task MissingDotnet_ThrowsWithEcosystemName()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(-1, string.Empty, "not found"));
        var provider = new DotnetToolExtensionProvider(runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListInstalledAsync());

        Assert.Contains("dotnet-tool", exception.Message);
    }
}
