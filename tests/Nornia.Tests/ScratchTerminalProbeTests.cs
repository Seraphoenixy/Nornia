using System.Text;
using Nornia.Desktop.Native;
using Xunit.Abstractions;

namespace Nornia.Tests;

/// <summary>临时诊断:绕过 TerminalService,在 ConPty 层用后台读线程采集原始输出。</summary>
public sealed class ScratchTerminalProbeTests
{
    private readonly ITestOutputHelper _output;

    public ScratchTerminalProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Probe()
    {
        var ok = ConPty.TryStartAttached(
            @"C:\Windows\System32\cmd.exe", string.Empty, Environment.CurrentDirectory, 120, 30,
            out var instance, out var process, out var error);
        _output.WriteLine($"TryStartAttached={ok} error={error ?? "none"} process={(process is null ? "null" : process.Id.ToString())}");
        if (!ok || instance is null)
        {
            return;
        }

        var collected = new StringBuilder();
        var reader = Task.Run(() =>
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var count = instance.Output.Read(buffer, 0, buffer.Length);
                    if (count <= 0)
                    {
                        break;
                    }

                    lock (collected)
                    {
                        collected.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    }
                }
            }
            catch (Exception ex)
            {
                lock (collected)
                {
                    collected.Append($"\n<reader-exit:{ex.GetType().Name}>");
                }
            }
        });

        try
        {
            await Task.Delay(3000);
            string sample;
            int lengthA;
            lock (collected)
            {
                lengthA = collected.Length;
                sample = collected.ToString();
            }

            _output.WriteLine($"AFTER-3S-BYTES={lengthA}");
            _output.WriteLine("SAMPLE=[" + sample.Replace("\x1b", "<ESC>").Replace("\r", "").Replace("\n", "\\n")[..Math.Min(300, sample.Length)] + "]");

            var input = Encoding.UTF8.GetBytes("echo __probe__\r");
            instance.Input.Write(input, 0, input.Length);
            instance.Input.Flush();
            await Task.Delay(2000);

            string all;
            lock (collected)
            {
                all = collected.ToString();
            }

            _output.WriteLine($"AFTER-INPUT-BYTES={all.Length} containsMarker={all.Contains("__probe__", StringComparison.Ordinal)}");
        }
        finally
        {
            instance.Dispose(); // 关闭输出流以解除读者阻塞
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }

            process?.Dispose();
        }
    }
}
