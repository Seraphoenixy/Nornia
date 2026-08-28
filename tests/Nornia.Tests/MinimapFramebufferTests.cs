using Nornia.Desktop.Code;

namespace Nornia.Tests;

/// <summary>E1 残余:MinimapFramebuffer(WriteableBitmap 双缓冲的 CPU 侧)脏行语义——
/// 内容相同行零脏、变化行只标脏一次、连续脏行合并为区间、尺寸边界行为。</summary>
public sealed class MinimapFramebufferTests
{
    private static int Bgra(byte b, byte g, byte r, byte a = 255) => b | (g << 8) | (r << 16) | (a << 24);

    [Fact]
    public void NewFramebuffer_IsFullyDirty_FirstCommitPaintsEverything()
    {
        var fb = new MinimapFramebuffer(8, 4);
        Assert.True(fb.HasDirtyRows);
        var runs = fb.TakeDirtyRowRuns();
        Assert.Single(runs);
        Assert.Equal((0, 4), runs[0]);
        Assert.False(fb.HasDirtyRows);
    }

    [Fact]
    public void IdenticalRowRewrite_DirtiesNothing_ScrollRedrawIsZeroCost()
    {
        var fb = new MinimapFramebuffer(8, 4);
        fb.TakeDirtyRowRuns(); // 初始全脏已消费

        var row = new int[8];
        Array.Fill(row, Bgra(0, 0, 255));
        fb.SetRow(0, row);
        fb.SetRow(3, row);
        Assert.True(fb.HasDirtyRows);
        var runs = fb.TakeDirtyRowRuns();
        Assert.Equal(2, runs.Count); // [0,1) 与 [3,1)
        Assert.Equal(new[] { (0, 1), (3, 1) }, runs);

        // 重写相同内容:零脏行(滚动/重复重绘不触发任何 WritePixels)。
        fb.SetRow(0, row);
        fb.SetRow(3, row);
        Assert.False(fb.HasDirtyRows);
        Assert.Empty(fb.TakeDirtyRowRuns());
    }

    [Fact]
    public void ChangedRow_MarksOnlyThatRow_AndCoalescesContiguousRuns()
    {
        var fb = new MinimapFramebuffer(6, 6);
        fb.TakeDirtyRowRuns();

        // 行 1 写入全零:与帧缓冲初始内容一致 → 不算脏;第 2、3 行同色连续 → 合并。
        var rowA = new int[6];
        var rowB = new int[6];
        Array.Fill(rowB, Bgra(255, 0, 0));
        fb.SetRow(1, rowA);
        fb.SetRow(2, rowB);
        fb.SetRow(3, rowB);
        fb.SetRow(5, rowB);

        var runs = fb.TakeDirtyRowRuns();
        Assert.Equal(new[] { (2, 2), (5, 1) }, runs);
    }

    [Fact]
    public void PixelEncoding_IsBgra_AndPartialRowsClearTheTail()
    {
        var fb = new MinimapFramebuffer(4, 1);
        fb.TakeDirtyRowRuns();

        // BGRA 直通编码:蓝=0xFF0000FF → 低字节序 B,G,R,A。
        fb.SetRow(0, new[] { Bgra(0xFF, 0x00, 0x00), 0 });
        Assert.True(fb.HasDirtyRows);
        var run = Assert.Single(fb.TakeDirtyRowRuns());
        Assert.Equal((0, 1), run);
        Assert.Equal(Bgra(0xFF, 0x00, 0x00), fb.Pixels[0]);
        Assert.Equal(0, fb.Pixels[1]); // 第二像素透明
    }

    [Fact]
    public void MarkAllDirty_ForcesFullRepaint_OnThemeOrSizeChange()
    {
        var fb = new MinimapFramebuffer(4, 3);
        fb.TakeDirtyRowRuns();
        fb.MarkAllDirty();
        var runs = fb.TakeDirtyRowRuns();
        Assert.Equal(new[] { (0, 3) }, runs);
    }

    [Fact]
    public void OutOfRangeRow_IsIgnored()
    {
        var fb = new MinimapFramebuffer(4, 2);
        fb.TakeDirtyRowRuns();
        fb.SetRow(5, new int[4]);   // 行号 ≥ 高度被忽略(空操作)
        Assert.False(fb.HasDirtyRows);
        Assert.Throws<ArgumentOutOfRangeException>(() => fb.SetRow(-1, new int[4])); // 负行号防御性抛错

        Assert.Throws<ArgumentOutOfRangeException>(() => new MinimapFramebuffer(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinimapFramebuffer(4, 0));
    }
}