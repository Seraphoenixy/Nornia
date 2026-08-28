using Nornia.Desktop.Terminal;

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
}
