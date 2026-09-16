using System.Text.Json;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.Runtime.Extensions;

/// <summary>Python 生态适配器:经 <c>python -m pip</c> 运行,不依赖 pip.exe 是否在 PATH
/// (定位到 python 即可)。列表命令输出 JSON,直接反序列化。</summary>
public sealed class PipExtensionProvider(IProcessRunner processRunner) : ToolExtensionProviderBase("pip", processRunner)
{
    public override ToolExtensionEcosystem Ecosystem => ToolExtensionEcosystem.Pip;

    public override async Task<IReadOnlyList<ToolExtension>> ListInstalledAsync(
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await RunPipAsync(["list", "--format=json"], progress, cancellationToken, "列出已安装包");
        return ParseInstalled(result.StandardOutput);
    }

    public override async Task<IReadOnlyList<ToolExtension>> ListOutdatedAsync(
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await RunPipAsync(["list", "--outdated", "--format=json"], progress, cancellationToken, "检查可用更新");
        return ParseOutdated(result.StandardOutput);
    }

    public override async Task InstallAsync(string name, string? version = null,
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var arguments = string.IsNullOrWhiteSpace(version)
            ? new List<string> { "install", "--upgrade", name }
            : new List<string> { "install", $"{name}=={version.Trim()}" };
        await RunPipAsync(arguments, progress, cancellationToken, $"安装 {name}");
    }

    public override async Task UninstallAsync(string name,
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        await RunPipAsync(["uninstall", "-y", name], progress, cancellationToken, $"卸载 {name}");

    public override async Task UpgradeAsync(string name,
        IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) =>
        await RunPipAsync(["install", "--upgrade", name], progress, cancellationToken, $"升级 {name}");

    public override async Task<IReadOnlyList<ToolExtensionDependency>> GetDependenciesAsync(
        string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await RunPipAsync(["show", name], progress, cancellationToken, "查询依赖");
        return ParseRequires(result.StandardOutput);
    }

    private async Task<ProcessResult> RunPipAsync(
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken,
        string description)
    {
        var python = await LocateExecutableAsync(ProcessRunner, "python", cancellationToken)
            ?? throw new InvalidOperationException($"{EcosystemName} 不可用：未在 PATH 中找到 python。");
        var fullArguments = new[] { "-m", "pip" }.Concat(arguments).ToArray();
        return await RunCheckedAsync(python, fullArguments, progress, cancellationToken, description);
    }

    internal static IReadOnlyList<ToolExtension> ParseInstalled(string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = new List<ToolExtension>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("name", out var name) || !element.TryGetProperty("version", out var version))
            {
                continue;
            }

            list.Add(new ToolExtension(ToolExtensionEcosystem.Pip, name.GetString() ?? string.Empty, version.GetString() ?? string.Empty, null));
        }

        return list;
    }

    internal static IReadOnlyList<ToolExtension> ParseOutdated(string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = new List<ToolExtension>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("name", out var name) || !element.TryGetProperty("latest_version", out var latest))
            {
                continue;
            }

            list.Add(new ToolExtension(ToolExtensionEcosystem.Pip, name.GetString() ?? string.Empty, string.Empty, latest.GetString() ?? string.Empty));
        }

        return list;
    }

    /// <summary>解析 <c>pip show</c> 的 RFC 头输出中的第一个 <c>Requires:</c> 字段
    /// (值可能以空白缩进延续到下一行);值以逗号分隔各直接依赖。</summary>
    internal static IReadOnlyList<ToolExtensionDependency> ParseRequires(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("Requires:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = lines[i]["Requires:".Length..].Trim();
            var j = i + 1;
            while (j < lines.Length && (lines[j][0] is ' ' or '\t'))
            {
                value += " " + lines[j].Trim();
                j++;
            }

            return SplitRequirements(value);
        }

        return [];
    }

    /// <summary>拆分 pip 需求字符串为依赖项:"pkg[extra]&gt;=1.0,&lt;2" → Name="pkg"、
    /// Constraint="&gt;=1.0,&lt;2";无版本说明符时 Constraint=null。IsLeaf 不可提前判定(要递归
    /// 展开才知道),统一置 false 让节点可尝试展开。</summary>
    internal static IReadOnlyList<ToolExtensionDependency> SplitRequirements(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var list = new List<ToolExtensionDependency>();
        foreach (var part in value.Split(','))
        {
            var requirement = part.Trim();
            if (requirement.Length == 0)
            {
                continue;
            }

            // 版本说明符内部的续段(如 "pkg>=1.0,<2" 的 "<2"):以版本符号开头则并入前一项的
            // Constraint,避免被误判为独立包。
            if (requirement[0] is '<' or '>' or '=' or '!' or '~')
            {
                if (list.Count == 0)
                {
                    continue;
                }

                var previous = list[^1];
                list[^1] = previous with
                {
                    Constraint = previous.Constraint is null ? requirement : previous.Constraint + "," + requirement,
                };
                continue;
            }

            var nameEnd = requirement.IndexOfAny(['[', '=', '<', '>', '!', '~', ';']);
            if (nameEnd < 0)
            {
                list.Add(new ToolExtensionDependency(requirement, null, null, IsLeaf: false));
                continue;
            }

            var name = requirement[..nameEnd].Trim();
            var constraint = requirement[nameEnd..].Trim();
            var extrasStart = constraint.IndexOf('[');
            if (extrasStart >= 0)
            {
                var extrasEnd = constraint.IndexOf(']', extrasStart);
                if (extrasEnd >= 0)
                {
                    constraint = constraint.Remove(extrasStart, extrasEnd - extrasStart + 1);
                }
            }

            constraint = constraint.Trim();
            if (name.Length > 0)
            {
                list.Add(new ToolExtensionDependency(name, null, constraint.Length == 0 ? null : constraint, IsLeaf: false));
            }
        }

        return list;
    }
}
