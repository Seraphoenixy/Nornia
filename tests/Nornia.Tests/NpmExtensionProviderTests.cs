using Nornia.Core.Models;
using Nornia.Runtime.Extensions;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class NpmExtensionProviderTests
{
    private const string InstalledJson = """
        {"dependencies": {"typescript": {"version": "5.5.4"}, "nodemon": {"version": "3.1.4"}}}
        """;

    private const string OutdatedJson = """
        {"typescript": {"latest": "5.6.2"}}
        """;

    /// <summary>npm 一律经 cmd.exe /s /c 运行:handler 按完整命令串区分列表/变更命令。</summary>
    private static (FakeProcessRunner Runner, NpmExtensionProvider Provider) CreateProvider(
        Func<string, IReadOnlyList<string>, ProcessResult>? respond = null)
    {
        var runner = new FakeProcessRunner(respond ?? ((fileName, arguments) =>
        {
            var commandLine = arguments[^1];
            return new ProcessResult(0,
                commandLine.Contains("outdated") ? OutdatedJson : InstalledJson,
                string.Empty);
        }));
        return (runner, new NpmExtensionProvider(runner));
    }

    [Fact]
    public async Task ListInstalledAsync_ParsesDependenciesObject()
    {
        var (runner, provider) = CreateProvider();

        var packages = await provider.ListInstalledAsync();

        Assert.Equal(["typescript", "nodemon"], packages.Select(package => package.Name));
        Assert.Equal(["5.5.4", "3.1.4"], packages.Select(package => package.Version));
        Assert.All(packages, package =>
        {
            Assert.Equal(ToolExtensionEcosystem.Npm, package.Ecosystem);
            Assert.Null(package.AvailableVersion);
        });
        Assert.Equal("npm ls -g --depth=0 --json", Assert.Single(runner.Calls).Arguments[^1]);
    }

    [Fact]
    public async Task ListInstalledAsync_NonZeroExitWithValidJson_StillParses()
    {
        // npm 在存在损坏包时以非零码退出但输出仍是合法 JSON:列表解析不得按退出码失败。
        var (_, provider) = CreateProvider((_, _) => new ProcessResult(1, InstalledJson, string.Empty));

        var packages = await provider.ListInstalledAsync();

        Assert.Equal(2, packages.Count);
    }

    [Fact]
    public async Task ListOutdatedAsync_ParsesLatestPerPackage()
    {
        var (_, provider) = CreateProvider();

        var outdated = await provider.ListOutdatedAsync();

        var typescript = Assert.Single(outdated);
        Assert.Equal("typescript", typescript.Name);
        Assert.Equal("5.6.2", typescript.AvailableVersion);
    }

    [Fact]
    public async Task InstallAsync_WithoutVersion_UsesBareName()
    {
        var (runner, provider) = CreateProvider();

        await provider.InstallAsync("typescript");

        Assert.Equal("npm install -g typescript", Assert.Single(runner.Calls).Arguments[^1]);
    }

    [Fact]
    public async Task InstallAsync_WithVersion_JoinsAtVersion()
    {
        var (runner, provider) = CreateProvider();

        await provider.InstallAsync("typescript", "5.5.4");

        Assert.Equal("npm install -g typescript@5.5.4", Assert.Single(runner.Calls).Arguments[^1]);
    }

    [Fact]
    public async Task UninstallAsync_UsesRemoveGlobal()
    {
        var (runner, provider) = CreateProvider();

        await provider.UninstallAsync("typescript");

        Assert.Equal("npm rm -g typescript", Assert.Single(runner.Calls).Arguments[^1]);
    }

    [Fact]
    public async Task UpgradeAsync_UsesUpdateGlobal()
    {
        var (runner, provider) = CreateProvider();

        await provider.UpgradeAsync("typescript");

        Assert.Equal("npm update -g typescript", Assert.Single(runner.Calls).Arguments[^1]);
    }

    [Fact]
    public async Task ChangeCommand_NonZeroExit_Throws()
    {
        var (_, provider) = CreateProvider((_, _) => new ProcessResult(1, string.Empty, "ETARGET"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InstallAsync("typescript"));

        Assert.Contains("npm", exception.Message);
        Assert.Contains("ETARGET", exception.Message);
    }

    [Fact]
    public async Task AllCommands_RunThroughCmdExe()
    {
        var (runner, provider) = CreateProvider();

        await provider.ListInstalledAsync();

        var call = Assert.Single(runner.Calls);
        Assert.Equal("cmd.exe", call.FileName);
        Assert.Equal(["/s", "/c"], call.Arguments.Take(2));
    }

    // ===== 依赖关系(npm ls -g --json 整树索引) =====

    private const string TreeJson = """
        {
          "name": "npm",
          "dependencies": {
            "codex": {
              "version": "0.148.0",
              "dependencies": {
                "codex-win32-x64": { "version": "0.148.0-win32-x64" },
                "@openai/helper": { "version": "1.0.0" }
              }
            },
            "pnpm": { "version": "11.18.0" }
          }
        }
        """;

    [Fact]
    public async Task GetDependenciesAsync_ReturnsDirectChildrenFromTree()
    {
        var (_, provider) = CreateProvider((_, _) => new ProcessResult(0, TreeJson, string.Empty));

        var dependencies = await provider.GetDependenciesAsync("codex");

        Assert.Equal(["codex-win32-x64", "@openai/helper"], dependencies.Select(d => d.Name));
        Assert.Equal("0.148.0-win32-x64", dependencies[0].Version);
        Assert.All(dependencies, d => Assert.True(d.IsLeaf)); // 无嵌套 dependencies
    }

    [Fact]
    public async Task GetDependenciesAsync_LeafPackage_ReturnsEmpty()
    {
        var (_, provider) = CreateProvider((_, _) => new ProcessResult(0, TreeJson, string.Empty));

        Assert.Empty(await provider.GetDependenciesAsync("pnpm"));
        Assert.Empty(await provider.GetDependenciesAsync("not-installed"));
    }

    [Fact]
    public async Task GetDependenciesAsync_CachesWholeTree_AfterFirstQuery()
    {
        var (runner, provider) = CreateProvider((_, _) => new ProcessResult(0, TreeJson, string.Empty));

        await provider.GetDependenciesAsync("codex");
        await provider.GetDependenciesAsync("codex-win32-x64");
        await provider.GetDependenciesAsync("pnpm");

        // 整树只派生一次进程,后续查询全部命中缓存
        var call = Assert.Single(runner.Calls);
        Assert.Equal("npm ls -g --json", call.Arguments[^1]);
    }

    [Fact]
    public async Task GetDependenciesAsync_NonZeroExitWithValidJson_StillParses()
    {
        // npm 在存在损坏/冲突包时以非零码退出但输出仍是合法 JSON。
        var (_, provider) = CreateProvider((_, _) => new ProcessResult(1, TreeJson, string.Empty));

        Assert.Equal(2, (await provider.GetDependenciesAsync("codex")).Count);
    }

    [Fact]
    public void BuildDependencyIndex_StripsVersionSuffixFromKeys()
    {
        const string json = """
            {
              "dependencies": {
                "@scope/pkg@1.2.3": {
                  "version": "1.2.3",
                  "dependencies": { "child@0.1.0": { "version": "0.1.0" } }
                }
              }
            }
            """;

        var index = NpmExtensionProvider.BuildDependencyIndex(json);

        Assert.True(index.ContainsKey("@scope/pkg"));
        Assert.Equal("child", Assert.Single(index["@scope/pkg"]).Name);
    }

    [Fact]
    public void StripVersionSuffix_KeepsScopedPackageNames()
    {
        Assert.Equal("@scope/pkg", NpmExtensionProvider.StripVersionSuffix("@scope/pkg"));
        Assert.Equal("@scope/pkg", NpmExtensionProvider.StripVersionSuffix("@scope/pkg@1.2.3"));
        Assert.Equal("pkg", NpmExtensionProvider.StripVersionSuffix("pkg@1.2.3"));
    }
}
