namespace Nornia.Core.Models;

/// <summary>开发工具的可扩展依赖生态。每个生态对应一个 CLI 管理命令
/// (pip / npm / dotnet tool),由独立的 <see cref="Nornia.Core.Interfaces.IToolExtensionProvider"/> 适配。</summary>
public enum ToolExtensionEcosystem
{
    Pip,
    Npm,
    DotnetTool
}

/// <summary>开发工具的一个已安装扩展依赖(生态包)。AvailableVersion 为 null 表示
/// 未检测到可用更新(dotnet tool 无可靠 outdated 命令)或已是最新。</summary>
public sealed record ToolExtension(
    ToolExtensionEcosystem Ecosystem,
    string Name,
    string Version,
    string? AvailableVersion)
{
    public bool HasUpdate => !string.IsNullOrWhiteSpace(AvailableVersion);
}

/// <summary>扩展依赖树的直接依赖项(一层)。Version 为该依赖的已装版本(不可得时为 null),
/// Constraint 存 pip 版本说明符原文(如 "&gt;=1.0,&lt;2",npm 通常无)。</summary>
public sealed record ToolExtensionDependency(string Name, string? Version, string? Constraint, bool IsLeaf);
