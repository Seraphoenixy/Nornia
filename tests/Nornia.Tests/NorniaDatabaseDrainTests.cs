using Nornia.Storage.Database;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class NorniaDatabaseDrainTests
{
    private static NorniaDatabase CreateDatabase() =>
        new(TestTempRoot.NewFile("drain", ".db"));

    [Fact]
    public async Task DrainAsync_WhenIdle_CompletesImmediately()
    {
        var database = CreateDatabase();

        var completed = await database.DrainAsync(TimeSpan.FromSeconds(1));

        Assert.True(completed);
    }

    [Fact]
    public async Task DrainAsync_WaitsForInFlightOperationsToFinish()
    {
        var database = CreateDatabase();
        using var scope = database.TrackOperation();
        var releaser = Task.Delay(300).ContinueWith(_ => scope.Dispose());
        var startedAt = Environment.TickCount64;

        var completed = await database.DrainAsync(TimeSpan.FromSeconds(5));

        await releaser;
        Assert.True(completed);
        // 确实等到了在途操作释放,而不是立即返回。
        Assert.True(Environment.TickCount64 - startedAt >= 250);
    }

    [Fact]
    public async Task DrainAsync_ReturnsFalseOnTimeoutThenRecovers()
    {
        var database = CreateDatabase();
        using var scope = database.TrackOperation();
        var releaser = Task.Delay(1500).ContinueWith(_ => scope.Dispose());

        var completed = await database.DrainAsync(TimeSpan.FromMilliseconds(200));

        Assert.False(completed);
        await releaser;
        // 在途操作结束后,再次排空立即成功。
        Assert.True(await database.DrainAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task DrainAsync_HandlesOverlappingOperations()
    {
        var database = CreateDatabase();
        using var first = database.TrackOperation();
        using var second = database.TrackOperation();
        var releaser = Task.Delay(300).ContinueWith(_ =>
        {
            first.Dispose();
            second.Dispose();
        });

        var completed = await database.DrainAsync(TimeSpan.FromSeconds(5));

        await releaser;
        Assert.True(completed);
    }
}
