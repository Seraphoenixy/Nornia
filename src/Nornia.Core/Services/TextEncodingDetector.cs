using System.Text;

namespace Nornia.Core.Services;

/// <summary>
/// Encoding detection for raw text that may carry a legacy Chinese encoding. Git diff content
/// lines and untracked worktree files carry the file's raw bytes: the payload is validated as
/// strict UTF-8 over its <em>entire</em> content (a legacy GBK file must not pass because only
/// its head happens to be ASCII), and anything not well-formed UTF-8 is decoded as GB18030 —
/// the same BOM-first / strict-UTF-8 / GB18030-fallback strategy the read-only preview decoder
/// uses, so the diff view can no longer garble what the preview renders correctly.
/// </summary>
public static class TextEncodingDetector
{
    /// <summary>GB18030 code page (the legacy-Chinese superset of GBK/GB2312).</summary>
    public const int Gb18030CodePage = 54936;

    /// <summary>UTF-8 with throw-on-invalid, for decoding payloads already validated as clean.</summary>
    public static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 with the default replacement fallback: decodes any byte sequence, stray
    /// invalid bytes become U+FFFD instead of re-classifying the whole file.</summary>
    public static readonly UTF8Encoding Utf8Replacement = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>GB18030 — total over every byte value, so decoding can never fail. Falls back to
    /// UTF-8 replacement when the code-pages provider is unavailable so detection still completes.</summary>
    public static readonly Encoding Gb18030 = LoadGb18030();

    private static readonly byte[] Empty = [];

    /// <summary>Length of a leading UTF-8 BOM (3), or 0. A BOM is an authoritative declaration:
    /// BOM-carrying UTF-8 is always decoded as UTF-8 (replacement-tolerant), never GB18030.
    /// UTF-16/32 BOMs are irrelevant here: their NUL bytes mark the content binary upstream.</summary>
    public static int Utf8BomLength(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;

    /// <summary>Picks the decode encoding for a fully inspected payload: a UTF-8 BOM is
    /// authoritative; otherwise clean strict UTF-8 wins and anything else falls back to GB18030.</summary>
    public static Encoding ChooseForContent(bool hasUtf8Bom, bool isStrictUtf8)
    {
        if (hasUtf8Bom)
        {
            return Utf8Replacement;
        }

        return isStrictUtf8 ? Utf8Strict : Gb18030;
    }

    /// <summary>True when the complete payload is well-formed UTF-8 under strict Unicode rules
    /// (stray continuation bytes, overlong forms, surrogates and code points above U+10FFFF are
    /// all invalid — the same acceptance set as .NET's strict UTF-8 decoder). Scans the whole
    /// payload and stops at the first invalid byte, so legacy files fail fast at their first
    /// non-ASCII character.</summary>
    public static bool IsStrictUtf8(byte[] bytes) => IsStrictUtf8(bytes.AsSpan());

    /// <summary>Whole-span strict UTF-8 validation (no chunk carry-over: the span must contain
    /// the complete payload; an incomplete sequence at the end is invalid).</summary>
    public static bool IsStrictUtf8(ReadOnlySpan<byte> bytes) =>
        ValidateSpan(bytes, out var pending) && pending.Length == 0;

    /// <summary>Streaming strict UTF-8 validation across chunk boundaries:
    /// <paramref name="pending"/> carries the 0–3 trailing bytes of an incomplete sequence from
    /// the previous chunk; <paramref name="newPending"/> receives the trailing bytes that must be
    /// carried into the next chunk. Returns false at the first invalid byte (the caller may stop
    /// feeding chunks); when true, no invalid sequence exists in the data seen so far and
    /// <paramref name="newPending"/> is the only state to keep.</summary>
    public static bool IsStrictUtf8Chunk(ReadOnlySpan<byte> chunk, byte[] pending, out byte[] newPending)
    {
        // The pending bytes (at most three) are prepended so a sequence split across the chunk
        // boundary is validated as a unit; with no pending state the chunk is validated in place.
        var data = pending.Length == 0 ? chunk : Combine(pending, chunk).AsSpan();
        return ValidateSpan(data, out newPending);
    }

    private static bool ValidateSpan(ReadOnlySpan<byte> data, out byte[] newPending)
    {
        var i = 0;
        while (i < data.Length)
        {
            var b0 = data[i];
            int length;
            if (b0 < 0x80)
            {
                i++;
                continue;
            }

            if (b0 >= 0xC2 && b0 <= 0xDF) length = 2;
            else if (b0 >= 0xE0 && b0 <= 0xEF) length = 3;
            else if (b0 >= 0xF0 && b0 <= 0xF4) length = 4;
            else
            {
                newPending = data[i..].ToArray();
                return false;
            }

            if (i + length > data.Length)
            {
                break; // incomplete sequence at the end of this chunk: carry it over
            }

            for (var j = 1; j < length; j++)
            {
                if ((data[i + j] & 0xC0) != 0x80)
                {
                    newPending = data[i..].ToArray();
                    return false;
                }
            }

            if (IsOverlongOrSurrogate(data, i, length))
            {
                newPending = data[i..].ToArray();
                return false;
            }

            i += length;
        }

        newPending = data[i..].ToArray();
        return true;
    }

    private static bool IsOverlongOrSurrogate(ReadOnlySpan<byte> bytes, int start, int length)
    {
        if (length == 3)
        {
            // E0 A0-BF .. encodes overlong U+0000-U+007F; ED A0-BF .. encodes UTF-16 surrogates.
            return (bytes[start] == 0xE0 && bytes[start + 1] < 0xA0)
                || (bytes[start] == 0xED && bytes[start + 1] > 0x9F);
        }

        if (length == 4)
        {
            // F0 90-BF .. encodes overlong U+0800-U+FFFF; F4 90-FF .. exceeds U+10FFFF.
            return (bytes[start] == 0xF0 && bytes[start + 1] < 0x90)
                || (bytes[start] == 0xF4 && bytes[start + 1] > 0x8F);
        }

        return false; // 2-byte sequences with lead >= 0xC2 are never overlong
    }

    private static Encoding LoadGb18030()
    {
        // RegisterProvider is idempotent; code page 54936 is inbox on Windows desktop.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(Gb18030CodePage);
        }
        catch (Exception)
        {
            // Without the code-pages provider GB18030 is unavailable; UTF-8 with replacement
            // keeps decoding alive (garbled bytes instead of a crash).
            return Utf8Replacement;
        }
    }

    private static byte[] Combine(byte[] pending, ReadOnlySpan<byte> bytes)
    {
        var data = new byte[pending.Length + bytes.Length];
        pending.CopyTo(data);
        bytes.CopyTo(data.AsSpan(pending.Length));
        return data;
    }
}
