using Nornia.Core.Models;
using Nornia.Project.Models;
using Nornia.Project.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

public sealed class EnvironmentProfileTests
{
    [Fact]
    public async Task Profile_RoundTripsNorniaYaml()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nornia-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var service = new EnvironmentProfileService();
            var path = await service.InitializeAsync(directory, "Ygdria");
            var profile = await service.LoadAsync(path);

            Assert.Equal("Ygdria", profile.Project.Name);
            Assert.Equal(EnvironmentProfileService.FileName, Path.GetFileName(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_MigratesKnownComponentsToTheirCorrectCategories()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nornia-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, EnvironmentProfileService.FileName);
            await File.WriteAllTextAsync(path, """
                project:
                  name: Ygdria
                runtime:
                  dotnet:
                    version: '10'
                  node:
                    version: '22'
                tools:
                  git:
                    version: '2.50'
                  python:
                    version: '3.13'
                """);

            var profile = await new EnvironmentProfileService().LoadAsync(path);

            Assert.Empty(profile.Runtime);
            Assert.Equal("22", profile.Tools["node"].Version);
            Assert.Equal("3.13", profile.Tools["python"].Version);
            Assert.Equal("10", profile.Tools["dotnet"].Version);
            Assert.Equal("2.50", profile.Tools["git"].Version);
            var migratedYaml = await File.ReadAllTextAsync(path);
            Assert.Contains("tools:", migratedYaml);
            Assert.Contains("runtime:", migratedYaml);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_PreservesCorrectCategoryWhenComponentIsDuplicated()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nornia-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, EnvironmentProfileService.FileName);
            await File.WriteAllTextAsync(path, """
                project:
                  name: Ygdria
                runtime:
                  git:
                    version: '1.0'
                tools:
                  git:
                    version: '2.50'
                """);

            var profile = await new EnvironmentProfileService().LoadAsync(path);

            Assert.Empty(profile.Runtime);
            Assert.Equal("2.50", profile.Tools["git"].Version);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAndImportAsync_RoundTripsPortableEnvironmentDeclaration()
    {
        var source = Path.Combine(Path.GetTempPath(), $"nornia-tests-{Guid.NewGuid():N}");
        var destination = Path.Combine(Path.GetTempPath(), $"nornia-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            var service = new EnvironmentProfileService();
            await service.InitializeAsync(source, "Portable");
            var profile = await service.LoadAsync(source);
            profile.Project.StartCommand = "dotnet run";
            profile.Architectures.Add("X64");
            profile.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            await service.SaveAsync(Path.Combine(source, EnvironmentProfileService.FileName), profile);
            var exportPath = Path.Combine(source, "Nornia.snapshot.yaml");

            await service.ExportAsync(source, exportPath);
            await service.ImportAsync(exportPath, destination);

            var imported = await service.LoadAsync(destination);
            Assert.Equal("dotnet run", imported.Project.StartCommand);
            Assert.Equal(["X64"], imported.Architectures);
            Assert.Equal("1", imported.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void CheckEngine_ReportsPassWarningAndFail()
    {
        var profile = new EnvironmentProfile
        {
            Project = new ProjectMetadata { Name = "Ygdria" },
            Tools = new Dictionary<string, VersionRequirement>
            {
                ["dotnet"] = new() { Version = "10" },
                ["node"] = new() { Version = "22" },
                ["python"] = new() { Version = "3.13" }
            }
        };
        CoreRuntime[] installed =
        [
            CreateRuntime(".NET", "10.0.100"),
            CreateRuntime("Node.js", "20.18.0")
        ];

        var results = new EnvironmentCheckEngine().Check(profile, installed);

        Assert.Contains(results, result => result.Component == ".NET SDK" && result.Status == EnvironmentCheckStatus.Pass);
        Assert.Contains(results, result => result.Component == "Node.js" && result.Status == EnvironmentCheckStatus.Warning);
        Assert.Contains(results, result => result.Component == "Python" && result.Status == EnvironmentCheckStatus.Fail);
    }

    [Fact]
    public void CheckEngine_DoesNotTreatPreviewAsStableVersionMatch()
    {
        var profile = new EnvironmentProfile
        {
            Project = new ProjectMetadata { Name = "Ygdria" },
            Tools = new Dictionary<string, VersionRequirement> { ["dotnet"] = new() { Version = "10" } }
        };

        var results = new EnvironmentCheckEngine().Check(profile, [CreateRuntime(".NET", "10.0.100-preview.1")]);

        Assert.Equal(EnvironmentCheckStatus.Warning, Assert.Single(results).Status);
    }

    private static CoreRuntime CreateRuntime(string name, string version) =>
        new(Guid.NewGuid(), name, version, $"C:\\{name}", "X64", "Test", 0, RuntimeStatus.Installed);
}
