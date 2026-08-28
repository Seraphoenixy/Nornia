using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Services;

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

}
