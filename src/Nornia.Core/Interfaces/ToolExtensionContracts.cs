using Nornia.Core.Models;

namespace Nornia.Core.Interfaces;

/// <summary>一个开发工具生态(pip / npm / dotnet tool)的扩展依赖适配器。实现方负责
/// 定位生态 CLI 并执行 列表/安装/卸载/升级 命令;与软件包管理(IPackageProvider/Winget)
/// 完全独立,只服务于「开发工具」页的扩展依赖子面板。</summary>
public interface IToolExtensionProvider
{
    ToolExtensionEcosystem Ecosystem { get; }
    string EcosystemName { get; }

    /// <summary>列出已安装的扩展依赖。生态 CLI 缺失时抛 <see cref="InvalidOperationException"/>。</summary>
    Task<IReadOnlyList<ToolExtension>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>列出存在可用更新的扩展依赖(仅返回过时的项,含其可用版本)。
    /// 生态无可靠 outdated 命令时返回空列表。</summary>
    Task<IReadOnlyList<ToolExtension>> ListOutdatedAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);

    Task InstallAsync(string name, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    Task UninstallAsync(string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    Task UpgradeAsync(string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>返回 <paramref name="name"/> 的直接依赖(一层);递归展开由调用方逐节点驱动。
    /// 生态无依赖概念(dotnet tool)时返回空列表。</summary>
    Task<IReadOnlyList<ToolExtensionDependency>> GetDependenciesAsync(string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>开发工具扩展依赖清单服务:组件名 → 生态映射、已装+可用更新合并、
/// 内存 30 秒冷却缓存(与既有 InventoryScanCacheSeconds 一致)。变更操作后调用方须以
/// force=true 刷新,避免冷却缓存掩盖变更结果。</summary>
public interface IToolExtensionInventoryService
{
    /// <summary>组件 catalog Id(python/node/dotnet)→ 生态;无扩展生态的组件返回 null。</summary>
    ToolExtensionEcosystem? GetEcosystemForComponent(string componentId);

    /// <summary>已装 + 可用更新合并后的列表。force=false 命中 30 秒冷却缓存时直接返回,不派生进程。</summary>
    Task<IReadOnlyList<ToolExtension>> RefreshAsync(ToolExtensionEcosystem ecosystem, bool force, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);

    IToolExtensionProvider GetProvider(ToolExtensionEcosystem ecosystem);
}
