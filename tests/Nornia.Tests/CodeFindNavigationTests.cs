using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>Find-bar matcher: case, whole-word and regex modes with offsets + line numbers.</summary>
public sealed class TextSearchServiceTests
{
    private const string Sample = "alpha beta\nAlpha dog\nbeta cat";

    [Fact]
    public void Plain_IsCaseInsensitiveByDefault()
    {
        var matches = TextSearchService.Instance.FindAll(Sample, "alpha", new TextSearchOptions(CaseSensitive: false, WholeWord: false, UseRegex: false));

        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].Offset);
        Assert.Equal(11, matches[1].Offset);
        Assert.Equal(1, matches[0].Line);
        Assert.Equal(2, matches[1].Line);
        Assert.All(matches, m => Assert.Equal(5, m.Length));
    }

    [Fact]
    public void Plain_CaseSensitive_RespectsCase()
    {
        var matches = TextSearchService.Instance.FindAll(Sample, "Alpha", new TextSearchOptions(true, false, false));

        Assert.Single(matches);
        Assert.Equal(11, matches[0].Offset);
        Assert.Equal(2, matches[0].Line);
    }

    [Fact]
    public void WholeWord_RequiresWordBoundaries()
    {
        // "cat catalog cat\nscat": standalone "cat" at 0 and 12; the "cat" inside "scat" is not a
        // whole word and must be excluded.
        var text = "cat catalog cat\nscat";
        var matches = TextSearchService.Instance.FindAll(text, "cat", new TextSearchOptions(false, WholeWord: true, false));

        Assert.Equal(new[] { 0, 12 }, matches.Select(m => m.Offset));
        Assert.DoesNotContain(matches, m => m.Offset == 17);
    }

    [Fact]
    public void Regex_MatchesPatternAndHonorsWholeWord()
    {
        var matches = TextSearchService.Instance.FindAll("ver 1.0; ver 2.0; x1", @"ver \d\.\d", new TextSearchOptions(false, WholeWord: false, UseRegex: true));

        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].Offset);
        Assert.Equal(9, matches[1].Offset);
    }

    [Fact]
    public void Regex_MalformedExpression_ReturnsNoMatches()
    {
        var matches = TextSearchService.Instance.FindAll("any text", "[unclosed", new TextSearchOptions(false, false, UseRegex: true));

        Assert.Empty(matches);
    }

    [Fact]
    public void EmptyTermOrText_ReturnsNoMatches()
    {
        Assert.Empty(TextSearchService.Instance.FindAll(Sample, string.Empty, new TextSearchOptions(false, false, false)));
        Assert.Empty(TextSearchService.Instance.FindAll(string.Empty, "x", new TextSearchOptions(false, false, false)));
    }

    [Fact]
    public void LineNumbers_TrackNewlines()
    {
        var matches = TextSearchService.Instance.FindAll("a\nb\na\na", "a", new TextSearchOptions(false, false, false));

        Assert.Equal(new[] { 1, 3, 4 }, matches.Select(m => m.Line));
    }

    [Fact]
    public void GetRegexError_ValidAndEmptyTermsReturnNull()
    {
        Assert.Null(TextSearchService.GetRegexError(@"ver \d\.\d", caseSensitive: false));
        Assert.Null(TextSearchService.GetRegexError("[a-z]+", caseSensitive: true));
        Assert.Null(TextSearchService.GetRegexError(string.Empty, caseSensitive: false));
        Assert.Null(TextSearchService.GetRegexError(null, caseSensitive: false));
    }

    [Fact]
    public void GetRegexError_MalformedTermReturnsCompilerMessage()
    {
        Assert.False(string.IsNullOrEmpty(TextSearchService.GetRegexError("[unclosed", caseSensitive: false)));
    }
}

/// <summary>Preview-tab find state: live match re-count, next/previous cycling, clear, go-to-line
/// validation and reading-option toggles.</summary>
public sealed class FilePreviewSearchTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-find-{Guid.NewGuid():N}");

    public FilePreviewSearchTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    private static async Task<FilePreviewTab> CreateTabAsync(string path)
    {
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();
        return tab;
    }

    [Fact]
    public async Task SearchText_RecountsMatchesAndShowsCounter()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "foo bar\nfoo baz\nnope");
        var tab = await CreateTabAsync(path);

        tab.SearchText = "foo";

        Assert.Equal(2, tab.MatchCount);
        Assert.Equal("1 / 2", tab.CurrentMatchDisplay);
        Assert.Equal(new[] { 1, 2 }, tab.SearchMatches.Select(m => m.Line));
        Assert.Equal(0, tab.SearchMatches[0].Offset);
    }

    [Fact]
    public async Task FindNextAndPrevious_CycleThroughMatches()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "x a x a x a");
        var tab = await CreateTabAsync(path);
        tab.SearchText = "a";

        Assert.Equal("1 / 3", tab.CurrentMatchDisplay);

        tab.FindNextCommand.Execute(null);
        Assert.Equal("2 / 3", tab.CurrentMatchDisplay);

        tab.FindNextCommand.Execute(null);
        Assert.Equal("3 / 3", tab.CurrentMatchDisplay);

        tab.FindNextCommand.Execute(null); // wraps
        Assert.Equal("1 / 3", tab.CurrentMatchDisplay);

        tab.FindPreviousCommand.Execute(null);
        Assert.Equal("3 / 3", tab.CurrentMatchDisplay);
    }

    [Fact]
    public async Task SearchOptions_RecomputeMatchSet()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "Foo foo FOO");
        var tab = await CreateTabAsync(path);

        tab.SearchWholeWord = true;
        tab.SearchText = "foo";
        Assert.Equal(3, tab.MatchCount);

        tab.SearchCaseSensitive = true;
        Assert.Equal(1, tab.MatchCount);
    }

    [Fact]
    public async Task ZeroMatches_ShowsNoMatchCounter()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "nothing here");
        var tab = await CreateTabAsync(path);
        tab.SearchText = "zzz";

        Assert.Equal(0, tab.MatchCount);
        Assert.Equal("无匹配", tab.CurrentMatchDisplay);
        Assert.Empty(tab.SearchMatches);
    }

    [Fact]
    public async Task MalformedRegex_ShowsErrorAndClearsMatches_UntilFixed()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "any text");
        var tab = await CreateTabAsync(path);

        tab.SearchUseRegex = true;
        tab.SearchText = "[unclosed";

        Assert.Equal(0, tab.MatchCount);
        Assert.Empty(tab.SearchMatches);
        Assert.False(string.IsNullOrEmpty(tab.SearchErrorMessage));
        Assert.Equal("无匹配", tab.CurrentMatchDisplay);

        // 修正表达式后错误消失,匹配恢复。
        tab.SearchText = "an";
        Assert.Equal(string.Empty, tab.SearchErrorMessage);
        Assert.Equal(1, tab.MatchCount);
    }

    [Fact]
    public async Task SearchEnteredBeforeLoad_ComputesWhenContentArrives()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "foo bar\nfoo baz\nnope");
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);

        // 惰性加载前输入查询:内容未就绪,暂无匹配。
        tab.SearchText = "foo";
        Assert.Equal(0, tab.MatchCount);

        await tab.LoadAsync();

        // 内容就绪后按新文档重算。
        Assert.Equal(2, tab.MatchCount);
        Assert.Equal("1 / 2", tab.CurrentMatchDisplay);
    }

    [Fact]
    public async Task FindStepCommands_EnableOnlyWithMatches_AndNotifyOnCountChange()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "foo bar\nfoo baz");
        var tab = await CreateTabAsync(path);

        Assert.False(tab.FindNextCommand.CanExecute(null));
        Assert.False(tab.FindPreviousCommand.CanExecute(null));

        var raisedNext = 0;
        var raisedPrevious = 0;
        tab.FindNextCommand.CanExecuteChanged += (_, _) => raisedNext++;
        tab.FindPreviousCommand.CanExecuteChanged += (_, _) => raisedPrevious++;

        // 匹配数变化必须显式刷新按钮可用态(窗口化异步搜索完成时无键盘 requery 事件)。
        tab.SearchText = "foo";
        Assert.Equal(2, tab.MatchCount);
        Assert.True(tab.FindNextCommand.CanExecute(null));
        Assert.True(tab.FindPreviousCommand.CanExecute(null));
        Assert.True(raisedNext > 0);
        Assert.True(raisedPrevious > 0);

        tab.ClearSearchCommand.Execute(null);
        Assert.False(tab.FindNextCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClearSearch_ResetsFindState()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "a a a");
        var tab = await CreateTabAsync(path);
        tab.SearchText = "a";
        tab.IsFindBarOpen = true;

        tab.ClearSearchCommand.Execute(null);

        Assert.False(tab.IsFindBarOpen);
        Assert.Equal(string.Empty, tab.SearchText);
        Assert.Equal(0, tab.MatchCount);
        Assert.Empty(tab.SearchMatches);
    }

    [Fact]
    public async Task GoToLine_ValidInputRaisesClampedJump()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "1\n2\n3\n4");
        var tab = await CreateTabAsync(path);
        var jumps = new List<int>();
        tab.GoToLineRequested += (_, line) => jumps.Add(line);

        tab.GoToLineInput = "3";
        tab.GoToLineCommand.Execute(null);
        Assert.Equal([3], jumps);

        // beyond the document clamps to the last line
        tab.GoToLineInput = "999";
        tab.GoToLineCommand.Execute(null);
        Assert.Equal([3, 4], jumps);

        // garbage is ignored
        tab.GoToLineInput = "abc";
        tab.GoToLineCommand.Execute(null);
        Assert.Equal([3, 4], jumps);

        tab.GoToLineInput = "-2";
        tab.GoToLineCommand.Execute(null);
        Assert.Equal([3, 4], jumps);

        Assert.False(tab.IsGoToLineBarOpen);
    }

    [Fact]
    public async Task GoToLine_WithColumnRaisesPrecisePositionJump()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "012345\nabcdef\nlast");
        var tab = await CreateTabAsync(path);
        var positions = new List<(int Line, int Column)>();
        var lineJumps = new List<int>();
        tab.GoToPositionRequested += (_, position) => positions.Add(position);
        tab.GoToLineRequested += (_, line) => lineJumps.Add(line);

        tab.GoToLineInput = "2:4";
        tab.GoToLineCommand.Execute(null);

        Assert.Equal([(2, 4)], positions);
        Assert.Empty(lineJumps);
    }

    [Fact]
    public async Task ReadingOptionToggles_FlipDefaults()
    {
        var path = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(path, "text");
        var tab = await CreateTabAsync(path);

        Assert.False(tab.WordWrap);   // default: no wrap

        tab.ToggleWordWrapCommand.Execute(null);

        Assert.True(tab.WordWrap);
    }
}
