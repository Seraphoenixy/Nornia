using Nornia.Core.Models;
using Nornia.Desktop.Code;

namespace Nornia.Tests;

/// <summary>Covers <see cref="BlameAnnotationLayout"/> — the pure blame-margin layout (run folding
/// + relative date text) shared by <c>CodeBlameMargin</c>. No WPF required.</summary>
public sealed class BlameAnnotationLayoutTests
{
    private static GitBlameCommit Commit(string hash, string author, int daysAgo, bool boundary = false) =>
        new(hash, author, "a@x", DateTimeOffset.Now.AddDays(-daysAgo), "subject", boundary);

    [Fact]
    public void Build_FoldsConsecutiveLinesOfTheSameCommitToRunStarts()
    {
        var blame = new GitBlame(
        [
            Commit("aaaaaaa1", "Alice", 10),
            Commit("bbbbbbb2", "Bob", 3),
        ],
        [0, 0, 0, 1, 1]);

        var annotations = BlameAnnotationLayout.Build(blame);

        Assert.Equal(2, annotations.Count);
        Assert.Equal(1, annotations[0].Line); // 第 1 行 = 第一段(Alice)段首
        Assert.Equal("Alice", annotations[0].DisplayAuthor);
        Assert.Equal("aaaaaaa", annotations[0].ShortHash);
        Assert.Equal(4, annotations[1].Line); // 第 4 行 = 第二段(Bob)段首
        Assert.Equal("Bob", annotations[1].DisplayAuthor);
    }

    [Fact]
    public void Build_MarksUncommittedZeroHashLines()
    {
        var blame = new GitBlame(
        [
            Commit("0000000000000000000000000000000000000000", "You", 0),
            Commit("cccccc3", "Root", 100, boundary: true),
        ],
        [1, 1, 0]);

        var annotations = BlameAnnotationLayout.Build(blame);

        Assert.Equal(2, annotations.Count);
        Assert.False(annotations[0].IsUncommitted);
        Assert.True(annotations[0].IsBoundary);
        Assert.True(annotations[1].IsUncommitted);
        Assert.Equal("未提交", annotations[1].DisplayAuthor);
        Assert.Equal("未提交", BlameAnnotationLayout.Format(annotations[1], DateTimeOffset.Now));
    }

    [Fact]
    public void Format_JoinsAuthorRelativeDateAndShortHash()
    {
        var annotation = new BlameAnnotation(1, "Alice", DateTimeOffset.Now.AddDays(-2), "abc1234", false, false);

        var text = BlameAnnotationLayout.Format(annotation, DateTimeOffset.Now);

        Assert.Equal("Alice · 2 天前 · abc1234", text);
    }

    [Theory]
    [InlineData(0, "今天")]
    [InlineData(1, "昨天")]
    [InlineData(9, "9 天前")]
    [InlineData(30, "30 天前")]
    [InlineData(31, "1 个月前")]
    [InlineData(365 * 5, "2021 年")] // 跨闰年后按日历差远超 11 个月 → 落年份档
    public void FormatRelativeDate_UsesHumanBuckets(int daysAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.FromHours(8));
        var when = now.AddDays(-daysAgo);

        Assert.Equal(expected, BlameAnnotationLayout.FormatRelativeDate(when, now));
    }
}
