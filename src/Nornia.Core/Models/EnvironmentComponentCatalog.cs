namespace Nornia.Core.Models;

public enum EnvironmentComponentCategory
{
    Runtime,
    DevelopmentTool
}

public sealed record EnvironmentComponent(
    string Id,
    string DisplayName,
    EnvironmentComponentCategory Category,
    bool CanManageWithWinget,
    IReadOnlyList<string> Aliases);

public static class EnvironmentComponentCatalog
{
    private static readonly EnvironmentComponent[] Components =
    [
        new("dotnet", ".NET SDK", EnvironmentComponentCategory.DevelopmentTool, true, ["dotnet", ".net", ".net sdk"]),
        new("node", "Node.js", EnvironmentComponentCategory.DevelopmentTool, true, ["node", "nodejs", "node.js"]),
        new("python", "Python", EnvironmentComponentCategory.DevelopmentTool, true, ["python"]),
        new("java", "Java", EnvironmentComponentCategory.DevelopmentTool, true, ["java", "openjdk"]),
        new("git", "Git", EnvironmentComponentCategory.DevelopmentTool, true, ["git"]),
        new("visual-cpp-redistributable", "Visual C++ Redistributable", EnvironmentComponentCategory.Runtime, true, ["vcredist", "visual c++ redistributable"]),
        new("dotnet-desktop-runtime", ".NET Desktop Runtime", EnvironmentComponentCategory.Runtime, true, ["dotnet desktop runtime", "windowsdesktop"]),
        new("windows-app-runtime", "Windows App Runtime", EnvironmentComponentCategory.Runtime, true, ["windowsappruntime", "windows app runtime"])
    ];

    /// <summary>Pre-built lookup index over Id, DisplayName and every alias so that name resolution
    /// is O(1) instead of a linear scan, while staying case-insensitive like the legacy search.</summary>
    private static readonly Dictionary<string, EnvironmentComponent> Index = BuildIndex();

    public static IReadOnlyList<EnvironmentComponent> All { get; } = Components;

    public static bool TryGet(string name, out EnvironmentComponent component)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            component = null!;
            return false;
        }

        return Index.TryGetValue(name, out component!);
    }

    public static EnvironmentComponent? Get(string name) => TryGet(name, out var component) ? component : null;

    public static IReadOnlyList<EnvironmentComponent> GetByCategory(EnvironmentComponentCategory category) =>
        Components.Where(component => component.Category == category).ToArray();

    private static Dictionary<string, EnvironmentComponent> BuildIndex()
    {
        var index = new Dictionary<string, EnvironmentComponent>(Components.Length * 3, StringComparer.OrdinalIgnoreCase);
        foreach (var component in Components)
        {
            index.TryAdd(component.Id, component);
            index.TryAdd(component.DisplayName, component);
            foreach (var alias in component.Aliases)
            {
                index.TryAdd(alias, component);
            }
        }

        return index;
    }
}