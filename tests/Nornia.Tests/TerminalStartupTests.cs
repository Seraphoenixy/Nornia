using Nornia.Desktop.Services;
using Nornia.Desktop.Views.Controls;
using System.IO;

namespace Nornia.Tests;

/// <summary>
/// Regression coverage for the reported "启动 PowerShell 7失败：NullReferenceException" on repeated
/// 新建终端 clicks. The crash lived in the WPF binding layer (Session re-bind with a fallback
/// session whose Screen is null); here we exercise the service + view-model path twice so the
/// session-creation side stays safe in BOTH modes (ConPTY interactive or redirected fallback).
/// Real shell processes are spawned; machines without PowerShell 7 skip.
/// </summary>
public sealed class TerminalStartupTests
{
    [Fact]
    public async Task ConPtyLaunchFailure_ReturnsRetryableFailedSession()
    {
        var service = new TerminalService();
        try
        {
            var session = await service.StartAsync(
                new ShellProfile("missing", "Missing shell", "Z:\\does-not-exist\\shell.exe"),
                Environment.CurrentDirectory);

            Assert.Equal(TerminalSessionState.Failed, session.State);
            Assert.False(session.CanAcceptInput);
            Assert.False(string.IsNullOrWhiteSpace(session.FailureReason));
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartTwice_DoesNotThrowAndKeepsSessionState()
    {
        var pwsh = FindExecutable("pwsh.exe");
        if (pwsh is null)
        {
            return; // No PowerShell 7 on this machine.
        }

        var service = new TerminalService();
        var profile = new ShellProfile("pwsh", "PowerShell 7", pwsh, "-NoLogo");
        try
        {
            var first = await service.StartAsync(profile, Environment.CurrentDirectory);
            Assert.Equal(TerminalSessionState.Running, first.State);

            var second = await service.StartAsync(profile, Environment.CurrentDirectory);
            Assert.Equal(TerminalSessionState.Running, second.State);

            // Mode-specific contract: interactive sessions expose a screen model; fallback sessions
            // (ConPTY unavailable) simply have none — but both must keep the session alive.
            if (first.IsInteractive)
            {
                Assert.NotNull(first.Screen);
                Assert.Null(first.StartupWarning);
            }

            if (second.IsInteractive)
            {
                Assert.NotNull(second.Screen);
            }
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task InteractiveSession_InputRoundTrip_ReachesScreen()
    {
        // 回归:ConPTY 的输入读端句柄曾被提前 Dispose,键盘输入通道当场 EOF——外壳提示符
        // 照常显示但所有键入被丢弃。这里把命令写入会话并等待回显出现在屏幕模型上,
        // 直接验证输入管道在启动后仍然存活。
        var pwsh = FindExecutable("pwsh.exe") ?? FindExecutable("powershell.exe");
        if (pwsh is null)
        {
            return; // No PowerShell on this machine.
        }

        var service = new TerminalService();
        var profile = new ShellProfile("shell", "PowerShell", pwsh, "-NoLogo");
        try
        {
            var session = await service.StartAsync(profile, Environment.CurrentDirectory);
            if (!session.IsInteractive)
            {
                return; // ConPTY 不可用(回退模式无屏幕),该机器上无法验证输入回环。
            }

            Assert.Null(session.StartupWarning);
            Assert.NotNull(session.Screen);

            // 等待外壳输出提示符(输出管道打通)。
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(session.Screen!.ToPlainText()), TimeSpan.FromSeconds(20));

            const string marker = "__nornia_input_alive__";
            session.WriteTextAsync($"echo {marker}\r");

            var echoed = await WaitUntilAsync(
                () => session.Screen!.ToPlainText().Contains(marker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(20));
            Assert.True(echoed, "写入终端的命令未在屏幕模型中回显——输入管道在启动后失效。");
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task InteractiveHistoryNavigation_ReplacesLongCurrentDraft()
    {
        var pwsh = FindExecutable("pwsh.exe") ?? FindExecutable("powershell.exe");
        if (pwsh is null) return;

        var service = new TerminalService();
        var profile = new ShellProfile("shell-history", "PowerShell", pwsh, "-NoLogo");
        try
        {
            var session = await service.StartAsync(profile, Environment.CurrentDirectory);
            if (!session.IsInteractive) return;

            Assert.NotNull(session.Screen);
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(session.Screen!.ToPlainText()), TimeSpan.FromSeconds(20));

            const string historyCommand = "Write-Output __nornia_history_short__";
            const string longerHistoryCommand = "Write-Output __nornia_longer_history_item_with_tail_marker__";
            const string longerTail = "tail_marker";
            const string draft = "this_is_a_much_longer_unsubmitted_terminal_command_draft";
            const string draftTail = "terminal_command_draft";

            // 先执行"更长"的历史项再执行"更短"的:之后 Up→Up→Down 时,短项会覆盖长项的
            // 行尾区域,是 ConPTY 差分发出 ECH(行中段擦除)的真实场景。
            session.WriteTextAsync(longerHistoryCommand + "\r");
            Assert.True(await WaitUntilAsync(
                () => session.Screen!.ToPlainText().Contains("__nornia_longer_history_item", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20)));

            session.WriteTextAsync(historyCommand + "\r");
            Assert.True(await WaitUntilAsync(
                () => session.Screen!.ToPlainText().Contains("__nornia_history_short__", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20)));

            await Task.Delay(1500);
            session.WriteTextAsync(draft);
            Assert.True(await WaitUntilAsync(
                () => session.Screen!.ToPlainText().Replace("\n", string.Empty, StringComparison.Ordinal)
                    .Contains(draft, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)), session.Screen!.ToPlainText());

            // 纯透传(不加清行前缀):PSReadLine 原生用历史项整行替换当前草稿,并在按 Down 时
            // 恢复草稿——历史枚举状态不被打断,连续 Up 可逐条回溯。
            session.WriteTextAsync(TerminalSurfaceControl.ArrowUpSequence);
            Assert.True(await WaitUntilAsync(() =>
            {
                var output = session.Screen!.ToPlainText().Replace("\n", string.Empty, StringComparison.Ordinal);
                var prompt = output.LastIndexOf("PS ", StringComparison.Ordinal);
                var activeLine = prompt >= 0 ? output[prompt..] : output;
                return activeLine.Contains(historyCommand, StringComparison.Ordinal)
                    && !activeLine.Contains(draft, StringComparison.Ordinal)
                    && !activeLine.Contains(draftTail, StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(10)), "上箭头未用历史命令完整替换当前长草稿。");

            // 连续翻阅:Up 到更长历史项,再 Down 回到更短项——短项替换长项后行尾不得残留
            // 长项尾部(依赖 ECH/EL 擦除链路完整)。
            session.WriteTextAsync(TerminalSurfaceControl.ArrowUpSequence);
            Assert.True(await WaitUntilAsync(() =>
            {
                var output = session.Screen!.ToPlainText().Replace("\n", string.Empty, StringComparison.Ordinal);
                var prompt = output.LastIndexOf("PS ", StringComparison.Ordinal);
                var activeLine = prompt >= 0 ? output[prompt..] : output;
                return activeLine.Contains("__nornia_longer_history_item", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(10)), "连续上箭头未回溯到更早的历史项。");

            session.WriteTextAsync(TerminalSurfaceControl.ArrowDownSequence);
            Assert.True(await WaitUntilAsync(() =>
            {
                var output = session.Screen!.ToPlainText().Replace("\n", string.Empty, StringComparison.Ordinal);
                var prompt = output.LastIndexOf("PS ", StringComparison.Ordinal);
                var activeLine = prompt >= 0 ? output[prompt..] : output;
                return activeLine.Contains(historyCommand, StringComparison.Ordinal)
                    && !activeLine.Contains(longerTail, StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(10)), "下箭头回到短历史项后,长历史项尾部文字残留在行尾。");
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task InteractiveRapidTyping_ExecutesDotnetBuildWithoutReorderingEnter()
    {
        var pwsh = FindExecutable("pwsh.exe") ?? FindExecutable("powershell.exe");
        if (pwsh is null) return;

        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "Nornia.Core", "Nornia.Core.csproj"));
        if (!File.Exists(project)) return;

        var service = new TerminalService();
        var profile = new ShellProfile("shell-dotnet-build", "PowerShell", pwsh, "-NoLogo");
        try
        {
            var session = await service.StartAsync(profile, Environment.CurrentDirectory);
            if (!session.IsInteractive) return;

            Assert.NotNull(session.Screen);
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(session.Screen!.ToPlainText()),
                TimeSpan.FromSeconds(20));

            var outputPath = Path.Combine(Path.GetTempPath(), "nornia-terminal-build", Guid.NewGuid().ToString("N"));
            var command = $"dotnet build \"{project}\" --no-restore --nologo -o \"{outputPath}\"; Write-Output __nornia_dotnet_build_done_$LASTEXITCODE";
            foreach (var character in command)
            {
                session.WriteTextAsync(character.ToString());
            }

            session.WriteTextAsync("\r");

            Assert.True(await WaitUntilAsync(
                () => session.Screen!.ToPlainText().Contains("__nornia_dotnet_build_done_0", StringComparison.Ordinal),
                TimeSpan.FromSeconds(40)), session.Screen!.ToPlainText());
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task RedirectedOrInteractive_InputRoundTrip_ReachesScreen()
    {
        // 端到端契约:无论 ConPTY 可用(交互式)还是在受限环境判定失败回退(重定向),
        // 写入会话的命令都必须经 stdin→进程→stdout 回流到屏幕模型(echo 的输出而非回显)。
        var pwsh = FindExecutable("pwsh.exe") ?? FindExecutable("powershell.exe");
        if (pwsh is null)
        {
            return;
        }

        var service = new TerminalService();
        var profile = new ShellProfile("shell", "PowerShell", pwsh, "-NoLogo");
        try
        {
            var session = await service.StartAsync(profile, Environment.CurrentDirectory);
            Assert.NotNull(session.Screen);   // 交互式与重定向回退都带屏
            Assert.Equal(TerminalSessionState.Running, session.State);

            const string marker = "__nornia_fb_alive__";
            session.WriteTextAsync($"echo {marker}\r");

            var echoed = await WaitUntilAsync(
                () => session.Screen!.ToPlainText().Contains(marker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(25));
            Assert.True(echoed, "写入终端的命令输出未回流到屏幕模型——输入或输出管道失效。");
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return condition();
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
