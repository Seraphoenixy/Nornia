using System.Windows.Documents;
using System.Windows;
using System.Windows.Controls;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.Views.Controls;

namespace Nornia.Tests;

public sealed class CommitMessageViewTests
{
    [Fact]
    public void Renderer_TreatsRawMessageListLinesAsMarkdownBlocks()
    {
        WpfStaContext.Run(() =>
        {
            var document = CommitMessageRenderer.Render("feat: improve git\n- first change\n- second change");
            var blocks = document.Blocks.Cast<Block>().ToArray();

            Assert.Equal(3, blocks.Length);
            var firstListItem = Assert.IsType<Paragraph>(blocks[1]);
            Assert.Equal(6, firstListItem.Padding.Left);
            Assert.Equal("• ", Assert.IsType<Run>(firstListItem.Inlines.FirstInline).Text);
        });
    }

    [Fact]
    public void Renderer_KeepsLongMixedLanguageLinesLeftAlignedAndBreakable()
    {
        WpfStaContext.Run(() =>
        {
            const string message = "新增 scan_state 表、InventoryScanStateRepository、IEnvironmentFingerprintProvider";
            var document = CommitMessageRenderer.Render(message);
            var paragraph = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            var rendered = string.Concat(paragraph.Inlines.OfType<Run>().Select(run => run.Text));

            Assert.Equal(TextAlignment.Left, document.TextAlignment);
            Assert.Equal(TextAlignment.Left, paragraph.TextAlignment);
            Assert.Contains('\u200B', rendered);
            Assert.Equal(message, rendered.Replace("\u200B", string.Empty, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void HoverMarkdown_GrowsWithContentWithoutInternalScrollBar()
    {
        WpfStaContext.Run(() =>
        {
            var view = new MarkdownMessageView();

            Assert.True(double.IsPositiveInfinity(view.MaxHeight));
            Assert.Equal(ScrollBarVisibility.Disabled, view.VerticalScrollBarVisibility);
        });
    }

    [Fact]
    public void HoverMarkdown_TracksNaturalContentWidthUpToMaximum()
    {
        WpfStaContext.Run(() =>
        {
            var view = new MarkdownMessageView
            {
                Markdown = "fix: short",
                MaxWidth = 550,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var shortWidth = view.DesiredSize.Width;

            Assert.True(shortWidth < 550,
                $"短内容应按自然宽度测量，实际为 {shortWidth}px");

            view.Markdown = $"feat: {new string('W', 120)}";
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Assert.True(view.DesiredSize.Width > shortWidth);
            Assert.Equal(550, view.DesiredSize.Width);
        });
    }
}
