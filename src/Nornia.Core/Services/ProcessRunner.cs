using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.Core.Services;

public sealed class ProcessRunner : IProcessRunner, IProcessRunnerShutdown
{
    // Text fallback must inspect the retained byte stream to decide UTF-8 vs GB18030. Keep the
    // retained prefix explicitly bounded; an unterminated generated line must not turn a read-only
    // diff into an unbounded allocation. The prefix is spooled to disk instead of a large byte[] so
    // a large diff does not create a transient 64 MB managed allocation.
    private const int MaximumTextFallbackCaptureBytes = 64 * 1024 * 1024;
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        int? maximumOutputBytes = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"Unable to start '{fileName}'.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }

        _activeProcesses.TryAdd(process.Id, process);

        try
        {
            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            var outputTask = ReadStreamAsync(process.StandardOutput, standardOutput, false, progress, maximumOutputBytes, cancellationToken);
            var errorTask = ReadStreamAsync(process.StandardError, standardError, true, progress, maximumOutputBytes, cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(outputTask, errorTask);
            }
            catch (OperationCanceledException)
            {
                StopProcess(process);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }

            return new ProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString(), outputTask.Result || errorTask.Result);
        }
        finally
        {
            _activeProcesses.TryRemove(process.Id, out _);
        }
    }

    public async Task<ProcessRawResult> RunRawAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(fileName, arguments, environmentVariables);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessRawResult(-1, [], $"Unable to start '{fileName}'.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new ProcessRawResult(-1, [], ex.Message);
        }

        _activeProcesses.TryAdd(process.Id, process);
        try
        {
            var captured = new MemoryStream(64 * 1024);
            var chunk = new byte[64 * 1024];
            var outputTask = ReadToAsync(process.StandardOutput.BaseStream, captured, chunk, cancellationToken);
            var error = new StringBuilder();
            var errorTask = ReadStreamAsync(process.StandardError, error, true, null, null, cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(outputTask, errorTask);
            }
            catch (OperationCanceledException)
            {
                StopProcess(process);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }

            return new ProcessRawResult(process.ExitCode, captured.ToArray(), error.ToString());
        }
        finally
        {
            _activeProcesses.TryRemove(process.Id, out _);
        }
    }

    /// <summary>Drains the raw stream into <paramref name="target"/> in fixed-size chunks.
    /// Returns when the stream is at EOF; never decodes.</summary>
    private static async Task ReadToAsync(Stream source, MemoryStream target, byte[] chunk, CancellationToken cancellationToken)
    {
        while (true)
        {
            var read = await source.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            target.Write(chunk, 0, read);
        }
    }

    public void StopActiveProcesses()
    {
        // Process.Kill(entireProcessTree: true) can block for several seconds per process tree
        // (the runtime waits for descendants to exit before returning). This method runs on the
        // desktop app's shutdown thread, which works against a small shared cleanup budget, so
        // the termination requests are issued immediately but the per-tree wait happens on
        // background threads, in parallel per process. Every active process still receives its
        // kill before the shutdown path continues; only the confirmation that the whole tree has
        // exited is no longer synchronous (and the process is exiting anyway).
        var snapshot = _activeProcesses.ToArray();
        if (snapshot.Length == 0) return;

        Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(snapshot.Select(entry =>
                    Task.Run(() => StopProcess(entry.Value)))).ConfigureAwait(false);
            }
            catch
            {
                // Last-resort: termination of one process must never prevent the remaining
                // processes from being terminated while the application exits.
            }
        }, CancellationToken.None);
    }

    public async IAsyncEnumerable<ProcessStreamEvent> StreamLinesAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int maximumLines = 1_000_000,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        maximumLines = Math.Clamp(maximumLines, 1, 5_000_000);
        var startInfo = CreateStartInfo(fileName, arguments, environmentVariables);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            yield return new ProcessStreamEvent(null, true, -1);
            yield break;
        }

        _activeProcesses.TryAdd(process.Id, process);
        var channel = Channel.CreateBounded<ProcessStreamEvent>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var emitted = 0;
        var limitReached = false;
        async Task PumpAsync(StreamReader reader, bool isError)
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (Interlocked.Increment(ref emitted) > maximumLines)
                {
                    limitReached = true;
                    StopProcess(process);
                    break;
                }

                await channel.Writer.WriteAsync(new ProcessStreamEvent(line, isError), cancellationToken).ConfigureAwait(false);
            }
        }

        var output = PumpAsync(process.StandardOutput, false);
        var error = PumpAsync(process.StandardError, true);
        var completion = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                await channel.Writer.WriteAsync(new ProcessStreamEvent(null, false, process.ExitCode, limitReached), CancellationToken.None).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                StopProcess(process);
                channel.Writer.TryComplete(exception);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
            await completion.ConfigureAwait(false);
        }
        finally
        {
            _activeProcesses.TryRemove(process.Id, out _);
            if (!process.HasExited) StopProcess(process);
        }
    }

    /// <summary>
    /// Like <see cref="IProcessRunner.StreamLinesAsync"/> but for text output that may carry a
    /// legacy Chinese encoding — git diff content lines pass the file's raw bytes through, so a
    /// GBK/ANSI file garbles under the fixed UTF-8 decode of <see cref="StreamLinesAsync"/>.
    /// Stdout is captured as a bounded raw-byte prefix, validated as strict UTF-8 over the whole
    /// payload, and decoded as GB18030 when it is not clean — the same BOM/strict-UTF-8/GB18030
    /// strategy as the read-only preview decoder. Clean UTF-8 output costs one validation pass
    /// (a byte scan at memory-bandwidth speed) and otherwise streams lines exactly like
    /// <see cref="StreamLinesAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<ProcessStreamEvent> StreamLinesWithTextFallbackAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int maximumLines = 1_000_000,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        maximumLines = Math.Clamp(maximumLines, 1, 5_000_000);
        var startInfo = CreateStartInfo(fileName, arguments, environmentVariables);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            yield return new ProcessStreamEvent(null, true, -1);
            yield break;
        }

        _activeProcesses.TryAdd(process.Id, process);
        var channel = Channel.CreateBounded<ProcessStreamEvent>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var emitted = 0;
        var limitReached = false;

        // Retain exactly the first maximumLines complete stdout lines, capped in bytes as well.
        // Continue draining after either cap so the child never blocks on a full pipe.
        async Task<string> CaptureStdoutAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"nornia-diff-{Guid.NewGuid():N}.tmp");
            await using var captured = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var chunk = new byte[64 * 1024];
            var newlines = 0;
            var capturing = true;
            await using var source = process.StandardOutput.BaseStream;
            while (true)
            {
                var read = await source.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (!capturing)
                {
                    continue;
                }

                var retained = 0;
                while (retained < read && newlines < maximumLines && captured.Length + retained < MaximumTextFallbackCaptureBytes)
                {
                    if (chunk[retained] == (byte)'\n')
                    {
                        newlines++;
                    }

                    retained++;
                }

                if (retained > 0)
                {
                    await captured.WriteAsync(chunk.AsMemory(0, retained), cancellationToken).ConfigureAwait(false);
                }

                if (retained < read)
                {
                    capturing = false;
                    limitReached = true;
                }
            }

            await captured.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }

        async Task PumpAsync(StreamReader reader, bool isError)
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (Interlocked.Increment(ref emitted) > maximumLines)
                {
                    limitReached = true;
                    StopProcess(process);
                    break;
                }

                await channel.Writer.WriteAsync(new ProcessStreamEvent(line, isError), cancellationToken).ConfigureAwait(false);
            }
        }

        var error = PumpAsync(process.StandardError, true);
        string? stdoutPath = null;
        var completion = Task.Run(async () =>
        {
            try
            {
                // 顺序关键:必须先抽干 stdout 再等退出。整段捕获要求"进程结束前"持续读取——
                // 若先等退出,子进程输出超过管道缓冲(Windows 默认数 KB)时会阻塞在写端,
                // 永远退不出,等待方也随之永久挂起(大 diff 视图空白即源于此)。
                // CaptureStdoutAsync 在管道写端关闭(进程退出)时返回,其后 WaitForExitAsync
                // 立即完成;取消经 ReadAsync 触发 catch 分支杀进程,语义与原先一致。
                stdoutPath = await CaptureStdoutAsync().ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await error.ConfigureAwait(false);
                await using (var stdout = new FileStream(
                    stdoutPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var (encoding, bomLength) = await DetectEncodingForCapturedTextAsync(stdout, cancellationToken).ConfigureAwait(false);
                    stdout.Position = bomLength;
                    await PumpAsync(new StreamReader(stdout, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true), false).ConfigureAwait(false);
                }
                await channel.Writer.WriteAsync(new ProcessStreamEvent(null, false, process.ExitCode, limitReached), CancellationToken.None).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                StopProcess(process);
                channel.Writer.TryComplete(exception);
            }
            finally
            {
                if (stdoutPath is not null)
                {
                    try { File.Delete(stdoutPath); } catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
            await completion.ConfigureAwait(false);
        }
        finally
        {
            _activeProcesses.TryRemove(process.Id, out _);
            if (!process.HasExited) StopProcess(process);
        }
    }

    /// <summary>Chooses the decode encoding for the retained stdout spool. UTF-8 validation is
    /// incremental so the complete retained payload never has to be copied into a managed array.</summary>
    private static async Task<(Encoding Encoding, int BomLength)> DetectEncodingForCapturedTextAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        var head = new byte[3];
        var headLength = 0;
        while (headLength < head.Length)
        {
            var read = await stream.ReadAsync(head.AsMemory(headLength), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            headLength += read;
        }

        var bomLength = TextEncodingDetector.Utf8BomLength(head.AsSpan(0, headLength));
        if (bomLength > 0)
        {
            stream.Position = 0;
            return (TextEncodingDetector.Utf8Replacement, bomLength);
        }

        stream.Position = 0;
        var strict = true;
        var pending = Array.Empty<byte>();
        var buffer = new byte[64 * 1024];
        while (strict)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            strict = TextEncodingDetector.IsStrictUtf8Chunk(buffer.AsSpan(0, read), pending, out pending);
        }

        if (strict && pending.Length > 0)
        {
            strict = false;
        }

        stream.Position = 0;
        return (strict ? TextEncodingDetector.Utf8Strict : TextEncodingDetector.Gb18030, 0);
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ObjectDisposedException)
        {
            // The owning RunAsync finished and disposed the Process before the shutdown pass
            // (now backgrounded) reached it.
        }
        catch (InvalidOperationException)
        {
            // The process exited while shutdown was in progress.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Windows rejected termination because the process has already exited or is protected.
        }
    }

    /// <summary>Drains the reader into <paramref name="buffer"/>, appending at most
    /// <paramref name="maximumOutputBytes"/> characters. Reading always continues to the end so the
    /// child process can never block on a full pipe; excess output is dropped and reported through
    /// the return value (<see cref="ProcessResult.OutputTruncated"/>).</summary>
    private static async Task<bool> ReadStreamAsync(
        StreamReader reader,
        StringBuilder buffer,
        bool isError,
        IProgress<ProcessOutput>? progress,
        int? maximumOutputBytes,
        CancellationToken cancellationToken)
    {
        var truncated = false;
        long limit = maximumOutputBytes is { } cap ? cap : long.MaxValue;

        // Known structured output that does not need live reporting is read in block passes, which
        // is significantly faster than stepping through every line for large outputs (e.g. Winget
        // lists). The byte cap is applied per block so memory stays bounded even without a cap hit.
        if (progress is null)
        {
            var block = new char[8192];
            while (true)
            {
                var read = await reader.ReadAsync(block.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length < limit)
                {
                    var take = Math.Min(read, (int)Math.Min(limit - buffer.Length, int.MaxValue));
                    buffer.Append(block, 0, take);
                    if (take < read)
                    {
                        truncated = true;
                    }
                }
                else
                {
                    truncated = true;
                }
            }
            return truncated;
        }

        // With a progress sink we keep streaming line by line so callers observe output in real time.
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (buffer.Length < limit)
            {
                var room = (int)Math.Min(limit - buffer.Length, int.MaxValue);
                if (line.Length + 1 <= room)
                {
                    buffer.AppendLine(line);
                }
                else
                {
                    buffer.Append(line.AsSpan(0, Math.Max(0, room - 1)));
                    truncated = true;
                }
            }
            else
            {
                truncated = true;
            }

            progress.Report(new ProcessOutput(line, isError));
        }
        return truncated;
    }

    private static ProcessStartInfo CreateStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables) startInfo.EnvironmentVariables[key] = value;
        }
        return startInfo;
    }
}
