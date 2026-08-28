using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Nornia.Core.Models;

namespace Nornia.Desktop.Code;

public sealed record DiffContentMetadata(int TotalLines, ReadOnlyContentTier CapacityTier, IReadOnlyList<DiffHunkIndexEntry> Hunks, bool IsComplete);
public sealed record DiffHunkIndexEntry(int HunkNumber, int DisplayLine, long SpoolOffset, int OldStart, int NewStart, string Header);
public sealed record DiffContentWindow(int StartLine, IReadOnlyList<GitDiffEvent> Events, bool HasPrevious, bool HasNext);

public interface IDiffContentSource : IAsyncDisposable
{
    ValueTask<DiffContentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);
    ValueTask<DiffContentWindow> ReadWindowAsync(DocumentRange range, CancellationToken cancellationToken = default);
    Task CopyAllAsync(Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>Disk-spooled diff source with a compact binary envelope (D4): one record per event,
/// a single kind byte, varint numbers, and length-prefixed raw UTF-8 text — no per-event JSON
/// serialization and no per-line "\n" array allocation. Its in-memory footprint is hunk/sparse
/// offsets rather than all content lines; the temporary file is deleted when the tab releases
/// the source. The reader/writer are symmetric: records are self-describing (the line record
/// carries its own spool line number) and window reads work for arbitrary windows, including a
/// partial last window (D8).</summary>
public sealed class SpoolingDiffContentSource : IDiffContentSource
{
    // Record kind bytes.
    private const byte KindLine = 1;
    private const byte KindHunk = 2;
    private const byte KindMetadata = 3;
    private const byte KindCompleted = 4;

    // Max bytes a varint (uint32) needs.
    private const int MaxVarint = 5;

    private const int IndexStride = 512;
    private const int DefaultMaxWindowLines = 10_000;

    private readonly string _spoolPath;
    private readonly List<DiffHunkIndexEntry> _hunks;
    private readonly List<(int Line, long Offset)> _checkpoints;
    private readonly int _totalLines;
    private readonly bool _outputLimitReached;
    private bool _disposed;

    private SpoolingDiffContentSource(string spoolPath, List<DiffHunkIndexEntry> hunks, List<(int, long)> checkpoints, int totalLines, bool outputLimitReached)
    {
        _spoolPath = spoolPath;
        _hunks = hunks;
        _checkpoints = checkpoints;
        _totalLines = totalLines;
        _outputLimitReached = outputLimitReached;
    }

    public static async Task<SpoolingDiffContentSource> CreateAsync(IAsyncEnumerable<GitDiffEvent> events, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nornia-diff-{Guid.NewGuid():N}.spool");
        var hunks = new List<DiffHunkIndexEntry>();
        var checkpoints = new List<(int Line, long Offset)> { (1, 0) };
        var line = 0;
        var outputLimitReached = false;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await foreach (var item in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = stream.Position;
                switch (item)
                {
                    case GitDiffLineEvent lineEvent:
                        line++;
                        buffer = WriteLine(buffer, line, lineEvent.Line, out var lineWritten);
                        await stream.WriteAsync(buffer.AsMemory(0, lineWritten), cancellationToken).ConfigureAwait(false);
                        break;
                    case GitDiffHunkEvent hunk:
                        buffer = WriteHunk(buffer, hunk, out var hunkWritten);
                        await stream.WriteAsync(buffer.AsMemory(0, hunkWritten), cancellationToken).ConfigureAwait(false);
                        // Hunk-index building (D4): the offset points at the hunk record itself,
                        // so window reads that start at this hunk can seek straight to it.
                        hunks.Add(new DiffHunkIndexEntry(hunks.Count, Math.Max(1, line + 1), offset, hunk.OldStart, hunk.NewStart, hunk.Header));
                        break;
                    case GitDiffMetadataEvent metadata:
                        buffer = WriteMetadata(buffer, metadata, out var metadataWritten);
                        await stream.WriteAsync(buffer.AsMemory(0, metadataWritten), cancellationToken).ConfigureAwait(false);
                        break;
                    case GitDiffCompletedEvent completed:
                        if (completed.OutputLimitReached) outputLimitReached = true;
                        buffer = WriteCompleted(buffer, completed, out var completedWritten);
                        await stream.WriteAsync(buffer.AsMemory(0, completedWritten), cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(item), item, "Unknown diff event.");
                }

                if (line > 0 && line % IndexStride == 0 && checkpoints[^1].Line != line + 1) checkpoints.Add((line + 1, stream.Position));
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new SpoolingDiffContentSource(path, hunks, checkpoints, line, outputLimitReached);
    }

    public ValueTask<DiffContentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tier = _outputLimitReached ? ReadOnlyContentTier.Summary : ReadOnlyContentCapacity.ForDiff(_totalLines);
        return ValueTask.FromResult(new DiffContentMetadata(_totalLines, tier, _hunks, !_outputLimitReached));
    }

    /// <summary>Reads a window of at most <see cref="DefaultMaxWindowLines"/> (10k) lines.
    /// <see cref="DiffContentWindow.HasNext"/> reports whether more lines follow, so callers can
    /// paginate with successive ranges (D8).</summary>
    public ValueTask<DiffContentWindow> ReadWindowAsync(DocumentRange range, CancellationToken cancellationToken = default) =>
        ReadWindowAsync(range, DefaultMaxWindowLines, cancellationToken);

    /// <summary>Reads an arbitrary window (D8): any start line and any window size up to
    /// <paramref name="maxWindowLines"/>. The last window may be partial; <see cref="DiffContentWindow.HasNext"/>
    /// is false once the window reaches the final line.</summary>
    public async ValueTask<DiffContentWindow> ReadWindowAsync(DocumentRange range, int maxWindowLines, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        maxWindowLines = Math.Max(1, maxWindowLines);
        var start = Math.Clamp(range.StartLine, 1, Math.Max(1, _totalLines));
        var count = Math.Clamp(range.LineCount, 1, maxWindowLines);
        var checkpoint = _checkpoints.Last(point => point.Line <= start);
        var hunkCheckpoint = _hunks.LastOrDefault(hunk => hunk.DisplayLine <= start);
        if (hunkCheckpoint is not null && hunkCheckpoint.DisplayLine > checkpoint.Line)
        {
            checkpoint = (hunkCheckpoint.DisplayLine, hunkCheckpoint.SpoolOffset);
        }
        await using var stream = new FileStream(_spoolPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = checkpoint.Offset;
        await using var reader = new SpoolRecordReader(stream);
        var result = new List<GitDiffEvent>();
        var currentLine = checkpoint.Line;
        while (await reader.ReadRecordAsync(cancellationToken).ConfigureAwait(false) is { } item)
        {
            if (item is GitDiffLineEvent)
            {
                if (currentLine < start)
                {
                    currentLine++;
                    continue;
                }

                if (currentLine > start + count - 1)
                {
                    break;
                }

                currentLine++;
                result.Add(item);
                continue;
            }

            // Non-line records are positioned by the line that follows them (metadata: the first
            // line, hunk: its first line, completed: past the last line). Include them once that
            // line has reached the window start — the same rule as the previous counting reader.
            var nextLine = item switch
            {
                GitDiffMetadataEvent => 1,
                GitDiffCompletedEvent => _totalLines + 1,
                _ => currentLine,
            };
            if (nextLine >= start)
            {
                result.Add(item);
            }
        }

        return new DiffContentWindow(start, result, start > 1, start + count <= _totalLines);
    }

    public async Task CopyAllAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var stream = new FileStream(_spoolPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var reader = new SpoolRecordReader(stream);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        while (await reader.ReadRecordAsync(cancellationToken).ConfigureAwait(false) is GitDiffLineEvent { Line: { } line })
        {
            await writer.WriteLineAsync(line.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { File.Delete(_spoolPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _hunks.Clear();
        _checkpoints.Clear();
        return ValueTask.CompletedTask;
    }

    // ---------- compact binary record codec (D4) ----------
    //
    // LINE:      [KindLine][varint spoolLineNumber][varint diffLineKind][varint oldLine(-1=null)][varint newLine(-1=null)][varint textBytes][utf8]
    // HUNK:      [KindHunk][varint oldStart][varint oldCount][varint newStart][varint newCount][varint headerBytes][utf8]
    // METADATA:  [KindMetadata][flags: bit0 staged | bit1 binary | bit2 newFile][varint pathBytes][utf8][varint oldPathBytes(0=null)][utf8]
    // COMPLETED: [KindCompleted][varint exitCode][flags: bit0 outputLimitReached][varint errorBytes(0=null)][utf8]

    private static byte[] EnsureCapacity(byte[] buffer, int needed)
    {
        if (needed <= buffer.Length)
        {
            return buffer;
        }

        ArrayPool<byte>.Shared.Return(buffer);
        return ArrayPool<byte>.Shared.Rent(Math.Max(needed, buffer.Length * 2));
    }

    private static byte[] WriteLine(byte[] buffer, int spoolLineNumber, GitDiffLine line, out int written)
    {
        var textBytes = Encoding.UTF8.GetByteCount(line.Text);
        buffer = EnsureCapacity(buffer, 1 + MaxVarint * 4 + textBytes);
        var position = 0;
        buffer[position++] = KindLine;
        position = WriteVarint(buffer, position, spoolLineNumber);
        position = WriteVarint(buffer, position, (int)line.Kind);
        position = WriteVarint(buffer, position, line.OldLineNumber ?? -1);
        position = WriteVarint(buffer, position, line.NewLineNumber ?? -1);
        position = WriteVarint(buffer, position, textBytes);
        Encoding.UTF8.GetBytes(line.Text, buffer.AsSpan(position));
        written = position + textBytes;
        return buffer;
    }

    private static byte[] WriteHunk(byte[] buffer, GitDiffHunkEvent hunk, out int written)
    {
        var headerBytes = Encoding.UTF8.GetByteCount(hunk.Header);
        buffer = EnsureCapacity(buffer, 1 + MaxVarint * 5 + headerBytes);
        var position = 0;
        buffer[position++] = KindHunk;
        position = WriteVarint(buffer, position, hunk.OldStart);
        position = WriteVarint(buffer, position, hunk.OldCount);
        position = WriteVarint(buffer, position, hunk.NewStart);
        position = WriteVarint(buffer, position, hunk.NewCount);
        position = WriteVarint(buffer, position, headerBytes);
        Encoding.UTF8.GetBytes(hunk.Header, buffer.AsSpan(position));
        written = position + headerBytes;
        return buffer;
    }

    private static byte[] WriteMetadata(byte[] buffer, GitDiffMetadataEvent metadata, out int written)
    {
        var pathBytes = Encoding.UTF8.GetByteCount(metadata.Path);
        var oldPathBytes = metadata.OldPath is null ? 0 : Encoding.UTF8.GetByteCount(metadata.OldPath);
        buffer = EnsureCapacity(buffer, 2 + MaxVarint * 2 + pathBytes + oldPathBytes);
        var position = 0;
        buffer[position++] = KindMetadata;
        buffer[position++] = (byte)((metadata.IsStaged ? 1 : 0) | (metadata.IsBinary ? 2 : 0) | (metadata.IsNewFile ? 4 : 0));
        position = WriteVarint(buffer, position, pathBytes);
        Encoding.UTF8.GetBytes(metadata.Path, buffer.AsSpan(position));
        position += pathBytes;
        position = WriteVarint(buffer, position, oldPathBytes);
        if (metadata.OldPath is not null)
        {
            Encoding.UTF8.GetBytes(metadata.OldPath, buffer.AsSpan(position));
            position += oldPathBytes;
        }

        written = position;
        return buffer;
    }

    private static byte[] WriteCompleted(byte[] buffer, GitDiffCompletedEvent completed, out int written)
    {
        var errorBytes = completed.Error is null ? 0 : Encoding.UTF8.GetByteCount(completed.Error);
        buffer = EnsureCapacity(buffer, 2 + 2 * MaxVarint + errorBytes);
        var position = 0;
        buffer[position++] = KindCompleted;
        position = WriteVarint(buffer, position, completed.ExitCode);
        buffer[position++] = (byte)(completed.OutputLimitReached ? 1 : 0);
        position = WriteVarint(buffer, position, errorBytes);
        if (completed.Error is not null)
        {
            Encoding.UTF8.GetBytes(completed.Error, buffer.AsSpan(position));
            position += errorBytes;
        }

        written = position;
        return buffer;
    }

    private static int WriteVarint(byte[] buffer, int position, int value)
    {
        var unsigned = (uint)value;
        while (unsigned >= 0x80)
        {
            buffer[position++] = (byte)(unsigned | 0x80);
            unsigned >>= 7;
        }

        buffer[position++] = (byte)unsigned;
        return position;
    }

    /// <summary>Async reader over the compact record stream (D4). Text is materialized exactly
    /// once per record, on read-back (D9).</summary>
    private sealed class SpoolRecordReader(Stream stream) : IAsyncDisposable
    {
        private byte[] _scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
        private readonly byte[] _singleByte = new byte[1];

        public async ValueTask<GitDiffEvent?> ReadRecordAsync(CancellationToken cancellationToken)
        {
            var kind = await ReadByteCoreAsync(cancellationToken).ConfigureAwait(false);
            if (kind < 0)
            {
                return null;
            }

            switch (kind)
            {
                case KindLine:
                {
                    var (_, p1) = await ReadVarintAsync(cancellationToken, 0).ConfigureAwait(false);
                    var (diffKind, p2) = await ReadVarintAsync(cancellationToken, p1).ConfigureAwait(false);
                    var (oldNumber, p3) = await ReadVarintAsync(cancellationToken, p2).ConfigureAwait(false);
                    var (newNumber, p4) = await ReadVarintAsync(cancellationToken, p3).ConfigureAwait(false);
                    var (length, _) = await ReadVarintAsync(cancellationToken, p4).ConfigureAwait(false);
                    EnsureScratch(length);
                    await ReadExactAsync(_scratch, 0, length, cancellationToken).ConfigureAwait(false);
                    // The line text is materialized exactly once, here (D9); the embedded spool
                    // line number is redundant with the window reader's counter and is consumed
                    // above.
                    var text = Encoding.UTF8.GetString(_scratch, 0, length);
                    return new GitDiffLineEvent(new GitDiffLine(
                        (GitDiffLineKind)diffKind,
                        oldNumber < 0 ? null : oldNumber,
                        newNumber < 0 ? null : newNumber,
                        text));
                }
                case KindHunk:
                {
                    var (oldStart, q1) = await ReadVarintAsync(cancellationToken, 0).ConfigureAwait(false);
                    var (oldCount, q2) = await ReadVarintAsync(cancellationToken, q1).ConfigureAwait(false);
                    var (newStart, q3) = await ReadVarintAsync(cancellationToken, q2).ConfigureAwait(false);
                    var (newCount, q4) = await ReadVarintAsync(cancellationToken, q3).ConfigureAwait(false);
                    var (headerLength, _) = await ReadVarintAsync(cancellationToken, q4).ConfigureAwait(false);
                    EnsureScratch(headerLength);
                    await ReadExactAsync(_scratch, 0, headerLength, cancellationToken).ConfigureAwait(false);
                    return new GitDiffHunkEvent(oldStart, oldCount, newStart, newCount,
                        headerLength == 0 ? string.Empty : Encoding.UTF8.GetString(_scratch, 0, headerLength));
                }
                case KindMetadata:
                {
                    var flags = await ReadRequiredByteAsync(cancellationToken).ConfigureAwait(false);
                    var (pathLength, r1) = await ReadVarintAsync(cancellationToken, 0).ConfigureAwait(false);
                    EnsureScratch(pathLength);
                    await ReadExactAsync(_scratch, 0, pathLength, cancellationToken).ConfigureAwait(false);
                    var path = pathLength == 0 ? string.Empty : Encoding.UTF8.GetString(_scratch, 0, pathLength);
                    var (oldPathLength, _) = await ReadVarintAsync(cancellationToken, r1).ConfigureAwait(false);
                    EnsureScratch(oldPathLength);
                    await ReadExactAsync(_scratch, 0, oldPathLength, cancellationToken).ConfigureAwait(false);
                    return new GitDiffMetadataEvent(
                        path,
                        oldPathLength == 0 ? null : Encoding.UTF8.GetString(_scratch, 0, oldPathLength),
                        (flags & 1) != 0,
                        (flags & 2) != 0,
                        (flags & 4) != 0);
                }
                case KindCompleted:
                {
                    var (exitCode, _) = await ReadVarintAsync(cancellationToken, 0).ConfigureAwait(false);
                    var flags = await ReadRequiredByteAsync(cancellationToken).ConfigureAwait(false);
                    var (errorLength, _) = await ReadVarintAsync(cancellationToken, 0).ConfigureAwait(false);
                    EnsureScratch(errorLength);
                    await ReadExactAsync(_scratch, 0, errorLength, cancellationToken).ConfigureAwait(false);
                    return new GitDiffCompletedEvent(exitCode, (flags & 1) != 0,
                        errorLength == 0 ? null : Encoding.UTF8.GetString(_scratch, 0, errorLength));
                }
                default:
                    throw new InvalidDataException($"Unknown diff spool record kind '{kind}'.");
            }
        }

        public ValueTask DisposeAsync()
        {
            ArrayPool<byte>.Shared.Return(_scratch);
            return ValueTask.CompletedTask;
        }

        private void EnsureScratch(int count)
        {
            if (count <= _scratch.Length)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = ArrayPool<byte>.Shared.Rent(Math.Max(count, _scratch.Length * 2));
        }

        /// <summary>Stream 没有 ReadByteAsync:单字节缓冲 + ReadAsync(-1 表示流尾)。</summary>
        private async ValueTask<int> ReadByteCoreAsync(CancellationToken cancellationToken)
        {
            var read = await stream.ReadAsync(_singleByte.AsMemory(), cancellationToken).ConfigureAwait(false);
            return read == 0 ? -1 : _singleByte[0];
        }

        private async ValueTask<(int Value, int Position)> ReadVarintAsync(CancellationToken cancellationToken, int startPosition)
        {
            var result = 0u;
            var shift = 0;
            var position = startPosition;
            while (true)
            {
                var b = await ReadByteCoreAsync(cancellationToken).ConfigureAwait(false);
                if (b < 0)
                {
                    throw new EndOfStreamException("Truncated diff spool record.");
                }

                position++;
                result |= (uint)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    break;
                }

                shift += 7;
            }

            return ((int)result, position);
        }

        private async ValueTask<byte> ReadRequiredByteAsync(CancellationToken cancellationToken)
        {
            var b = await ReadByteCoreAsync(cancellationToken).ConfigureAwait(false);
            if (b < 0)
            {
                throw new EndOfStreamException("Truncated diff spool record.");
            }

            return (byte)b;
        }

        private async Task ReadExactAsync(byte[] destination, int offset, int count, CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < count)
            {
                var n = await stream.ReadAsync(destination.AsMemory(offset + read, count - read), cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    throw new EndOfStreamException("Truncated diff spool record.");
                }

                read += n;
            }
        }
    }
}
