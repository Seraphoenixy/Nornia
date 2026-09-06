using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Pure full-document minimap mapping: whole-document strip, map Y conversions and
/// viewport drag round-trips.</summary>
public sealed class MinimapLayoutTests
{
    private const double LineHeight = 18.0;

    [Fact]
    public void Compute_SmallDocument_EveryLineGetsAtLeastItsShare()
    {
        // 100 lines over a 200px strip: lineStrip = max(1/100, 1/200) = 0.01 (2px per line).
        var map = MinimapLayout.Compute(editorLineCount: 100, LineHeight, editorViewportHeight: 180, mapStripHeight: 200, editorScrollOffset: 0);

        Assert.Equal(0.01, map.LineStripHeight, 6);
        Assert.Equal(100, map.VisibleLineCount);
        Assert.Equal(0, map.FirstVisibleLine);
        Assert.Equal(0, map.ViewportTopInMap, 4);
    }

    [Fact]
    public void Compute_LargeDocument_StripClampsToOnePixelPerLine()
    {
        // 10k lines over a 200px strip: lineStrip clamps to 1/200 → one document line per pixel.
        var map = MinimapLayout.Compute(10000, LineHeight, editorViewportHeight: 180, mapStripHeight: 200, editorScrollOffset: 0);

        Assert.Equal(1.0 / 200, map.LineStripHeight, 6);
        Assert.Equal(200, map.VisibleLineCount);
    }

    [Fact]
    public void Compute_Viewport_TracksScrollOffset()
    {
        var map = MinimapLayout.Compute(10000, LineHeight, editorViewportHeight: 180, mapStripHeight: 200, editorScrollOffset: 40 * LineHeight);

        Assert.Equal(40.0 / 10000, map.ViewportTopInMap, 4); // 40 lines * 1/10000
        Assert.Equal(10.0 / 10000, map.ViewportHeightInMap, 4); // 10 viewport lines * 1/10000
    }

    [Fact]
    public void MapY_And_DragInverse_RoundTrip()
    {
        const double viewport = 300.0;
        var map = MinimapLayout.Compute(500, LineHeight, viewport, mapStripHeight: 200, editorScrollOffset: 0);

        var targetLine = 230.0;
        var mapY = MinimapLayout.MapY((int)targetLine, map);

        var restored = MinimapLayout.EditorLineFromMapY(mapY, map);
        Assert.Equal(targetLine, restored, 4);

        // 点击跳转语义:目标行显示在视口垂直居中位置 → 偏移 = 行像素位置 − 视口高度/2。
        var offset = MinimapLayout.ScrollOffsetForLine(restored, LineHeight, viewport);
        Assert.Equal(targetLine * LineHeight - viewport / 2, offset, 4);
    }

    [Fact]
    public void ScrollOffsetForLine_CentersTargetAndClampsAtDocumentStart()
    {
        // 顶部附近的行:居中偏移为负 → 钳制到文档顶部(0)。
        Assert.Equal(0, MinimapLayout.ScrollOffsetForLine(2, LineHeight, viewportHeight: 300));

        // 中部行:精确居中(行像素位置 − 视口高度/2)。
        Assert.Equal(100 * LineHeight - 300 / 2, MinimapLayout.ScrollOffsetForLine(100, LineHeight, viewportHeight: 300));
    }

    [Fact]
    public void LargeDocument_MapNavigationUsesTheWholeDocument()
    {
        var map = MinimapLayout.Compute(10000, LineHeight, editorViewportHeight: 180, mapStripHeight: 200, editorScrollOffset: 0);

        var mapY = MinimapLayout.MapY(9000, map);

        Assert.Equal(0.9, mapY, 4);
        Assert.Equal(9000, MinimapLayout.EditorLineFromMapY(mapY, map), 4);
    }

    [Fact]
    public void Compute_EmptyDocument_ReturnsZeroedMap()
    {
        var map = MinimapLayout.Compute(0, LineHeight, editorViewportHeight: 180, mapStripHeight: 200, editorScrollOffset: 0);

        Assert.Equal(0, map.VisibleLineCount);
        Assert.Equal(0, map.ViewportHeightInMap);
    }
}

/// <summary>Pure diff-document builders: inline mapping, side-by-side alignment, change blocks.</summary>
public sealed class DiffDocumentBuilderTests
{
    [Theory]
    [InlineData("src/A.cs", "C#")]
    [InlineData("Views/Main.xaml", "XML")]
    [InlineData("scripts/app.js", "JavaScript")]
    [InlineData("Program.cpp", "C++")]
    [InlineData("unknown.zzz", "")]
    [InlineData("README", "")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void ResolveHighlightingName_MapsPathToAvalonEditDefinition(string? path, string expected)
    {
        // Diff 视图与代码源视图共用同一注册表:同一文件在两处的高亮完全一致。
        Assert.Equal(expected, DiffDocumentBuilders.ResolveHighlightingName(path));
    }

    private static GitFileDiff SampleDiff() => new(
        "a.txt",
        null,
        false,
        false,
        false,
        [
            new GitDiffHunk(1, 2, 1, 3, "@@ -1,2 +1,3 @@",
            [
                new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep-a"),
                new GitDiffLine(GitDiffLineKind.Removed, 2, null, "gone"),
                new GitDiffLine(GitDiffLineKind.Added, null, 2, "fresh"),
                new GitDiffLine(GitDiffLineKind.Added, null, 3, "extra"),
                new GitDiffLine(GitDiffLineKind.Context, 3, 4, "keep-b"),
            ])
        ]);

    [Fact]
    public void BuildInline_MapsKindsAndNumbersOneToOne()
    {
        var lines = DiffDocumentBuilders.BuildInline(SampleDiff().Hunks.SelectMany(h => h.Lines));

        Assert.Equal(5, lines.Count);
        Assert.Equal(GitDiffLineKind.Context, lines[0].Kind);
        Assert.Equal("keep-a", lines[0].Text);
        Assert.Equal((1, 1), (lines[0].OldLineNumber, lines[0].NewLineNumber));
        Assert.Equal(GitDiffLineKind.Removed, lines[1].Kind);
        Assert.Equal(2, lines[1].OldLineNumber);
        Assert.Null(lines[1].NewLineNumber);
        Assert.Equal(GitDiffLineKind.Added, lines[2].Kind);
        Assert.Null(lines[2].OldLineNumber);
        Assert.Equal(2, lines[2].NewLineNumber);
    }

    [Fact]
    public void BuildSideBySide_AlignsRowsAndPadsNonSideLines()
    {
        var rows = SampleDiff().ToSideBySideRows();
        var (oldSide, newSide) = DiffDocumentBuilders.BuildSideBySide(rows);

        Assert.Equal(rows.Count, oldSide.Count);
        Assert.Equal(rows.Count, newSide.Count);

        // removed "gone" sits on the old side, paired with the first added line on the new side
        var removedIndex = oldSide.ToList().FindIndex(l => l.Kind == GitDiffLineKind.Removed && l.Text == "gone");
        Assert.True(removedIndex >= 0);
        Assert.Equal(GitDiffLineKind.Added, newSide[removedIndex].Kind);
        Assert.Equal("fresh", newSide[removedIndex].Text);

        // the uneven "extra" added line keeps the old side empty (padding, row alignment intact)
        var extraIndex = newSide.ToList().FindIndex(l => l.Kind == GitDiffLineKind.Added && l.Text == "extra");
        Assert.True(extraIndex >= 0);
        Assert.Equal(string.Empty, oldSide[extraIndex].Text);
        Assert.Equal(GitDiffLineKind.None, oldSide[extraIndex].Kind);

        // context lines appear on both sides with their respective numbers
        var contextIndex = oldSide.ToList().FindIndex(l => l.Kind == GitDiffLineKind.Context && l.Text == "keep-b");
        Assert.True(contextIndex >= 0);
        Assert.Equal((3, 4), (oldSide[contextIndex].OldLineNumber, newSide[contextIndex].NewLineNumber));
    }

    [Fact]
    public void MissingSideRows_AreTreatedAsPlaceholderShadows()
    {
        var rows = SampleDiff().ToSideBySideRows();
        var (oldSide, newSide) = DiffDocumentBuilders.BuildSideBySide(rows);

        Assert.Contains(oldSide, line => DiffDocumentBuilders.IsPlaceholderLine(line));
        Assert.Contains(newSide, line => DiffDocumentBuilders.IsPlaceholderLine(line));
        Assert.False(DiffDocumentBuilders.IsPlaceholderLine(new DiffRenderLine(string.Empty, GitDiffLineKind.Context, 1, 1)));
    }

    [Fact]
    public void SideBySide_MarksOnlyUnmatchedCellsForDiagonalShadow()
    {
        var rows = new[]
        {
            new GitSideBySideRow(null, GitDiffLineKind.None, string.Empty,
                1, GitDiffLineKind.Added, "new line"),
            new GitSideBySideRow(2, GitDiffLineKind.Removed, "old line",
                null, GitDiffLineKind.None, string.Empty),
            new GitSideBySideRow(null, GitDiffLineKind.None, string.Empty,
                null, GitDiffLineKind.None, string.Empty),
            new GitSideBySideRow(null, GitDiffLineKind.HunkHeader, "@@ -1 +1 @@",
                null, GitDiffLineKind.HunkHeader, "@@ -1 +1 @@"),
        };

        var (oldSide, newSide) = DiffDocumentBuilders.BuildSideBySide(rows);

        Assert.True(oldSide[0].IsSideBySidePlaceholder);
        Assert.False(newSide[0].IsSideBySidePlaceholder);
        Assert.False(oldSide[1].IsSideBySidePlaceholder);
        Assert.True(newSide[1].IsSideBySidePlaceholder);
        Assert.False(oldSide[2].IsSideBySidePlaceholder);
        Assert.False(newSide[2].IsSideBySidePlaceholder);
        Assert.False(oldSide[3].IsSideBySidePlaceholder);
        Assert.False(newSide[3].IsSideBySidePlaceholder);
    }

    [Fact]
    public void WhitespaceOnlyPaddingRows_AreAlsoPlaceholderShadows()
    {
        var placeholder = new DiffRenderLine("   ", GitDiffLineKind.None, null, null);
        Assert.True(DiffDocumentBuilders.IsPlaceholderLine(placeholder));
    }

    [Fact]
    public void CollapsedPlaceholder_ReportsHiddenContextCount()
    {
        var line = new DiffDisplayLine(null, 7, 12);

        Assert.True(line.IsCollapsedContext);
        Assert.Equal("  … 展开 12 行未更改内容 …", line.Text);

        var renderLine = new DiffRenderLine(line.Text, GitDiffLineKind.None, null, null,
            HiddenLineCount: line.HiddenLineCount);
        Assert.True(renderLine.IsCollapsedContext);
        Assert.True(DiffDocumentBuilders.IsPlaceholderLine(renderLine));
    }

    [Fact]
    public void SideBySideCollapsedContext_CreatesPlaceholderPromptRows()
    {
        var hunk = new GitDiffHunk(1, 20, 1, 20, "@@ -1,20 +1,20 @@",
        [
            new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep-a"),
            new GitDiffLine(GitDiffLineKind.Context, 2, 2, "keep-b"),
            new GitDiffLine(GitDiffLineKind.Context, 3, 3, "keep-c"),
            new GitDiffLine(GitDiffLineKind.Context, 4, 4, "keep-d"),
            new GitDiffLine(GitDiffLineKind.Context, 5, 5, "keep-e"),
            new GitDiffLine(GitDiffLineKind.Context, 6, 6, "keep-f"),
            new GitDiffLine(GitDiffLineKind.Context, 7, 7, "keep-g"),
            new GitDiffLine(GitDiffLineKind.Context, 8, 8, "keep-h"),
            new GitDiffLine(GitDiffLineKind.Context, 9, 9, "keep-i"),
            new GitDiffLine(GitDiffLineKind.Context, 10, 10, "keep-j"),
            new GitDiffLine(GitDiffLineKind.Removed, 11, null, "gone"),
            new GitDiffLine(GitDiffLineKind.Added, null, 11, "new"),
            new GitDiffLine(GitDiffLineKind.Context, 12, 12, "keep-k"),
            new GitDiffLine(GitDiffLineKind.Context, 13, 13, "keep-l"),
            new GitDiffLine(GitDiffLineKind.Context, 14, 14, "keep-m"),
            new GitDiffLine(GitDiffLineKind.Context, 15, 15, "keep-n"),
            new GitDiffLine(GitDiffLineKind.Context, 16, 16, "keep-o"),
            new GitDiffLine(GitDiffLineKind.Context, 17, 17, "keep-p"),
            new GitDiffLine(GitDiffLineKind.Context, 18, 18, "keep-q"),
            new GitDiffLine(GitDiffLineKind.Context, 19, 19, "keep-r"),
            new GitDiffLine(GitDiffLineKind.Context, 20, 20, "keep-s"),
        ]);
        var diff = new GitFileDiff("a.txt", null, false, false, false, [hunk]);
        var sideBySide = DiffDocumentBuilders.BuildSideBySide(diff.ToSideBySideRows());
        var source = DiffDocumentBuilders.BuildSideBySideCollapseSource(sideBySide.Old, sideBySide.New);
        var map = new DiffDisplayMap(source, collapseContext: true, includeHunkHeaders: false);

        Assert.Contains(map.Lines, line => line.IsCollapsedContext);
        Assert.Contains(map.Lines, line => line.Text.Contains("未更改内容"));
        Assert.DoesNotContain(map.Lines, line => line.Source?.Kind == GitDiffLineKind.HunkHeader);
        Assert.All(map.Lines.Where(line => line.IsCollapsedContext), line =>
            Assert.Null(line.Source));

        var oldDisplay = map.Lines.Select(line => line.ToRenderLine(sideBySide.Old)).ToArray();
        var newDisplay = map.Lines.Select(line => line.ToRenderLine(sideBySide.New)).ToArray();
        var oldPrompts = oldDisplay.Where(line => line.IsCollapsedContext).ToArray();
        var newPrompts = newDisplay.Where(line => line.IsCollapsedContext).ToArray();
        Assert.Equal(oldPrompts.Length, newPrompts.Length);
        Assert.NotEmpty(oldPrompts);
        Assert.Equal(oldPrompts.Select(line => line.Text), newPrompts.Select(line => line.Text));
        Assert.Equal(oldPrompts.Select(line => line.HiddenLineCount), newPrompts.Select(line => line.HiddenLineCount));
    }

    [Fact]
    public void ContextProjection_MergesContextAcrossHunkHeaderWhenNoChangeLiesBetween()
    {
        var lines = new List<DiffRenderLine>
        {
            new("@@ -1,11 +1,11 @@", GitDiffLineKind.HunkHeader, null, null),
            new("first change", GitDiffLineKind.Removed, 1, null),
        };
        for (var lineNumber = 2; lineNumber <= 11; lineNumber++)
        {
            lines.Add(new DiffRenderLine($"context-{lineNumber}", GitDiffLineKind.Context, lineNumber, lineNumber));
        }

        lines.Add(new DiffRenderLine("@@ -12,11 +12,11 @@", GitDiffLineKind.HunkHeader, null, null));
        for (var lineNumber = 12; lineNumber <= 21; lineNumber++)
        {
            lines.Add(new DiffRenderLine($"context-{lineNumber}", GitDiffLineKind.Context, lineNumber, lineNumber));
        }

        lines.Add(new DiffRenderLine("second change", GitDiffLineKind.Added, null, 22));

        var map = new DiffDisplayMap(lines, collapseContext: true);
        var prompts = map.Lines.Where(line => line.IsCollapsedContext).ToArray();

        // Two Git hunks contribute one unchanged run between the two actual changes, so there is
        // one prompt for all 20 context rows (3 visible on each side, 14 hidden in the middle).
        Assert.Single(prompts);
        Assert.Equal(14, prompts[0].HiddenLineCount);

        var promptIndex = map.Lines.ToList().FindIndex(line => line.IsCollapsedContext);
        Assert.True(map.ExpandAtDisplayIndex(promptIndex));
        Assert.DoesNotContain(map.Lines, line => line.IsCollapsedContext);
        Assert.Equal(lines.Count, map.Lines.Count);
    }

    [Fact]
    public void CountBlocks_GroupsContiguousChanges()
    {
        var lines = DiffDocumentBuilders.BuildInline(SampleDiff().Hunks.SelectMany(h => h.Lines));

        Assert.Equal(1, DiffDocumentBuilders.CountBlocks(lines.Select(l => l.Kind)));
    }

    [Fact]
    public void CountBlocks_SeparatedByContext_AreDistinctBlocks()
    {
        var kinds = new[]
        {
            GitDiffLineKind.Context, GitDiffLineKind.Added, GitDiffLineKind.Removed,
            GitDiffLineKind.Context, GitDiffLineKind.Added, GitDiffLineKind.Context,
        };

        Assert.Equal(2, DiffDocumentBuilders.CountBlocks(kinds));
    }

    [Fact]
    public void BlockAtIndex_ReturnsInclusiveRanges()
    {
        var lines = DiffDocumentBuilders.BuildInline(SampleDiff().Hunks.SelectMany(h => h.Lines));
        // block 0 spans the removed+added run: indices 1..3
        var (start, end) = DiffDocumentBuilders.BlockAtIndex(lines, 0);

        Assert.Equal(1, start);
        Assert.Equal(3, end);
        Assert.All(lines.Skip(start).Take(end - start + 1), line => Assert.True(line.IsChange));
    }

    [Fact]
    public void BlockAtIndex_OutOfRange_ReturnsNegativeMarkers()
    {
        var lines = DiffDocumentBuilders.BuildInline(Array.Empty<GitDiffLine>());

        Assert.Equal((-1, -1), DiffDocumentBuilders.BlockAtIndex(lines, 0));
    }

    [Fact]
    public void BuildSideBySideChangeMask_MarksRowsChangedWhenEitherSideChanged()
    {
        var rows = SampleDiff().ToSideBySideRows();
        var (oldSide, newSide) = DiffDocumentBuilders.BuildSideBySide(rows);
        var mask = DiffDocumentBuilders.BuildSideBySideChangeMask(oldSide, newSide);

        Assert.Equal(oldSide.Count, mask.Count);
        // spacer + context are not changes; the removed/added pair and the uneven added row
        // are; the trailing context is not.
        Assert.False(mask[0].IsChange); // hunk spacer (both cells empty)
        Assert.False(mask[1].IsChange); // context "keep-a"
        Assert.True(mask[2].IsChange);  // removed "gone" | added "fresh"
        Assert.True(mask[3].IsChange);  // (empty) | added "extra"
        Assert.False(mask[4].IsChange); // context "keep-b"
    }

    [Fact]
    public void BuildSideBySideChangeMask_BlockStartsTrackSideRowAlignment()
    {
        // A modified pair is two inline rows but ONE side row: the second block's side start
        // must not carry the inline projection's extra row (the drift that made side-by-side
        // change navigation scroll below the real block).
        var diff = new GitFileDiff("a.txt", null, false, false, false,
        [
            new GitDiffHunk(1, 5, 1, 5, "@@ -1,5 +1,5 @@",
            [
                new GitDiffLine(GitDiffLineKind.HunkHeader, null, null, "@@ -1,5 +1,5 @@"),
                new GitDiffLine(GitDiffLineKind.Context, 1, 1, "a"),
                new GitDiffLine(GitDiffLineKind.Removed, 2, null, "b-old"),
                new GitDiffLine(GitDiffLineKind.Added, null, 2, "b-new"),
                new GitDiffLine(GitDiffLineKind.Removed, 3, null, "c-old"),
                new GitDiffLine(GitDiffLineKind.Added, null, 3, "c-new"),
                new GitDiffLine(GitDiffLineKind.Context, 4, 4, "d"),
                new GitDiffLine(GitDiffLineKind.Removed, 5, null, "e-old"),
                new GitDiffLine(GitDiffLineKind.Added, null, 5, "e-new"),
            ]),
        ]);

        var inline = DiffDocumentBuilders.BuildInline(diff.Hunks.SelectMany(h => h.Lines));
        var (oldSide, newSide) = DiffDocumentBuilders.BuildSideBySide(diff.ToSideBySideRows());
        var mask = DiffDocumentBuilders.BuildSideBySideChangeMask(oldSide, newSide);

        // Same block count in both layouts ...
        Assert.Equal(
            DiffDocumentBuilders.CountBlocks(inline.Select(l => l.Kind)),
            DiffDocumentBuilders.CountBlocks(mask.Select(l => l.Kind)));

        var (inlineStart, _) = DiffDocumentBuilders.BlockAtIndex(inline, 1);
        var (sideStart, _) = DiffDocumentBuilders.BlockAtIndex(mask, 1);

        // Inline block 1 starts at "e-old" (index 7); the side block 1 sits at the aligned
        // side row (index 6) — one fewer per modified pair before it, minus the spacer the
        // side layout adds before the hunk.
        Assert.Equal(7, inlineStart);
        Assert.Equal(6, sideStart);
        Assert.True(sideStart < inlineStart);

        // The side row at the mask block start is the changed row in BOTH side documents.
        Assert.True(oldSide[sideStart].IsChange);
        Assert.True(newSide[sideStart].IsChange);
    }
}

/// <summary>DiffTab state: change statistics, next/previous navigation and layout toggle.</summary>
public sealed class DiffTabNavigationTests
{
    private static GitFileDiff SampleDiff() => new(
        "src/A.cs",
        null,
        false,
        false,
        false,
        [
            new GitDiffHunk(1, 2, 1, 2, "@@ -1,2 +1,2 @@",
            [
                new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep"),
                new GitDiffLine(GitDiffLineKind.Removed, 2, null, "old-line"),
                new GitDiffLine(GitDiffLineKind.Added, null, 2, "new-line"),
                new GitDiffLine(GitDiffLineKind.Added, null, 3, "extra"),
            ])
        ]);

    private static EditorAreaViewModel CreateEditor(FakeGitService git) =>
        new(git, new FakeUiLogService());

    [Fact]
    public async Task Load_ComputesChangeStatistics()
    {
        var editor = CreateEditor(new FakeGitService { DiffResult = SampleDiff() });

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false));

        var diff = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal(1, diff.RemovedCount);
        Assert.Equal(2, diff.AddedCount);
        Assert.Equal(1, diff.ChangeCount);
        Assert.Equal("+2 −1", diff.ChangeSummary);
        Assert.Equal("1 / 1", diff.ChangeCounter);
    }

    [Fact]
    public async Task SourceLabel_ReflectsDiffSource()
    {
        var editor = CreateEditor(new FakeGitService { DiffResult = SampleDiff() });
        var hash = "c".PadRight(40, '0');

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "a.cs", IsStaged: true, false));
        Assert.Equal("已暂存", Assert.IsType<DiffTab>(editor.OpenTabs[0]).SourceLabel);

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "b.cs", false, IsUntracked: true));
        Assert.Equal("未跟踪", Assert.IsType<DiffTab>(editor.OpenTabs[1]).SourceLabel);

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "c.cs", false, false, hash));
        Assert.Equal("提交 c000000", Assert.IsType<DiffTab>(editor.OpenTabs[2]).SourceLabel);

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "d.cs", false, false));
        Assert.Equal("未暂存", Assert.IsType<DiffTab>(editor.OpenTabs[3]).SourceLabel);
    }

    [Fact]
    public async Task ChangeNavigation_CyclesAndRaisesRequests()
    {
        var editor = CreateEditor(new FakeGitService
        {
            DiffResult = new GitFileDiff("a.cs", null, false, false, false,
            [
                new GitDiffHunk(1, 4, 1, 4, "@@ -1,4 +1,4 @@",
                [
                    new GitDiffLine(GitDiffLineKind.Added, null, 1, "a1"),
                    new GitDiffLine(GitDiffLineKind.Context, 1, 2, "ctx"),
                    new GitDiffLine(GitDiffLineKind.Added, null, 3, "a2"),
                ])
            ]),
        });
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "a.cs", false, false));
        var diff = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal(2, diff.ChangeCount);

        var jumps = new List<int>();
        diff.ChangeNavigationRequested += (_, block) => jumps.Add(block);

        diff.GoToNextChangeCommand.Execute(null);
        Assert.Equal("2 / 2", diff.ChangeCounter);
        diff.GoToNextChangeCommand.Execute(null); // wraps
        Assert.Equal("1 / 2", diff.ChangeCounter);
        diff.GoToPreviousChangeCommand.Execute(null);
        Assert.Equal("2 / 2", diff.ChangeCounter);
        Assert.Equal([1, 0, 1], jumps);
    }

    [Fact]
    public async Task ModeToggle_FlipsLayoutAndLabel()
    {
        var editor = CreateEditor(new FakeGitService { DiffResult = SampleDiff() });
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "a.cs", false, false));
        var diff = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));

        Assert.True(diff.IsSideBySideDiff);
        Assert.Equal("并排 Diff", diff.DiffModeLabel);

        diff.ToggleDiffModeCommand.Execute(null);

        Assert.True(diff.IsInlineDiff);
        Assert.Equal("内联 Diff", diff.DiffModeLabel);
        Assert.False(diff.IsSideBySideDiff);
    }

    [Fact]
    public void DiffDisplayMap_ToggleCollapseContext_RebuildsProjection()
    {
        var lines = Enumerable.Range(1, 17)
            .Select(line => line == 9
                ? new DiffRenderLine("changed", GitDiffLineKind.Added, null, line)
                : new DiffRenderLine($"line {line}", GitDiffLineKind.Context, line, line))
            .ToArray();

        var map = new DiffDisplayMap(lines, collapseContext: true);
        var prompts = map.Lines.Where(line => line.IsCollapsedContext).ToArray();
        Assert.Equal(2, prompts.Length);
        Assert.All(prompts, line => Assert.Equal(2, line.HiddenLineCount));

        var promptIndex = map.Lines.ToList().IndexOf(prompts[0]);
        Assert.True(map.ExpandAtDisplayIndex(promptIndex));
        Assert.Single(map.Lines, line => line.IsCollapsedContext);

        var remainingPrompt = Assert.Single(map.Lines, line => line.IsCollapsedContext);
        Assert.True(map.ExpandAtDisplayIndex(map.Lines.ToList().IndexOf(remainingPrompt)));
        Assert.DoesNotContain(map.Lines, line => line.IsCollapsedContext);
        Assert.Equal(lines.Length, map.Lines.Count);

        map.SetCollapseContext(false);
        Assert.DoesNotContain(map.Lines, line => line.IsCollapsedContext);
        Assert.Equal(lines.Length, map.Lines.Count);

        map.SetCollapseContext(true);
        Assert.Contains(map.Lines, line => line.IsCollapsedContext);
    }

    [Fact]
    public async Task ManualLayoutOverride_PreventsAutoInlineSwitchOnNarrowWidth()
    {
        var editor = CreateEditor(new FakeGitService { DiffResult = SampleDiff() });
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "a.cs", false, false));
        var diff = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));

        diff.UseInlineWhenNarrow = true;
        diff.IsLayoutManuallySelected = false;
        Assert.True(diff.ShouldAutoUseInlineForWidth(699));
        Assert.False(diff.ShouldAutoUseInlineForWidth(700));
        Assert.False(diff.ShouldAutoUseInlineForWidth(2000));

        diff.DiffMode = GitDiffMode.SideBySide;
        diff.IsLayoutManuallySelected = true;
        Assert.False(diff.ShouldAutoUseInlineForWidth(500));

        diff.DiffMode = GitDiffMode.Inline;
        diff.IsLayoutManuallySelected = true;
        Assert.False(diff.ShouldAutoUseInlineForWidth(500));
    }
}
