using System.Windows;
using System.Windows.Media;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class FilePreviewViewReuseTests
{
    [Fact]
    public void SwitchingFileTabs_ReusesThePreviewAndOutlineSurface()
    {
        WpfStaContext.Run(() =>
        {
            var firstTab = new FilePreviewTab(@"C:\first.cs");
            var secondTab = new FilePreviewTab(@"C:\second.cs");
            var group = new EditorGroupViewModel("group");
            group.Tabs.Add(firstTab);
            group.Tabs.Add(secondTab);
            group.SelectedTab = firstTab;
            var groupView = new EditorGroupView { DataContext = group };
            var window = new Window
            {
                Content = groupView,
                Width = 900,
                Height = 600,
                ShowInTaskbar = false,
            };

            window.Show();
            window.UpdateLayout();
            WpfStaContext.PumpQueue();
            try
            {
                var editor = FindVisualChild<EditorAreaView>(groupView);
                var persistentPreview = FindVisualChild<FilePreviewView>(editor);
                Assert.Same(firstTab, persistentPreview.DataContext);
                Assert.Equal(Visibility.Visible, persistentPreview.Visibility);

                group.SelectedTab = secondTab;
                window.UpdateLayout();
                WpfStaContext.PumpQueue();

                Assert.Same(persistentPreview, FindVisualChild<FilePreviewView>(editor));
                Assert.Same(secondTab, persistentPreview.DataContext);
            }
            finally
            {
                // 已加载视图持有 ThemeEvents 强订阅;断言失败也必须卸载,避免跨测试类泄漏。
                window.Close();
            }
        });
    }

    [Fact]
    public void SwitchingFromFileToDiff_RendersTheSelectedDiff()
    {
        WpfStaContext.Run(() =>
        {
            var fileTab = new FilePreviewTab(@"C:\first.cs");
            var diffTab = new DiffTab(
                new FakeGitService(),
                new GitDiffRequest(@"C:\repo", "src/first.cs", IsStaged: false, IsUntracked: false),
                new FakeUiLogService(),
                new FakeClipboardService());
            var group = new EditorGroupViewModel("group");
            group.Tabs.Add(fileTab);
            group.Tabs.Add(diffTab);
            group.SelectedTab = fileTab;
            var groupView = new EditorGroupView { DataContext = group };
            var window = new Window
            {
                Content = groupView,
                Width = 900,
                Height = 600,
                ShowInTaskbar = false,
            };

            window.Show();
            window.UpdateLayout();
            WpfStaContext.PumpQueue();
            try
            {
                var editor = FindVisualChild<EditorAreaView>(groupView);
                var persistentPreview = FindVisualChild<FilePreviewView>(editor);

                group.SelectedTab = diffTab;
                window.UpdateLayout();
                WpfStaContext.PumpQueue();

                Assert.Equal(Visibility.Collapsed, persistentPreview.Visibility);
                Assert.Same(diffTab, FindVisualChild<DiffDocumentView>(editor).DataContext);
            }
            finally
            {
                // 已加载视图持有 ThemeEvents 强订阅;断言失败也必须卸载,避免跨测试类泄漏。
                window.Close();
            }
        });
    }

    private static T FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            try
            {
                return FindVisualChild<T>(child);
            }
            catch (InvalidOperationException)
            {
                // Search the remaining siblings.
            }
        }

        throw new InvalidOperationException($"未找到 {typeof(T).Name}。");
    }
}
