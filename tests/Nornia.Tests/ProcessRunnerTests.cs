using System.Text;
using Nornia.Core.Models;
using Nornia.Core.Services;

namespace Nornia.Tests;

public sealed class ProcessRunnerTests
{
    // 生产字段(内部注册 code-pages provider),避免测试类静态字段与 provider 注册的时序竞态。
    private static Encoding Gbk => TextEncodingDetector.Gb18030;

    [Fact]
    public async Task StreamLinesWithTextFallbackAsync_DecodesLegacyGb18030Stdout()
    {
        // git diff 的内容行携带文件原始字节:GBK 文件的 stdout 不再是合法 UTF-8,
        // 固定 UTF-8 解码会整段乱码——回退到 GB18030 后必须还原中文。
        // 三个解码变体保持独立子进程:检测按读块进行,把不同编码的字节流拼进同一次
        // 输出会在分块边界混合,人为触发误回退(已试过合并,不可行)。
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
    public async Task StreamLinesWithTextFallbackAsync_DrainsStdoutLargerThanPipeBuffer()
    {
        // 回归:整段捕获曾"先等退出、后读 stdout"——子进程输出超过 OS 管道缓冲(Windows 数 KB)
        // 时阻塞在写端永不退出,消费方随之永久挂起(大 diff 视图空白即源于此)。此处输出约 1 MB,
        // 必须完整收回;若死锁复现,30 秒取消令牌让测试快速失败而非无限等待。
        // 大输出经预生成临时文件 + cmd type 流出:避免 powershell 冷启(本类耗时主源)。
        const int lineCount = 8000; // 8000 × ~130 字节 ≈ 1 MB,远超管道缓冲
        var spill = Path.Combine(Path.GetTempPath(), $"nornia-drain-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(spill, string.Join("\r\n",
                Enumerable.Range(1, lineCount).Select(i => ("L" + i).PadRight(128, 'x'))) + "\r\n");
            var runner = new ProcessRunner();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var events = new List<Nornia.Core.Models.ProcessStreamEvent>();
            // 不加引号分参传递:cmd /c 对带引号的参数有自己的重解析规则(整条会被误判为
            // 语法错误);Temp 路径无空格,直接作为独立参数最稳。
            await foreach (var item in runner.StreamLinesWithTextFallbackAsync(
                             "cmd.exe", ["/c", "type", spill], 1_000_000, null, cancellation.Token))
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
        finally
        {
            try { File.Delete(spill); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task StreamLinesWithTextFallbackAsync_LineCapKeepsTheAllowedPrefix()
    {
        // The byte capture may read several lines in one 64 KB chunk. Crossing the cap must retain
        // the allowed prefix, not drop that whole chunk. cmd.exe 冷启比 powershell 快一个数量级,
        // 本用例不需要 PS 语法,用 cmd 降低套件耗时。
        var events = new List<Nornia.Core.Models.ProcessStreamEvent>();
        await foreach (var item in new ProcessRunner().StreamLinesWithTextFallbackAsync(
                           "cmd.exe", ["/c", "echo first& echo second& echo third"], maximumLines: 2))
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
    public async Task RunAsync_ReportsProgressAndVirtualTerminalFramesBeforeProcessExits()
    {
        // 两个场景(纯文本 42% 与 winget 的 OSC/VT 进度帧)合并为一次子进程:
        // 进度必须先于进程退出上报(否则 UI 进度条只能在命令结束后闪现)。
        var plainReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vtReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new CallbackProgress(output =>
        {
            if (output.Text.Contains("42%", StringComparison.Ordinal))
            {
                plainReached.TrySetResult(true);
            }

            if (output.Text.Contains("]9;4;1;42", StringComparison.Ordinal))
            {
                vtReached.TrySetResult(true);
            }
        });
        var vtSequence = Encoding.UTF8.GetBytes("\u001b]9;4;1;42\u0007");
        var runner = new ProcessRunner();
        var running = runner.RunAsync(
            "powershell.exe",
            [
                "-NoProfile",
                "-Command",
                RawBytesCommand(Encoding.UTF8.GetBytes("42%")) + "; " + RawBytesCommand(vtSequence) + "; Start-Sleep -Milliseconds 1500"
            ],
            progress);

        Assert.Same(plainReached.Task, await Task.WhenAny(plainReached.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Same(vtReached.Task, await Task.WhenAny(vtReached.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.False(running.IsCompleted);
        await running;
    }

    [Fact]
    public async Task RunAsync_CancellationTerminatesTheStartedProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var runner = new ProcessRunner();

        // ping -n 31 = ~30 秒的长命子进程;cmd 冷启远快于 powershell,取消路径不需要 PS。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            "cmd.exe",
            ["/c", "ping -n 31 127.0.0.1 > nul"],
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_PassesEnvironmentVariablesToTheChildProcess()
    {
        // G4 dependency: git read commands are launched with GIT_OPTIONAL_LOCKS=0 through this
        // seam, so the environment must actually reach the child process.
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            "cmd.exe",
            ["/c", "echo %NORNIA_TEST_ENV_MARKER%"],
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
        // 2000 行 × ~51 字符 ≈ 100 KB,远超 4096 上限;cmd 循环足够。
        var result = await new ProcessRunner().RunAsync(
            "cmd.exe",
            ["/c", "for /L %i in (1,1,2000) do @echo 0123456789012345678901234567890123456789st"],
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
            "cmd.exe",
            ["/c", "echo " + new string('x', 2000)]);

        Assert.True(result.IsSuccess);
        Assert.False(result.OutputTruncated);
        Assert.True(result.StandardOutput.TrimEnd().Length >= 2000);
    }

    [Fact]
    public async Task StopActiveProcesses_TerminatesAnUncancelledProcess()
    {
        var runner = new ProcessRunner();
        var running = runner.RunAsync(
            "cmd.exe",
            ["/c", "ping -n 31 127.0.0.1 > nul"]);

        await Task.Delay(150);
        runner.StopActiveProcesses();

        var completed = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(running, completed);
        await running;
    }

    [Fact]
    public async Task RunBoundedAsync_TerminatesTheChildOnceTheCapIsReached()
    {
        // G11: 有界读取与"截断但继续排空"的区别在于子进程会被终止。病态仓库里 git status 会一直
        // 产出到几十 MB,排空意味着把整段 stdout 读进内存并等它自然结束;终止才是固定成本。
        // 必须 powershell 单进程悬挂:cmd 链式命令会孤儿化孙进程(ping),孤儿持有管道写端,
        // 读端等不到 EOF,终止语义无法验证。
        var sentinel = Path.Combine(Path.GetTempPath(), $"nornia-bounded-{Guid.NewGuid():N}.done");
        var command = "$o=[Console]::OpenStandardOutput(); $b=[Text.Encoding]::ASCII.GetBytes('x'*100); "
            + "for($i=0;$i -lt 4000;$i++){ $o.Write($b,0,100) };"
            + $"Start-Sleep -Seconds 30; [IO.File]::WriteAllText('{sentinel}','done')";
        try
        {
            var running = new ProcessRunner().RunBoundedAsync(
                "powershell.exe", ["-NoProfile", "-Command", command], maximumOutputBytes: 4096);
            var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(20)));

            Assert.Same(running, finished); // 没有被终止的话,这里要等到子进程睡完 30 秒
            var result = await running;
            Assert.True(result.OutputTruncated);
            Assert.True(result.StandardOutput.Length <= 4096,
                $"保留了 {result.StandardOutput.Length} 字符,超过上限。");
            Assert.False(File.Exists(sentinel), "子进程没有被终止:它继续走到了 sleep 之后的写入。");
        }
        finally
        {
            try { File.Delete(sentinel); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RunBoundedAsync_UnderTheCap_StillDrainsToCompletion()
    {
        // 只有真的越界才终止;正常仓库的一次 status 必须原样读完,否则截断标记会变成假阳性。
        var result = await new ProcessRunner().RunBoundedAsync(
            "cmd.exe",
            ["/c", "echo " + new string('y', 2000)],
            maximumOutputBytes: 65536);

        Assert.True(result.IsSuccess);
        Assert.False(result.OutputTruncated);
        Assert.Equal(2000, result.StandardOutput.Trim().Length);
    }

    private sealed class CallbackProgress(Action<ProcessOutput> callback) : IProgress<ProcessOutput>
    {
        public void Report(ProcessOutput value) => callback(value);
    }
}
