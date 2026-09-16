using Nornia.Desktop.Views.Controls;

namespace Nornia.Tests;

/// <summary>方向键输入契约:上/下键必须原样透传标准 VT 序列(CSI A/B)。shell 的历史导航
/// (PSReadLine/bash/cmd)由其原生实现——整行替换、连续翻阅都依赖序列纯净;任何前缀
/// (如先清行)都会重置 PSReadLine 的历史枚举,表现为只能召回最近一条、Down 永远无效。</summary>
public sealed class TerminalInputSequenceTests
{
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
