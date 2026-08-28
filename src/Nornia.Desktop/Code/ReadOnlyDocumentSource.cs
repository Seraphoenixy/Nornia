using System.Runtime.CompilerServices;
using System.IO;
using System.Text;

namespace Nornia.Desktop.Code;

public sealed record ReadOnlyDocumentMetadata(
    long ByteLength,
    Encoding Encoding,
    string LineEnding,
    int TotalLines,
    ReadOnlyContentTier CapacityTier,
    bool IsBinary);

public readonly record struct DocumentRange(int StartLine, int LineCount);

public sealed record DocumentWindow(
    int StartLine,
    string Text,
    IReadOnlyList<int> LineOffsets,
    bool HasPrevious,
    bool HasNext);

public sealed record DocumentSearchMatch(int Line, int Column, int Length, string Preview);

public interface IReadOnlyDocumentSource : IAsyncDisposable
{
    ValueTask<ReadOnlyDocumentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);
    ValueTask<DocumentWindow> ReadWindowAsync(DocumentRange range, CancellationToken cancellationToken = default);
    IAsyncEnumerable<DocumentSearchMatch> SearchAsync(string query, int maximumMatches = 1_000, TextSearchOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Disk-backed, read-only source with an exact sparse byte/line index. AvalonEdit receives
/// only a requested logical-line window; indexing and searching stream through the file.</summary>
public sealed class FileReadOnlyDocumentSource : IReadOnlyDocumentSource
{
    public const int DefaultWindowLines = 5_000;
    public const int MaximumWindowCharacters = 2 * 1024 * 1024;
    private const int IndexStride = 256;
    private readonly string _path;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private ReadOnlyDocumentMetadata? _metadata;
    private List<LineCheckpoint>? _checkpoints;
    private bool _disposed;

    public FileReadOnlyDocumentSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async ValueTask<ReadOnlyDocumentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_metadata is not null) return _metadata;
        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_metadata is not null) return _metadata;
            (_metadata, _checkpoints) = await BuildIndexAsync(cancellationToken).ConfigureAwait(false);
            return _metadata;
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public async ValueTask<DocumentWindow> ReadWindowAsync(DocumentRange range, CancellationToken cancellationToken = default)
    {
        var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (metadata.IsBinary) return new DocumentWindow(1, string.Empty, [0], false, false);
        var startLine = Math.Clamp(range.StartLine, 1, Math.Max(1, metadata.TotalLines));
        var lineCount = Math.Clamp(range.LineCount, 1, DefaultWindowLines);
        var checkpoint = FindCheckpoint(_checkpoints!, startLine);

        await using var stream = OpenStream();
        stream.Position = checkpoint.ByteOffset;
        using var reader = new StreamReader(stream, metadata.Encoding, detectEncodingFromByteOrderMarks: checkpoint.ByteOffset == 0, bufferSize: 64 * 1024, leaveOpen: false);
        var text = new StringBuilder();
        var offsets = new List<int>(lineCount + 1) { 0 };
        var readLines = 0;
        var currentLine = checkpoint.Line;
        var buffer = new char[16 * 1024];
        var finished = false;
        while (!finished)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    if (currentLine >= startLine)
                    {
                        readLines++;
                        offsets.Add(text.Length);
                        if (readLines >= lineCount) { finished = true; break; }
                        text.Append('\n');
                    }
                    currentLine++;
                    continue;
                }

                if (currentLine >= startLine && character != '\r')
                {
                    if (text.Length >= MaximumWindowCharacters) { finished = true; break; }
                    text.Append(character);
                }
            }
        }

        if (text.Length > 0 && offsets[^1] != text.Length) offsets.Add(text.Length);
        var coveredLines = Math.Max(1, readLines);
        return new DocumentWindow(startLine, text.ToString(), offsets, startLine > 1, startLine + coveredLines <= metadata.TotalLines || finished);
    }

    /// <summary>Streams the file line by line and yields matches honouring the find-bar options
    /// (case / whole-word / regex). A null options argument keeps the legacy plain case-insensitive
    /// search; a malformed regex yields no matches (the caller reports the validation error).</summary>
    public async IAsyncEnumerable<DocumentSearchMatch> SearchAsync(
        string query,
        int maximumMatches = 1_000,
        TextSearchOptions? options = null,
        IProgress<double>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (metadata.IsBinary) yield break;
        maximumMatches = Math.Clamp(maximumMatches, 1, 100_000);
        var effective = options ?? new TextSearchOptions(CaseSensitive: false, WholeWord: false, UseRegex: false);

        System.Text.RegularExpressions.Regex? regex = null;
        if (effective.UseRegex && TextSearchService.GetRegexError(query, effective.CaseSensitive) is null)
        {
            regex = new System.Text.RegularExpressions.Regex(query, TextSearchService.BuildRegexOptions(effective.CaseSensitive));
        }

        var comparison = effective.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        await using var stream = OpenStream();
        using var reader = new StreamReader(stream, metadata.Encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: false);
        var lineNumber = 0;
        var matches = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (regex is not null)
            {
                // Whole-word is enforced on top of the pattern (same rule as the in-memory search).
                foreach (System.Text.RegularExpressions.Match match in regex.Matches(line))
                {
                    if (effective.WholeWord && !TextSearchService.IsWholeWord(line, match.Index, match.Length))
                    {
                        continue;
                    }

                    yield return new DocumentSearchMatch(lineNumber, match.Index + 1, match.Length, LinePreview(line));
                    if (++matches >= maximumMatches) { progress?.Report(1); yield break; }
                }
            }
            else
            {
                for (var from = 0; matches < maximumMatches;)
                {
                    var column = line.IndexOf(query, from, comparison);
                    if (column < 0) break;
                    if (!effective.WholeWord || TextSearchService.IsWholeWord(line, column, query.Length))
                    {
                        yield return new DocumentSearchMatch(lineNumber, column + 1, query.Length, LinePreview(line));
                        matches++;
                    }

                    from = column + Math.Max(1, query.Length);
                }
            }

            if ((lineNumber & 1023) == 0) progress?.Report((double)stream.Position / Math.Max(1, stream.Length));
            if (matches >= maximumMatches) yield break;
        }

        progress?.Report(1);
    }

    private static string LinePreview(string line) => line.Length <= 240 ? line : line[..240];

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _metadataGate.Dispose();
        _checkpoints = null;
        _metadata = null;
        return ValueTask.CompletedTask;
    }

    private async Task<(ReadOnlyDocumentMetadata Metadata, List<LineCheckpoint> Checkpoints)> BuildIndexAsync(CancellationToken cancellationToken)
    {
        await using var stream = OpenStream();
        var probe = new byte[(int)Math.Min(4096, stream.Length)];
        _ = await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
        var isBinary = probe.AsSpan().IndexOf((byte)0) >= 0 && !HasUnicodeBom(probe);
        var encoding = DetectEncoding(probe);
        stream.Position = 0;
        var checkpoints = new List<LineCheckpoint> { new(1, 0) };
        var line = 1;
        var lineEnding = string.Empty;
        var unit = encoding.CodePage is 12000 or 12001 ? 4 : encoding.CodePage is 1200 or 1201 ? 2 : 1;
        var littleEndian = encoding.CodePage is not (1201 or 12001);
        var buffer = new byte[64 * 1024];
        long absolute = 0;
        var carry = Array.Empty<byte>();
        while (!isBinary)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var data = carry.Length == 0 ? buffer.AsSpan(0, read).ToArray() : [.. carry, .. buffer.AsSpan(0, read).ToArray()];
            var scanLength = data.Length - data.Length % unit;
            for (var index = 0; index < scanLength; index += unit)
            {
                if (!IsLineFeed(data, index, unit, littleEndian)) continue;
                if (lineEnding.Length == 0) lineEnding = IsPrecededByCarriageReturn(data, index, unit, littleEndian) ? "\r\n" : "\n";
                line++;
                if ((line - 1) % IndexStride == 0)
                {
                    var byteOffset = absolute - carry.Length + index + unit;
                    checkpoints.Add(new LineCheckpoint(line, byteOffset));
                }
            }

            absolute += read;
            carry = scanLength == data.Length ? [] : data[scanLength..];
        }

        var metadata = new ReadOnlyDocumentMetadata(stream.Length, encoding, lineEnding.Length == 0 ? Environment.NewLine : lineEnding, line, ReadOnlyContentCapacity.ForSource(stream.Length), isBinary);
        return (metadata, checkpoints);
    }

    private FileStream OpenStream() => new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static Encoding DetectEncoding(ReadOnlySpan<byte> probe)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (HasPrefix(probe, 0xFF, 0xFE, 0x00, 0x00)) return new UTF32Encoding(false, true);
        if (HasPrefix(probe, 0x00, 0x00, 0xFE, 0xFF)) return new UTF32Encoding(true, true);
        if (HasPrefix(probe, 0xFF, 0xFE)) return Encoding.Unicode;
        if (HasPrefix(probe, 0xFE, 0xFF)) return Encoding.BigEndianUnicode;
        if (HasPrefix(probe, 0xEF, 0xBB, 0xBF)) return new UTF8Encoding(true, true);
        try
        {
            _ = new UTF8Encoding(false, true).GetString(probe);
            return new UTF8Encoding(false, true);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }
    }

    private static bool HasUnicodeBom(ReadOnlySpan<byte> value) =>
        HasPrefix(value, 0xFF, 0xFE) || HasPrefix(value, 0xFE, 0xFF) || HasPrefix(value, 0x00, 0x00, 0xFE, 0xFF);

    /// <summary>Checkpoints are ordered by line. Binary search keeps deep window jumps O(log n)
    /// instead of scanning the complete sparse index on every navigation.</summary>
    private static LineCheckpoint FindCheckpoint(IReadOnlyList<LineCheckpoint> checkpoints, int line)
    {
        var low = 0;
        var high = checkpoints.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (checkpoints[middle].Line <= line)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return checkpoints[low];
    }

    private static bool HasPrefix(ReadOnlySpan<byte> value, params byte[] prefix) => value.StartsWith(prefix);

    private static bool IsLineFeed(byte[] data, int index, int unit, bool littleEndian) => unit switch
    {
        1 => data[index] == 0x0A,
        2 => littleEndian ? data[index] == 0x0A && data[index + 1] == 0 : data[index] == 0 && data[index + 1] == 0x0A,
        _ => littleEndian ? data[index] == 0x0A && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 0 : data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 0x0A,
    };

    private static bool IsPrecededByCarriageReturn(byte[] data, int index, int unit, bool littleEndian) =>
        index >= unit && unit switch
        {
            1 => data[index - 1] == 0x0D,
            2 => littleEndian ? data[index - 2] == 0x0D && data[index - 1] == 0 : data[index - 2] == 0 && data[index - 1] == 0x0D,
            _ => littleEndian ? data[index - 4] == 0x0D && data[index - 3] == 0 && data[index - 2] == 0 && data[index - 1] == 0 : data[index - 4] == 0 && data[index - 3] == 0 && data[index - 2] == 0 && data[index - 1] == 0x0D,
        };

    private sealed record LineCheckpoint(int Line, long ByteOffset);
}
