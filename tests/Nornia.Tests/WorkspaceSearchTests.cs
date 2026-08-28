using System.Text;
using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class WorkspaceSearchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nornia-workspace-search-{Guid.NewGuid():N}");

    public WorkspaceSearchServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SearchTreeNode_FormatsOneBasedLineAndColumnWithoutRepeatingPath()
    {
        var match = new WorkspaceSearchMatch(12, 8, 3, "value = needle;");
        var node = new SearchTreeNode(SearchTreeNodeKind.Match, "ignored", "src/sample.cs", match,
            Path.Combine(_root, "src", "sample.cs"), parent: null, depth: 1, isListResult: true);

        Assert.Equal("12:8", node.LocationLabel);
        Assert.Equal("12:8", node.DisplayName);
        Assert.Equal($"src/sample.cs:12:8\nvalue = needle;", node.ToolTipText);
    }

    [Fact]
    public async Task SearchAsync_FindsTextAcrossFilesAndRespectsGitIgnore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "generated"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "generated/\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "sample.cs"), "Alpha\nalpha Alpha\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "generated", "ignored.cs"), "Alpha\n");

        var service = new WorkspaceSearchService(new FakeSettingsService());
        var results = await CollectAsync(service.SearchAsync(new(
            _root, "alpha", new TextSearchOptions(false, false, false))));

        var file = Assert.Single(results);
        Assert.Equal("src/sample.cs", file.RelativePath);
        Assert.Equal(3, file.Matches.Count);
        Assert.Equal(1, file.Matches[0].Line);
    }

    [Fact]
    public async Task SearchAsync_SupportsRegexWholeWordAndIncludeExclude()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "one.cs"), "cat catalog\nCAT\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "two.txt"), "cat\n");

        var service = new WorkspaceSearchService(new FakeSettingsService());
        var results = await CollectAsync(service.SearchAsync(new(
            _root, "cat", new TextSearchOptions(false, true, false), "*.cs", "")));

        var file = Assert.Single(results);
        Assert.Equal("one.cs", file.RelativePath);
        Assert.Equal(2, file.Matches.Count);

        results = await CollectAsync(service.SearchAsync(new(
            _root, "C\\w{2}", new TextSearchOptions(true, false, true), "*.cs", "")));
        Assert.Single(results);
        Assert.Single(results[0].Matches);
    }

    [Fact]
    public async Task SearchAsync_SkipsBinaryFilesAndCapsPerFileMatches()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "binary.dat"), [65, 0, 66, 0, 67]);
        await File.WriteAllTextAsync(Path.Combine(_root, "large.txt"), string.Join(Environment.NewLine, Enumerable.Repeat("needle", 700)));

        var service = new WorkspaceSearchService(new FakeSettingsService());
        var results = await CollectAsync(service.SearchAsync(new(
            _root, "needle", new TextSearchOptions(false, false, false))));

        var file = Assert.Single(results);
        Assert.Equal("large.txt", file.RelativePath);
        Assert.Equal(WorkspaceSearchService.MaximumMatchesPerFile, file.Matches.Count);
    }

    [Fact]
    public async Task SearchAsync_ReadsUtf16AndGb18030Text()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await File.WriteAllTextAsync(Path.Combine(_root, "utf16.txt"), "needle UTF16\n", new UnicodeEncoding(false, true));
        await File.WriteAllTextAsync(Path.Combine(_root, "gb18030.txt"), "中文 needle\n", Encoding.GetEncoding(54936));

        var service = new WorkspaceSearchService(new FakeSettingsService());
        var results = await CollectAsync(service.SearchAsync(new(
            _root, "needle", new TextSearchOptions(false, false, false))));

        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.RelativePath == "utf16.txt");
        Assert.Contains(results, result => result.RelativePath == "gb18030.txt");
    }

    [Fact]
    public async Task SearchAsync_CanBeCancelled()
    {
        for (var index = 0; index < 30; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"{index}.txt"), "needle\n");
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new WorkspaceSearchService(new FakeSettingsService());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.SearchAsync(new(
                _root, "needle", new TextSearchOptions(false, false, false)), cancellationToken: cancellation.Token))
            {
            }
        });
    }

    private static async Task<IReadOnlyList<WorkspaceSearchFileResult>> CollectAsync(
        IAsyncEnumerable<WorkspaceSearchFileResult> source)
    {
        var results = new List<WorkspaceSearchFileResult>();
        await foreach (var result in source) results.Add(result);
        return results;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
