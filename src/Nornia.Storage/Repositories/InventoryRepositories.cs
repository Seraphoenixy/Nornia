using Dapper;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Storage.Database;

namespace Nornia.Storage.Repositories;

public sealed class RuntimeRepository(ISqliteConnectionFactory database) : IRuntimeRepository
{
    public async Task UpsertSnapshotAsync(IReadOnlyCollection<Runtime> runtimes, long scannedAt, CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE runtimes SET status = $missingStatus WHERE status <> $missingStatus;",
                new { missingStatus = (int)RuntimeStatus.Missing }, transaction, cancellationToken: cancellationToken));

            const string sql = """
                INSERT INTO runtimes (id, name, version, install_path, architecture, provider, install_date, status, last_seen_at)
                VALUES ($Id, $Name, $Version, $InstallPath, $Architecture, $Provider, $InstallDate, $Status, $LastSeenAt)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, version = excluded.version, install_path = excluded.install_path,
                    architecture = excluded.architecture, provider = excluded.provider, install_date = excluded.install_date,
                    status = excluded.status, last_seen_at = excluded.last_seen_at;
                """;
            foreach (var runtime in runtimes)
            {
                await connection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    Id = runtime.Id.ToString("N"),
                    runtime.Name,
                    runtime.Version,
                    runtime.InstallPath,
                    runtime.Architecture,
                    runtime.Provider,
                    runtime.InstallDate,
                    Status = (int)runtime.Status,
                    LastSeenAt = scannedAt
                }, transaction, cancellationToken: cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<Runtime>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<RuntimeRow>(new CommandDefinition("""
            SELECT id AS Id, name AS Name, version AS Version, install_path AS InstallPath, architecture AS Architecture,
                   provider AS Provider, install_date AS InstallDate, status AS Status
            FROM runtimes ORDER BY name, version DESC;
            """, cancellationToken: cancellationToken));
        return rows.Select(row => new Runtime(Guid.Parse(row.Id), row.Name, row.Version, row.InstallPath, row.Architecture,
            row.Provider, row.InstallDate, (RuntimeStatus)row.Status)).ToArray();
    }

    private sealed class RuntimeRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string InstallPath { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public long InstallDate { get; init; }
        public int Status { get; init; }
    }
}

public sealed class PackageRepository(ISqliteConnectionFactory database) : IPackageRepository
{
    public async Task ReplaceSnapshotAsync(IReadOnlyCollection<PackageInfo> packages, long scannedAt, CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("UPDATE packages SET status = 0 WHERE status <> 0;", transaction: transaction, cancellationToken: cancellationToken));
            const string sql = """
                INSERT INTO packages (package_key, id, name, version, available_version, provider, architecture, installed_date, last_seen_at, status)
                VALUES ($PackageKey, $Id, $Name, $Version, $AvailableVersion, $Provider, $Architecture, $InstalledDate, $LastSeenAt, $Status)
                ON CONFLICT(package_key) DO UPDATE SET
                    name = excluded.name, version = excluded.version, available_version = excluded.available_version,
                    provider = excluded.provider, architecture = excluded.architecture, last_seen_at = excluded.last_seen_at, status = excluded.status;
                """;
            foreach (var package in packages)
            {
                await connection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    package.Id,
                    package.Name,
                    package.Version,
                    AvailableVersion = package.AvailableVersion,
                    package.Provider,
                    package.Architecture,
                    PackageKey = CreatePackageKey(package),
                    InstalledDate = scannedAt,
                    LastSeenAt = scannedAt,
                    Status = package.IsInstalled ? 1 : 0
                }, transaction, cancellationToken: cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<PackageInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<PackageRow>(new CommandDefinition("""
            SELECT id AS Id, name AS Name, version AS Version, available_version AS AvailableVersion,
                   provider AS Provider, architecture AS Architecture, status AS Status
            FROM packages ORDER BY name;
            """, cancellationToken: cancellationToken));
        return rows.Select(row => new PackageInfo(row.Id, row.Name, row.Version, row.AvailableVersion, row.Provider, row.Status != 0, row.Architecture)).ToArray();
    }

    private static string CreatePackageKey(PackageInfo package) => $"{package.Id}\u001F{package.Provider}\u001F{package.Architecture}".ToLowerInvariant();

    private sealed class PackageRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string? AvailableVersion { get; init; }
        public string Provider { get; init; } = string.Empty;
        public string Architecture { get; init; } = "Unknown";
        public int Status { get; init; }
    }
}

/// <summary>Computes the dashboard counters with one aggregated query instead of separate scans of the
/// projects and packages tables. Also returns a short list of projects needing attention so metric
/// cards can navigate straight to the relevant project instead of the page root.</summary>
public sealed class SummaryRepository(ISqliteConnectionFactory database) : IDashboardSummaryReader
{
    public async Task<DashboardSummary> GetAsync(CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT
                (SELECT COUNT(*) FROM projects) AS ProjectCount,
                (SELECT COUNT(*) FROM projects WHERE last_environment_status = $AttentionStatus) AS IssueCount,
                (SELECT COUNT(*) FROM packages
                    WHERE status = 1 AND available_version IS NOT NULL AND available_version <> '') AS UpdateCount;

            SELECT id AS Id, name AS Name, path AS Path, last_environment_status AS Status
            FROM projects
            WHERE last_environment_status = $AttentionStatus
            ORDER BY last_checked_at DESC NULLS LAST, created_at DESC
            LIMIT 5;
            """, new { AttentionStatus = (int)EnvironmentHealthStatus.NeedsAttention }, cancellationToken: cancellationToken));

        var row = await grid.ReadSingleAsync<SummaryRow>();
        var references = (await grid.ReadAsync<ProjectReferenceRow>())
            .Select(proj => new DashboardProjectReference(
                Guid.Parse(proj.Id),
                proj.Name,
                proj.Path,
                (EnvironmentHealthStatus)proj.Status))
            .ToArray();

        return new DashboardSummary(row.ProjectCount, row.IssueCount, row.UpdateCount)
        {
            ProjectsNeedingAttention = references
        };
    }

    private sealed class SummaryRow
    {
        public int ProjectCount { get; init; }
        public int IssueCount { get; init; }
        public int UpdateCount { get; init; }
    }

    private sealed class ProjectReferenceRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public int Status { get; init; }
    }
}