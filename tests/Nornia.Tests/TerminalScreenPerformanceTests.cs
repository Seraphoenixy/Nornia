using Nornia.Desktop.Terminal;

namespace Nornia.Tests;

/// <summary>性能评审 T1–T8 的增量护栏:环形滚动缓冲、批量写入路径、行文本缓存与脏行区间、
/// 宽字符、rows-only resize 快路径、复制与渲染共用文本。
/// 注意:TerminalScreen 的行数下限为 10(ctr 内 Math.Max(10, rows)),测试统一用
/// self-adaptive 行号(经 screen.Rows 换算),并符合"末尾 '\n' 触发滚屏"的语义。</summary>
public sealed class TerminalScreenPerformanceTests
{
    /// <summary>自底向上行号 → 去尾 NUL 文本。</summary>
    private static string Lfb(TerminalScreen screen, int lineFromBottom) =>
        new string(screen.GetLine(lineFromBottom)!.Select(cell => cell.Char).ToArray()).TrimEnd('\0').TrimEnd();

    [Fact]
    public void ScrollbackRing_CapsAndPreservesNewestLines()
    {
        var screen = new TerminalScreen(columns: 20, rows: 10, scrollbackLimit: 2);
        for (var i = 0; i < 29; i++)
        {
            screen.FeedText($"L{i}\n");
        }

        screen.FeedText("L29"); // 无尾随换行 → L29 停驻底行(光标行)

        // 保持旧语义:2 条滚动缓冲 + 10 行屏幕。
        Assert.Equal(12, screen.TotalLines);
        Assert.Equal("L29", Lfb(screen, 0)); // 底行 = 最新写入
        Assert.Equal("L28", Lfb(screen, 1));
        // 滚动缓冲(自底向上 10/11):最新两条 L19 / L18。
        Assert.Equal("L19", Lfb(screen, 10));
        Assert.Equal("L18", Lfb(screen, 11));
    }

    [Fact]
    public void ScrollbackRing_EvictionRecyclesOldestRowWithoutStaleContent()
    {
        var screen = new TerminalScreen(columns: 8, rows: 10, scrollbackLimit: 2);
        for (var i = 0; i < 13; i++)
        {
            screen.FeedText($"{i:0000000}\n");
        }

        // 满环多次覆盖后底行为空(被覆盖并被清空的旧行数组复用为底行)。
        Assert.Equal('\0', screen.GetCell(9, 0).Char);
        screen.FeedText("x");
        Assert.Equal('x', screen.GetCell(9, 0).Char);
        Assert.Equal('\0', screen.GetCell(9, 1).Char); // 复用行必须已清空,无旧字符残留
    }

    [Fact]
    public void WriteChars_BatchMatchesPerCharWrites()
    {
        var batched = new TerminalScreen(columns: 20, rows: 5);
        var perChar = new TerminalScreen(columns: 20, rows: 5);

        const string sample = "hello\x1b[31m red\x1b[0m end";
        batched.FeedText(sample);
        foreach (var character in sample)
        {
            perChar.FeedText(character.ToString());
        }

        Assert.Equal(perChar.ToPlainText(), batched.ToPlainText());
        Assert.Equal(perChar.Cursor, batched.Cursor);
        for (var row = 0; row < batched.Rows; row++)
        {
            Assert.Equal(perChar.GetLineVersion(row), batched.GetLineVersion(row));
        }
    }

    [Fact]
    public void CsiParameters_ParseIdenticallyForEmptyAndSkippedSegments()
    {
        var screen = new TerminalScreen(columns: 20, rows: 5);
        screen.FeedText("\x1b[2;3Hx"); // CUP 2,3(1-based)
        Assert.Equal('x', screen.GetCell(1, 2).Char);
        Assert.Equal((1, 3), screen.Cursor);

        screen.FeedText("\x1b[;J"); // 仅空段 → 参数 [] → 擦除同 0,光标不动
        Assert.Equal((1, 3), screen.Cursor);

        // 中间空段被丢弃(RemoveEmptyEntries 语义):CUP 2;;4 → 行 2 列 4。
        screen.FeedText("\x1b[2;;4H");
        Assert.Equal((1, 3), screen.Cursor);
    }

    [Fact]
    public void GetRenderedLine_ReusesCachedStringUntilContentChanges()
    {
        var screen = new TerminalScreen(columns: 20, rows: 10);
        screen.FeedText("abc"); // 第 0 行 → LFB = Rows-1

        var first = screen.GetRenderedLine(screen.Rows - 1);
        var second = screen.GetRenderedLine(screen.Rows - 1);
        Assert.Same(first, second); // 同版本复用同一实例

        screen.MoveCursorTo(0, 3);
        screen.WriteChar('d', 0, 0, false, false);
        var third = screen.GetRenderedLine(screen.Rows - 1);
        Assert.Equal("abcd", third);
        Assert.NotSame(first, third); // 内容变化 → 新实例
    }

    [Fact]
    public void RowsChanged_ReportsAffectedLineRange()
    {
        var screen = new TerminalScreen(columns: 20, rows: 10);
        var ranges = new List<(int Start, int End)>();
        screen.RowsChanged += (start, end) => ranges.Add((start, end));

        screen.FeedText("abc"); // 写入第 0 行
        Assert.Equal((0, 0), ranges[^1]);

        screen.FeedText("\x1b[2J"); // 整屏擦除
        Assert.Equal((0, screen.Rows - 1), ranges[^1]);
    }

    [Fact]
    public void WideCharacter_AdvancesTwoColumnsAndTracksWidth()
    {
        var screen = new TerminalScreen(columns: 20, rows: 10);
        screen.FeedText("中");

        Assert.Equal((0, 2), screen.Cursor); // 宽字符推进两列
        Assert.Equal('中', screen.GetCell(0, 0).Char);
        Assert.Equal(2, screen.GetCell(0, 0).Width);
        Assert.Equal("中", screen.GetRenderedLine(screen.Rows - 1));
    }

    [Fact]
    public void CombiningMark_DoesNotAdvanceColumn()
    {
        var screen = new TerminalScreen(columns: 20, rows: 10);
        screen.FeedText("a\u0301"); // 'a' + combining acute

        // 'a' 落在 (0,0);组合符零宽,写入当前格(0,1)但不推进列。
        Assert.Equal((0, 1), screen.Cursor);
        Assert.Equal('a', screen.GetCell(0, 0).Char);
        Assert.Equal('\u0301', screen.GetCell(0, 1).Char);
    }

    [Fact]
    public void ResizeRowsOnly_KeepsRetainedContentAndVersions()
    {
        var screen = new TerminalScreen(columns: 20, rows: 12);
        screen.MoveCursorTo(11, 0);
        screen.FeedText("last"); // 底行(行 11)内容
        var versionBefore = screen.GetLineVersion(0);

        screen.Resize(20, 14); // 列不变,行数增加 → 快路径

        Assert.Equal(14, screen.Rows);
        Assert.Equal(20, screen.Columns);
        Assert.Equal('l', screen.GetCell(11, 0).Char); // 原行内容保留在原行号
        // 行 11 在增长后的 LFB = Rows-1-11 = 2;内容未变 → 版本不变。
        Assert.Equal(versionBefore, screen.GetLineVersion(2));

        screen.Resize(20, 10); // 行数收缩(保留底部 10 行:原 14 行保留 4..13)

        Assert.Equal(10, screen.Rows);
        // 原行 11 在收缩后的行位置 = 11 - 4 = 7;LFB = 10-1-7 = 2。
        Assert.Equal("last", Lfb(screen, 2)); // 底对齐内容随收缩保留
    }

    [Fact]
    public void AlternateBuffer_LazyAllocation_SurvivesRowsOnlyResize()
    {
        var screen = new TerminalScreen(columns: 20, rows: 10);
        screen.FeedText("\x1b[?1049h"); // 进入备用屏(惰性分配)
        screen.FeedText("alt"); // 光标 (0,3)
        Assert.True(screen.InAlternateBuffer);
        Assert.Equal('a', screen.GetCell(0, 0).Char);

        screen.Resize(20, 12); // 列不变的行数变化
        Assert.Equal(12, screen.Rows);
        screen.FeedText("x"); // 行数同步后的备用屏仍可写(光标停于原行)
        Assert.Equal('x', screen.GetCell(0, 3).Char);

        screen.FeedText("\x1b[?1049l"); // 退出
        Assert.False(screen.InAlternateBuffer);
        Assert.Equal(12, screen.Rows);
    }

    [Fact]
    public void CopyText_MatchesRenderedLineContent()
    {
        var screen = new TerminalScreen(columns: 12, rows: 10);
        screen.FeedText("alpha\nbeta");

        Assert.Equal("beta", screen.GetRenderedLine(screen.Rows - 2));
        var expected = screen.ToPlainText();
        Assert.Contains("alpha", expected);
        Assert.Contains("beta", expected);
    }
}