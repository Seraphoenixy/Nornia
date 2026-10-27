using Dapper;
using Microsoft.Data.Sqlite;
using Nornia.Storage.Database;

namespace Nornia.Tests;

/// <summary>schema 创建/升级的一次性语义。这里的用例锁住两个真实隐患:
/// ① 旧实现每次启动都无条件 <c>DROP TABLE logs / environment_repair_logs / schema_migrations</c>,
///    没有版本门控 —— 将来一旦有人恢复某张日志表,数据会被静默销毁;
/// ② <c>CreateSchemaAsync</c> 注释声称「对旧库幂等」,但 <c>CREATE UNIQUE INDEX ux_projects_path</c>
///    在有重复 path 的旧库上会直接抛异常,让应用起不来。</summary>
public sealed class SchemaMigrationTests
{
    [Fact]
    public async Task CreateSchemaAsync_CanBeCalledRepeatedly()
    {
        await using var harness = await SchemaHarness.CreateAsync();

        // 第一次在 CreateAsync 里已执行;这里再跑两次,必须不抛。
        await harness.ExecuteCreateSchemaAsync();
        await harness.ExecuteCreateSchemaAsync();

        Assert.Equal(NorniaDatabase.SchemaVersion, await harness.GetUserVersionAsync());
    }

    [Fact]
    public async Task LegacyLogTables_AreDroppedOnceAndNotThereafter()
    {
        await using var harness = await SchemaHarness.CreateAsync(seedLegacyLogTable: true);

        // 首次执行:版本落后 → 一次性清理,废弃表被移除。
        await harness.ExecuteCreateSchemaAsync();
        Assert.False(await harness.TableExistsAsync("logs"));
        Assert.Equal(NorniaDatabase.SchemaVersion, await harness.GetUserVersionAsync());

        // 再次造一张同名的表,模拟「未来有人恢复了日志表」。版本已抬升,升级块不再执行,
        // 这张表必须原样保留 —— 这正是版本门控要防的静默数据销毁。
        await harness.ExecuteAsync("CREATE TABLE logs (id TEXT PRIMARY KEY, message TEXT);");
        await harness.ExecuteCreateSchemaAsync();

        Assert.True(await harness.TableExistsAsync("logs"));
    }

    [Fact]
    public async Task LegacyProjectsWithDuplicatePaths_AreDeduplicatedBeforeTheUniqueIndexIsCreated()
    {
        await using         var harness = await SchemaHarness.CreateAsync();
        // 造出「唯一索引缺失期间」的历史数据:同一路径两条记录(重复的那条更早打开)。
        // 先把本轮建出来的唯一索引摘掉,否则重复 path 根本插不进去 —— 正是这个索引缺失的
        // 历史窗口才会积累重复数据。
        await harness.ExecuteAsync("DROP INDEX IF EXISTS ux_projects_path;");
        await harness.ExecuteAsync("""
            INSERT INTO projects (id, name, path, created_at, last_opened_at) VALUES
                ('p1', 'dup', 'C:\legacy', 10, 100),
                ('p2', 'dup', 'C:\legacy', 20, 300),
                ('p3', 'other', 'C:\other', 30, 50);
            """);
        // 把版本退回 0,模拟尚未升级过的旧库。
        await harness.ExecuteAsync("PRAGMA user_version = 0;");

        await harness.ExecuteCreateSchemaAsync();

        // 去重保留最近打开的一条,且唯一索引确实建起来了。
        var remaining = (await harness.QueryAsync<ProjectRow>(
            "SELECT id AS Id, last_opened_at AS OpenedAt FROM projects ORDER BY path;")).ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.Contains(remaining, row => row.Id == "p2" && row.OpenedAt == 300);
        Assert.True(await harness.IndexExistsAsync("ux_projects_path"));
    }

    private sealed record ProjectRow(string Id, long OpenedAt);

    private sealed class SchemaHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _keeper;

        private SchemaHarness(SqliteConnection keeper) => _keeper = keeper;

        public static async Task<SchemaHarness> CreateAsync(bool seedLegacyLogTable = false)
        {
            // 共享内存库需要一条 keep-alive 连接;关闭连接池以免其它测试清池把它带走。
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"file:schema{Guid.NewGuid():N}?mode=memory&cache=shared",
                Pooling = false
            }.ToString();
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            var harness = new SchemaHarness(keeper);
            await harness.ExecuteCreateSchemaAsync();
            if (seedLegacyLogTable)
            {
                await harness.ExecuteAsync("CREATE TABLE logs (id TEXT PRIMARY KEY, message TEXT);");
                await harness.ExecuteAsync("PRAGMA user_version = 0;");
            }

            return harness;
        }

        public async Task ExecuteCreateSchemaAsync()
        {
            await NorniaDatabase.CreateSchemaAsync(_keeper);
        }

        public async Task ExecuteAsync(string sql) => await _keeper.ExecuteAsync(sql);

        public async Task<long> GetUserVersionAsync() =>
            await _keeper.ExecuteScalarAsync<long>("PRAGMA user_version;");

        public async Task<bool> TableExistsAsync(string name) =>
            await _keeper.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;",
                new { name }) > 0;

        public async Task<bool> IndexExistsAsync(string name) =>
            await _keeper.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name;",
                new { name }) > 0;

        public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql) =>
            (await _keeper.QueryAsync<T>(sql)).AsList();

        public async ValueTask DisposeAsync() => await _keeper.DisposeAsync();
    }
}
