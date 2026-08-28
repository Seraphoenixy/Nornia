using System.Text;
using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>Disk-based streaming search (<see cref="FileReadOnlyDocumentSource.SearchAsync"/>) honours
/// the find-bar options — case / whole-word / regex — with the same semantics as the in-memory
/// <see cref="TextSearchService"/>.</summary>
public sealed class FileSourceSearchOptionsTests
{
    private static string TempFile(string name) =>
        Path.Combine(Path.GetTempPath(), $"nornia-src-search-{Guid.NewGuid():N}{name}");

    private static async Task<IReadOnlyList<DocumentSearchMatch>> SearchAsyncAsync(string path, string query, TextSearchOptions? options)
    {
        await using var source = new FileReadOnlyDocumentSource(path);
        var matches = new List<DocumentSearchMatch>();
        await foreach (var match in source.SearchAsync(query, 1_000, options)) matches.Add(match);
        return matches;
    }

    [Fact]
    public async Task SearchAsync_DefaultsStayCaseInsensitive()
    {
        var path = TempFile(".txt");
        await File.WriteAllTextAsync(path, "alpha beta\nAlpha dog\n");
        try
        {
            // 缺省(不传 options)保持大小写不敏感;显式大小写敏感只命中第一行。
            Assert.Equal(new[] { 1, 2 }, (await SearchAsyncAsync(path, "alpha", null)).Select(m => m.Line).ToArray());
            Assert.Equal(1, Assert.Single(await SearchAsyncAsync(path, "alpha", new TextSearchOptions(true, false, false))).Line);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SearchAsync_HonorsWholeWord()
    {
        var path = TempFile(".txt");
        await File.WriteAllTextAsync(path, "cat catalog cat\nscat\n");
        try
        {
            var matches = await SearchAsyncAsync(path, "cat", new TextSearchOptions(false, WholeWord: true, UseRegex: false));

            // standalone "cat" at columns 1 and 13; the "cat" inside "catalog" and "scat" is excluded.
            Assert.Equal(new[] { (1, 1), (1, 13) }, matches.Select(m => (m.Line, m.Column)));
            Assert.DoesNotContain(matches, m => m.Line == 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SearchAsync_HonorsRegexAndCase()
    {
        var path = TempFile(".txt");
        await File.WriteAllTextAsync(path, "ver 1.0; ver 2.0\nVER 3.0\n");
        try
        {
            // 大小写不敏感命中三处(含第二行 "VER 3.0");大小写敏感只剩第一行两处。
            Assert.Equal(new[] { (1, 1), (1, 10), (2, 1) },
                (await SearchAsyncAsync(path, @"ver \d\.\d", new TextSearchOptions(false, false, UseRegex: true)))
                    .Select(m => (m.Line, m.Column)).ToArray());
            Assert.Equal(new[] { (1, 1), (1, 10) },
                (await SearchAsyncAsync(path, @"ver \d\.\d", new TextSearchOptions(true, false, UseRegex: true)))
                    .Select(m => (m.Line, m.Column)).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SearchAsync_MalformedRegex_YieldsNoMatches()
    {
        var path = TempFile(".txt");
        await File.WriteAllTextAsync(path, "any text\n");
        try
        {
            Assert.Empty(await SearchAsyncAsync(path, "[unclosed", new TextSearchOptions(false, false, UseRegex: true)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Find bar over large (windowed) files: the case / whole-word / regex toggles apply to the
/// streamed disk search, and next/previous navigation loads the window that owns the target match,
/// remapping the visible match subset to window-local coordinates.</summary>
public sealed class WindowedFindNavigationTests : IDisposable
{
    private const string Filler = "lorem ipsum dolor sit amet consectetur adipiscing elit sed do";
    private const int TotalLines = 150_000; // ≈ 9.5 MB — above the 8 MB full-source threshold
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-windowed-find-{Guid.NewGuid():N}");

    public WindowedFindNavigationTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    /// <summary>9.5 MB file with four needles: line 100 (in the initial window) and lines 10000 /
    /// 20000 / 30000 (outside it), one of them different case and one embedded in a longer word.</summary>
    private static async Task<FilePreviewTab> CreateWindowedTabAsync(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        var lines = new string[TotalLines];
        Array.Fill(lines, Filler);
        lines[99] = "needle alpha";     // line 100
        lines[9999] = "needle beta";    // line 10000
        lines[19999] = "NEEDLE gamma";  // line 20000
        lines[29999] = "pneedle delta"; // line 30000
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false));

        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();
        Assert.Equal(1, tab.WindowStartLine);
        return tab;
    }

    private static async Task WaitForAsync(Func<bool> condition, Func<string>? describe = null, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Find state did not converge in time. " + (describe?.Invoke() ?? string.Empty));
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task WindowedSearch_HonorsFindOptions()
    {
        var tab = await CreateWindowedTabAsync(_tempDir, "options.txt");

        tab.SearchText = "needle";
        await WaitForAsync(() => tab.MatchCount == 4);
        Assert.Equal(4, tab.MatchCount);            // 默认大小写不敏感:needle×2 + NEEDLE + pneedle
        Assert.Equal("1 / 4", tab.CurrentMatchDisplay);
        // 当前窗口(1–5000)只含第 100 行的匹配;偏移按窗口文本坐标(前 99 行填充行)。
        var inWindow = Assert.Single(tab.SearchMatches);
        Assert.Equal(100, inWindow.Line);
        Assert.Equal(99 * (Filler.Length + 1), inWindow.Offset);

        tab.SearchCaseSensitive = true;
        await WaitForAsync(() => tab.MatchCount == 3);

        tab.SearchCaseSensitive = false;
        await WaitForAsync(() => tab.MatchCount == 4,
            () => $"MatchCount={tab.MatchCount} case={tab.SearchCaseSensitive} ww={tab.SearchWholeWord} regex={tab.SearchUseRegex} text='{tab.SearchText}'");

        tab.SearchWholeWord = true;
        // 大小写不敏感 + 全词:needle×2 与 NEEDLE(整词)命中,pneedle 被全词排除。
        await WaitForAsync(() => tab.MatchCount == 3,
            () => $"MatchCount={tab.MatchCount} case={tab.SearchCaseSensitive} ww={tab.SearchWholeWord} regex={tab.SearchUseRegex} text='{tab.SearchText}'");

        tab.SearchWholeWord = false;
        tab.SearchUseRegex = true;
        tab.SearchText = "needle (alpha|beta)";
        await WaitForAsync(() => tab.MatchCount == 2,
            () => $"MatchCount={tab.MatchCount} case={tab.SearchCaseSensitive} ww={tab.SearchWholeWord} regex={tab.SearchUseRegex} text='{tab.SearchText}'");

        tab.SearchUseRegex = false;
        tab.SearchText = "needle";
        await WaitForAsync(() => tab.MatchCount == 4,
            () => $"MatchCount={tab.MatchCount} case={tab.SearchCaseSensitive} ww={tab.SearchWholeWord} regex={tab.SearchUseRegex} text='{tab.SearchText}'");
    }

    [Fact]
    public async Task FindNext_NavigatesAcrossWindows_LoadingTheOwningWindow()
    {
        var tab = await CreateWindowedTabAsync(_tempDir, "navigate.txt");

        tab.SearchText = "needle";
        await WaitForAsync(() => tab.MatchCount == 4);
        Assert.Equal(1, tab.WindowStartLine);

        // 第 1 个匹配(100 行)→ 第 2 个(10000 行):目标在当前窗口外,须先加载其窗口。
        tab.FindNextCommand.Execute(null);
        await WaitForAsync(() => tab.WindowStartLine != 1);

        var expectedStart = 10000 - FileReadOnlyDocumentSource.DefaultWindowLines / 3; // 8334
        Assert.Equal(expectedStart, tab.WindowStartLine);
        Assert.Equal("2 / 4", tab.CurrentMatchDisplay);
        // 新窗口(8334–13333)内只剩第 10000 行的匹配,行号/偏移已重挂到窗口坐标。
        var match = Assert.Single(tab.SearchMatches);
        var localLine = 10000 - expectedStart + 1;
        Assert.Equal(localLine, match.Line);
        Assert.Equal((localLine - 1) * (Filler.Length + 1), match.Offset);
        Assert.Equal(0, tab.CurrentMatchViewIndex);

        // 返回第 1 个匹配(100 行):首个窗口重新载入,当前匹配落位。
        tab.FindPreviousCommand.Execute(null);
        await WaitForAsync(() => tab.WindowStartLine == 1);
        Assert.Equal("1 / 4", tab.CurrentMatchDisplay);
        var back = Assert.Single(tab.SearchMatches);
        Assert.Equal(100, back.Line);
    }

    [Fact]
    public async Task WindowedSearch_MalformedRegex_ShowsErrorUntilFixed()
    {
        var tab = await CreateWindowedTabAsync(_tempDir, "regex.txt");

        tab.SearchUseRegex = true;
        tab.SearchText = "[unclosed";

        Assert.False(string.IsNullOrEmpty(tab.SearchErrorMessage));
        Assert.Equal(0, tab.MatchCount);
        Assert.Empty(tab.SearchMatches);

        tab.SearchText = "needle";
        Assert.Equal(string.Empty, tab.SearchErrorMessage);
        await WaitForAsync(() => tab.MatchCount == 4);
    }
}
