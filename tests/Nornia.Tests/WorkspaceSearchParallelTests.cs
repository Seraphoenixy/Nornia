using System.Text;
using Nornia.Desktop.Code;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Behavioral invariants of the parallel-rewritten workspace search: completeness of the
/// bounded parallel scan, cross-search .gitignore caching (including invalidation on change), and
/// the single-open encoding sniff (UTF-8 boundary splits, GB18030 files with UTF-8 heads).</summary>
public sealed class WorkspaceSearchParallelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nornia-search-par-{Guid.NewGuid():N}");

    public WorkspaceSearchParallelTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SearchAsync_ParallelScan_FindsEveryMatchedFile()
    {
        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var directory = 0; directory < 10; directory++)
        {
            var dirPath = Path.Combine(_root, $"dir{directory}");
            Directory.CreateDirectory(dirPath);
            for (var file = 0; file < 20; file++)
            {
                var path = Path.Combine(dirPath, $"file{file}.txt");
                var needleCount = file % 5; // 0..4 needles
                var lines = new List<string>();
                for (var line = 0; line < 6; line++)
                {
                    lines.Add(line == needleCount - 1 ? $"prefix needle suffix" : $"line {line} filler");
                }
                await File.WriteAllLinesAsync(path, lines);
                if (needleCount > 0)
                {
                    expected[$"dir{directory}/file{file}.txt"] = 1;
                }
            }
        }

        var service = new WorkspaceSearchService(new FakeSettingsService());
        var results = await CollectAsync(service.SearchAsync(new(
            _root, "needle", new TextSearchOptions(false, false, false))));

        Assert.Equal(expected.Count, results.Count);
        foreach (var result in results)
        {
            Assert.True(expected.TryGetValue(result.RelativePath, out var count),
                $"unexpected file {result.RelativePath}");
            Assert.Equal(count, result.Matches.Count);
        }
    }

    [Fact]
    public async Task SearchAsync_RepeatSearch_UsesIgnoreCacheAndInvalidatesOnChange()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ignored"));
        Directory.CreateDirectory(Path.Combine(_root, "kept"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "ignored/\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "ignored", "a.txt"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "kept", "a.txt"), "needle\n");

        var service = new WorkspaceSearchService(new FakeSettingsService());
        var first = await CollectAsync(service.SearchAsync(new(
            _root, "needle", new TextSearchOptions(false, false, false))));
        Assert.Single(first);
        Assert.Equal("kept/a.txt", first[0].RelativePath);

        // Second search: the .gitignore cache hit path must behave identically.
        var second = await CollectAsync(service.SearchAsync(new(
            _root, "needle", new TextSearchOptions(false, false, false))));
        Assert.Single(second);
        Assert.Equal("kept/a.txt", second[0].RelativePath);

        // Change the ignore file (mtime/length change invalidates the cache entry).
        Directory.CreateDirectory(Path.Combine(_root, "kept"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "ignored/\nkept/\n");
        var third = await CollectAsync(service.SearchAsync(new(
            _root, "needle", new TextSearchOptions(false, false, false))));
        Assert.Empty(third);
    }

    [Fact]
    public async Task SearchAsync_Utf8FileWithCharacterSplitAt4KbBoundary_StillDecodes()
    {
        // Build a UTF-8 file where a 3-byte CJK character straddles the 4096-byte sniff boundary;
        // the sniff must not mistake the incomplete sequence for a non-UTF-8 head.
        var builder = new StringBuilder();
        var cjk = "测";
        var position = 4096 - 1; // start the 3-byte character one byte before the cut
        while (builder.Length < position) builder.Append('a');
        builder.Append(cjk);
        builder.Append('\n').Append("needle after boundary\n");

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        Assert.True(bytes[4095] >= 0x80, "test invariant: the CJK sequence must straddle the 4096 cut");
        await File.WriteAllBytesAsync(Path.Combine(_root, "boundary.txt"), bytes);

        var service = new WorkspaceSearchService(new FakeSettingsService());
        var results = await CollectAsync(service.SearchAsync(new(
            _root, "needle", new TextSearchOptions(false, false, false))));

        var file = Assert.Single(results);
        Assert.Equal("boundary.txt", file.RelativePath);
        Assert.Single(file.Matches);
    }

    [Fact]
    public async Task SearchAsync_Gb18030FileWithUtf8ValidHead_FallsBackMidStream()
    {
        // First 4KB is pure ASCII (valid UTF-8); GB18030 content (plus the needle) comes after,
        // so the strict-UTF-8 scan must break mid-stream and fall back to GB18030.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb = Encoding.GetEncoding(54936);
        using var memory = new MemoryStream();
        using (var writer = new StreamWriter(memory, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write(new string('f', 4096)); // UTF-8-valid head
            writer.Flush();
        }
        var tail = gb.GetBytes("中文内容 needle\n");
        var filePath = Path.Combine(_root, "mixed-head.txt");
        await using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
        {
            memory.Position = 0;
            await memory.CopyToAsync(fileStream);
            await fileStream.WriteAsync(tail);
        }

        var service = new WorkspaceSearchService(new FakeSettingsService());
        var results = await CollectAsync(service.SearchAsync(new(
            _root, "needle", new TextSearchOptions(false, false, false))));

        var file = Assert.Single(results);
        Assert.Equal("mixed-head.txt", file.RelativePath);
        Assert.Single(file.Matches);
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
