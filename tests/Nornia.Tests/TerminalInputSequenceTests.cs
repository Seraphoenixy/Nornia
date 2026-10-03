using Nornia.Desktop.Views.Controls;

namespace Nornia.Tests;

/// <summary>方向键输入契约:上/下键必须原样透传标准 VT 序列(CSI A/B)。shell 的历史导航
/// (PSReadLine/bash/cmd)由其原生实现——整行替换、连续翻阅都依赖序列纯净;任何前缀
/// (如先清行)都会重置 PSReadLine 的历史枚举,表现为只能召回最近一条、Down 永远无效。</summary>
public sealed class TerminalInputSequenceTests
{
    [Fact]
    public void CopySelection_PreservesBlankLinesAndDragDirection()
    {
        var screen = new Nornia.Desktop.Terminal.TerminalScreen(columns: 20, rows: 10);
        screen.FeedText("first\r\n\r\nlast");
        var top = screen.Rows - 1;
        var expected = "first" + Environment.NewLine + Environment.NewLine + "last";
        Assert.Equal(expected, TerminalSurfaceControl.ExtractSelectionText(screen, (top, 0), (top - 2, 3)));
        Assert.Equal(expected, TerminalSurfaceControl.ExtractSelectionText(screen, (top - 2, 3), (top, 0)));
    }

    [Fact]
    public void CopySelection_OmitsWideCharacterContinuationCells()
    {
        var screen = new Nornia.Desktop.Terminal.TerminalScreen(columns: 20, rows: 10);
        screen.FeedText("中文 ABC");
        Assert.Equal("中文 ABC", TerminalSurfaceControl.ExtractSelectionText(screen,
            (screen.Rows - 1, 0), (screen.Rows - 1, 7)));
    }

    [Fact]
    public void ArrowKeySequences_ArePlainVtPassThrough()
    {
        Assert.Equal("\x1b[A", TerminalSurfaceControl.ArrowUpSequence);
        Assert.Equal("\x1b[B", TerminalSurfaceControl.ArrowDownSequence);
    }

    [Theory]
    [InlineData(9, 10, 10, 10, 0)]
    [InlineData(0, 30, 30, 10, 20)]
    [InlineData(15, 30, 30, 10, 14)]
    [InlineData(0, 30, 40, 10, 29)]
    public void ScrollOffsetForCursor_PlacesCursorInTheViewport(
        int cursorRow, int screenRows, int totalLines, int visibleRows, int expected)
    {
        Assert.Equal(expected, TerminalSurfaceControl.ScrollOffsetForCursor(
            cursorRow, screenRows, totalLines, visibleRows));
    }

    [Fact]
    public void TerminalScreen_ClampsDisplayRowsToConPtyLimit()
    {
        var screen = new Nornia.Desktop.Terminal.TerminalScreen(columns: 40, rows: 999);
        screen.Resize(40, 999);

        Assert.Equal(Nornia.Desktop.Terminal.TerminalScreen.MaximumRows, screen.Rows);
    }
}
