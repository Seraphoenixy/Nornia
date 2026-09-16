using System.Text.Json;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.Runtime.Extensions;

/// <summary>Node.js 生态适配器。Windows 上 npm 是 .cmd shim(同 <c>code</c>),直接
/// CreateProcess 无法启动,故经 <c>cmd.exe /s /c npm …</c> 运行:既获得 shim 解析,又保留
/// 输出捕获。npm 的 ls/outdated 在「存在异常包 / 存在过时包」时以非零码退出但输出仍是合法
/// JSON,因此列表解析不按退出码失败,仅在 JSON 不可解析时报错;变更命令(install/rm/update)
/// 则按退出码校验。</summary>
public sealed class NpmExtensionProvider(IProcessRunner processRunner) : ToolExtensionProviderBase("npm", processRunner)
{
    private const string CmdExe = "cmd.exe";

    public override ToolExtensionEcosystem Ecosystem => ToolExtensionEcosystem.Npm;

    public override async Task<IReadOnlyList<ToolExtension>> ListInstalledAsync(
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await RunNpmAsync(["ls", "-g", "--depth=0", "--json"], progress, cancellationToken, checkExitCode: false);
        return ParseInstalled(result.StandardOutput);
    }

    public override async Task<IReadOnlyList<ToolExtension>> ListOutdatedAsync(
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await RunNpmAsync(["outdated", "-g", "--json"], progress, cancellationToken, checkExitCode: false);
        return ParseOutdated(result.StandardOutput);
    }

    public override async Task InstallAsync(string name, string? version = null,
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var spec = string.IsNullOrWhiteSpace(version) ? name : $"{name}@{version.Trim()}";
        await RunNpmAsync(["install", "-g", spec], progress, cancellationToken, checkExitCode: true, description: $"安装 {name}");
    }

    public override async Task UninstallAsync(string name,
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        await RunNpmAsync(["rm", "-g", name], progress, cancellationToken, checkExitCode: true, description: $"卸载 {name}");

    public override async Task UpgradeAsync(string name,
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        await RunNpmAsync(["update", "-g", name], progress, cancellationToken, checkExitCode: true, description: $"升级 {name}");

    // 全局依赖树索引:首次查询时跑一次 `npm ls -g --json`(整树),之后节点懒加载直接命中缓存,
    // 进程只派生一次。全局包通常很少,整树体积可接受。
    private readonly Dictionary<string, IReadOnlyList<ToolExtensionDependency>> _dependencyIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _dependencyTreeLoaded;

    public override async Task<IReadOnlyList<ToolExtensionDependency>> GetDependenciesAsync(
        string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_dependencyIndex.TryGetValue(name, out var cached))
        {
            return cached;
        }

        if (!_dependencyTreeLoaded)
        {
            var result = await RunNpmAsync(["ls", "-g", "--json"], progress, cancellationToken, checkExitCode: false, description: "查询依赖");
            foreach (var (packageName, dependencies) in BuildDependencyIndex(result.StandardOutput))
            {
                _dependencyIndex[packageName] = dependencies;
            }

            _dependencyTreeLoaded = true;
        }

        return _dependencyIndex.TryGetValue(name, out var found) ? found : [];
    }

    private async Task<ProcessResult> RunNpmAsync(
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken,
        bool checkExitCode,
        string? description = null)
    {
        // cmd.exe /s /c 剥掉整个命令行最外层的引号:把完整命令串作为单个参数传入,
        // ArgumentList 负责加引号,cmd 还原后执行,从而解析 PATH 上的 npm.cmd shim。
        var commandLine = string.Join(" ", new[] { "npm" }.Concat(arguments).Select(QuoteForCommandLine));
        var cmdArguments = new[] { "/s", "/c", commandLine };
        var result = await ProcessRunner.RunAsync(CmdExe, cmdArguments, progress, cancellationToken);
        if (checkExitCode && !result.IsSuccess)
        {
            var detail = (result.StandardError.Trim() is { Length: > 0 } error ? error : result.StandardOutput.Trim());
            throw new InvalidOperationException(
                $"{EcosystemName} {description}失败（exit {result.ExitCode}）：{Truncate(detail)}");
        }

        return result;
    }

    private static string QuoteForCommandLine(string value) =>
        value.IndexOfAny([' ', '\t', '"', '&', '|', '(', ')', '<', '>', '^', '%']) >= 0
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;

    internal static IReadOnlyList<ToolExtension> ParseInstalled(string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = new List<ToolExtension>();
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("dependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var package in dependencies.EnumerateObject())
        {
            var version = package.Value.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString() ?? string.Empty
                : string.Empty;
            list.Add(new ToolExtension(ToolExtensionEcosystem.Npm, package.Name, version, null));
        }

        return list;
    }

    internal static IReadOnlyList<ToolExtension> ParseOutdated(string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = new List<ToolExtension>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var package in document.RootElement.EnumerateObject())
        {
            var latest = package.Value.TryGetProperty("latest", out var latestElement)
                ? latestElement.GetString() ?? string.Empty
                : string.Empty;
            list.Add(new ToolExtension(ToolExtensionEcosystem.Npm, package.Name, string.Empty, latest));
        }

        return list;
    }

    /// <summary>把 <c>npm ls -g --json</c> 整树转成 name → 直接依赖 索引。遍历每个包节点:
    /// 其直接 dependencies 即该包的子节点;嵌套包自身有 dependencies 时非叶子。peer 冲突/
    /// override 场景下 key 可能带 "@版本" 后缀,索引时剥除,匹配用 OrdinalIgnoreCase。</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<ToolExtensionDependency>> BuildDependencyIndex(string json)
    {
        var index = new Dictionary<string, IReadOnlyList<ToolExtensionDependency>>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return index;
        }

        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object
                || !node.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var package in dependencies.EnumerateObject())
            {
                var packageName = StripVersionSuffix(package.Name);
                var version = package.Value.TryGetProperty("version", out var versionElement)
                    ? versionElement.GetString()
                    : null;
                var children = new List<ToolExtensionDependency>();
                if (package.Value.TryGetProperty("dependencies", out var childDependencies)
                    && childDependencies.ValueKind == JsonValueKind.Object)
                {
                    foreach (var child in childDependencies.EnumerateObject())
                    {
                        var childVersion = child.Value.TryGetProperty("version", out var childVersionElement)
                            ? childVersionElement.GetString()
                            : null;
                        var childHasChildren = child.Value.TryGetProperty("dependencies", out var grandchildren)
                            && grandchildren.ValueKind == JsonValueKind.Object;
                        children.Add(new ToolExtensionDependency(StripVersionSuffix(child.Name), childVersion, null, IsLeaf: !childHasChildren));
                    }
                }

                index[packageName] = children;
                Walk(package.Value);
            }
        }

        Walk(document.RootElement);
        return index;
    }

    /// <summary>剥除 npm 树 key 上可能存在的 "@版本" 后缀(peer 冲突/override 场景);
    /// scoped 包名("@scope/pkg")自身的 @ 从下标 1 起找,不会误剥。</summary>
    internal static string StripVersionSuffix(string packageName)
    {
        var atIndex = packageName.IndexOf('@', 1);
        return atIndex > 1 ? packageName[..atIndex] : packageName;
    }

    private static string Truncate(string value, int maxLen = 200) =>
        value.Length <= maxLen ? value : string.Concat(value.AsSpan(0, maxLen - 3), "...");
}
