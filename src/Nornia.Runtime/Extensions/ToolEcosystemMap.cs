using Nornia.Core.Models;

namespace Nornia.Runtime.Extensions;

/// <summary>开发工具组件(catalog Id)→ 扩展依赖生态的静态映射。独立于
/// <see cref="Nornia.Core.Models.EnvironmentComponentCatalog"/> 保持该目录与既有测试零扰动;
/// 生态能力是工具管理侧的新维度,按需查询。</summary>
public static class ToolEcosystemMap
{
    private static readonly Dictionary<string, ToolExtensionEcosystem> ByComponent = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = ToolExtensionEcosystem.Pip,
        ["node"] = ToolExtensionEcosystem.Npm,
        ["dotnet"] = ToolExtensionEcosystem.DotnetTool
    };

    public static bool TryGet(string componentId, out ToolExtensionEcosystem ecosystem)
    {
        if (string.IsNullOrWhiteSpace(componentId))
        {
            ecosystem = default;
            return false;
        }

        return ByComponent.TryGetValue(componentId, out ecosystem);
    }
}
