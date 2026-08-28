using System.Reflection;
using Nornia.Core.Interfaces;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Project.Services;

namespace Nornia.Tests;

public sealed class ProjectsViewModelConstructorTests
{
    [Fact]
    public void ProjectsViewModel_UsesOneCompleteDependencyInjectionConstructor()
    {
        var constructor = Assert.Single(typeof(ProjectsViewModel).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        var parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Contains(typeof(IProjectWorkspaceService), parameterTypes);
        Assert.Contains(typeof(IUiPerformanceMetrics), parameterTypes);
    }
}
