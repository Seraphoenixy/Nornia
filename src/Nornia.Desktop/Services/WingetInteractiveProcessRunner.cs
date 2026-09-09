using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.Native;

namespace Nornia.Desktop.Services;

/// <summary>Runs winget through ConPTY so the CLI sees a real terminal and emits its native
/// virtual-terminal progress frames. WinGet intentionally suppresses those frames when its
/// standard streams are redirected.</summary>
public sealed class WingetInteractiveProcessRunner(IProcessRunner fallback) : IInteractiveProcessRunner
{
    private const int PtyColumns = 160;
    private const int PtyRows = 30;

    public async Task<ProcessResult> RunInteractiveAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return await fallback.RunAsync(fileName, arguments, progress, cancellationToken).ConfigureAwait(false);
        }

        ConPty.Instance? pty;
        Process? process;
        try
        {
            if (!ConPty.TryStartAttached(
                    fileName,
                    BuildArguments(arguments),
                    Environment.CurrentDirectory,
                    PtyColumns,
                    PtyRows,
                    out pty,
                    out process,
                    out _)
                || pty is null
                || process is null)
            {
                return await fallback.RunAsync(fileName, arguments, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception
            or EntryPointNotFoundException or DllNotFoundException)
        {
            return await fallback.RunAsync(fileName, arguments, progress, cancellationToken).ConfigureAwait(false);
        }

        using (pty)
        using (process)
        {
            var outputTask = ReadOutputAsync(pty.Output, progress);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                StopProcess(process);
                pty.Dispose();
                await ObserveOutputAsync(outputTask).ConfigureAwait(false);
                throw;
            }
            catch
            {
                StopProcess(process);
                pty.Dispose();
                await ObserveOutputAsync(outputTask).ConfigureAwait(false);
                throw;
            }

            // Closing the host side of ConPTY signals EOF after the child's final terminal bytes
            // have been flushed. The reader then completes and the final progress frame is kept.
            pty.Dispose();
            var captured = await outputTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, captured, string.Empty);
        }
    }

    private static async Task<string> ReadOutputAsync(Stream output, IProgress<ProcessOutput>? progress)
    {
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var captured = new StringBuilder();
        if (progress is null)
        {
            var block = new char[4096];
            while (true)
            {
                var read = await reader.ReadAsync(block.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                captured.Append(block, 0, read);
            }

            return captured.ToString();
        }

        var progressBlock = new char[4096];
        var pending = new StringBuilder();
        var previousWasCarriageReturn = false;
        while (true)
        {
            var read = await reader.ReadAsync(progressBlock.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            captured.Append(progressBlock, 0, read);
            for (var index = 0; index < read; index++)
            {
                var character = progressBlock[index];
                if (character is '\r' or '\n')
                {
                    if (character == '\n' && previousWasCarriageReturn)
                    {
                        previousWasCarriageReturn = false;
                        continue;
                    }

                    progress.Report(new ProcessOutput(pending.ToString(), false));
                    pending.Clear();
                    previousWasCarriageReturn = character == '\r';
                    continue;
                }

                pending.Append(character);
                previousWasCarriageReturn = false;
                var isVirtualTerminalTerminator = character == '\a'
                    || (character == '\\'
                        && pending.Length >= 2
                        && pending[pending.Length - 2] == '\x1b');
                if (isVirtualTerminalTerminator)
                {
                    progress.Report(new ProcessOutput(pending.ToString(), false));
                    pending.Clear();
                    continue;
                }

                if (character is '%' or '％')
                {
                    progress.Report(new ProcessOutput(pending.ToString(), false));
                }
            }
        }

        if (pending.Length > 0)
        {
            progress.Report(new ProcessOutput(pending.ToString(), false));
        }

        return captured.ToString();
    }

    private static async Task ObserveOutputAsync(Task<string> outputTask)
    {
        try
        {
            await outputTask.ConfigureAwait(false);
        }
        catch
        {
            // The process is already being terminated; its output pipe may close with an IO error.
        }
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
        catch
        {
            // The process may have exited between HasExited and Kill.
        }
    }

    private static string BuildArguments(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Select(QuoteCommandLineArgument));

    private static string QuoteCommandLineArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var result = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            result.Append(character);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2);
        return result.Append('"').ToString();
    }
}
