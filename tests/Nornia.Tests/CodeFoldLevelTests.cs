using Nornia.Desktop.Views;

namespace Nornia.Tests;

/// <summary><see cref="CodeDocumentView.ComputeFoldDepths"/> 的 O(n log n) 扫描语义测试:
/// 深度必须等于"被多少段严格包含"(含者 Start 严格更小 且 End 严格更大)的 O(n²) 参考实现,
/// 覆盖同 start 兄弟段、同 end 段与随机区间差分。</summary>
public sealed class CodeFoldLevelTests
{
    /// <summary>按 start 升序稳定整理(与原实现 OrderBy(StartOffset) 相同),返回 (starts, ends)。</summary>
    private static (int[] Starts, int[] Ends) SortByStart(params (int Start, int End)[] intervals)
    {
        var sorted = intervals.OrderBy(iv => iv.Start).ToArray();
        return (sorted.Select(iv => iv.Start).ToArray(), sorted.Select(iv => iv.End).ToArray());
    }

    /// <summary>排序后数组上的 O(n²) 参考实现(谓词与旧代码逐字一致)。</summary>
    private static int[] ReferenceDepths((int Start, int End)[] sorted) =>
        sorted.Select(iv => sorted.Count(other => other.Start < iv.Start && other.End > iv.End)).ToArray();

    [Fact]
    public void ComputeFoldDepths_NestedCStyleSections()
    {
        // C 风格嵌套: A{ B{ C } D } + 顶层 E
        var (starts, ends) = SortByStart((0, 100), (10, 50), (20, 30), (60, 90), (110, 120));

        var depths = CodeDocumentView.ComputeFoldDepths(starts, ends);
        Assert.Equal(new[] { 0, 1, 2, 1, 0 }, depths);
    }

    [Fact]
    public void ComputeFoldDepths_SameStartSectionsAreSiblings()
    {
        // 原谓词 Start 严格 < :同 start 的段互不包含,深度均为 0。
        var (starts, ends) = SortByStart((0, 10), (0, 5));

        var depths = CodeDocumentView.ComputeFoldDepths(starts, ends);
        Assert.Equal(new[] { 0, 0 }, depths);
    }

    [Fact]
    public void ComputeFoldDepths_SharedEndIsNotStrictlyContaining()
    {
        // End 相同不满足"End 严格更大":(0,50) 与 (10,50) 互不包含;
        // (20,60) 的 End 更大,反而不被 (0,50)/(10,50) 包含。
        var (starts, ends) = SortByStart((0, 50), (10, 50), (20, 60));

        var depths = CodeDocumentView.ComputeFoldDepths(starts, ends);
        Assert.Equal(new[] { 0, 0, 0 }, depths);
    }

    [Fact]
    public void ComputeFoldDepths_EmptyInput()
    {
        Assert.Empty(CodeDocumentView.ComputeFoldDepths([], []));
    }

    [Fact]
    public void ComputeFoldDepths_MatchesReferenceImplementation_OnRandomIntervals()
    {
        var rng = new Random(20260827);
        for (var round = 0; round < 25; round++)
        {
            var count = rng.Next(1, 400);
            var intervals = new (int, int)[count];
            for (var i = 0; i < count; i++)
            {
                var start = rng.Next(0, 500);
                intervals[i] = (start, start + rng.Next(1, 200));
            }

            var sorted = intervals.OrderBy(iv => iv.Item1).ToArray();
            var (starts, ends) = SortByStart(intervals);

            var expected = ReferenceDepths(sorted);
            var depths = CodeDocumentView.ComputeFoldDepths(starts, ends);
            Assert.True(expected.SequenceEqual(depths), $"round={round}");
        }
    }
}
