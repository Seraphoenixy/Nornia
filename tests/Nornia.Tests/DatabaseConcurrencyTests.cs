using Dapper;
using Nornia.Core.Models;
using Nornia.Storage.Database;
using Nornia.Storage.Repositories;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

public sealed class DatabaseConcurrencyTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-concurrency-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_directory, "nornia.db");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InitializeAsync_ConcurrentInitializationsYieldCompleteSchema()
    {
        var tasks = Enumerable.Range(0, 6).Select(_ => new NorniaDatabase(DatabasePath).InitializeAsync()).ToArray();

        await Task.WhenAll(tasks);

        await using var connection = new NorniaDatabase(DatabasePath).CreateConnection();
        var tables = (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table';")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("runtimes", tables);
        Assert.Contains("packages", tables);
        Assert.Contains("projects", tables);
        Assert.Contains("environment_profiles", tables);
        Assert.Contains("environment_bindings", tables);
        Assert.DoesNotContain("schema_migrations", tables);
    }

    [Fact]
    public async Task RuntimeSnapshots_ConcurrentSameSnapshotWritesRemainConsistent()
    {
        var database = new NorniaDatabase(DatabasePath);
        await database.InitializeAsync();
        var repository = new RuntimeRepository(database);
        var runtimes = Enumerable.Range(0, 10).Select(index => CreateRuntime($"Runtime-{index}", "1.0.0")).ToArray();
        var writes = Enumerable.Range(0, 4)
            .Select(_ => repository.UpsertSnapshotAsync(runtimes, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            .ToArray();

        await Task.WhenAll(writes);

        Assert.Equal(10, (await repository.GetAllAsync()).Count(runtime => runtime.Status == RuntimeStatus.Installed));
    }

    private static CoreRuntime CreateRuntime(string name, string version) =>
        new(Guid.NewGuid(), name, version, $"C:\\{name}", "X64", "Test", 0, RuntimeStatus.Installed);
}