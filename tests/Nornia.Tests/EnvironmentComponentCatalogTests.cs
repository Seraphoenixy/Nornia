using Nornia.Core.Models;

namespace Nornia.Tests;

public sealed class EnvironmentComponentCatalogTests
{
    [Theory]
    [InlineData("Node.js", "node", EnvironmentComponentCategory.DevelopmentTool, true)]
    [InlineData("python", "python", EnvironmentComponentCategory.DevelopmentTool, true)]
    [InlineData("java", "java", EnvironmentComponentCategory.DevelopmentTool, true)]
    [InlineData(".NET", "dotnet", EnvironmentComponentCategory.DevelopmentTool, true)]
    [InlineData("Git", "git", EnvironmentComponentCategory.DevelopmentTool, true)]
    [InlineData("vcredist", "visual-cpp-redistributable", EnvironmentComponentCategory.Runtime, true)]
    public void TryGet_MapsAliasesToTheirCanonicalComponent(string name, string id, EnvironmentComponentCategory category, bool canManageWithWinget)
    {
        Assert.True(EnvironmentComponentCatalog.TryGet(name, out var component));

        Assert.Equal(id, component.Id);
        Assert.Equal(category, component.Category);
        Assert.Equal(canManageWithWinget, component.CanManageWithWinget);
    }
}
