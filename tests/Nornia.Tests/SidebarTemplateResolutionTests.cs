using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

[Collection("WpfStaSequential")]
public sealed class SidebarTemplateResolutionTests
{
    [Theory]
    [InlineData(typeof(GitViewModel), typeof(GitView))]
    [InlineData(typeof(ProjectsViewModel), typeof(ProjectsSidebarView))]
    public void SidebarUsesApplicationCatalogInsteadOfEditorPageTemplate(Type pageType, Type viewType)
    {
        WpfStaContext.Run(() =>
        {
            var template = MainWindow.ResolveSidebarTemplate(pageType);
            Assert.NotNull(template);
            Assert.IsType(viewType, template.LoadContent());
        });
    }
}
