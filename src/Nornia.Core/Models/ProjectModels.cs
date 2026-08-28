namespace Nornia.Core.Models;

public sealed record ProjectAsset(
    Guid Id,
    string Name,
    string Path,
    ProjectPathStatus PathStatus,
    long CreatedAt,
    long? LastCheckedAt,
    long? LastOpenedAt,
    EnvironmentHealthStatus LastEnvironmentStatus);

public enum ProjectPathStatus
{
    Available,
    Missing
}

public enum EnvironmentHealthStatus
{
    Unknown,
    Healthy,
    NeedsAttention
}

public sealed record EnvironmentBinding(Guid Id, Guid ProjectId, Guid RuntimeId, string Component, string RequiredVersion, long CreatedAt);

public sealed record EnvironmentProfileSnapshot(Guid Id, Guid ProjectId, string Content, long UpdatedAt);
