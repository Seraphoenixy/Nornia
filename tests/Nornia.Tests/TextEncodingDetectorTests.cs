using System.Text;
using Nornia.Core.Services;

namespace Nornia.Tests;

public sealed class TextEncodingDetectorTests
{
    // 用生产字段(内部注册 code-pages provider)而不是测试类静态字段:后者在 provider 注册前
    // 初始化会因类初始化时序竞态抛 NotSupportedException。
    private static Encoding Gbk => TextEncodingDetector.Gb18030;

    [Fact]
    public void PureAscii_IsStrictUtf8()
    {
        var bytes = Gbk.GetBytes("# Demo\nplain text 123\n");
        Assert.True(TextEncodingDetector.IsStrictUtf8(bytes));
    }

    [Fact]
    public void Utf8Chinese_IsStrictAndRoundTrips()
    {
        const string text = "# 中文标题\n这是 UTF-8 内容。\n";
        var bytes = new UTF8Encoding(false).GetBytes(text);
        Assert.True(TextEncodingDetector.IsStrictUtf8(bytes));
        Assert.Equal(text, TextEncodingDetector.Utf8Strict.GetString(bytes));
    }

    [Fact]
    public void GbkChinese_IsNotStrictUtf8_AndRoundTripsViaGb18030()
    {
        const string text = "# 中文标题\n这是遗留 GBK 内容。\n";
        var bytes = Gbk.GetBytes(text);
        Assert.False(TextEncodingDetector.IsStrictUtf8(bytes));
        Assert.Equal(text, TextEncodingDetector.Gb18030.GetString(bytes));
    }

    [Fact]
    public void GbkFileWithLongAsciiHead_IsNotStrictUtf8()
    {
        // 回归:旧实现只看前 4096 字节——ASCII 头超过 4KB 的 GBK 文件被误判为 UTF-8,
        // 后文中的中文全部乱码。全量校验必须在任何位置抓住非法序列。
        var asciiHead = string.Concat(Enumerable.Range(0, 300).Select(i => $"// English line {i}\n"));
        var text = "# Demo\n" + asciiHead + "\n后文才是中文内容。\n";
        var bytes = Gbk.GetBytes(text);
        var firstNonAscii = Array.FindIndex(bytes, b => b > 127);
        Assert.True(firstNonAscii > 4096, $"test premise: first non-ASCII byte must sit past the old probe head, got {firstNonAscii}");
        Assert.False(TextEncodingDetector.IsStrictUtf8(bytes));
        Assert.Equal(text, TextEncodingDetector.Gb18030.GetString(bytes));
    }

    [Fact]
    public void ChooseForContent_FollowsBomStrictThenFallback()
    {
        Assert.Same(TextEncodingDetector.Utf8Replacement, TextEncodingDetector.ChooseForContent(hasUtf8Bom: true, isStrictUtf8: true));
        Assert.Same(TextEncodingDetector.Utf8Replacement, TextEncodingDetector.ChooseForContent(hasUtf8Bom: true, isStrictUtf8: false));
        Assert.Same(TextEncodingDetector.Utf8Strict, TextEncodingDetector.ChooseForContent(hasUtf8Bom: false, isStrictUtf8: true));
        Assert.Same(TextEncodingDetector.Gb18030, TextEncodingDetector.ChooseForContent(hasUtf8Bom: false, isStrictUtf8: false));
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 })]
    [InlineData(new byte[] { 0xEF, 0xBB })]
    public void Utf8BomLength_DetectsOnlyTheFullBom(byte[] head)
    {
        Assert.Equal(head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0,
            TextEncodingDetector.Utf8BomLength(head));
    }

    [Fact]
    public void RejectsStrayContinuationByte()
    {
        Assert.False(TextEncodingDetector.IsStrictUtf8(new byte[] { 0x61, 0x80, 0x62 }));
    }

    [Theory]
    [InlineData(new byte[] { 0xC1, 0x85 })]          // overlong 2-byte encoding of 'E'
    [InlineData(new byte[] { 0xE0, 0x80, 0xA0 })]     // overlong 3-byte encoding of U+0020
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]     // UTF-16 surrogate U+D800
    [InlineData(new byte[] { 0xF0, 0x80, 0x80, 0x80 })] // overlong 4-byte
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })] // above U+10FFFF
    [InlineData(new byte[] { 0xF5, 0x80, 0x80, 0x80 })] // invalid lead
    public void RejectsOverlongSurrogatesAndAbovePlane16(byte[] bytes)
    {
        Assert.False(TextEncodingDetector.IsStrictUtf8(bytes));
    }

    [Fact]
    public void IncompleteSequenceAtEndOfWholePayload_IsInvalid()
    {
        Assert.False(TextEncodingDetector.IsStrictUtf8(new byte[] { 0xE4, 0xBD }));
    }

    [Fact]
    public void ChunkValidation_CarriesSplitSequenceAcrossBoundary()
    {
        // 中 = E4 B8 AD: split 1+2 and 2+1 across the chunk boundary.
        var bytes = new byte[] { 0xE4, 0xBD, 0xAD, 0x61 };
        byte[] carry = [];

        Assert.True(TextEncodingDetector.IsStrictUtf8Chunk(bytes.AsSpan(0, 1), carry, out carry));
        Assert.Single(carry);
        Assert.True(TextEncodingDetector.IsStrictUtf8Chunk(bytes.AsSpan(1), carry, out carry));
        Assert.Empty(carry);

        carry = [];
        Assert.True(TextEncodingDetector.IsStrictUtf8Chunk(bytes.AsSpan(0, 2), carry, out carry));
        Assert.Equal(2, carry.Length);
        Assert.True(TextEncodingDetector.IsStrictUtf8Chunk(bytes.AsSpan(2), carry, out carry));
        Assert.Empty(carry);
    }

    [Fact]
    public void ChunkValidation_GbkPairSplitAcrossBoundary_FailsWhenTrailArrives()
    {
        // 中 in GBK = D6 D0: the lead alone at a chunk end is an incomplete UTF-8 sequence
        // (carried, not yet a verdict); the trail 0xD0 is not a continuation byte → invalid.
        var bytes = new byte[] { 0xD6, (byte)'a', 0xD0, 0x61 };
        byte[] carry = [];

        Assert.True(TextEncodingDetector.IsStrictUtf8Chunk(bytes.AsSpan(0, 1), carry, out carry));
        Assert.False(TextEncodingDetector.IsStrictUtf8Chunk(bytes.AsSpan(1, 2), carry, out carry));
    }

    [Fact]
    public void ChunkValidation_IncompleteAtEndIsCarriedNotInvalid()
    {
        byte[] carry = [];
        Assert.True(TextEncodingDetector.IsStrictUtf8Chunk(new byte[] { 0x61, 0xE4, 0xBD }, carry, out carry));
        Assert.Equal(2, carry.Length);
    }

    [Fact]
    public void GbkChineseDecodedAsUtf8_GarblesWhileGb18030RoundTrips()
    {
        // 乱码判定基准:GBK 字节按 UTF-8 解码产生 U+FFFD(或无意义字符),按 GB18030 解码还原原文。
        const string text = "遗留中文内容测试。";
        var bytes = Gbk.GetBytes(text);
        Assert.DoesNotContain("中文", TextEncodingDetector.Utf8Replacement.GetString(bytes));
        Assert.Equal(text, TextEncodingDetector.Gb18030.GetString(bytes));
    }
}
