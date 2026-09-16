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
        screen.FeedText("\x1b[99;99Z");   // CBT back-tab: ignored
        screen.FeedText("\x1b]50;ignored\x07"); // OSC: skipped
        screen.FeedText("ok");
        Assert.Equal('o', screen.GetCell(0, 0).Char);
        Assert.Equal('k', screen.GetCell(0, 1).Char);
    }

    [Fact]
    public void EraseCharacters_BlanksMidLineWithoutTouchingRest()
    {
        // 回归:ConPTY 差分渲染在"短历史命令替换长内容"时用 ECH(CSI X)擦行中段,
        // 只有空白延伸到行尾才用 EL。不支持 ECH 时召回后旧文字残留。
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("ABCDE");
        screen.FeedText("\x1b[1;3H");     // CUP row1 col3 → index (0,2)
        screen.FeedText("\x1b[2X");       // ECH: 擦 2 格

        Assert.Equal('A', screen.GetCell(0, 0).Char);
        Assert.Equal('B', screen.GetCell(0, 1).Char);
        Assert.Equal(default(char), screen.GetCell(0, 2).Char);
        Assert.Equal(default(char), screen.GetCell(0, 3).Char);
        Assert.Equal('E', screen.GetCell(0, 4).Char); // 右侧保留
        Assert.Equal((0, 2), screen.Cursor);          // 光标不动
    }

    [Fact]
    public void EraseCharacters_ClampsAtLineEnd()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("ABCDE");
        screen.FeedText("\x1b[1;2H");     // (0,1)
        screen.FeedText("\x1b[9X");       // 请求擦 9 格,越过行尾须钳制

        Assert.Equal('A', screen.GetCell(0, 0).Char);
        Assert.Equal(default(char), screen.GetCell(0, 1).Char);
        Assert.Equal(default(char), screen.GetCell(0, 4).Char);
        Assert.Equal(default(char), screen.GetCell(0, 9).Char);
    }

    [Fact]
    public void DeleteCharacters_ShiftsRemainderLeft()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("ABCDE");
        screen.FeedText("\x1b[1;2H");     // (0,1)
        screen.FeedText("\x1b[2P");       // DCH 2

        Assert.Equal('A', screen.GetCell(0, 0).Char);
        Assert.Equal('D', screen.GetCell(0, 1).Char);
        Assert.Equal('E', screen.GetCell(0, 2).Char);
        Assert.Equal(default(char), screen.GetCell(0, 3).Char);
        Assert.Equal((0, 1), screen.Cursor);
    }

    [Fact]
    public void InsertCharacters_ShiftsTailRight()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("ABCDE");
        screen.FeedText("\x1b[1;2H");     // (0,1)
        screen.FeedText("\x1b[2@");       // ICH 2

        Assert.Equal('A', screen.GetCell(0, 0).Char);
        Assert.Equal(default(char), screen.GetCell(0, 1).Char);
        Assert.Equal(default(char), screen.GetCell(0, 2).Char);
        Assert.Equal('B', screen.GetCell(0, 3).Char);
        Assert.Equal('D', screen.GetCell(0, 5).Char); // E 右移至 col6
    }

    [Fact]
    public void RepeatLastCharacter_WritesWithOriginalAttributes()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("\x1b[31mab\x1b[0m"); // 红色 ab
        screen.FeedText("\x1b[3b");           // REP 3

        Assert.Equal('b', screen.GetCell(0, 2).Char);
        Assert.Equal('b', screen.GetCell(0, 4).Char);
        Assert.Equal((0, 5), screen.Cursor);
        // 重复字符沿用原属性(红色前景):CellWidth 的 Foreground 为解析器调色板解析值,只验证非默认
        Assert.NotEqual(default, screen.GetCell(0, 2).Foreground);
    }

    [Fact]
    public void RepeatLastCharacter_WideCharacterOccupiesTwoCells()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        screen.FeedText("中");
        screen.FeedText("\x1b[1b");       // REP 宽字符

        Assert.Equal('中', screen.GetCell(0, 0).Char);
        Assert.Equal('中', screen.GetCell(0, 2).Char); // 第二个"中"占 col2-3
        Assert.Equal((0, 4), screen.Cursor);
    }

    [Fact]
    public void InsertLines_BlanksCursorRowAndPushesBelow()
    {
        var screen = new TerminalScreen(columns: 10, rows: 4);
        screen.FeedText("a\nb\nc");
        screen.FeedText("\x1b[2;1H");     // 光标到第 2 行
        screen.FeedText("\x1b[1L");       // IL 1

        Assert.Equal('a', screen.GetCell(0, 0).Char);
        Assert.Equal(default(char), screen.GetCell(1, 0).Char); // 光标行变空
        Assert.Equal('b', screen.GetCell(2, 0).Char);
        Assert.Equal('c', screen.GetCell(3, 0).Char);
        Assert.Equal((1, 0), screen.Cursor);
    }

    [Fact]
    public void DeleteLines_RemovesCursorRowAndPullsBelow()
    {
        var screen = new TerminalScreen(columns: 10, rows: 4);
        screen.FeedText("a\nb\nc\nd");
        screen.FeedText("\x1b[2;1H");     // 光标到第 2 行
        screen.FeedText("\x1b[1M");       // DL 1

        Assert.Equal('a', screen.GetCell(0, 0).Char);
        Assert.Equal('c', screen.GetCell(1, 0).Char);
        Assert.Equal('d', screen.GetCell(2, 0).Char);
        Assert.Equal(default(char), screen.GetCell(3, 0).Char); // 底行补空
    }

    [Fact]
    public void HistoryRecall_ResidueScenario_ShortOverLongIsFullyErased()
    {
        // 用户场景的模型级复现:长命令(含中文宽字符)已在提示符行,召回更短的命令,
        // ConPTY 差分发出 ECH/覆盖写,行尾必须干净——不得残留长命令尾部。
        var screen = new TerminalScreen(columns: 20, rows: 3);
        screen.FeedText("PS> 恶臭的份67890");   // 长内容(宽字符占双格)
        screen.FeedText("\r");                  // 回行首,模拟历史召回重绘
        screen.FeedText("PS> echo hi");         // 短内容覆盖前缀
        screen.FeedText("\x1b[10X");            // 差分:擦除中段残留(ConPTY ECH)

        var line = screen.GetRenderedLine(screen.Rows - 1) ?? string.Empty; // 屏幕第 0 行
        Assert.StartsWith("PS> echo hi", line);
        Assert.Equal("PS> echo hi", line.TrimEnd());
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

    [Fact]
    public void ResizeColumns_ThenScroll_RecycledScrollbackRowMatchesNewWidth()
    {
        // 回归:列宽变化后主屏行重建,但滚动环形缓冲里的行数组仍是旧宽度;下一次滚动会把
        // 环形槽里的旧宽度数组"就地复用"为新底行——行比 _columns 短,随后写入右侧列即
        // IndexOutOfRangeException(用户执行 dotnet build 长输出滚动时触发)。
        var screen = new TerminalScreen(columns: 20, rows: 10, scrollbackLimit: 4);
        screen.FeedText("a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\n"); // 旧宽度下滚动,环形槽填入 20 宽数组

        screen.Resize(40, 10); // 终端面板变宽(侧栏开合/窗口布局变化都会触发)

        screen.FeedText("m\nn\n");            // resize 后再次滚动
        screen.FeedText(new string('x', 90)); // 跨两行折行:第二行落在回收行上,写到第 20-39 列

        // 90 个 x = 三个 40 列整行 + 尾行 10 列:第二整行正是回收行(修复前写入第 20 列即越界)。
        Assert.Equal((9, 10), screen.Cursor);
        Assert.Equal('x', screen.GetCell(7, 39).Char);
        Assert.Equal('x', screen.GetCell(8, 39).Char); // 回收行的右端列
        Assert.Equal('x', screen.GetCell(9, 9).Char);
        Assert.All(Enumerable.Range(0, screen.Rows), row =>
            Assert.Equal(40, screen.GetLine(screen.Rows - 1 - row)!.Length)); // 屏幕行宽 == 列数
        Assert.Equal(40, screen.GetLine(screen.TotalLines - 1)!.Length); // 滚动行也已同步到新宽度
    }

    [Fact]
    public void ResizeColumns_InvalidateScrollbackRenderCache()
    {
        // 列宽变化会截断/补齐滚动行内容,渲染端按 (行号 → 版本) 缓存文本——版本必须失效,
        // 否则复制/渲染拿到旧宽度的陈旧行文本。
        var screen = new TerminalScreen(columns: 20, rows: 5, scrollbackLimit: 8);
        screen.FeedText(new string('a', 18) + '\n');
        screen.FeedText("x\n");
        var before = screen.GetRenderedLine(screen.TotalLines - 1);

        screen.Resize(40, 5);

        var after = screen.GetRenderedLine(screen.TotalLines - 1);
        Assert.NotNull(before);
        Assert.Equal(before!.TrimEnd(), after!.TrimEnd()); // 内容语义不变(仅宽度截断/补齐)
        Assert.Equal(40, screen.GetLine(screen.TotalLines - 1)!.Length); // 但行宽已同步
    }
}
