using Microsoft.Data.Sqlite;
using Nornia.Core.Models;
using Nornia.Storage.Database;
using Nornia.Storage.Repositories;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

/// <summary>Demonstrates that repositories depend on <see cref="ISqliteConnectionFactory"/> rather than
/// the concrete database, so any connection source (file, shared in-memory) can be substituted — the
/// seam that makes database operations mockable in tests.</summary>
public sealed class ConnectionFactoryTests
{
    [Fact]
    public async Task Repositories_WorkWithAnyConnectionFactory()
    {
        var factory = new SharedMemoryConnectionFactory();
        // A keeper connection keeps the shared in-memory database alive for the whole test; pooled
        // connections are disabled so other test classes clearing pools cannot invalidate it.
        await using var keeper = factory.CreateConnection();
        await keeper.OpenAsync();
        await InitializeSchemaAsync(factory);

        var runtimeRepository = new RuntimeRepository(factory);
        var runtime = new CoreRuntime(Guid.NewGuid(), ".NET", "10.0.100", "C:\\dotnet", "X64", "Test", 0, RuntimeStatus.Installed);
        await runtimeRepository.UpsertSnapshotAsync([runtime], 100);

        Assert.Equal(".NET", Assert.Single(await runtimeRepository.GetAllAsync()).Name);

        var packageRepository = new PackageRepository(factory);
        await packageRepository.ReplaceSnapshotAsync([new PackageInfo("Git.Git", "Git", "2.55.0", null, "winget", true)], 100);
        Assert.Equal("Git.Git", Assert.Single(await packageRepository.GetAllAsync()).Id);
    }

    private static async Task InitializeSchemaAsync(ISqliteConnectionFactory factory)
    {
        await using var connection = factory.CreateConnection();
        await connection.OpenAsync();
        await NorniaDatabase.CreateSchemaAsync(connection);
    }

    private sealed class SharedMemoryConnectionFactory : ISqliteConnectionFactory
    {
        private readonly string _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"file:memdb{Guid.NewGuid():N}?mode=memory&cache=shared",
            Pooling = false
        }.ToString();

        public SqliteConnection CreateConnection() => new(_connectionString);

        public Nornia.Storage.Database.OperationScope TrackOperation() => new();
    }
}