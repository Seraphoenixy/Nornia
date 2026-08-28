using Nornia.Core.Models;

namespace Nornia.Core.Interfaces;

public interface IRuntimeRepository
{
    /// <summary>Upserts the scanned snapshot: existing rows keep their identity, rows no longer
    /// discovered are marked <see cref="RuntimeStatus.Missing"/> instead of being deleted.</summary>
    Task UpsertSnapshotAsync(IReadOnlyCollection<Runtime> runtimes, long scannedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Runtime>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IPackageRepository
{
    Task ReplaceSnapshotAsync(IReadOnlyCollection<PackageInfo> packages, long scannedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PackageInfo>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IProjectRepository
{
    Task<ProjectAsset?> GetByPathAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectAsset>> GetAllAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(ProjectAsset project, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>Optional batch capability for repositories that can publish a catalog snapshot in one
/// database operation. Older repository implementations remain valid through IProjectRepository.</summary>
public interface IProjectBatchRepository
{
    Task UpsertManyAsync(IReadOnlyCollection<ProjectAsset> projects, CancellationToken cancellationToken = default);
}

public interface IEnvironmentProfileRepository
{
    Task UpsertAsync(EnvironmentProfileSnapshot profile, CancellationToken cancellationToken = default);
    Task<EnvironmentProfileSnapshot?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task ReplaceBindingsAsync(Guid projectId, IReadOnlyCollection<EnvironmentBinding> bindings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EnvironmentBinding>> GetBindingsAsync(Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>Reads the aggregate counters the Dashboard shows, replacing several separate full-table
/// scans with one SQL statement.</summary>
public interface IDashboardSummaryReader
{
    Task<DashboardSummary> GetAsync(CancellationToken cancellationToken = default);
}
