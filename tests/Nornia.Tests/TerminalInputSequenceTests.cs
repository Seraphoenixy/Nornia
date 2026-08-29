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
}
