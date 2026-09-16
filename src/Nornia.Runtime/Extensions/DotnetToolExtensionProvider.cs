using System.Text.RegularExpressions;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.Runtime.Extensions;

/// <summary>.NET SDK 生态适配器:管理全局 dotnet 工具(<c>dotnet tool -g</c>)。
/// dotnet tool CLI 没有可靠的 outdated 命令,故「可用更新」恒为空列表,升级命令仍可用
/// (<c>dotnet tool update -g</c> 只升级有更新的工具)。</summary>
public sealed partial class DotnetToolExtensionProvider(IProcessRunner processRunner) : ToolExtensionProviderBase("dotnet-tool", processRunner)
{
    private const string Executable = "dotnet";

    public override ToolExtensionEcosystem Ecosystem => ToolExtensionEcosystem.DotnetTool;

    public override async Task<IReadOnlyList<ToolExtension>> ListInstalledAsync(
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await RunDotnetToolAsync(["tool", "list", "-g"], progress, cancellationToken, "列出已安装工具");
        return ParseInstalled(result.StandardOutput);
    }

    public override Task<IReadOnlyList<ToolExtension>> ListOutdatedAsync(
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        // dotnet tool CLI 无可靠 outdated 命令;升级命令仍可用。
        Task.FromResult<IReadOnlyList<ToolExtension>>([]);

    public override async Task InstallAsync(string name, string? version = null,
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "tool", "install", "-g", name };
        if (!string.IsNullOrWhiteSpace(version))
        {
            arguments.Add("--version");
            arguments.Add(version.Trim());
        }

        await RunDotnetToolAsync(arguments, progress, cancellationToken, $"安装 {name}");
    }

    public override async Task UninstallAsync(string name,
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        await RunDotnetToolAsync(["tool", "uninstall", "-g", name], progress, cancellationToken, $"卸载 {name}");

    public override async Task UpgradeAsync(string name,
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        await RunDotnetToolAsync(["tool", "update", "-g", name], progress, cancellationToken, $"升级 {name}");

    public override Task<IReadOnlyList<ToolExtensionDependency>> GetDependenciesAsync(
        string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        // dotnet tool CLI 无依赖概念,返回空列表。
        Task.FromResult<IReadOnlyList<ToolExtensionDependency>>([]);

    private async Task<ProcessResult> RunDotnetToolAsync(
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken,
        string description)
    {
        var dotnet = await LocateExecutableAsync(ProcessRunner, Executable, cancellationToken)
            ?? throw new InvalidOperationException($"{EcosystemName} 不可用：未在 PATH 中找到 dotnet。");
        return await RunCheckedAsync(dotnet, arguments, progress, cancellationToken, description);
    }

    internal static IReadOnlyList<ToolExtension> ParseInstalled(string output)
    {
        var list = new List<ToolExtension>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("Package Id", StringComparison.OrdinalIgnoreCase) || HeaderSeparatorRegex().IsMatch(line))
            {
                continue;
            }

            var columns = Regex.Split(line.Trim(), @"\s{2,}", RegexOptions.None)
                .Where(column => column.Length > 0)
                .ToArray();
            if (columns.Length >= 2)
            {
                list.Add(new ToolExtension(ToolExtensionEcosystem.DotnetTool, columns[0], columns[1], null));
            }
        }

        return list;
    }

    [GeneratedRegex(@"^[\s\-—=]+$")]
    private static partial Regex HeaderSeparatorRegex();
}
