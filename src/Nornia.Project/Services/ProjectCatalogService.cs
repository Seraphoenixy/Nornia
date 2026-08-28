using CoreRuntime = Nornia.Core.Models.Runtime;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Project.Models;

namespace Nornia.Project.Services;

public interface IProjectCatalogService
{
    Task<ProjectAsset> RegisterAsync(
        string projectPath,
        EnvironmentProfile? profile = null,
        IReadOnlyCollection<EnvironmentCheckResult>? checkResults = null,
        IReadOnlyCollection<Runtime>? runtimes = null,
        bool opened = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectAsset>> GetAllAsync(CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class ProjectCatalogService(
    IProjectRepository projectRepository,
    IEnvironmentProfileRepository profileRepository,
    EnvironmentProfileService profileService) : IProjectCatalogService
{
    public async Task<ProjectAsset> RegisterAsync(
        string projectPath,
        EnvironmentProfile? profile = null,
        IReadOnlyCollection<EnvironmentCheckResult>? checkResults = null,
        IReadOnlyCollection<CoreRuntime>? runtimes = null,
        bool opened = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath);
        var existing = await projectRepository.GetByPathAsync(normalizedPath, cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var status = checkResults is null
            ? existing?.LastEnvironmentStatus ?? EnvironmentHealthStatus.Unknown
            : checkResults.All(result => result.Status == EnvironmentCheckStatus.Pass)
                ? EnvironmentHealthStatus.Healthy
                : EnvironmentHealthStatus.NeedsAttention;
        var project = new ProjectAsset(
            existing?.Id ?? Guid.NewGuid(),
            profile?.Project.Name ?? existing?.Name ?? new DirectoryInfo(normalizedPath).Name,
            normalizedPath,
            Directory.Exists(normalizedPath) ? ProjectPathStatus.Available : ProjectPathStatus.Missing,
            existing?.CreatedAt ?? now,
            checkResults is null ? existing?.LastCheckedAt : now,
            opened ? now : existing?.LastOpenedAt,
            status);

        await projectRepository.UpsertAsync(project, cancellationToken);
        if (profile is not null)
        {
            await profileRepository.UpsertAsync(
                new EnvironmentProfileSnapshot(Guid.NewGuid(), project.Id, profileService.Serialize(profile), now),
                cancellationToken);
        }

        if (checkResults is not null && runtimes is not null)
        {
            var bindings = checkResults
                .Where(result => result.Status == EnvironmentCheckStatus.Pass && result.InstalledVersion is not null)
                .Select(result =>
                {
                    var runtime = runtimes.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, result.Component, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.Version, result.InstalledVersion, StringComparison.OrdinalIgnoreCase));
                    return runtime is null
                        ? null
                        : new EnvironmentBinding(Guid.NewGuid(), project.Id, runtime.Id, result.Component, result.RequiredVersion, now);
                })
                .Where(binding => binding is not null)
                .Cast<EnvironmentBinding>()
                .ToArray();
            await profileRepository.ReplaceBindingsAsync(project.Id, bindings, cancellationToken);
        }

        return project;
    }

    public async Task<IReadOnlyList<ProjectAsset>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var projects = await projectRepository.GetAllAsync(cancellationToken);
        var refreshed = new List<ProjectAsset>(projects.Count);
        var changed = new List<ProjectAsset>();
        foreach (var project in projects)
        {
            var pathStatus = Directory.Exists(project.Path) ? ProjectPathStatus.Available : ProjectPathStatus.Missing;
            var updated = project with { PathStatus = pathStatus };
            if (updated != project)
            {
                changed.Add(updated);
            }

            refreshed.Add(updated);
        }

        if (changed.Count > 0)
        {
            if (projectRepository is IProjectBatchRepository batchRepository)
                await batchRepository.UpsertManyAsync(changed, cancellationToken);
            else
                foreach (var project in changed)
                    await projectRepository.UpsertAsync(project, cancellationToken);
        }

        return refreshed;
    }

    public Task RemoveAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        projectRepository.RemoveAsync(projectId, cancellationToken);
}
