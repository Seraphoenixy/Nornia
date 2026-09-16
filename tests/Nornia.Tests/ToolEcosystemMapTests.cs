using Nornia.Core.Models;
using Nornia.Runtime.Extensions;

namespace Nornia.Tests;

public sealed class ToolEcosystemMapTests
{
    [Theory]
    [InlineData("python", ToolExtensionEcosystem.Pip)]
    [InlineData("node", ToolExtensionEcosystem.Npm)]
    [InlineData("dotnet", ToolExtensionEcosystem.DotnetTool)]
    [InlineData("Python", ToolExtensionEcosystem.Pip)]
    [InlineData("NODE", ToolExtensionEcosystem.Npm)]
    public void TryGet_MapsKnownComponents(string componentId, ToolExtensionEcosystem expected)
    {
        Assert.True(ToolEcosystemMap.TryGet(componentId, out var ecosystem));
        Assert.Equal(expected, ecosystem);
    }

    [Theory]
    [InlineData("java")]
    [InlineData("git")]
    [InlineData("visual-cpp-redistributable")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGet_UnknownOrEmptyComponentsReturnFalse(string? componentId)
    {
        Assert.False(ToolEcosystemMap.TryGet(componentId!, out _));
    }
}
