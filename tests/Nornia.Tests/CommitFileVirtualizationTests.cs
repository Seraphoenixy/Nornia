using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

[Collection("WpfStaSequential")]
public sealed class CommitFileVirtualizationTests
{
    [Fact]
    public void LargeCommit_RealizesOnlyVisibleFilesAndCanScrollToLastFile()
    {
        WpfStaContext.Run(() =>
        {
            var view = new GitView();
            var row = new GitLogRow(new GitCommitInfo("abc", "abc", "large", null, "A", "a@x", DateTimeOffset.UtcNow), null)
            { IsExpanded = true, IsLoaded = true };
            row.Files.ReplaceRange(Enumerable.Range(0, 5000).Select(i => new LogFileRow(row,
                new GitFileChange($"src/file{i:D5}.cs", GitChangeStatus.Modified, GitChangeStatus.Unmodified))));
            view.Content = new ContentControl
            {
                Content = row,
                ContentTemplate = (DataTemplate)view.Resources["ScmCommitBarTemplate"]
            };
            var window = new Window { Content = view, Width = 600, Height = 600, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                WpfStaContext.PumpQueue();
                var list = Find<ListBox>(view)!;
                Assert.NotNull(list);
                Assert.Equal(5000, list.Items.Count);
                Assert.InRange(Find<VirtualizingStackPanel>(list)!.Children.Count, 1, 100);
                list.ScrollIntoView(row.Files[^1]);
                window.UpdateLayout();
                WpfStaContext.PumpQueue();
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(4999));
                Assert.InRange(Find<VirtualizingStackPanel>(list)!.Children.Count, 1, 100);
            }
            finally { window.Close(); }
        });
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Find<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
