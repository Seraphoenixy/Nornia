using Nornia.Core.Models;
using Nornia.Runtime.Extensions;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class PipExtensionProviderTests
{
    private const string InstalledJson = """
        [{"name": "requests", "version": "2.32.3"}, {"name": "flask", "version": "3.0.3"}]
        """;

    private const string OutdatedJson = """
        [{"name": "flask", "version": "3.0.3", "latest_version": "3.1.0"}]
        """;

    /// <summary>where.exe 返回一个真实存在的临时 python.exe,之后所有 python -m pip 调用
    /// 按参数区分返回 installed/outdated JSON(与 RuntimeProviderTests 相同的定位模拟手法)。</summary>
    private static (FakeProcessRunner Runner, PipExtensionProvider Provider, TempDirectory Dir) CreateProvider()
    {
        var dir = new TempDirectory();
        var python = Path.Combine(dir.Path, "python.exe");
        File.WriteAllText(python, string.Empty);
        var runner = new FakeProcessRunner((fileName, arguments) => fileName == "where.exe"
            ? new ProcessResult(0, $"{python}\n", string.Empty)
            : new ProcessResult(0, arguments.Contains("--outdated") ? OutdatedJson : InstalledJson, string.Empty));
        return (runner, new PipExtensionProvider(runner), dir);
    }

    [Fact]
    public async Task ListInstalledAsync_ParsesPackages()
    {
        var (_, provider, _) = CreateProvider();

        var packages = await provider.ListInstalledAsync();

        Assert.Equal(["requests", "flask"], packages.Select(package => package.Name));
        Assert.Equal(["2.32.3", "3.0.3"], packages.Select(package => package.Version));
        Assert.All(packages, package =>
        {
            Assert.Equal(ToolExtensionEcosystem.Pip, package.Ecosystem);
            Assert.Null(package.AvailableVersion);
        });
    }

    [Fact]
    public async Task ListOutdatedAsync_ParsesLatestVersions()
    {
        var (_, provider, _) = CreateProvider();

        var outdated = await provider.ListOutdatedAsync();

        var flask = Assert.Single(outdated);
        Assert.Equal("flask", flask.Name);
        Assert.Equal("3.1.0", flask.AvailableVersion);
    }

    [Fact]
    public async Task InstallAsync_WithoutVersion_UpgradesToLatest()
    {
        var (runner, provider, _) = CreateProvider();

        await provider.InstallAsync("requests");

        var call = runner.Calls[^1];
        Assert.Equal(["-m", "pip", "install", "--upgrade", "requests"], call.Arguments);
    }

    [Fact]
    public async Task InstallAsync_WithVersion_PinsExactVersion()
    {
        var (runner, provider, _) = CreateProvider();

        await provider.InstallAsync("requests", "2.32.3");

        var call = runner.Calls[^1];
        Assert.Equal(["-m", "pip", "install", "requests==2.32.3"], call.Arguments);
    }

    [Fact]
    public async Task UninstallAsync_UsesYesFlag()
    {
        var (runner, provider, _) = CreateProvider();

        await provider.UninstallAsync("requests");

        var call = runner.Calls[^1];
        Assert.Equal(["-m", "pip", "uninstall", "-y", "requests"], call.Arguments);
    }

    [Fact]
    public async Task UpgradeAsync_UsesUpgradeFlag()
    {
        var (runner, provider, _) = CreateProvider();

        await provider.UpgradeAsync("requests");

        var call = runner.Calls[^1];
        Assert.Equal(["-m", "pip", "install", "--upgrade", "requests"], call.Arguments);
    }

    [Fact]
    public async Task MissingPython_ThrowsWithEcosystemName()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(-1, string.Empty, "not found"));
        var provider = new PipExtensionProvider(runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListInstalledAsync());

        Assert.Contains("pip", exception.Message);
    }

    // ===== 依赖关系(pip show 的 Requires 字段) =====

    /// <summary>同 CreateProvider,但 "show" 调用返回 <paramref name="showOutput"/>。</summary>
    private static (FakeProcessRunner Runner, PipExtensionProvider Provider) CreateShowProvider(string showOutput)
    {
        var dir = new TempDirectory();
        var python = Path.Combine(dir.Path, "python.exe");
        File.WriteAllText(python, string.Empty);
        var runner = new FakeProcessRunner((fileName, arguments) => fileName == "where.exe"
            ? new ProcessResult(0, $"{python}\n", string.Empty)
            : new ProcessResult(0, arguments.Contains("show") ? showOutput : "[]", string.Empty));
        return (runner, new PipExtensionProvider(runner));
    }

    [Fact]
    public async Task GetDependenciesAsync_ParsesRequiresField()
    {
        var (_, provider) = CreateShowProvider("""
            Name: requests
            Version: 2.32.3
            Requires: certifi, idna>=2.5, urllib3, charset-normalizer
            Required-by: flask
            """);

        var dependencies = await provider.GetDependenciesAsync("requests");

        Assert.Equal(["certifi", "idna", "urllib3", "charset-normalizer"], dependencies.Select(d => d.Name));
        Assert.Equal(">=2.5", dependencies[1].Constraint);
        Assert.All(dependencies, d =>
        {
            Assert.Null(d.Version);   // pip show 的 Requires 只给名字,不给已装版本
            Assert.False(d.IsLeaf);   // 是否叶子需递归展开才知道
        });
    }

    [Fact]
    public async Task GetDependenciesAsync_ShowCommandForm()
    {
        var (runner, provider) = CreateShowProvider("Name: requests\nRequires: \n");

        await provider.GetDependenciesAsync("requests");

        var call = runner.Calls[^1];
        Assert.Equal(["-m", "pip", "show", "requests"], call.Arguments);
    }

    [Fact]
    public async Task GetDependenciesAsync_UnknownPackage_ReturnsEmpty()
    {
        // pip show 对不存在的包输出空、退出码 0:应返回空列表而非抛异常。
        var (_, provider) = CreateShowProvider(string.Empty);

        Assert.Empty(await provider.GetDependenciesAsync("does-not-exist"));
    }

    [Fact]
    public void ParseRequires_MissingOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(PipExtensionProvider.ParseRequires("Name: requests\nVersion: 2.32.3\n"));
        Assert.Empty(PipExtensionProvider.ParseRequires(string.Empty));
        Assert.Empty(PipExtensionProvider.ParseRequires("Requires: \nRequired-by: flask\n"));
    }

    [Fact]
    public void SplitRequirements_StripsExtrasAndKeepsConstraint()
    {
        var dependencies = PipExtensionProvider.SplitRequirements("pkg[extra]>=1.0,<2, bare, another==2.0");

        Assert.Equal(["pkg", "bare", "another"], dependencies.Select(d => d.Name));
        Assert.Equal(">=1.0,<2", dependencies[0].Constraint);
        Assert.Null(dependencies[1].Constraint);
        Assert.Equal("==2.0", dependencies[2].Constraint);
    }
}
