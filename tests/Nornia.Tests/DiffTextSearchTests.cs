using Nornia.Core.Models;
using Nornia.Desktop.Code;

namespace Nornia.Tests;

/// <summary>Covers <see cref="DiffTextSearch"/> — the diff find engine that used to live inside
/// <c>DiffDocumentView.xaml.cs</c> and had zero test coverage. Pure string logic, so no STA host.</summary>
public sealed class DiffTextSearchTests
{
    private static DiffRenderLine Line(string text, GitDiffLineKind kind = GitDiffLineKind.Context) =>
        new(text, kind, null, null);

    private static IReadOnlyList<DiffRenderLine> Lines(params string[] texts) =>
        [.. texts.Select(text => Line(text))];

    [Fact]
    public void SearchLines_ReportsEveryOccurrenceWithinALine()
    {
        var matches = DiffTextSearch.SearchLines(Lines("foo bar foo"), DiffSearchPane.Inline, "foo", TextSearchService.Instance);

        Assert.Equal(2, matches.Count);
        Assert.Equal((0, 0, 3), (matches[0].LineIndex, matches[0].Column, matches[0].Length));
        Assert.Equal((0, 8, 3), (matches[1].LineIndex, matches[1].Column, matches[1].Length));
        Assert.All(matches, match => Assert.Equal(DiffSearchPane.Inline, match.Pane));
    }

    [Fact]
    public void SearchLines_IsCaseInsensitive()
    {
        var matches = DiffTextSearch.SearchLines(Lines("Foo"), DiffSearchPane.Old, "foo", TextSearchService.Instance);

        Assert.Equal((0, 0, 3), (Assert.Single(matches).LineIndex, matches[0].Column, matches[0].Length));
    }

    [Fact]
    public void SearchLines_ReturnsMatchesInLineThenColumnOrder()
    {
        var matches = DiffTextSearch.SearchLines(
            Lines("b", "a b", "b"), DiffSearchPane.New, "b", TextSearchService.Instance);

        Assert.Equal([0, 1, 2], matches.Select(match => match.LineIndex));
        Assert.Equal([0, 2, 0], matches.Select(match => match.Column));
    }

    [Fact]
    public void SearchLines_EmptyQueryOrEmptyModelYieldsNothing()
    {
        Assert.Empty(DiffTextSearch.SearchLines(Lines("a"), DiffSearchPane.Inline, "", TextSearchService.Instance));
        Assert.Empty(DiffTextSearch.SearchLines([], DiffSearchPane.Inline, "a", TextSearchService.Instance));
    }

    [Fact]
    public void SearchLines_StopsAtTheMatchCap()
    {
        var matches = DiffTextSearch.SearchLines(
            Lines("aaaa", "aaaa"), DiffSearchPane.Inline, "a", TextSearchService.Instance, maximumMatches: 3);

        Assert.Equal(3, matches.Count);
        Assert.Equal([0, 0, 0], matches.Select(match => match.LineIndex));
    }

    [Fact]
    public void MergeSideBySide_InterleavesByLineNumberWithTheOldSideFirst()
    {
        var oldMatches = new[]
        {
            new DiffSearchMatch(DiffSearchPane.Old, 0, 0, 1),
            new DiffSearchMatch(DiffSearchPane.Old, 3, 0, 1),
        };
        var newMatches = new[]
        {
            new DiffSearchMatch(DiffSearchPane.New, 0, 5, 1),
            new DiffSearchMatch(DiffSearchPane.New, 7, 0, 1),
        };

        var merged = DiffTextSearch.MergeSideBySide(oldMatches, newMatches);

        // 同一行上旧侧在前,行号严格递增 —— F3 才能自上而下推进而不是走完一侧再跳回。
        Assert.Equal(
            [DiffSearchPane.Old, DiffSearchPane.New, DiffSearchPane.Old, DiffSearchPane.New],
            merged.Select(match => match.Pane));
        Assert.Equal([0, 0, 3, 7], merged.Select(match => match.LineIndex));
    }

    [Fact]
    public void MergeSideBySide_HandlesOneEmptySide()
    {
        var only = new[] { new DiffSearchMatch(DiffSearchPane.New, 2, 1, 4) };

        Assert.Equal(only, DiffTextSearch.MergeSideBySide([], only));
        Assert.Equal(only, DiffTextSearch.MergeSideBySide(only, []));
        Assert.Empty(DiffTextSearch.MergeSideBySide([], []));
    }

    [Fact]
    public void MergeSideBySide_RespectsTheMatchCap()
    {
        var oldMatches = Enumerable.Range(0, 5).Select(i => new DiffSearchMatch(DiffSearchPane.Old, i, 0, 1)).ToArray();
        var newMatches = Enumerable.Range(0, 5).Select(i => new DiffSearchMatch(DiffSearchPane.New, i, 0, 1)).ToArray();

        var merged = DiffTextSearch.MergeSideBySide(oldMatches, newMatches, maximumMatches: 4);

        Assert.Equal(4, merged.Count);
    }

    [Theory]
    [InlineData(-1, 5, 1, 0)]   // 尚无当前项 → 从头开始
    [InlineData(-1, 5, -1, 4)]  // 反向 → 从末尾开始
    [InlineData(4, 5, 1, 0)]    // 环形回绕
    [InlineData(0, 5, -1, 4)]
    [InlineData(2, 5, 1, 3)]
    [InlineData(2, 5, -1, 1)]
    public void Step_WrapsAroundTheRingTheWayFindNextAndFindPreviousDo(int current, int count, int step, int expected) =>
        Assert.Equal(expected, DiffTextSearch.Step(current, count, step));

    [Fact]
    public void Step_ReportsNoMatchWhenNothingMatched() =>
        Assert.Equal(-1, DiffTextSearch.Step(-1, 0, 1));

    [Fact]
    public void SearchLines_HonoursCallerSuppliedOptions()
    {
        // 查找框现在把大小写/整词/正则开关传进来(此前固定 DefaultOptions):
        // 大小写敏感时同词不同大小写不再命中。
        var options = new TextSearchOptions(CaseSensitive: true, WholeWord: false, UseRegex: false);
        var matches = DiffTextSearch.SearchLines(Lines("Foo foo"), DiffSearchPane.Inline, "foo", TextSearchService.Instance, options);

        var match = Assert.Single(matches);
        Assert.Equal((0, 4, 3), (match.LineIndex, match.Column, match.Length));
    }

    [Fact]
    public void DefaultOptions_MatchTheHistoricalDiffFindBehaviour()
    {
        // 面板已暴露开关,但引擎的兜底默认(调用方不传 options 时)必须与历史行为一致
        // (忽略大小写、非整词、非正则),否则默认路径会悄悄改变行为。
        Assert.False(DiffTextSearch.DefaultOptions.CaseSensitive);
        Assert.False(DiffTextSearch.DefaultOptions.WholeWord);
        Assert.False(DiffTextSearch.DefaultOptions.UseRegex);
    }
}
