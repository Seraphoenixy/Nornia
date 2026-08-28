using Dapper;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Storage.Database;

namespace Nornia.Storage.Repositories;

public sealed class ProjectRepository(ISqliteConnectionFactory database) : IProjectRepository, IProjectBatchRepository
{
    public async Task<ProjectAsset?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(SelectByPathSql, new { Path = path }, cancellationToken: cancellationToken));
        return row is null ? null : ToProject(row);
    }

    public async Task<IReadOnlyList<ProjectAsset>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<ProjectRow>(new CommandDefinition("""
            SELECT id AS Id, name AS Name, path AS Path, path_status AS PathStatus, created_at AS CreatedAt,
                   last_checked_at AS LastCheckedAt, last_opened_at AS LastOpenedAt,
                   last_environment_status AS LastEnvironmentStatus
            FROM projects ORDER BY COALESCE(last_opened_at, last_checked_at, created_at) DESC, name;
            """, cancellationToken: cancellationToken));
        return rows.Select(ToProject).ToArray();
    }

    public async Task UpsertAsync(ProjectAsset project, CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO projects (id, name, path, created_at, path_status, last_checked_at, last_opened_at, last_environment_status)
            VALUES ($Id, $Name, $Path, $CreatedAt, $PathStatus, $LastCheckedAt, $LastOpenedAt, $LastEnvironmentStatus)
            ON CONFLICT(path) DO UPDATE SET
                name = excluded.name, path_status = excluded.path_status, last_checked_at = excluded.last_checked_at,
                last_opened_at = excluded.last_opened_at, last_environment_status = excluded.last_environment_status;
            """, new
        {
            Id = project.Id.ToString("N"),
            project.Name,
            project.Path,
            project.CreatedAt,
            PathStatus = (int)project.PathStatus,
            project.LastCheckedAt,
            project.LastOpenedAt,
            LastEnvironmentStatus = (int)project.LastEnvironmentStatus
        }, cancellationToken: cancellationToken));
    }

    public async Task UpsertManyAsync(IReadOnlyCollection<ProjectAsset> projects, CancellationToken cancellationToken = default)
    {
        if (projects.Count == 0) return;

        using var _ = database.TrackOperation();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            INSERT INTO projects (id, name, path, created_at, path_status, last_checked_at, last_opened_at, last_environment_status)
            VALUES ($Id, $Name, $Path, $CreatedAt, $PathStatus, $LastCheckedAt, $LastOpenedAt, $LastEnvironmentStatus)
            ON CONFLICT(path) DO UPDATE SET
                name = excluded.name, path_status = excluded.path_status, last_checked_at = excluded.last_checked_at,
                last_opened_at = excluded.last_opened_at, last_environment_status = excluded.last_environment_status;
            """;
        var parameters = projects.Select(project => new
        {
            Id = project.Id.ToString("N"),
            project.Name,
            project.Path,
            project.CreatedAt,
            PathStatus = (int)project.PathStatus,
            project.LastCheckedAt,
            project.LastOpenedAt,
            LastEnvironmentStatus = (int)project.LastEnvironmentStatus
        }).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM environment_bindings WHERE project_id = $ProjectId;", new { ProjectId = projectId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM environment_profiles WHERE project_id = $ProjectId;", new { ProjectId = projectId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM projects WHERE id = $ProjectId;", new { ProjectId = projectId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private const string SelectByPathSql = """
        SELECT id AS Id, name AS Name, path AS Path, path_status AS PathStatus, created_at AS CreatedAt,
               last_checked_at AS LastCheckedAt, last_opened_at AS LastOpenedAt,
               last_environment_status AS LastEnvironmentStatus
        FROM projects WHERE path = $Path;
        """;

    private static ProjectAsset ToProject(ProjectRow row) => new(
        Guid.Parse(row.Id), row.Name, row.Path, (ProjectPathStatus)row.PathStatus, row.CreatedAt,
        row.LastCheckedAt, row.LastOpenedAt, (EnvironmentHealthStatus)row.LastEnvironmentStatus);

    private sealed class ProjectRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public int PathStatus { get; init; }
        public long CreatedAt { get; init; }
        public long? LastCheckedAt { get; init; }
        public long? LastOpenedAt { get; init; }
        public int LastEnvironmentStatus { get; init; }
    }
}

public sealed class EnvironmentProfileRepository(ISqliteConnectionFactory database) : IEnvironmentProfileRepository
{
    public async Task UpsertAsync(EnvironmentProfileSnapshot profile, CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO environment_profiles (id, project_id, content, updated_at)
            VALUES ($Id, $ProjectId, $Content, $UpdatedAt)
            ON CONFLICT(project_id) DO UPDATE SET content = excluded.content, updated_at = excluded.updated_at;
            """, new
        {
            Id = profile.Id.ToString("N"),
            ProjectId = profile.ProjectId.ToString("N"),
            profile.Content,
            profile.UpdatedAt
        }, cancellationToken: cancellationToken));
    }

    public async Task<EnvironmentProfileSnapshot?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition("""
            SELECT id AS Id, project_id AS ProjectId, content AS Content, updated_at AS UpdatedAt
            FROM environment_profiles WHERE project_id = $ProjectId;
            """, new { ProjectId = projectId.ToString("N") }, cancellationToken: cancellationToken));
        return row is null ? null : new EnvironmentProfileSnapshot(Guid.Parse(row.Id), Guid.Parse(row.ProjectId), row.Content, row.UpdatedAt);
    }

    public async Task ReplaceBindingsAsync(Guid projectId, IReadOnlyCollection<EnvironmentBinding> bindings, CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM environment_bindings WHERE project_id = $ProjectId;", new { ProjectId = projectId.ToString("N") }, transaction, cancellationToken: cancellationToken));
            const string sql = """
                INSERT INTO environment_bindings (id, project_id, runtime_id, component, required_version, created_at)
                VALUES ($Id, $ProjectId, $RuntimeId, $Component, $RequiredVersion, $CreatedAt);
                """;
            foreach (var binding in bindings)
            {
                await connection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    Id = binding.Id.ToString("N"),
                    ProjectId = binding.ProjectId.ToString("N"),
                    RuntimeId = binding.RuntimeId.ToString("N"),
                    binding.Component,
                    binding.RequiredVersion,
                    binding.CreatedAt
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

    public async Task<IReadOnlyList<EnvironmentBinding>> GetBindingsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var _ = database.TrackOperation(); // 在途操作跟踪:退出时 DrainAsync 等待此操作完成。
        await using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<BindingRow>(new CommandDefinition("""
            SELECT id AS Id, project_id AS ProjectId, runtime_id AS RuntimeId, component AS Component,
                   required_version AS RequiredVersion, created_at AS CreatedAt
            FROM environment_bindings WHERE project_id = $ProjectId;
            """, new { ProjectId = projectId.ToString("N") }, cancellationToken: cancellationToken));
        return rows.Select(row => new EnvironmentBinding(Guid.Parse(row.Id), Guid.Parse(row.ProjectId), Guid.Parse(row.RuntimeId), row.Component, row.RequiredVersion, row.CreatedAt)).ToArray();
    }

    private sealed class ProfileRow
    {
        public string Id { get; init; } = string.Empty;
        public string ProjectId { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public long UpdatedAt { get; init; }
    }

    private sealed class BindingRow
    {
        public string Id { get; init; } = string.Empty;
        public string ProjectId { get; init; } = string.Empty;
        public string RuntimeId { get; init; } = string.Empty;
        public string Component { get; init; } = string.Empty;
        public string RequiredVersion { get; init; } = string.Empty;
        public long CreatedAt { get; init; }
    }
}
