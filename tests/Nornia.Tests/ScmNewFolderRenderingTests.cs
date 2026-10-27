using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

[Collection("WpfStaSequential")]
public sealed class ScmNewFolderRenderingTests
{
    [Fact]
    public void UntrackedFileInNewFolder_HasVisibleNameInTreeRow()
    {
        WpfStaContext.Run(() =>
        {
            var view = new GitView();
            var rows = GitViewModel.BuildTreeRows([new GitChangeItem(new GitFileChange(
                "new-folder/nested/new-file.txt", GitChangeStatus.Unmodified, GitChangeStatus.Untracked))]);
            var file = Assert.Single(rows.OfType<ScmFileNode>());
            var list = new ListBox
            {
                ItemsSource = new[] { file },
                ItemContainerStyle = (Style)view.Resources["ScmRowStyle"]
            };
            var wrapper = new FrameworkElementFactory(typeof(ContentControl));
            wrapper.SetBinding(ContentControl.ContentProperty, new Binding());
            wrapper.SetValue(ContentControl.ContentTemplateProperty, view.Resources["ScmTreeFileRowTemplate"]);
            list.Resources.Add(new DataTemplateKey(typeof(ScmFileNode)), new DataTemplate { VisualTree = wrapper });
            view.Content = list;
            var window = new Window { Content = view, Width = 320, Height = 200, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                WpfStaContext.PumpQueue();
                var name = Descendants(list).OfType<TextBlock>().Single(x => x.Text == "new-file.txt");
                Assert.True(name.ActualWidth > 20, $"File name width: {name.ActualWidth}");
                Assert.True(name.ActualHeight > 0);
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
