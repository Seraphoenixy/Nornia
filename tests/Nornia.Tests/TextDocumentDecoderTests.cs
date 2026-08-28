using Nornia.Desktop.Code;

namespace Nornia.Tests;

/// <summary>Preview loading contract: BOM detection, strict UTF-8 validation with GB18030 fallback,
/// binary / oversize / missing-file states and newline / line-count statistics.</summary>
public sealed class TextDocumentDecoderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-decode-{Guid.NewGuid():N}");

    public TextDocumentDecoderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    private async Task<TextDecodeResult> DecodeAsync(string name, byte[] bytes, long limit = 1_000_000)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllBytesAsync(path, bytes);
        return await TextDocumentDecoder.Instance.DecodeAsync(path, limit);
    }

    [Fact]
    public async Task Utf8_PlainText_DecodesAndCountsLines()
    {
        var result = await DecodeAsync("a.txt", "hello\nworld"u8.ToArray());

        Assert.True(result.Success);
        Assert.Equal("hello\nworld", result.Text);
        Assert.Equal("UTF-8", result.EncodingName);
        Assert.Equal(NewlineType.Lf, result.NewlineType);
        Assert.Equal(2, result.LineCount);
    }

    [Fact]
    public async Task Utf8_WithBom_StripsPreambleAndReportsBom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("hi\r\nok"u8.ToArray()).ToArray();
        var result = await DecodeAsync("bom.txt", bytes);

        Assert.True(result.Success);
        Assert.Equal("hi\r\nok", result.Text);
        Assert.Equal("UTF-8 BOM", result.EncodingName);
        Assert.Equal(NewlineType.CrLf, result.NewlineType);
        Assert.Equal(2, result.LineCount);
    }

    [Fact]
    public async Task Utf16Le_WithBom_Decodes()
    {
        var text = "测试\r\n第二行";
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes(text)).ToArray();
        var result = await DecodeAsync("c.txt", bytes);

        Assert.True(result.Success);
        Assert.Equal(text, result.Text);
        Assert.Equal("UTF-16 LE BOM", result.EncodingName);
        Assert.Equal(NewlineType.CrLf, result.NewlineType);
        Assert.Equal(2, result.LineCount);
    }

    [Fact]
    public async Task Gb18030_Fallback_DecodesLegacyChineseWithoutGarbling()
    {
        var text = "中文编码测试，防止乱码";
        var bytes = System.Text.Encoding.GetEncoding(54936).GetBytes(text);
        var result = await DecodeAsync("gb.txt", bytes);

        Assert.True(result.Success);
        Assert.Equal(text, result.Text);
        Assert.Equal("GB18030", result.EncodingName);
    }

    [Fact]
    public async Task Binary_WithNulByte_IsReported()
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x00, 0x03, 0x04 };
        var result = await DecodeAsync("bin.dat", bytes);

        Assert.True(result.Success);
        Assert.True(result.IsBinary);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task Oversize_ReportsLimitWithoutLoading()
    {
        var result = await DecodeAsync("big.txt", new byte[1_000_001]);

        Assert.False(result.Success);
        Assert.True(result.IsTruncated);
        Assert.Null(result.Text);
        Assert.Equal(1_000_001, result.FileSize);
        Assert.Contains("超过 1 MB", result.Error);
    }

    [Fact]
    public async Task EmptyFile_IsEmptyUtf8()
    {
        var result = await DecodeAsync("empty.txt", []);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Text);
        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.LineCount);
        Assert.Equal("UTF-8", result.EncodingName);
    }

    [Fact]
    public async Task MissingFile_ReportsError()
    {
        var missing = Path.Combine(_tempDir, "missing.txt");

        var result = await TextDocumentDecoder.Instance.DecodeAsync(missing, 1_000_000);

        Assert.False(result.Success);
        Assert.Contains("文件不存在", result.Error ?? string.Empty);
    }

    [Fact]
    public void NewlineDetection_LfCrlfCrAndMixed()
    {
        Assert.Equal(NewlineType.Lf, TextDocumentDecoder.DetectNewlineType("a\nb\nc"));
        Assert.Equal(NewlineType.CrLf, TextDocumentDecoder.DetectNewlineType("a\r\nb\r\n"));
        Assert.Equal(NewlineType.Cr, TextDocumentDecoder.DetectNewlineType("a\rb\rc"));
        Assert.Equal(NewlineType.Mixed, TextDocumentDecoder.DetectNewlineType("a\r\nb\nc\r"));
        Assert.Equal(NewlineType.Unknown, TextDocumentDecoder.DetectNewlineType("no breaks"));
    }

    [Fact]
    public void LineCounting_HandlesTrailingBreaks()
    {
        Assert.Equal(1, TextDocumentDecoder.CountLines("single"));
        Assert.Equal(2, TextDocumentDecoder.CountLines("a\nb"));
        Assert.Equal(1, TextDocumentDecoder.CountLines("a\n"));
        Assert.Equal(3, TextDocumentDecoder.CountLines("a\r\nb\r\nc"));
        Assert.Equal(2, TextDocumentDecoder.CountLines("a\r\nb\r\n"));
        Assert.Equal(0, TextDocumentDecoder.CountLines(string.Empty));
    }

    [Fact]
    public void FormatSize_ProducesReadableUnits()
    {
        Assert.Equal("500 B", TextDocumentDecoder.FormatSize(500));
        Assert.Equal("1.5 KB", TextDocumentDecoder.FormatSize(1536));
        Assert.Equal("1 MB", TextDocumentDecoder.FormatSize(1024 * 1024));
    }
}