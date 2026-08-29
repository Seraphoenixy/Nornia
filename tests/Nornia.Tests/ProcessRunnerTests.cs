using System.Text;
using Nornia.Core.Services;

namespace Nornia.Tests;

public sealed class ProcessRunnerTests
{
    // 生产字段(内部注册 code-pages provider),避免测试类静态字段与 provider 注册的时序竞态。
    private static Encoding Gbk => TextEncodingDetector.Gb18030;

    /// <summary>Makes the child emit the exact raw bytes (no console re-encoding) via base64.</summary>
    private static string RawBytesCommand(byte[] bytes) =>
        $"$b=[Convert]::FromBase64String('{Convert.ToBase64String(bytes)}'); $o=[Console]::OpenStandardOutput(); $o.Write($b,0,$b.Length); $o.Flush()";

    private static async Task<List<Nornia.Core.Models.ProcessStreamEvent>> CollectAsync(ProcessRunner runner, string command)
    {
        var events = new List<Nornia.Core.Models.ProcessStreamEvent>();
        await foreach (var item in runner.StreamLinesWithTextFallbackAsync(
                         "powershell.exe", ["-NoProfile", "-Command", command]))
        {
            events.Add(item);
        }

        return events;
    }

    [Fact]
    public async Task StreamLinesWithTextFallbackAsync_DecodesLegacyGb18030Stdout()
    {
        // git diff 的内容行携带文件原始字节:GBK 文件的 stdout 不再是合法 UTF-8,
        // 固定 UTF-8 解码会整段乱码——回退到 GB18030 后必须还原中文。
        var events = await CollectAsync(new ProcessRunner(), RawBytesCommand(Gbk.GetBytes("中文内容测试：乱码回归\n第二行。\n")));

        var lines = events.Where(e => e.Text is not null).Select(e => e.Text).ToList();
        Assert.Contains("中文内容测试：乱码回归", lines);
        Assert.Contains("第二行。", lines);
    }

    [Fact]
    public async Task StreamLinesWithTextFallbackAsync_KeepsUtf8Stdout()
    {
        var events = await CollectAsync(new ProcessRunner(), RawBytesCommand(new UTF8Encoding(false).GetBytes("UTF-8 中文保持不变\n第二行。\n")));

        var lines = events.Where(e => e.Text is not null).Select(e => e.Text).ToList();
        Assert.Contains("UTF-8 中文保持不变", lines);
        Assert.Contains("第二行。", lines);
    }

    [Fact]
    public async Task StreamLinesWithTextFallbackAsync_DetectsLegacyBytesAfterAsciiHead()
    {
        // 模拟真实 diff 输出:ASCII 头(diff --git / index / @@ 行)之后才是 GBK 内容——
        // 整段校验不得只看头部就放行 UTF-8。
        var output = "diff --git a/notes.md b/notes.md\nindex 1234567..89abcde 100644\n--- a/notes.md\n+++ b/notes.md\n@@ -1 +1 @@\n";
        var bytes = new UTF8Encoding(false).GetBytes(output).Concat(Gbk.GetBytes("+中文内容。\n")).ToArray();
        var events = await CollectAsync(new ProcessRunner(), RawBytesCommand(bytes));

        var lines = events.Where(e => e.Text is not null).Select(e => e.Text).ToList();
        Assert.Contains("diff --git a/notes.md b/notes.md", lines);
        Assert.Contains("+中文内容。", lines);
    }

    [Fact]
    public async Task StreamLinesWithTextFallbackAsync_DrainsStdoutLargerThanPipeBuffer()
    {
        // 回归:整段捕获曾"先等退出、后读 stdout"——子进程输出超过 OS 管道缓冲(Windows 数 KB)
        // 时阻塞在写端永不退出,消费方随之永久挂起(大 diff 视图空白即源于此)。此处输出约 1 MB,
        // 必须完整收回;若死锁复现,30 秒取消令牌让测试快速失败而非无限等待。
        const int lineCount = 8000; // 8000 × 128 字符 ≈ 1 MB,远超管道缓冲
        var command = "$o=[Console]::OpenStandardOutput(); $w=New-Object System.IO.StreamWriter($o); " +
                      "1..8000 | ForEach-Object { $w.WriteLine(('L' + $_).PadRight(128, 'x')) }; $w.Flush()";
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var events = new List<Nornia.Core.Models.ProcessStreamEvent>();
        await foreach (var item in runner.StreamLinesWithTextFallbackAsync(
                         "powershell.exe", ["-NoProfile", "-Command", command], 1_000_000, null, cancellation.Token))
        {
            events.Add(item);
        }

        var lines = events.Where(e => e.Text is not null).Select(e => e.Text).ToList();
        Assert.Equal(lineCount, lines.Count);
        Assert.StartsWith("L1", lines[0]);
        Assert.StartsWith("L8000", lines[^1]);
        Assert.True(events[^1].IsCompleted);
        Assert.Equal(0, events[^1].ExitCode);
    }

    [Fact]
    public async Task StreamLinesWithTextFallbackAsync_LineCapKeepsTheAllowedPrefix()
    {
        // The byte capture may read several lines in one 64 KB chunk. Crossing the cap must retain
        // the allowed prefix, not drop that whole chunk.
        var events = new List<Nornia.Core.Models.ProcessStreamEvent>();
        await foreach (var item in new ProcessRunner().StreamLinesWithTextFallbackAsync(
                           "powershell.exe", ["-NoProfile", "-Command", "'first'; 'second'; 'third'"], maximumLines: 2))
        {
            events.Add(item);
        }

        Assert.Equal(["first", "second"], events.Where(item => item.Text is not null).Select(item => item.Text!).ToArray());
        Assert.True(events[^1].OutputLimitReached);
    }

    [Fact]
    public async Task RunAsync_DecodesUtf8StandardOutput()
    {
        const string expected = "正在下载软件包";
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            "powershell.exe",
            [
                "-NoProfile",
                "-Command",
                "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); Write-Output '正在下载软件包'"
            ]);

        Assert.True(result.IsSuccess);
        Assert.Contains(expected, result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_CancellationTerminatesTheStartedProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var runner = new ProcessRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_PassesEnvironmentVariablesToTheChildProcess()
    {
        // G4 dependency: git read commands are launched with GIT_OPTIONAL_LOCKS=0 through this
        // seam, so the environment must actually reach the child process.
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-Command", "Write-Output $env:NORNIA_TEST_ENV_MARKER"],
            environmentVariables: new Dictionary<string, string> { ["NORNIA_TEST_ENV_MARKER"] = "locks-off" });

        Assert.True(result.IsSuccess);
        Assert.Equal("locks-off", result.StandardOutput.Trim());
        Assert.False(result.OutputTruncated);
    }

    [Fact]
    public async Task RunAsync_MaximumOutputBytes_TruncatesExcessOutputAndKeepsReading()
    {
        // G2 base capability: output beyond the cap is dropped (OutputTruncated) but the child
        // must never be left blocked on a full pipe — the call completes with the capped prefix.
        var result = await new ProcessRunner().RunAsync(
            "powershell.exe",
            ["-NoProfile", "-Command", "Write-Output ('x' * 100000)"],
            maximumOutputBytes: 4096);

        Assert.True(result.IsSuccess);
        Assert.True(result.OutputTruncated);
        var kept = result.StandardOutput.TrimEnd('\r', '\n');
        Assert.True(kept.Length <= 4096, $"expected <= 4096 chars, got {kept.Length}");
        Assert.True(kept.Length >= 4000, $"expected ~4096 chars kept, got {kept.Length}");
    }

    [Fact]
    public async Task RunAsync_WithoutCap_StillBuffersCompleteOutput()
    {
        var result = await new ProcessRunner().RunAsync(
            "powershell.exe",
            ["-NoProfile", "-Command", "Write-Output ('x' * 2000)"]);

        Assert.True(result.IsSuccess);
        Assert.False(result.OutputTruncated);
        Assert.True(result.StandardOutput.TrimEnd().Length >= 2000);
    }

    [Fact]
    public async Task StopActiveProcesses_TerminatesAnUncancelledProcess()
    {
        var runner = new ProcessRunner();
        var running = runner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"]);

        await Task.Delay(150);
        runner.StopActiveProcesses();

        var completed = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(running, completed);
        await running;
    }
}
