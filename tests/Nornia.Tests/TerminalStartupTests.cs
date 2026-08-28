using Nornia.Desktop.Services;
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
