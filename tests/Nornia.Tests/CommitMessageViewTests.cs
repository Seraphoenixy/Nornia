using System.Windows.Documents;
using Nornia.Desktop.Markdown;

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
}
