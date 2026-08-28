using Nornia.Core.Models;
using Nornia.Storage;

namespace Nornia.Tests;

public sealed class EnvironmentRepairLogStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-repair-log-{Guid.NewGuid():N}");
    private string _path => System.IO.Path.Combine(_directory, "environment-repair-logs.jsonl");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AppendAndRead_RoundTripsEntriesOrderedBySequence()
    {
        var store = new EnvironmentRepairLogStore(_path);
        var correlation = Guid.NewGuid();
        var entries = Enumerable.Range(0, 3).Select(index => CreateEntry(correlation, sequence: 3 - index)).ToArray();

        foreach (var entry in entries)
        {
            await store.AppendAsync(entry);
        }

        var persisted = await store.GetByCorrelationAsync(correlation);

        // 与 SQL 版一致:按步骤序号升序返回(回滚引擎再自行逆序执行)。
        Assert.Equal([1, 2, 3], persisted.Select(entry => entry.Sequence));
        Assert.All(persisted, entry => Assert.Equal(correlation, entry.CorrelationId));
        Assert.Equal("1.0.0", persisted[0].PreviousVersion);
        Assert.Equal(RollbackStrategy.Manual, persisted[0].RollbackStrategy);
        Assert.Null(persisted[0].RollbackHint);
    }

    [Fact]
    public async Task GetByCorrelation_WhenNoFile_ReturnsEmpty()
    {
        var store = new EnvironmentRepairLogStore(_path);

        var persisted = await store.GetByCorrelationAsync(Guid.NewGuid());

        Assert.Empty(persisted);
    }

    [Fact]
    public async Task Read_IgnoresCorruptedLines()
    {
        var store = new EnvironmentRepairLogStore(_path);
        var correlation = Guid.NewGuid();
        await store.AppendAsync(CreateEntry(correlation, 1));
        await File.AppendAllLinesAsync(_path, ["{not valid json"]);

        var persisted = await store.GetByCorrelationAsync(correlation);

        Assert.Single(persisted);
    }

    private static EnvironmentRepairLogEntry CreateEntry(Guid correlationId, int sequence) => new(
        Guid.NewGuid(),
        1_700_000_000 + sequence,
        correlationId,
        sequence,
        "Node.js",
        "1.0.0",
        "2.0.0",
        "OpenJS.NodeJS",
        null,
        "winget",
        true,
        1_700_000_000 + sequence,
        sequence == 1 ? RollbackStrategy.Manual : RollbackStrategy.Automatic,
        null,
        null);
}

