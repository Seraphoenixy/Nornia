using System.Text;
using Nornia.Core.Models;
using Nornia.Core.Services;
using Nornia.Desktop.Services;

namespace Nornia.Tests;

public sealed class WingetInteractiveProcessRunnerTests
{
    [Fact]
    public async Task RunInteractiveAsync_StreamsTerminalProgressBeforeProcessExits()
    {
        var progressReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wasStreamedBeforeExit = false;
        var runner = new WingetInteractiveProcessRunner(new ProcessRunner());
        Task<ProcessResult> running = null!;
        var progress = new CallbackProgress(output =>
        {
            if (output.Text.Contains("]9;4;1;42", StringComparison.Ordinal))
            {
                // Checked at the exact moment the frame is read from the pty — the child is
                // still mid-Sleep in its command — rather than after test-thread scheduling.
                wasStreamedBeforeExit = !running.IsCompleted;
                progressReached.TrySetResult(true);
            }
        });
        var sequence = Encoding.UTF8.GetBytes("\u001b]9;4;1;42\u0007");
        var command = "$b=[Convert]::FromBase64String('"
                      + Convert.ToBase64String(sequence)
                      + "'); $o=[Console]::OpenStandardOutput(); $o.Write($b,0,$b.Length); $o.Flush(); Start-Sleep -Milliseconds 500";
        running = runner.RunInteractiveAsync(
            "powershell.exe",
            ["-NoProfile", "-Command", command],
            progress);

        // The 30s window absorbs slow process/ConPTY startup on a loaded CI runner and should
        // never be reached in a healthy build; the previous 5s was too tight for a busy host.
        var signalled = await Task.WhenAny(progressReached.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(progressReached.Task, signalled);
        Assert.True(wasStreamedBeforeExit, "the terminal progress frame must be streamed before the process exits");
        var result = await running;
        Assert.True(result.IsSuccess);
    }

    private sealed class CallbackProgress(Action<ProcessOutput> callback) : IProgress<ProcessOutput>
    {
        public void Report(ProcessOutput value) => callback(value);
    }
}
