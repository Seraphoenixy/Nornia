using Nornia.Core.Models;
using Nornia.Desktop.Services;
using Nornia.Project.Models;
using Nornia.Project.Services;
using Nornia.Tests.Fakes;
using NSubstitute;

namespace Nornia.Tests;

/// <summary>工作区上下文的发布必须并发。各订阅者是同一上下文的独立投影,而顺序 await 让整个工作台
/// 一起等最慢的那个:源代码管理订阅者要读整棵索引的 <c>git status</c>(与跟踪文件数成正比,大仓库
/// 是秒级),资源管理器订阅者只需要枚举一个目录,却被排在它后面——用户看到的就是"打开这个目录很慢"。
/// 失败语义不变:任何订阅者失败都不阻止其余订阅者推进,调用方仍拿到单个异常或聚合异常以便回滚。</summary>
public sealed class ProjectWorkspaceServiceTests : IDisposable
{
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), $"nornia-workspace-{Guid.NewGuid():N}");

    public ProjectWorkspaceServiceTests() => Directory.CreateDirectory(_workspacePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp workspace directory.
        }
    }

    private ProjectWorkspaceService Create()
    {
        var catalog = Substitute.For<IProjectCatalogService>();
        catalog.RegisterAsync(
                Arg.Any<string>(),
                Arg.Any<EnvironmentProfile?>(),
                Arg.Any<IReadOnlyCollection<EnvironmentCheckResult>?>(),
                Arg.Any<IReadOnlyCollection<Nornia.Core.Models.Runtime>?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new ProjectAsset(
                Guid.NewGuid(),
                "Test",
                call.Arg<string>(),
                ProjectPathStatus.Available,
                0,
                0,
                0,
                EnvironmentHealthStatus.Unknown));
        return new ProjectWorkspaceService(catalog, new FakeSettingsService(), new FakeApplicationStateStore());
    }

    [Fact]
    public async Task ActivateAsync_DoesNotSerialiseSubscribers_SoASlowOneCannotDelayTheOthers()
    {
        var service = Create();
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        service.ContextChanged += async _ =>
        {
            order.Add("slow");
            slowStarted.SetResult();
            await releaseSlow.Task; // 模拟"读整棵索引的 git status"
        };
        service.ContextChanged += _ =>
        {
            order.Add("fast");
            fastCompleted.SetResult();
            return Task.CompletedTask;
        };

        var activation = service.ActivateAsync(_workspacePath);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // 慢订阅者仍被阻塞时,快订阅者必须已经跑完:顺序发布下它只能排在慢订阅者之后。
        await fastCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(activation.IsCompleted, "激活不应在慢订阅者完成前返回");
        Assert.Equal(["slow", "fast"], order);

        releaseSlow.SetResult();
        await activation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(_workspacePath, service.Current?.ProjectPath);
    }

    [Fact]
    public async Task ActivateAsync_SingleHandlerFailure_IsRethrownAndEverySubscriberStillRuns()
    {
        var service = Create();
        var ran = 0;
        service.ContextChanged += _ =>
        {
            Interlocked.Increment(ref ran);
            throw new InvalidOperationException("slow page failed");
        };
        service.ContextChanged += _ =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ActivateAsync(_workspacePath));

        Assert.Equal("slow page failed", exception.Message);
        // 两次发布:失败的那次(两个订阅者都跑到了)+ 回滚到上一个上下文的那次(再次都跑到)。
        Assert.Equal(4, ran);
        // 回滚到"没有工作区"是这里的正确结果:激活前 Current 就是 null。
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task ActivateAsync_MultipleHandlerFailures_AreAggregated()
    {
        var service = Create();
        service.ContextChanged += _ => throw new InvalidOperationException("first");
        service.ContextChanged += _ => throw new InvalidOperationException("second");

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => service.ActivateAsync(_workspacePath));

        Assert.Equal(2, exception.InnerExceptions.Count);
    }
}
