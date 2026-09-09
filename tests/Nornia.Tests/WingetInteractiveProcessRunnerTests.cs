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
        var progress = new CallbackProgress(output =>
        {
            if (output.Text.Contains("]9;4;1;42", StringComparison.Ordinal))
            {
                progressReached.TrySetResult(true);
            }
        });
        var sequence = Encoding.UTF8.GetBytes("\u001b]9;4;1;42\u0007");
        var command = "$b=[Convert]::FromBase64String('"
                      + Convert.ToBase64String(sequence)
                      + "'); $o=[Console]::OpenStandardOutput(); $o.Write($b,0,$b.Length); $o.Flush(); Start-Sleep -Milliseconds 500";
        var runner = new WingetInteractiveProcessRunner(new ProcessRunner());
        var running = runner.RunInteractiveAsync(
            "powershell.exe",
            ["-NoProfile", "-Command", command],
            progress);

        var signalled = await Task.WhenAny(progressReached.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(progressReached.Task, signalled);
        Assert.False(running.IsCompleted);
        var result = await running;
        Assert.True(result.IsSuccess);
    }

    private sealed class CallbackProgress(Action<ProcessOutput> callback) : IProgress<ProcessOutput>
    {
        public void Report(ProcessOutput value) => callback(value);
    }
}
