using Nornia.Desktop.Terminal;
using System.Text;

namespace Nornia.Tests;

/// <summary>Covers the per-line content versions on <see cref="TerminalScreen"/> that back the
/// terminal surface's dirty-row rendering (VS Code / xterm.js style partial redraw): a line's
/// version changes exactly when its cells change, and versions travel with content through
/// scrolls and scrollback. Model conventions: the cursor starts at the top row, the minimum
/// screen is 10 rows, and <c>GetLineVersion</c> is indexed newest-first (bottom row = 0).</summary>
public sealed class TerminalScreenVersionTests
{
    [Fact]
    public void WriteChar_BumpsOnlyTheCursorLineVersion()
    {
        var screen = new TerminalScreen(80, 10);
        var topBefore = screen.GetLineVersion(9);   // cursor starts at the top row
        var bottomBefore = screen.GetLineVersion(0);

        screen.WriteChar('x', 0, 0, false, false);

        Assert.Equal(topBefore + 1, screen.GetLineVersion(9));
        Assert.Equal(bottomBefore, screen.GetLineVersion(0)); // untouched line keeps its version
    }

    [Fact]
    public void CursorOnlyMoves_DoNotBumpLineVersions()
    {
        var screen = new TerminalScreen(80, 10);
        screen.WriteChar('a', 0, 0, false, false);
        var top = screen.GetLineVersion(9);

        screen.WriteChar('\r', 0, 0, false, false); // caret back to column 0, no cell written
        screen.MoveCursorTo(0, 0);                  // caret move only

        Assert.Equal(top, screen.GetLineVersion(9));
        Assert.Equal(0, screen.GetLineVersion(0));  // blank bottom row untouched
    }

    [Fact]
    public void Scroll_ShiftsVersionsWithTheContent()
    {
        var screen = new TerminalScreen(80, 10);
        screen.WriteChar('a', 0, 0, false, false);  // top row (LFB 9)
        var aVersion = screen.GetLineVersion(9);
        screen.MoveCursorTo(1, 0);
        screen.WriteChar('b', 0, 0, false, false);  // row 1 (LFB 8)
        var bVersion = screen.GetLineVersion(8);

        // Newline at the bottom row scrolls: top row → scrollback, content shifts down.
        screen.MoveCursorTo(9, 0);
        screen.WriteChar('\n', 0, 0, false, false);

        Assert.Equal(aVersion, screen.GetLineVersion(10)); // 'a' in scrollback, version preserved
        Assert.Equal(bVersion, screen.GetLineVersion(9));  // 'b' moved with the shift
        Assert.Equal(0, screen.GetLineVersion(0));         // new bottom row is blank
    }

    [Fact]
    public void EraseInDisplay_BumpsAllLines()
    {
        var screen = new TerminalScreen(80, 10);
        screen.WriteChar('a', 0, 0, false, false);
        var topBefore = screen.GetLineVersion(9);

        screen.EraseInDisplay(2); // erase whole screen

        Assert.True(screen.GetLineVersion(9) > topBefore);
    }

    [Fact]
    public void BufferSwitch_BumpsAllRowsOfTheActivatedBuffer()
    {
        var screen = new TerminalScreen(80, 10);
        screen.WriteChar('a', 0, 0, false, false); // primary top row
        var primaryTopVersion = screen.GetLineVersion(9);

        screen.EnterAlternateBuffer();
        // Entering the (fresh) alternate buffer bumps every row of it, so blank rows are no
        // longer version 0 — the renderer must re-bake everything on the switch.
        var alternateTopAfterEnter = screen.GetLineVersion(9);
        Assert.True(alternateTopAfterEnter > 0);

        screen.WriteChar('v', 0, 0, false, false);
        Assert.True(screen.GetLineVersion(9) > alternateTopAfterEnter);

        screen.ExitAlternateBuffer();
        // Back on primary with 'a' still at the top; the switch re-bumped every primary row.
        Assert.True(screen.GetLineVersion(9) > primaryTopVersion);
        Assert.Equal('a', screen.GetLine(9)![0].Char);
    }

    [Fact]
    public void Resize_BumpsLineVersionsBecauseColumnsChange()
    {
        var screen = new TerminalScreen(80, 10);
        screen.WriteChar('a', 0, 0, false, false);
        var before = screen.GetLineVersion(9);

        screen.Resize(40, 10); // narrower: content reflows/truncates

        Assert.True(screen.GetLineVersion(9) > before);
    }

    /// <summary>Regression for the scrambled-build-output bug: the render path must read a line's
    /// cells and its rendered text from one atomic snapshot. This proves <see cref="TerminalScreen.GetLineWithText"/>
    /// always returns a <c>line</c> whose visible cells correspond positionally to <c>text</c> across the
    /// output patterns a .NET build hammers the terminal with (plain text, CJK wide chars, in-place
    /// carriage-return progress overwrites, and scrolling).</summary>
    [Theory]
    [InlineData("plain ascii line")]
    [InlineData("中文进度 50%")]
    [InlineData("restore: 12/34 [####------]")]
    public void GetLineWithText_ReturnsSelfConsistentLineAndText(string content)
    {
        var screen = new TerminalScreen(80, 10);
        screen.MoveCursorTo(9, 0);                // bottom row (newest-first index 0)
        screen.FeedTextBatch(content);            // single line at the bottom (LFB 0)
        var (line, text, version) = screen.GetLineWithText(0);

        Assert.NotNull(line);
        Assert.Equal(content, text.TrimEnd());                       // visible text matches what was written
        Assert.Equal(ReconstructFromCells(line!).TrimEnd(), text.TrimEnd()); // cells ↔ text aligned
        Assert.Equal(version, screen.GetLineVersion(0));
    }

    [Fact]
    public void GetLineWithText_InPlaceCarriageReturnOverwrite_StaysConsistent()
    {
        var screen = new TerminalScreen(80, 10);
        screen.MoveCursorTo(9, 0);                // bottom row
        screen.FeedTextBatch("restoring packages......");
        screen.FeedTextBatch("\r");                // caret back to column 0, same physical row
        screen.FeedTextBatch("DONE              "); // in-place overwrite (no erase) keeps the tail

        var (line, text, _) = screen.GetLineWithText(0);
        Assert.Equal(ReconstructFromCells(line!).TrimEnd(), text.TrimEnd());
        Assert.StartsWith("DONE", text.TrimEnd());
    }

    [Fact]
    public void GetLineWithText_ScrollKeepsLineAndTextAligned()
    {
        var screen = new TerminalScreen(80, 10);
        screen.MoveCursorTo(9, 0);                // write from the bottom row so each newline scrolls
        for (var i = 0; i < 12; i++)              // more lines than the screen → scrollback
        {
            screen.FeedTextBatch($"line {i:D2}");
            if (i < 11) screen.FeedTextBatch("\r\n"); // newline only between lines, not after the last
        }

        // The bottom line (newest) must still have its cells and text aligned after many scrolls.
        var (line, text, version) = screen.GetLineWithText(0);
        Assert.Equal(ReconstructFromCells(line!).TrimEnd(), text.TrimEnd());
        Assert.Equal("line 11", text.TrimEnd());
        Assert.Equal(version, screen.GetLineVersion(0));
    }

    /// <summary>Reproduces the exact desync the old render path allowed, proving why the atomic
    /// accessor is required. The old code called GetLine, GetRenderedLine and GetLineVersion as three
    /// independent lock acquisitions; the ConPTY pump writes between the first two, so the cells say one
    /// thing and the text says another — and the renderer then baked that mismatch into its dirty-row
    /// cache (permanent garbling). GetLineWithText takes one lock and can never diverge.</summary>
    [Fact]
    public void OldSeparateReadsDesync_GetLineWithTextStable()
    {
        var screen = new TerminalScreen(80, 10);
        screen.MoveCursorTo(0, 0);
        screen.WriteChar('A', 0x00FF00, 0, false, false);

        // --- OLD pattern: GetLine then an intervening pump write then GetRenderedLine ---
        var oldLine = screen.GetLine(9)!;          // snapshot of cells ('A')
        screen.MoveCursorTo(0, 0);
        screen.WriteChar('B', 0x00FF00, 0, false, false); // next progress update, between the reads
        var oldText = screen.GetRenderedLine(9)!;  // newer text ('B')

        Assert.NotEqual(oldLine[0].Char, oldText.Length > 0 ? oldText[0] : '\0'); // desync reproduced

        // --- NEW pattern: one lock, line and text from the same snapshot ---
        var (newLine, newText, _) = screen.GetLineWithText(9);
        Assert.Equal(newLine![0].Char, newText.Length > 0 ? newText[0] : '\0');
        Assert.Equal('B', newText[0]);             // reflects the latest consistent state
    }

    /// <summary>Mirrors <see cref="TerminalScreen"/>'s internal LineToText: a visible cell emits its
    /// character, a NUL cell (continuation of a wide char or an unwritten gap) emits nothing.</summary>
    private static string ReconstructFromCells(TerminalCell[] line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var cell in line)
        {
            if (cell.Char != '\0')
            {
                builder.Append(cell.Char);
            }
        }

        return builder.ToString();
    }
}
