using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Localization;
using Nornia.Package.Providers;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Behavioral hardening tests for the winget provider: no-match exit codes, structured
/// failure exceptions, and probe observation wiring.</summary>
public sealed class WingetProviderHardeningTests
{
    private const string EnglishListOutput = """
        Name              Id                    Version  Source
        -------------------------------------------------------
        Git               Git.Git               2.50.0    winget
        """;

    /// <summary>复现 winget 交互式进度被重定向捕获后的脏输出:转轴帧(\r 重绘)、大段空格、
    /// 夹杂真实的警告与结论行。</summary>
    private static string SpinnerPollutedOutput =>
        "- \r   \r\\\r|\r/\r-\r\\\r|\r/\r" + new string(' ', 240) + "\r\n" +
        "搜索源时失败;结果将不包括在内： msstore\r\n" +
        "找不到与输入条件匹配的已安装程序包。\r\n";

    [Fact]
    public async Task SearchAsync_NoMatchExitCodeReturnsEmptyList()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(WingetExitCodes.NoMatch, "找不到与输入条件匹配的程序包。", ""));
        var provider = new WingetProvider(runner);

        var packages = await provider.SearchAsync("nothing");

        Assert.Empty(packages);
    }

    [Fact]
    public async Task SearchAsync_GenuineFailureThrowsWingetExceptionWithHexExitCode()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(1, "", "boom"));
        var provider = new WingetProvider(runner);

        var exception = await Assert.ThrowsAsync<WingetException>(() => provider.SearchAsync("git"));

        Assert.Equal(1, exception.ExitCode);
        Assert.Equal("search", exception.CommandName);
        Assert.Contains("boom", exception.OutputDetail);
        Assert.Contains("0x00000001", exception.Message);
    }

    [Fact]
    public async Task UninstallAsync_NoMatch_KeepsMeaningfulLinesDropsSpinnerNoiseAndHints()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(WingetExitCodes.NoMatch, SpinnerPollutedOutput, ""));
        var provider = new WingetProvider(runner);

        var exception = await Assert.ThrowsAsync<WingetException>(() => provider.UninstallAsync("Some.App"));

        Assert.Equal(WingetExitCodes.NoMatch, exception.ExitCode);
        Assert.Equal("uninstall", exception.CommandName);
        // 转轴帧与空白填充被清洗,真实警告/结论行保留,并追加“未匹配已安装项”的提示。
        // (详情多行间是 Environment.NewLine 换行,勿断言整体不含 \r)
        Assert.DoesNotContain("|", exception.OutputDetail);
        Assert.DoesNotContain("\\", exception.OutputDetail);
        Assert.Contains("搜索源时失败", exception.OutputDetail);
        Assert.Contains("找不到与输入条件匹配的已安装程序包", exception.OutputDetail);
        Assert.Contains(WingetText.Get("Winget_UninstallNoMatchHint"), exception.Message);
    }

    [Fact]
    public async Task MutationFailure_OtherExitCode_SanitizesDetailWithoutUninstallHint()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(1, SpinnerPollutedOutput, ""));
        var provider = new WingetProvider(runner);

        var exception = await Assert.ThrowsAsync<WingetException>(() => provider.UninstallAsync("Some.App"));

        Assert.Equal(1, exception.ExitCode);
        Assert.DoesNotContain("|", exception.OutputDetail);
        Assert.DoesNotContain("\\", exception.OutputDetail);
        Assert.Contains("找不到与输入条件匹配的已安装程序包", exception.OutputDetail);
        Assert.DoesNotContain(WingetText.Get("Winget_UninstallNoMatchHint"), exception.Message);
    }

    [Fact]
    public async Task UpgradeAsync_InstallerCancellationIsNotRetriedAndExplainsUacAction()
    {
        const string cancelled = "你已取消安装。\r\n安装程序失败，退出代码为: 1602";
        var runner = new FakeProcessRunner((_, _) =>
            new ProcessResult(WingetExitCodes.InstallerFailed, cancelled, ""));
        var provider = new WingetProvider(runner);

        var exception = await Assert.ThrowsAsync<WingetException>(() => provider.UpgradeAsync("Microsoft.DotNet.SDK.10"));

        Assert.True(exception.IsInstallerCancelled);
        Assert.Single(runner.Calls);
        Assert.Contains(WingetText.Get("Winget_InstallerCancelledHint"), exception.Message);
    }

    [Fact]
    public async Task UninstallAsync_IdNoMatchFallsBackToNameForLocalEntries()
    {
        // 本机安装(ARP)的应用不在 winget 目录中,--id 精确匹配得到 0x8A150014;
        // 提供显示名时应自动改用 --name 重试,由 winget 调起系统卸载程序。
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("--id")
                ? new ProcessResult(WingetExitCodes.NoMatch, "找不到与输入条件匹配的已安装程序包。", "")
                : new ProcessResult(0, "已成功卸载", ""));
        var provider = new WingetProvider(runner);
        var progress = new CollectingProgress();

        await provider.UninstallAsync("ARP\\Machine\\X64\\Firewall", "Firewall App", progress);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains("--id", runner.Calls[0].Arguments);
        Assert.Contains("ARP\\Machine\\X64\\Firewall", runner.Calls[0].Arguments);
        Assert.Contains("--name", runner.Calls[1].Arguments);
        Assert.Contains("Firewall App", runner.Calls[1].Arguments);
        // 回退提示是预期内的正常路径,不得标记为错误行(否则成功卸载也会在输出面板留下 ERROR)。
        Assert.Contains(new ProcessOutput(WingetText.Get("Winget_UninstallByNameFallback"), false), progress.Outputs);
    }

    /// <summary>复现 .NET SDK 这类版本族:同一 Id 安装多个 feature band,不带 --version 的卸载
    /// 返回 0x8A150016。应查询已安装版本,逐个 --version 精确卸载。</summary>
    [Fact]
    public async Task UninstallAsync_MultipleInstalled_UninstallsEachVersionById()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Count > 0 && arguments[0] == "uninstall")
            {
                // 初始按 Id 精确卸载匹配到多个版本;带 --version 的逐个卸载成功。
                return arguments.Contains("--version")
                    ? new ProcessResult(0, "已成功卸载", "")
                    : new ProcessResult(WingetExitCodes.MultipleInstalledPackagesMatched, "已安装此包的多个版本。", "");
            }

            if (arguments.Count > 0 && arguments[0] == "list" && arguments.Contains("--id"))
            {
                return new ProcessResult(0, MultiVersionListOutput, "");
            }

            return new ProcessResult(0, "", ""); // 能力探测(--version / list --help)
        });
        var provider = new WingetProvider(runner);
        var progress = new CollectingProgress();

        await provider.UninstallAsync("Microsoft.DotNet.SDK.8", null, progress);

        var versioned = runner.Calls
            .Where(call => call.Arguments.Contains("uninstall") && call.Arguments.Contains("--version"))
            .ToArray();
        Assert.Equal(2, versioned.Length);
        Assert.Contains(versioned, call => call.Arguments.Contains("8.0.100"));
        Assert.Contains(versioned, call => call.Arguments.Contains("8.0.404"));
        // 回退提示与按版本卸载进度都走 stdout,不得标记为错误行。
        Assert.Contains(new ProcessOutput(WingetText.Get("Winget_UninstallMultipleFallback"), false), progress.Outputs);
        Assert.Contains(new ProcessOutput(WingetText.Format("Winget_UninstallByVersion", "Microsoft.DotNet.SDK.8", "8.0.100"), false), progress.Outputs);
    }

    [Fact]
    public async Task UninstallAsync_MultipleInstalled_VersionQueryFailureRethrowsOriginalError()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Count > 0 && arguments[0] == "uninstall")
            {
                return new ProcessResult(WingetExitCodes.MultipleInstalledPackagesMatched, "已安装此包的多个版本。", "");
            }

            // 查询已安装版本失败(list 非零退出):不得用内部错误掩盖原始的 0x8A150016。
            return new ProcessResult(1, "", "list boom");
        });
        var provider = new WingetProvider(runner);

        var exception = await Assert.ThrowsAsync<WingetException>(() => provider.UninstallAsync("Microsoft.DotNet.SDK.8"));

        Assert.Equal(WingetExitCodes.MultipleInstalledPackagesMatched, exception.ExitCode);
        Assert.Equal("uninstall", exception.CommandName);
    }

    [Fact]
    public async Task UninstallAsync_MultipleInstalled_ContinuesWhenOneVersionAlreadyGone()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Count > 0 && arguments[0] == "uninstall")
            {
                if (arguments.Contains("--version"))
                {
                    // 8.0.100 已在逐个卸载时被移除,不应中断后续版本的卸载。
                    return arguments.Contains("8.0.100")
                        ? new ProcessResult(WingetExitCodes.NoMatch, "找不到与输入条件匹配的已安装程序包。", "")
                        : new ProcessResult(0, "已成功卸载", "");
                }

                return new ProcessResult(WingetExitCodes.MultipleInstalledPackagesMatched, "已安装此包的多个版本。", "");
            }

            if (arguments.Count > 0 && arguments[0] == "list" && arguments.Contains("--id"))
            {
                return new ProcessResult(0, MultiVersionListOutput, "");
            }

            return new ProcessResult(0, "", "");
        });
        var provider = new WingetProvider(runner);

        await provider.UninstallAsync("Microsoft.DotNet.SDK.8");

        Assert.Contains(runner.Calls, call =>
            call.Arguments.Contains("uninstall") && call.Arguments.Contains("8.0.404"));
    }

    private const string MultiVersionListOutput = """
        Name                 Id                     Version  Source
        ---------------------------------------------------------------------
        .NET SDK 8.0.100     Microsoft.DotNet.SDK.8  8.0.100  winget
        .NET SDK 8.0.404     Microsoft.DotNet.SDK.8  8.0.404  winget
        """;

    private sealed class CollectingProgress : IProgress<ProcessOutput>
    {
        public List<ProcessOutput> Outputs { get; } = [];

        public void Report(ProcessOutput value) => Outputs.Add(value);
    }

    [Fact]
    public async Task Probe_RecordsVersionLanguageAndObservedNoMatchCodeFromRealCalls()
    {
        var runner = new FakeProcessRunner((_, arguments) => arguments switch
        {
            [var first, ..] when first == "--version" => new ProcessResult(0, "Windows Package Manager v1.10.340", ""),
            ["list", "--help"] => new ProcessResult(0, "list - shows installed packages", ""),
            [var first, ..] when first == "search" => new ProcessResult(WingetExitCodes.NoMatch, "找不到与输入条件匹配的程序包。", ""),
            _ => new ProcessResult(0, EnglishListOutput, ""),
        });
        var probe = new WingetProbe(runner);
        var provider = new WingetProvider(runner, probe);

        Assert.Empty(await provider.SearchAsync("nothing"));
        var list = await provider.ListInstalledAsync();
        Assert.NotEmpty(list);

        var capabilities = await probe.GetCapabilitiesAsync();
        Assert.Equal(new Version(1, 10, 340), capabilities.Version);
        Assert.False(capabilities.HasStructuredOutput);
        Assert.Contains(WingetExitCodes.NoMatch, capabilities.NoMatchObservedCodes);
        Assert.Equal("en", capabilities.OutputLanguage);
    }
}
