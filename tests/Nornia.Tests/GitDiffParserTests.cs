using Nornia.Core.Models;
using Nornia.Git.Parsing;

namespace Nornia.Tests;

public sealed class GitDiffParserTests
{
    [Fact]
    public void Parse_SimpleHunkWithLineNumbers()
    {
        const string output = """
            diff --git a/src/A.cs b/src/A.cs
            index 123..456 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -10,4 +10,5 @@
             context-one
            -removed-line
            +added-line
            +another-added
             context-two
            """;

        var diff = GitDiffParser.Parse(output, "src/A.cs");

        Assert.False(diff.IsBinary);
        Assert.False(diff.IsNewFile);
        Assert.Equal("src/A.cs", diff.OldPath);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(10, hunk.OldStart);
        Assert.Equal(10, hunk.NewStart);
        Assert.Contains(hunk.Lines, line => line.Kind == GitDiffLineKind.Removed && line.Text == "removed-line" && line.OldLineNumber == 11);
        Assert.Contains(hunk.Lines, line => line.Kind == GitDiffLineKind.Added && line.Text == "added-line" && line.NewLineNumber == 11);
        Assert.Contains(hunk.Lines, line => line.Kind == GitDiffLineKind.Context && line.Text == "context-two" && line.OldLineNumber == 12 && line.NewLineNumber == 13);
    }

    [Fact]
    public void Parse_NewFileFromDevNull()
    {
        const string output = """
            diff --git a/src/New.cs b/src/New.cs
            new file mode 100644
            index 000..abc
            --- /dev/null
            +++ b/src/New.cs
            @@ -0,0 +1,2 @@
            +line-1
            +line-2
            """;

        var diff = GitDiffParser.Parse(output, "src/New.cs");

        Assert.True(diff.IsNewFile);
        Assert.Null(diff.OldPath);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(0, hunk.OldStart);
        Assert.Equal(1, hunk.NewStart);
        Assert.All(hunk.Lines.Where(line => line.Kind == GitDiffLineKind.Added), line => Assert.NotNull(line.NewLineNumber));
    }

    [Fact]
    public void Parse_BinaryDiffIsFlagged()
    {
        const string output = """
            diff --git a/img.png b/img.png
            index 123..456 100644
            Binary files a/img.png and b/img.png differ
            """;

        var diff = GitDiffParser.Parse(output, "img.png");

        Assert.True(diff.IsBinary);
        Assert.Empty(diff.Hunks);
    }

    [Fact]
    public void Parse_NoNewlineAtEndOfFileNotice()
    {
        const string output = """
            diff --git a/x.txt b/x.txt
            --- a/x.txt
            +++ b/x.txt
            @@ -1 +1 @@
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file
            """;

        var diff = GitDiffParser.Parse(output, "x.txt");

        var notices = Assert.Single(diff.Hunks).Lines.Where(line => line.Kind == GitDiffLineKind.Notice).ToArray();
        Assert.Equal(2, notices.Length);
    }

    [Fact]
    public void Parse_MultipleHunksAreCollected()
    {
        const string output = """
            --- a/multi.txt
            +++ b/multi.txt
            @@ -1,2 +1,2 @@
             a
             b
            @@ -10,1 +10,1 @@
            -x
            +y
            """;

        var diff = GitDiffParser.Parse(output, "multi.txt");

        Assert.Equal(2, diff.Hunks.Count);
        Assert.Equal(1, diff.Hunks[0].OldStart);
        Assert.Equal(10, diff.Hunks[1].OldStart);
    }

    [Fact]
    public void SideBySideRows_AlignContextAndPairRemovedAdded()
    {
        const string output = """
            --- a/side.txt
            +++ b/side.txt
            @@ -1,5 +1,5 @@
             keep
            -old-one
            -old-two
            +new-one
            +new-two
            +extra
             keep-end
            """;

        var diff = GitDiffParser.Parse(output, "side.txt");
        var rows = diff.ToSideBySideRows();

        // hunk header + spacer rows first
        var content = rows.Where(row => row.HasOldContent || row.HasNewContent).ToArray();
        Assert.Equal(5, content.Length); // keep, old1/new1, old2/new2, extra(padded), keep-end
        Assert.Contains(content, row => row.OldText == "old-one" && row.NewText == "new-one");
        Assert.Contains(content, row => row.OldText == "old-two" && row.NewText == "new-two");
        Assert.Contains(content, row => row.OldText == string.Empty && row.NewText == "extra");
        Assert.Equal(4, content.Count(row => row.HasOldContent)); // keep, old-one, old-two, keep-end
    }
}