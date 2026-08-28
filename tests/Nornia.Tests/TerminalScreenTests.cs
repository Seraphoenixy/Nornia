using Nornia.Desktop.Terminal;

namespace Nornia.Tests;

/// <summary>Pure-model coverage for the interactive terminal: ANSI parsing, screen primitives,
/// scrollback, alternate buffer and color resolution. No WPF types involved.</summary>
public sealed class TerminalScreenTests
{
    private const int Black = unchecked((int)0xFF000000);
    private const int White = unchecked((int)0xFFFFFFFF);
    private const int Red = unchecked((int)0xFFFF0000);

    [Fact]
    public void FeedText_WritesCharactersToScreen()
    {
        var screen = new TerminalScreen(columns: 20, rows: 5);
        screen.FeedText("hello");

        Assert.Equal('h', screen.GetCell(0, 0).Char);
        Assert.Equal('o', screen.GetCell(0, 4).Char);
        Assert.Equal((0, 5), screen.Cursor);
    }

    [Fact]
    public void NewlineAtBottomScrollsIntoScrollback()
    {
        var screen = new TerminalScreen(columns: 20, rows: 10, scrollbackLimit: 4);
        screen.FeedText("a\nb\nc\nd\n");
        screen.FeedText("e");

        Assert.Equal('e', screen.GetCell(4, 0).Char); // newest line sits at the cursor row
        Assert.Equal('d', screen.GetCell(3, 0).Char);
        Assert.Equal(10, screen.TotalLines);          // no scroll yet: 0 scrollback + 10 rows
        Assert.Equal('a', screen.GetCell(0, 0).Char);
    }

    [Fact]
    public void ScrollbackIsCapped()
    {
        var screen = new TerminalScreen(columns: 20, rows: 10, scrollbackLimit: 2);
        for (var i = 0; i < 30; i++)
        {
            screen.FeedText($"L{i}\n");
        }

        Assert.Equal(2, screen.ScrollbackLimit);
        Assert.True(screen.TotalLines == 12, $"scrollback={screen.TotalLines - screen.Rows}, limit={screen.ScrollbackLimit}, rows={screen.Rows}"); // 2 scrollback + 10 rows
        Assert.Equal('L', screen.GetLine(screen.TotalLines - 1)![0].Char);
    }

    [Fact]
    public void Sgr_ColorsFollowAnsiCodes()
    {
        var screen = new TerminalScreen(columns: 20, rows: 5);
        screen.FeedText("\x1b[31mred\x1b[0mplain");

        var red = AnsiPalette.ToArgb(1);
        Assert.Equal(red, screen.GetCell(0, 0).Foreground);
        Assert.Equal(red, screen.GetCell(0, 2).Foreground);
        Assert.Equal(0, screen.GetCell(0, 3).Foreground); // reset back to default
    }

    [Fact]
    public void Sgr_TrueColorResolvesRgb()
    {
        var screen = new TerminalScreen(columns: 20, rows: 2);
        screen.FeedText("\x1b[38;2;1;2;3mx");

        Assert.Equal(unchecked((int)0xFF010203), screen.GetCell(0, 0).Foreground);
    }

    [Fact]
    public void CursorMotion_HandlesCupAndRelativeMoves()
    {
        var screen = new TerminalScreen(columns: 20, rows: 5);
        screen.FeedText("\x1b[2;3Hx");       // CUP 2,3 (1-based) then write
        Assert.Equal('x', screen.GetCell(1, 2).Char);
        Assert.Equal((1, 3), screen.Cursor);

        screen.FeedText("\x1b[A\x1b[C");     // up + right
        Assert.Equal((0, 4), screen.Cursor);
    }

    [Fact]
    public void EraseInDisplay_ClearsButKeepsCursor()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("abc");
        screen.FeedText("\x1b[2J");

        Assert.Equal(default(char), screen.GetCell(0, 0).Char);
        Assert.Equal((0, 3), screen.Cursor); // ED never moves the cursor
    }

    [Fact]
    public void AlternateBuffer_SwitchesAndReturnsWithoutLosingPrimary()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("primary");
        screen.FeedText("\x1b[?1049h");
        screen.FeedText("alt");
        Assert.True(screen.InAlternateBuffer);
        Assert.Equal('a', screen.GetCell(0, 0).Char);

        screen.FeedText("\x1b[?1049l");
        Assert.False(screen.InAlternateBuffer);
        Assert.Equal('p', screen.GetCell(0, 0).Char); // primary content restored
    }

    [Fact]
    public void CursorVisibility_PrivateMode25()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        Assert.True(screen.CursorVisible);

        screen.FeedText("\x1b[?25l");
        Assert.False(screen.CursorVisible);

        screen.FeedText("\x1b[?25h");
        Assert.True(screen.CursorVisible);
    }

    [Fact]
    public void UnsupportedAndMalformedSequencesDoNotThrow()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("\x1b[?1000h");   // mouse reporting: ignored
        screen.FeedText("\x1b[P");        // delete chars: ignored
        screen.FeedText("\x1b]50;ignored\x07"); // OSC: skipped
        screen.FeedText("ok");
        Assert.Equal('o', screen.GetCell(0, 0).Char);
        Assert.Equal('k', screen.GetCell(0, 1).Char);
    }

    [Fact]
    public void Resize_ReallocatesGridAndClampsCursor()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("abcdefghij");    // fills row 0, wraps to row 1
        screen.Resize(24, 12);

        Assert.Equal(24, screen.Columns);
        Assert.Equal(12, screen.Rows);
        Assert.True(screen.Cursor.Row < 12);
        Assert.True(screen.Cursor.Column < 24);
    }

    [Fact]
    public void ToPlainText_JoinsScreenLines()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("abc\ndef");

        Assert.Contains("abc", screen.ToPlainText());
        Assert.Contains("def", screen.ToPlainText());
    }

    [Fact]
    public void SgrState_PersistsAcrossFeedChunks()
    {
        var screen = new TerminalScreen(columns: 20, rows: 3);
        screen.FeedText("\x1b[32m");
        screen.FeedText("green");

        Assert.Equal(AnsiPalette.ToArgb(2), screen.GetCell(0, 0).Foreground);
    }

    [Fact]
    public void CsiSequence_SplitAcrossChunks_ParsesCorrectly()
    {
        // 流式读取的块边界可以切断 ANSI 序列(如 "\x1b[2" / ";3H");parser 的状态机必须跨块
        // 保持,否则游标/颜色会错位(游标位置错误的防御性回归)。
        var screen = new TerminalScreen(columns: 20, rows: 5);

        screen.FeedText("\x1b[2");
        screen.FeedText(";3Hx");   // 宏: CUP 2,3 + 写字——序列被切成两段
        Assert.Equal('x', screen.GetCell(1, 2).Char);
        Assert.Equal((1, 3), screen.Cursor);

        screen.FeedText("\x1b[3");
        screen.FeedText("1m");
        screen.FeedText("y");
        Assert.Equal(AnsiPalette.ToArgb(1), screen.GetCell(1, 3).Foreground);
    }

    [Fact]
    public void FeedTextBatch_RaisesChangedExactlyOnce()
    {
        // 批量提交(每输出块一次)必须只触发一次 Changed,避免逐字符渲染抖动导致卡顿。
        var screen = new TerminalScreen(columns: 20, rows: 5);
        var raised = 0;
        screen.Changed += () => raised++;

        screen.FeedTextBatch("line1\nline2\nline3\n");

        Assert.Equal(1, raised);
        Assert.Equal(3, screen.Cursor.Row);
    }

    [Fact]
    public void CommandOutput_CursorEndsAtLastEchoedCharacter()
    {
        // 命令回显后模型游标应停在最后一行(新提示符行)行首——表面据此绘制光标块。
        var screen = new TerminalScreen(columns: 40, rows: 10);
        screen.FeedText("PS> echo hello\r\n");   // 提示符 + 命令回显
        screen.FeedText("hello\r\n");            // 命令输出

        Assert.Contains("PS> echo hello", screen.ToPlainText());
        Assert.Contains("hello", screen.ToPlainText());
        // 输出后的新提示符行是当前行(第 3 行),光标在其行首
        Assert.Equal((2, 0), screen.Cursor);
    }

    [Fact]
    public void WritingLastColumn_DefersWrapUntilTheNextPrintableCharacter()
    {
        var screen = new TerminalScreen(columns: 20, rows: 10);
        screen.FeedText(new string('x', 20));

        Assert.Equal((0, 19), screen.Cursor);
        screen.FeedText("y");

        Assert.Equal('y', screen.GetCell(1, 0).Char);
        Assert.Equal((1, 1), screen.Cursor);
    }
}
