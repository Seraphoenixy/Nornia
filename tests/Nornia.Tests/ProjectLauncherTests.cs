using Nornia.Core.Models;
using Nornia.Project.Services;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class ProjectLauncherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-launcher-{Guid.NewGuid():N}");

    [Fact]
    public async Task OpenAsync_UsesConfiguredEditorAndPassesProjectAsSingleArgument()
    {
        Directory.CreateDirectory(_directory);
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty));
        var launcher = new ProjectLauncher(runner);

        await launcher.OpenAsync(_directory, "custom-editor.exe");

        var call = Assert.Single(runner.Calls);
        Assert.Equal("custom-editor.exe", call.FileName);
        Assert.Equal([Path.GetFullPath(_directory)], call.Arguments);
    }

    [Fact]
    public async Task OpenAsync_ReportsConfiguredEditorFailure()
    {
        Directory.CreateDirectory(_directory);
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(1, string.Empty, "not found"));
        var launcher = new ProjectLauncher(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.OpenAsync(_directory, "missing-editor.exe"));

        Assert.Contains("missing-editor.exe", error.Message);
        Assert.Contains("not found", error.Message);
    }

    [Fact]
    public async Task OpenAsync_AcceptsAnExistingFileForExternalEditor()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "development.md");
        await File.WriteAllTextAsync(file, "# Development");
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty));
        var launcher = new ProjectLauncher(runner);

        await launcher.OpenAsync(file, "custom-editor.exe");

        var call = Assert.Single(runner.Calls);
        Assert.Equal([Path.GetFullPath(file)], call.Arguments);
    }

    [Fact]
    public async Task OpenAsync_ReportsMissingPathClearly()
    {
        var missing = Path.Combine(_directory, "development.md");
        var launcher = new ProjectLauncher(new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty)));

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => launcher.OpenAsync(missing));

        Assert.Contains("Path", error.Message);
        Assert.Equal(Path.GetFullPath(missing), error.FileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
