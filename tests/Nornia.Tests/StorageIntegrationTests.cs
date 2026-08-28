using Dapper;
using Microsoft.Data.Sqlite;
using Nornia.Core.Models;
using Nornia.Project.Models;
using Nornia.Project.Services;
using Nornia.Storage.Database;
using Nornia.Storage.Repositories;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

public sealed class StorageIntegrationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-storage-{Guid.NewGuid():N}");
    private NorniaDatabase _database = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new NorniaDatabase(Path.Combine(_directory, "nornia.db"));
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InitializeAsync_IsRepeatableAndLeavesFullSchema()
    {
        await _database.InitializeAsync();
        await using var connection = _database.CreateConnection();
        var tables = (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table';")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("runtimes", tables);
        Assert.Contains("packages", tables);
        Assert.Contains("projects", tables);
        Assert.Contains("environment_profiles", tables);
        Assert.Contains("environment_bindings", tables);
        // 日志类表已不再属于 schema(旧库上的残留会被清理)。
        Assert.DoesNotContain("logs", tables);
        Assert.DoesNotContain("environment_repair_logs", tables);
    }

    [Fact]
    public async Task RuntimeSnapshot_MarksUndiscoveredRuntimeMissing()
    {
        var repository = new RuntimeRepository(_database);
        var runtime = CreateRuntime(".NET", "10.0.100");
        await repository.UpsertSnapshotAsync([runtime], 100);
        await repository.UpsertSnapshotAsync([], 200);

        var persisted = Assert.Single(await repository.GetAllAsync());
        Assert.Equal(RuntimeStatus.Missing, persisted.Status);
    }

    [Fact]
    public async Task PackageSnapshot_StoresAvailableVersionAndMarksRemovedPackageUnavailable()
    {
        var repository = new PackageRepository(_database);
        var package = new PackageInfo("Git.Git", "Git", "2.55.0", "2.56.0", "winget", true);

        await repository.ReplaceSnapshotAsync([package], 100);
        await repository.ReplaceSnapshotAsync([], 200);

        var persisted = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("2.56.0", persisted.AvailableVersion);
        Assert.False(persisted.IsInstalled);
    }

    [Fact]
    public async Task PackageSnapshot_KeepsSamePackageIdForDifferentArchitectures()
    {
        var repository = new PackageRepository(_database);
        await repository.ReplaceSnapshotAsync(
        [
            new PackageInfo("Microsoft.VCRedist.2015+", "Visual C++", "14.51", null, "winget", true, "X64"),
            new PackageInfo("Microsoft.VCRedist.2015+", "Visual C++", "14.51", null, "winget", true, "X86")
        ], 100);

        var packages = await repository.GetAllAsync();
        Assert.Equal(2, packages.Count);
        Assert.Contains(packages, package => package.Architecture == "X64");
        Assert.Contains(packages, package => package.Architecture == "X86");
    }

    [Fact]
    public async Task ProjectCatalog_PersistsProfileAndPassingRuntimeBinding()
    {
        var projectPath = Path.Combine(_directory, "Ygdria");
        Directory.CreateDirectory(projectPath);
        var projectRepository = new ProjectRepository(_database);
        var profileRepository = new EnvironmentProfileRepository(_database);
        var profileService = new EnvironmentProfileService();
        var catalog = new ProjectCatalogService(projectRepository, profileRepository, profileService);
        var profile = new EnvironmentProfile
        {
            Project = new ProjectMetadata { Name = "Ygdria" },
            Runtime = new Dictionary<string, VersionRequirement> { ["dotnet"] = new() { Version = "10" } }
        };
        var runtime = CreateRuntime(".NET", "10.0.100");
        var results = new[] { new EnvironmentCheckResult(EnvironmentCheckStatus.Pass, ".NET", "10", "10.0.100", ".NET 10 installed") };

        var project = await catalog.RegisterAsync(projectPath, profile, results, [runtime]);

        Assert.Equal(EnvironmentHealthStatus.Healthy, project.LastEnvironmentStatus);
        Assert.NotNull(await profileRepository.GetByProjectIdAsync(project.Id));
        var binding = Assert.Single(await profileRepository.GetBindingsAsync(project.Id));
        Assert.Equal(runtime.Id, binding.RuntimeId);
    }

    [Fact]
    public async Task ProjectCatalog_RetainsMissingProjectUntilExplicitlyRemoved()
    {
        var projectPath = Path.Combine(_directory, "retained-project");
        Directory.CreateDirectory(projectPath);
        var projectRepository = new ProjectRepository(_database);
        var catalog = new ProjectCatalogService(projectRepository, new EnvironmentProfileRepository(_database), new EnvironmentProfileService());
        var registered = await catalog.RegisterAsync(projectPath);
        Directory.Delete(projectPath);

        var missing = Assert.Single(await catalog.GetAllAsync());
        Assert.Equal(ProjectPathStatus.Missing, missing.PathStatus);

        await catalog.RemoveAsync(registered.Id);
        Assert.Empty(await catalog.GetAllAsync());
    }

    private static CoreRuntime CreateRuntime(string name, string version) =>
        new(Guid.NewGuid(), name, version, $"C:\\{name}", "X64", "Test", 0, RuntimeStatus.Installed);
}
