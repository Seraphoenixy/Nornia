using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Git.Parsing;

namespace Nornia.Tests;

/// <summary>渲染前行尾归一(回归:“有时 diff 视图末尾多出一块实际没有更改的内容块”):
/// 文件末尾换行符状态变化(新增/去掉结尾换行、CRLF↔LF)时 git 会如实把该行标记为 删+增
/// 对,但两行可见文字完全相同——折叠为单条上下文行后,末尾不再出现假“变更块”;
/// “\ No newline at end of file” 提示行保留,行尾差异的原因仍然可见。</summary>
public sealed class DiffLineEndingAdjusterTests
{
    private const string NoNewline = @"\ No newline at end of file";

    private static GitDiffLine Ctx(string text, int oldNum, int newNum) =>
        new(GitDiffLineKind.Context, oldNum, newNum, text);

    private static GitDiffLine Add(string text, int newNum) =>
        new(GitDiffLineKind.Added, null, newNum, text);

    private static GitDiffLine Rem(string text, int oldNum) =>
        new(GitDiffLineKind.Removed, oldNum, null, text);

    private static GitDiffLine Notice() =>
        new(GitDiffLineKind.Notice, null, null, NoNewline);

    private static GitFileDiff OneHunk(params GitDiffLine[] lines) =>
        new("f.txt", null, false, false, false,
            [new GitDiffHunk(1, lines.Length, 1, lines.Length, "@@ -1,5 +1,5 @@", lines)]);

    private static List<GitDiffLine> Lines(GitFileDiff diff) =>
        diff.Hunks.SelectMany(hunk => hunk.Lines).ToList();

    [Fact]
    public void EofNewlineOnlyPair_CollapsesToSingleContextLine_NoticeKept()
    {
        var diff = OneHunk(Ctx("a", 1, 1), Rem("b", 2), Notice(), Add("b", 2), Ctx("c", 3, 3));

        var lines = Lines(DiffLineEndingAdjuster.Adjust(diff));

        Assert.Equal(4, lines.Count);
        Assert.Equal(
            [GitDiffLineKind.Context, GitDiffLineKind.Context, GitDiffLineKind.Notice, GitDiffLineKind.Context],
            lines.Select(line => line.Kind).ToArray());
        // 折叠出的上下文行保留旧/新两端行号,文本为可见文字。
        Assert.Equal(2, lines[1].OldLineNumber);
        Assert.Equal(2, lines[1].NewLineNumber);
        Assert.Equal("b", lines[1].Text);
        Assert.DoesNotContain(lines, line => line.Kind is GitDiffLineKind.Added or GitDiffLineKind.Removed);
    }

    [Fact]
    public void OldKeptNewline_LostInNew_Collapses()
    {
        var adjusted = DiffLineEndingAdjuster.AdjustLines([Rem("b", 2), Add("b", 2), Notice()]);

        Assert.Equal(2, adjusted.Count);
        Assert.Equal(GitDiffLineKind.Context, adjusted[0].Kind);
        Assert.Equal("b", adjusted[0].Text);
        Assert.Equal(GitDiffLineKind.Notice, adjusted[1].Kind);
    }

    [Fact]
    public void CrLfPair_CollapsesAndNormalizesDisplayText()
    {
        var adjusted = DiffLineEndingAdjuster.AdjustLines([Rem("b\r", 2), Add("b", 2)]);

        Assert.Single(adjusted);
        Assert.Equal(GitDiffLineKind.Context, adjusted[0].Kind);
        Assert.Equal("b", adjusted[0].Text); // 结尾 CR 归一,不在文档里留下裸 \r
        Assert.Equal(2, adjusted[0].OldLineNumber);
        Assert.Equal(2, adjusted[0].NewLineNumber);
    }

    [Fact]
    public void GenuineChange_IsUntouched()
    {
        var lines = new[] { Rem("x", 1), Add("y", 1) };

        var adjusted = DiffLineEndingAdjuster.AdjustLines(lines);

        Assert.Same(lines, adjusted); // 无折叠 → 返回原实例
        Assert.Contains(lines, line => line.Kind == GitDiffLineKind.Removed);
        Assert.Contains(lines, line => line.Kind == GitDiffLineKind.Added);
    }

    [Fact]
    public void TailLineEndingPair_StopsCountingAsExtraChangeBlock()
    {
        // 一次真实修改 + 文件末尾仅换行符状态变化:修复前末尾被计为第二个“变更块”。
        var diff = OneHunk(Rem("x", 1), Add("X", 1), Ctx("c", 2, 2), Rem("b", 3), Notice(), Add("b", 3));

        var before = DiffDocumentBuilders.CountBlocks(Lines(diff).Select(line => line.Kind));
        var after = DiffDocumentBuilders.CountBlocks(Lines(DiffLineEndingAdjuster.Adjust(diff)).Select(line => line.Kind));

        Assert.Equal(2, before);
        Assert.Equal(1, after);
    }

    [Fact]
    public void MixedRun_OnlyIdenticalPairsCollapse()
    {
        var adjusted = DiffLineEndingAdjuster.AdjustLines(
            [Rem("x", 1), Add("X", 1), Rem("b", 2), Add("b", 2), Notice()]);

        Assert.Equal(4, adjusted.Count);
        Assert.Equal(
            [GitDiffLineKind.Removed, GitDiffLineKind.Added, GitDiffLineKind.Context, GitDiffLineKind.Notice],
            adjusted.Select(line => line.Kind).ToArray());
        Assert.Equal("b", adjusted[2].Text);
    }

    [Fact]
    public void UnpairedLines_NeverCollapse()
    {
        var lines = new[] { Rem("a", 1), Rem("b", 2), Add("b", 2) };

        // 按序配对 (a,b) 文字不同 → 不折叠,避免把纯删除误判为“没变”。
        Assert.Same(lines, DiffLineEndingAdjuster.AdjustLines(lines));
    }

    [Fact]
    public void AllAddedHunk_Untouched()
    {
        var lines = new[] { Add("x", 1), Add("y", 2) };

        Assert.Same(lines, DiffLineEndingAdjuster.AdjustLines(lines));
    }

    [Fact]
    public void ContextOnly_ReturnsSameInstance()
    {
        var lines = new[] { Ctx("a", 1, 1), Ctx("b", 2, 2) };
        Assert.Same(lines, DiffLineEndingAdjuster.AdjustLines(lines));

        var diff = OneHunk(Ctx("a", 1, 1));
        Assert.Same(diff, DiffLineEndingAdjuster.Adjust(diff));
    }

    [Fact]
    public void MultiHunk_OnlyAffectedHunkReplaced()
    {
        var hunk1 = new GitDiffHunk(1, 1, 1, 1, "@@ -1,1 +1,1 @@", [Ctx("a", 1, 1)]);
        var hunk2 = new GitDiffHunk(5, 2, 5, 2, "@@ -5,2 +5,2 @@", [Rem("b", 5), Notice(), Add("b", 5)]);
        var diff = new GitFileDiff("f.txt", null, false, false, false, [hunk1, hunk2]);

        var adjusted = DiffLineEndingAdjuster.Adjust(diff);

        Assert.NotSame(diff, adjusted);
        Assert.Same(hunk1, adjusted.Hunks[0]);
        Assert.NotSame(hunk2, adjusted.Hunks[1]);
        Assert.DoesNotContain(
            adjusted.Hunks[1].Lines,
            line => line.Kind is GitDiffLineKind.Added or GitDiffLineKind.Removed);
    }

    [Fact]
    public void SideBySide_PhantomModifiedPairDisappears()
    {
        var diff = OneHunk(Ctx("a", 1, 1), Rem("b", 2), Notice(), Add("b", 2), Ctx("c", 3, 3));

        var before = diff.ToSideBySideRows();
        Assert.Contains(before, row => row.OldKind == GitDiffLineKind.Removed && row.NewKind == GitDiffLineKind.Added);

        var after = DiffLineEndingAdjuster.Adjust(diff).ToSideBySideRows();
        Assert.DoesNotContain(after, row => row.OldKind == GitDiffLineKind.Removed && row.NewKind == GitDiffLineKind.Added);

        var (old, next) = DiffDocumentBuilders.BuildSideBySide(after);
        Assert.All(old.Concat(next), line => Assert.False(line.IsModified));
    }

    [Fact]
    public void EndToEnd_ParsedGitDiff_TailBlockBecomesContext()
    {
        // 真实 git 输出:文件最后一行去掉结尾换行符,文字未变。
        var raw = string.Join('\n',
            "@@ -1,3 +1,3 @@",
            " a",
            "-b",
            NoNewline,
            "+b",
            NoNewline,
            " c");

        var parser = new GitDiffStreamParser("f.txt", false);
        var events = raw.Split('\n').SelectMany(parser.Accept).ToList();

        // 与 DiffTab/CollectDiffAsync 相同的聚合方式:hunk 事件 → HunkHeader 行 + 体行。
        var lines = new List<GitDiffLine>();
        var inHunk = false;
        foreach (var item in events)
        {
            switch (item)
            {
                case GitDiffHunkEvent value:
                    inHunk = true;
                    lines.Add(new GitDiffLine(GitDiffLineKind.HunkHeader, null, null, value.Header));
                    break;
                case GitDiffLineEvent value when inHunk:
                    lines.Add(value.Line);
                    break;
            }
        }

        var adjusted = DiffLineEndingAdjuster.AdjustLines(lines);
        var rendered = DiffDocumentBuilders.BuildInline(adjusted);

        Assert.DoesNotContain(rendered, line => line.IsChange);
        Assert.Contains(rendered, line => line.Kind == GitDiffLineKind.Notice);
        Assert.Equal("c", rendered[^1].Text);
        Assert.Equal(0, DiffDocumentBuilders.CountBlocks(adjusted.Select(line => line.Kind)));
    }
}
