using Dapper;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Nornia.Storage.Database;

/// <summary>Creates SQLite connections. Repositories depend on this abstraction instead of the
/// concrete <see cref="NorniaDatabase"/> so database access can be mocked or pointed at any
/// file/:memory: source in tests.</summary>
public interface ISqliteConnectionFactory
{
    SqliteConnection CreateConnection();

    /// <summary>取得一个数据库操作的在途跟踪作用域。生产实现(NorniaDatabase)用它支撑
    /// 退出前的 <see cref="NorniaDatabase.DrainAsync"/>;测试连接来源可返回 no-op 作用域。</summary>
    OperationScope TrackOperation();
}

/// <summary>在途数据库操作的作用域:dispose 时回调完成通知(幂等)。
/// 仓库方法以 <c>using var _ = database.TrackOperation();</c> 覆盖整个操作,
/// 连接池化归还不影响计数,因为计数以“操作”而非“连接”为单位。</summary>
public sealed class OperationScope : IDisposable
{
    private readonly Action? _onFinished;
    private int _disposed;

    /// <summary>No-op 作用域:供无在途跟踪需求的连接来源(如测试工厂)实现接口用。</summary>
    public OperationScope()
    {
    }

    public OperationScope(Action onStarted, Action onFinished)
    {
        _onFinished = onFinished;
        onStarted();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _onFinished?.Invoke();
        }
    }
}

public sealed class NorniaDatabase(string databasePath) : ISqliteConnectionFactory
{
    /// <summary>当前 schema 版本，记录在 SQLite 的 <c>user_version</c> 上。
    /// 提高版本号时，在 <see cref="CreateSchemaAsync"/> 的版本门控块里追加对应的一次性升级语句。</summary>
    public const int SchemaVersion = 1;

    private static readonly bool ProviderInitialized = InitializeProvider();

    public string DatabasePath { get; } = databasePath;

    private static bool InitializeProvider()
    {
        Batteries_V2.Init();
        return true;
    }

    private readonly TaskCompletionSource<bool> _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 在途数据库操作计数与"已排空"信号。计数变更与信号重挂/完成都在 _drainGate 下收敛,
    // 保证 DrainAsync 取到的等待目标必然对应当前(或已完成)的在途操作。
    private int _inFlight;
    private readonly object _drainGate = new();
    private TaskCompletionSource<bool>? _drainedTcs;

    /// <summary>初始化(建库/建表)完成信号。启动时数据库初始化在窗口显示之后后台执行,
    /// 首屏就要读库的调用方(如任务中心)先等待此 Task,避免首启"no such table"。</summary>
    public Task Initialization => _initialization.Task;

    /// <summary>取得一个数据库操作的在途跟踪作用域。仓库方法用它覆盖整个操作生命周期
    /// (从取连接到事务提交/回滚、连接归还),使退出时的 <see cref="DrainAsync"/>
    /// 能等到所有在途操作真正结束,而不是把写了一半的事务强杀回滚。</summary>
    public OperationScope TrackOperation() => new(OperationStarted, OperationFinished);

    internal void OperationStarted()
    {
        Interlocked.Increment(ref _inFlight);
        lock (_drainGate)
        {
            // 存在在途操作但尚无等待信号:建立(或沿用)“已排空”信号。
            _drainedTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void OperationFinished()
    {
        Interlocked.Decrement(ref _inFlight);
        lock (_drainGate)
        {
            if (Volatile.Read(ref _inFlight) == 0 && _drainedTcs is not null)
            {
                _drainedTcs.TrySetResult(true);
                _drainedTcs = null;
            }
        }
    }

    /// <summary>等待所有在途数据库操作完成;返回是否在超时内完成。退出路径用它把最后的
    /// 写入等到落库,再做 <c>SqliteConnection.ClearAllPools()</c>。超时不阻塞退出
    /// (退化为“在途事务整体回滚”的旧行为,仍不损坏数据)。</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task? wait;
        lock (_drainGate)
        {
            wait = _drainedTcs?.Task;
        }
        if (wait is null)
        {
            return true; // 无在途操作。
        }
        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }
        try
        {
            return await Task.WhenAny(wait, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) == wait;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public SqliteConnection CreateConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        ForeignKeys = true
    }.ToString());

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _ = ProviderInitialized;
        using var scope = TrackOperation(); // 建库/建表同样计入在途操作,退出时一并排空。
        try
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("PRAGMA journal_mode = WAL;");
            await connection.ExecuteAsync("PRAGMA foreign_keys = ON;");
            await CreateSchemaAsync(connection, cancellationToken);
        }
        catch (Exception exception)
        {
            // 让等待 Initialization 的首屏读库方同样感知失败(随后 App 会弹错并退出)。
            _initialization.TrySetException(exception);
            throw;
        }
        _initialization.TrySetResult(true);
    }

    /// <summary>当前 schema 的唯一实现:建出最终表结构,并执行一次性的版本化升级。
    /// 供 <see cref="InitializeAsync"/> 与测试(任意连接来源)共用。重复执行安全:
    /// 建表全部 <c>IF NOT EXISTS</c>,破坏性语句只在 <c>user_version</c> 落后时执行一次。</summary>
    public static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        // ① 建表。唯一索引留到一次性升级(②)之后:旧库可能残留同一 path 的多条项目记录,
        //    先建唯一索引会直接抛异常导致应用起不来。
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS runtimes (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, version TEXT NOT NULL, install_path TEXT NOT NULL,
                architecture TEXT NOT NULL, provider TEXT NOT NULL, install_date INTEGER NOT NULL,
                status INTEGER NOT NULL, last_seen_at INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS packages (
                package_key TEXT PRIMARY KEY, id TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
                available_version TEXT, provider TEXT NOT NULL DEFAULT 'winget', architecture TEXT NOT NULL DEFAULT 'Unknown',
                installed_date INTEGER NOT NULL, last_seen_at INTEGER NOT NULL DEFAULT 0, status INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS projects (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL, created_at INTEGER NOT NULL,
                path_status INTEGER NOT NULL DEFAULT 0, last_checked_at INTEGER, last_opened_at INTEGER,
                last_environment_status INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS environment_profiles (
                id TEXT PRIMARY KEY, project_id TEXT NOT NULL, content TEXT NOT NULL, updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS environment_bindings (
                id TEXT PRIMARY KEY, project_id TEXT NOT NULL, runtime_id TEXT NOT NULL,
                component TEXT NOT NULL DEFAULT '', required_version TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS scan_state (
                kind TEXT PRIMARY KEY, scanned_at INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL DEFAULT 0, fingerprint TEXT NOT NULL DEFAULT '');
            """, cancellationToken: cancellationToken));

        // ② 版本化的一次性升级。旧版实现把下面这些破坏性语句放在每次启动都跑的位置 ——
        //    它们没有版本或时间戳门控,一旦将来有人恢复某张日志表,数据会被静默销毁。
        //    现在只在 user_version 落后时执行一次。
        //    注意:不用 schema_migrations 表记录版本 —— 那张表本身就是要被清理的对象。
        var version = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("PRAGMA user_version;", cancellationToken: cancellationToken));
        if (version < SchemaVersion)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                -- 日志类数据已全部移出数据库(操作日志整体移除;环境修复明细改为数据目录下的文件)。
                DROP TABLE IF EXISTS logs;
                DROP TABLE IF EXISTS environment_repair_logs;
                DROP TABLE IF EXISTS schema_migrations;

                -- 唯一索引缺失期间可能积累同一 path 的多条项目记录。保留最近打开的一条,
                -- 否则下面的 CREATE UNIQUE INDEX ux_projects_path 会抛异常。
                DELETE FROM projects WHERE rowid NOT IN (
                    SELECT rowid FROM (
                        SELECT rowid, ROW_NUMBER() OVER (
                            PARTITION BY path
                            ORDER BY COALESCE(last_opened_at, 0) DESC, created_at DESC, rowid ASC) AS rn
                        FROM projects
                    ) WHERE rn = 1);
                """, cancellationToken: cancellationToken));
        }

        // ③ 索引。
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_path ON projects(path);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_environment_profiles_project_id ON environment_profiles(project_id);
            CREATE INDEX IF NOT EXISTS ix_packages_id_architecture ON packages(id, architecture);
            """, cancellationToken: cancellationToken));

        // ④ 抬升版本标记:下次启动不会再执行 ② 里的破坏性语句。
        if (version < SchemaVersion)
        {
            await connection.ExecuteAsync(
                new CommandDefinition($"PRAGMA user_version = {SchemaVersion};", cancellationToken: cancellationToken));
        }
    }
}
