using System.IO;
using System.Text;

namespace Nornia.Desktop.Code;

/// <summary>Dominant line-ending style of a decoded file.</summary>
public enum NewlineType
{
    Unknown,
    Lf,
    CrLf,
    Cr,
    Mixed,
}

/// <summary>Result of decoding one file for the read-only preview.</summary>
public sealed record TextDecodeResult(
    bool Success,
    string? Text,
    string? EncodingName,
    NewlineType NewlineType,
    int LineCount,
    long FileSize,
    bool IsBinary,
    bool IsTruncated,
    string? Error)
{
    /// <summary>True when the file decoded to an empty string (0 bytes of text).</summary>
    public bool IsEmpty => Text?.Length == 0;
}

/// <summary>Reads and decodes a text file for preview. BOM first (UTF-8 / UTF-16 / UTF-32), then
/// strict UTF-8 validation, then GB18030 so legacy Chinese sources do not garble. Binary data, files
/// over the preview limit and unreadable files produce explicit results (never a partial parse).</summary>
public interface ITextDocumentDecoder
{
    Task<TextDecodeResult> DecodeAsync(string path, long previewLimit, CancellationToken cancellationToken = default);
}

public sealed class TextDocumentDecoder : ITextDocumentDecoder
{
    public static readonly TextDocumentDecoder Instance = new();

    /// <summary>NUL-byte probe window: BOM-less files with a NUL in the head are treated as binary.
    /// BOM-carrying text (UTF-16/32) legitimately contains NUL bytes, so they are exempt.</summary>
    private const int BinaryProbeLength = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly Encoding Gb18030 = LoadGb18030();

    public async Task<TextDecodeResult> DecodeAsync(string path, long previewLimit, CancellationToken cancellationToken = default)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return Failed(0, $"无法读取文件：{ex.Message}");
        }

        if (!info.Exists)
        {
            return Failed(0, "文件不存在。");
        }

        if (info.Length > previewLimit)
        {
            return new TextDecodeResult(
                Success: false,
                Text: null,
                EncodingName: null,
                NewlineType: NewlineType.Unknown,
                LineCount: 0,
                FileSize: info.Length,
                IsBinary: false,
                IsTruncated: true,
                Error: $"文件超过 {FormatLimitLabel(previewLimit)}，仅支持只读预览较小的文本文件。");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return Failed(info.Length, $"无法读取文件：{ex.Message}");
        }

        var (encoding, bomLength) = DetectEncoding(bytes);

        // BOM-less files get the NUL probe; recognized text BOMs (UTF-16/32) legitimately contain
        // NUL bytes and must not be treated as binary.
        if (bomLength == 0 && bytes.Take(BinaryProbeLength).Any(value => value == 0))
        {
            return new TextDecodeResult(true, null, null, NewlineType.Unknown, 0, bytes.LongLength, true, false, null);
        }

        string text;
        try
        {
            text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        }
        catch (DecoderFallbackException)
        {
            // A fallback failure means the byte sequence is not well-formed for the detected
            // encoding; GB18030 is the last resort for legacy CJK text.
            text = Gb18030.GetString(bytes);
            encoding = Gb18030;
        }

        var newlineType = DetectNewlineType(text);
        return new TextDecodeResult(
            Success: true,
            Text: text,
            EncodingName: DescribeEncoding(encoding, bomLength),
            NewlineType: newlineType,
            LineCount: CountLines(text),
            FileSize: bytes.LongLength,
            IsBinary: false,
            IsTruncated: false,
            Error: null);
    }

    private static TextDecodeResult Failed(long size, string error) =>
        new(false, null, null, NewlineType.Unknown, 0, size, false, false, error);

    /// <summary>BOM-first detection; falls back to strict UTF-8 (whose failure is handled by the
    /// caller's GB18030 fallback). Returns the encoding and how many lead bytes to skip.</summary>
    private static (Encoding Encoding, int BomLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return (Encoding.UTF32, 4);
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8, 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);
        return (StrictUtf8, 0);
    }

    private static string DescribeEncoding(Encoding encoding, int bomLength)
    {
        if (bomLength == 0)
        {
            return encoding.CodePage switch
            {
                65001 => "UTF-8",
                54936 => "GB18030",
                1200 => "UTF-16 LE",
                1201 => "UTF-16 BE",
                _ => encoding.WebName,
            };
        }

        return encoding.CodePage switch
        {
            65001 => "UTF-8 BOM",
            1200 => "UTF-16 LE BOM",
            1201 => "UTF-16 BE BOM",
            12000 => "UTF-32 LE BOM",
            12001 => "UTF-32 BE BOM",
            _ => encoding.WebName,
        };
    }

    /// <summary>Classifies the dominant line ending (CRLF, LF, CR or mixed).</summary>
    internal static NewlineType DetectNewlineType(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    crlf++;
                    i++;
                    break;
                case '\r':
                    cr++;
                    break;
                case '\n':
                    lf++;
                    break;
            }
        }

        var total = crlf + lf + cr;
        if (total == 0)
        {
            return NewlineType.Unknown;
        }

        var dominantMax = Math.Max(crlf, Math.Max(lf, cr));
        if (dominantMax == total)
        {
            return crlf > 0 ? NewlineType.CrLf : lf > 0 ? NewlineType.Lf : NewlineType.Cr;
        }

        return NewlineType.Mixed;
    }

    /// <summary>Number of text lines: one more than the line breaks unless the text ends on one.</summary>
    internal static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var breaks = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    breaks++;
                    i++;
                    break;
                case '\r':
                case '\n':
                    breaks++;
                    break;
            }
        }

        var endsWithBreak = text[^1] is '\n' or '\r';
        return endsWithBreak ? breaks : breaks + 1;
    }

    private static Encoding LoadGb18030()
    {
        // The static field initializer runs before any static ctor, so registration must happen
        // here (RegisterProvider is idempotent). Codepage 54936 is inbox on Windows desktop.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(54936);
        }
        catch (Exception)
        {
            // Without the code-pages provider GB18030 is unavailable; UTF-8 with replacement
            // keeps the preview alive (garbled bytes instead of a crash).
            return Encoding.UTF8;
        }
    }

    /// <summary>The 1 MB (1,000,000-byte) default limit keeps the classic "1 MB" label; other limits
    /// get the exact formatted size.</summary>
    internal static string FormatLimitLabel(long limit) =>
        limit == 1_000_000 ? "1 MB" : FormatSize(limit);

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}
