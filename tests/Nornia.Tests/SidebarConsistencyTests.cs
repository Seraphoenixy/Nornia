using Nornia.Desktop.Views.Controls;
using Nornia.Desktop.Converters;
using Nornia.Desktop.Views;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Behaviors;
using Nornia.Core.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Nornia.Tests;

public sealed class SidebarConsistencyTests
{
    [Theory]
    [InlineData("A", SidebarFileStatusKind.Added, "A", "SuccessBrush")]
    [InlineData("?", SidebarFileStatusKind.Added, "A", "SuccessBrush")]
    [InlineData("M", SidebarFileStatusKind.Modified, "M", "ScmModifiedBrush")]
    [InlineData("D", SidebarFileStatusKind.Deleted, "D", "ScmDeletedBrush")]
    [InlineData("R", SidebarFileStatusKind.Renamed, "R", "InfoAccentBrush")]
    [InlineData("C", SidebarFileStatusKind.Copied, "C", "InfoAccentBrush")]
    [InlineData("T", SidebarFileStatusKind.TypeChanged, "T", "ScmModifiedBrush")]
    [InlineData("U", SidebarFileStatusKind.Conflict, "U", "ScmDeletedBrush")]
    [InlineData("", SidebarFileStatusKind.None, "", "")]
    [InlineData(".", SidebarFileStatusKind.None, "", "")]
    public void FileStatusPresentation_UsesOneMappingForExplorerAndGit(
        string input, SidebarFileStatusKind kind, string letter, string brushKey)
    {
        var presentation = SidebarFileStatusPresentation.FromLetter(input);

        Assert.Equal(kind, presentation.Kind);
        Assert.Equal(letter, presentation.Letter);
        Assert.Equal(brushKey, presentation.BrushKey);
        Assert.Equal(kind != SidebarFileStatusKind.None, presentation.IsVisible);
    }

    [Fact]
    public void SidebarViews_LoadSharedTemplatesOnWpfSta()
    {
        WpfStaContext.Run(() =>
        {
            _ = new SidebarShell();
            _ = new SidebarRowChrome();
            _ = new SidebarFileTypeBadge();
            _ = new SidebarTreeGuideLayer();
            _ = new FileChangeStatusIndicator { StatusLetter = "M" };
            _ = new ExplorerSidebarView();
            _ = new SearchSidebarView();
            _ = new GitView();
        });
    }

    [Fact]
    public void GitFlatChangeRowTemplate_LoadsConverterBindingsAtRuntime()
    {
        WpfStaContext.Run(() =>
        {
            var view = new GitView();
            var template = Assert.IsType<DataTemplate>(view.Resources["ScmFileRowTemplate"]);
            var row = Assert.IsAssignableFrom<FrameworkElement>(template.LoadContent());
            row.DataContext = new GitChangeItem(new GitFileChange(
                "src/features/Example.cs", GitChangeStatus.Unmodified, GitChangeStatus.Modified));

            row.Measure(new Size(420, 40));

            Assert.True(row.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void GitCommitRowTemplate_BindsReadOnlySyncTargetOneWayAtRuntime()
    {
        WpfStaContext.Run(() =>
        {
            var view = new GitView();
            var template = Assert.IsType<DataTemplate>(view.Resources["ScmCommitBarTemplate"]);
            var row = Assert.IsAssignableFrom<FrameworkElement>(template.LoadContent());
            row.DataContext = new GitLogRow(
                new GitCommitInfo("HEAD...origin/main", "", "传入的更改", null, "origin/main", "-", DateTimeOffset.UtcNow),
                new GitGraphRow(0, [], [], 1, DotHollow: true, DotDashed: true),
                syncTarget: "origin/main");

            row.Measure(new Size(420, 40));

            Assert.True(row.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void GitCommitList_ExpandedRowUpdatesPixelScrollExtent()
    {
        WpfStaContext.Run(() =>
        {
            var view = new GitView();
            var rows = Enumerable.Range(0, 20).Select(index => new GitLogRow(
                new GitCommitInfo($"c{index}", $"c{index}", $"commit {index}", null, "A", "a@x", DateTimeOffset.UtcNow),
                new GitGraphRow(0, [], [], 1))).ToArray();
            var list = new ListBox
            {
                Width = 420,
                Height = 120,
                ItemsSource = rows,
                ItemTemplate = Assert.IsType<DataTemplate>(view.Resources["ScmCommitBarTemplate"]),
                ItemContainerStyle = Assert.IsType<Style>(view.Resources["ScmGraphRowStyle"]),
            };
            ScrollViewer.SetCanContentScroll(list, true);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
            var host = new Window { Content = list, Width = 440, Height = 160, ShowInTaskbar = false };
            host.Show();
            try
            {
                host.UpdateLayout();
                var scrollViewer = FindVisualChild<ScrollViewer>(list);
                var collapsedExtent = scrollViewer.ExtentHeight;

                rows[0].IsExpanded = true;
                foreach (var index in Enumerable.Range(0, 12))
                {
                    rows[0].Files.Add(new LogFileRow(rows[0], new GitFileChange(
                        $"src/File{index}.cs", GitChangeStatus.Modified, GitChangeStatus.Unmodified)));
                }
                rows[0].IsLoaded = true;
                list.UpdateLayout();
                host.UpdateLayout();

                Assert.True(scrollViewer.ExtentHeight > collapsedExtent,
                    $"展开前 extent={collapsedExtent}，展开后 extent={scrollViewer.ExtentHeight}");
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void GitRowAncestorLookup_AcceptsInlineRunEventSources()
    {
        WpfStaContext.Run(() =>
        {
            var text = new TextBlock();
            var run = new Run("Example.cs");
            text.Inlines.Add(run);

            Assert.Same(text, GitView.FindAncestor<TextBlock>(run));
        });
    }

    [Fact]
    public void RightClickSelectionAncestorLookup_AcceptsInlineRunEventSources()
    {
        WpfStaContext.Run(() =>
        {
            var text = new TextBlock();
            var run = new Run("Example.cs");
            text.Inlines.Add(run);

            Assert.Same(text, ListSelectionBehavior.FindAncestor<TextBlock>(run));
        });
    }

    [Fact]
    public void EnvironmentDataGrid_GeneratedTextCellsAreVerticallyCentered()
    {
        WpfStaContext.Run(() =>
        {
            var grid = new DataGrid
            {
                Width = 400,
                Height = 100,
                AutoGenerateColumns = false,
                RowHeight = 32,
                CellStyle = (Style)Application.Current.FindResource("EnvironmentDataGridCellStyle"),
                ItemsSource = new[] { new EnvironmentTableRow("runtime") },
            };
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            grid.Resources.Add(typeof(TextBlock), textStyle);
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "名称",
                Binding = new System.Windows.Data.Binding(nameof(EnvironmentTableRow.Name)),
                ElementStyle = textStyle,
            });

            var host = new Window { Content = grid, Width = 420, Height = 140, ShowInTaskbar = false };
            host.Show();
            try
            {
                host.UpdateLayout();

                var cell = FindVisualChild<DataGridCell>(grid);
                var text = FindVisualChild<TextBlock>(cell);

                Assert.Equal(VerticalAlignment.Center, cell.VerticalContentAlignment);
                Assert.Equal(VerticalAlignment.Center, text.VerticalAlignment);
            }
            finally
            {
                // 已加载视图持有 ThemeEvents 强订阅;断言失败也必须卸载,避免跨测试类泄漏。
                host.Close();
            }
        });
    }

    [Fact]
    public void BadgeCountVisibility_ReservesZeroSlotOnlyWhenRequested()
    {
        var converter = new BadgeCountVisibilityConverter();
        Assert.Equal(Visibility.Visible, converter.Convert([0, true], typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, converter.Convert([2, false], typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, converter.Convert([0, false], typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    private static T FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
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
                // Continue searching sibling branches.
            }
        }

        throw new InvalidOperationException($"未找到 {typeof(T).Name}。");
    }

    private sealed record EnvironmentTableRow(string Name);
}
