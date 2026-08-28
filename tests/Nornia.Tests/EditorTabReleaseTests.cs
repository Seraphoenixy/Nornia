using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Closing editor tabs releases their large payloads (decoded text, search matches, diff
/// rows) so the managed heap is reclaimed immediately instead of lingering in stale references.</summary>
public sealed class EditorTabReleaseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-release-{Guid.NewGuid():N}");

    public EditorTabReleaseTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task ClosingPreviewTab_FreesContentAndSearchState()
    {
        var editor = new EditorAreaViewModel(new FakeGitService(), new FakeUiLogService());
        var file = Path.Combine(_tempDir, "a.cs");
        await File.WriteAllTextAsync(file, "class A { }");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        tab.SearchText = "class";
        Assert.NotEmpty(tab.Content);
        Assert.Equal(1, tab.MatchCount);

        editor.CloseTabCommand.Execute(tab);

        Assert.Empty(editor.OpenTabs);
        Assert.Equal(string.Empty, tab.Content);
        Assert.Empty(tab.SearchMatches);
        Assert.Empty(tab.OutlineEntries);
    }

    [Fact]
    public async Task ClosingDiffTab_ClearsRows()
    {
        var diff = new GitFileDiff("a.cs", null, false, false, false,
        [
            new GitDiffHunk(1, 1, 1, 1, "@@ -1 +1 @@",
            [
                new GitDiffLine(GitDiffLineKind.Added, null, 1, "x"),
            ])
        ]);
        var editor = new EditorAreaViewModel(new FakeGitService { DiffResult = diff }, new FakeUiLogService());
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "a.cs", false, false));
        var tab = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.NotEmpty(tab.DiffLines);

        editor.CloseTabCommand.Execute(tab);

        Assert.Empty(tab.DiffLines);
        Assert.Empty(tab.SideBySideRows);
        Assert.Empty(tab.SelectedDiffLines);
    }

    [Fact]
    public async Task ClosingAllTabs_ReleasesEveryTab()
    {
        var editor = new EditorAreaViewModel(new FakeGitService(), new FakeUiLogService());
        var file = Path.Combine(_tempDir, "b.txt");
        await File.WriteAllTextAsync(file, "hello");
        await editor.OpenFileAsync(file);
        Assert.Single(editor.OpenTabs);

        editor.CloseAllTabsCommand.Execute(null);

        Assert.Empty(editor.OpenTabs);
        Assert.Null(editor.SelectedTab);
    }
}