using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Services;
using Nornia.Core.Models;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class DesktopUsabilityTests
{
    [Fact]
    public void DashboardTasks_PrioritizeFirstScanBeforeProjectRegistration()
    {
        var tasks = DashboardTaskPlanner.Create(0, 0, 0, 0, 0, "0 B");
        Assert.Equal("开始扫描本机环境", tasks[0].Title);
        Assert.Equal("登记第一个项目", tasks[1].Title);
    }

    [Fact]
    public void DashboardTasks_PrioritizeIssuesBeforeUpdatesAndCache()
    {
        var tasks = DashboardTaskPlanner.Create(2, 4, 1, 3, 5, "1 GB");
        Assert.Equal(["Projects", "Packages", "Packages"], tasks.Select(task => task.Destination));
    }

    [Fact]
    public void OperationState_ExposesStableShortLogReference()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var state = new OperationState("扫描", id, true);
        Assert.Equal("11111111", state.LogReference);
        Assert.Equal(OperationPhase.Running, state.Phase);
        Assert.True(state.CanCancel);

        state.Progress = 42.5;
        Assert.True(state.HasProgress);
        Assert.Equal("42.5%", state.ProgressDisplay);
    }

    [Theory]
    [InlineData("正在下载 42%", 42)]
    [InlineData("正在下载 42％", 42)]
    [InlineData("Downloading 12.5 %", 12.5)]
    [InlineData("0%\r27%", 27)]
    [InlineData("\u001b]9;4;1;42\u0007", 42)]
    [InlineData("\u001b]9;4;4;87\u001b\\", 87)]
    public void OperationProgressParser_ReadsLatestPercentage(string text, double expected)
    {
        Assert.True(OperationProgressParser.TryGetPercentage(text, out var percentage));
        Assert.Equal(expected, percentage, 3);
    }

    [Fact]
    public void OperationProgressParser_IgnoresNonPercentageOutput()
    {
        Assert.False(OperationProgressParser.TryGetPercentage("正在解析软件包元数据", out _));
        Assert.False(OperationProgressParser.TryGetPercentage("下载失败 120%", out _));
    }

    [Fact]
    public async Task PageOperation_PublishesLastProgressBeforeTerminalState()
    {
        var page = new ProgressPage(new FakeUiLogService());

        await page.RunWithProgressAsync("Downloading 42%");

        Assert.Equal(OperationPhase.Succeeded, page.CurrentOperation?.Phase);
        Assert.Equal(42, page.CurrentOperation?.Progress);
        Assert.Equal("安装软件包完成", page.CurrentOperation?.Detail);
    }

    [Fact]
    public async Task PageOperation_PublishesWingetVirtualTerminalProgress()
    {
        var page = new ProgressPage(new FakeUiLogService());

        await page.RunWithProgressAsync("\u001b]9;4;1;42\u0007");

        Assert.Equal(OperationPhase.Succeeded, page.CurrentOperation?.Phase);
        Assert.Equal(42, page.CurrentOperation?.Progress);
    }

    [Fact]
    public void NavigationService_PreservesDestinationContext()
    {
        var service = new DesktopNavigationService();
        NavigationRequest? received = null;
        service.NavigationRequested += (_, request) => received = request;
        service.Navigate("Packages", new NavigationContext.Updates());
        Assert.Equal(new NavigationRequest("Packages", new NavigationContext.Updates()), received);
    }

    private sealed class ProgressPage(IUiLogService logService) : PageViewModel("测试", logService)
    {
        public Task RunWithProgressAsync(string output) => RunAsync("安装软件包", _ =>
        {
            OperationProgress.Report(new ProcessOutput(output, false));
            return Task.CompletedTask;
        });
    }

}
