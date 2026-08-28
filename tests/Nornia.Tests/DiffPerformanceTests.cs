using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Git.Parsing;

namespace Nornia.Tests;

/// <summary>Performance-path tests for the diff subsystem improvements D2–D9
/// (docs/performance-review.md §2.5): single-pass tokenization, document-level budget,
/// compact spool format, hunk-index binary search, VS Code fold thresholds, window pagination,
/// and the incremental diff parser.</summary>
public sealed class DiffPerformanceTests
{
    // ------------------------------------------------------------------ D2

    [Fact]
    public void D2_Refine_SharesOneTokenizePassInsteadOfThree()
    {
        const string oldText = "return oldValue;";
        const string newText = "return newValue;";

        // The pre-D2 path tokenized each side three times (Similarity, anchor, Build) = 6
        // total. The single-pass Refine must tokenize each side exactly once = 2 total. The
        // counter is process-wide, so measure in a quiet window and retry if a parallel test
        // happens to tokenize in the meantime.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var before = Interlocked.Read(ref IntralineDiffBuilder.TextElementsInvocations);
            IntralineDiffBuilder.Similarity(oldText, newText);
            IntralineDiffBuilder.HasMeaningfulAnchor(oldText, newText);
            IntralineDiffBuilder.Build(oldText, newText);
            var legacyDelta = Interlocked.Read(ref IntralineDiffBuilder.TextElementsInvocations) - before;
            if (legacyDelta != 6)
            {
                Thread.Sleep(20);
                continue;
            }

            var beforeRefine = Interlocked.Read(ref IntralineDiffBuilder.TextElementsInvocations);
            var result = IntralineDiffBuilder.Refine(oldText, newText);
            var refinedDelta = Interlocked.Read(ref IntralineDiffBuilder.TextElementsInvocations) - beforeRefine;
            if (refinedDelta != 2)
            {
                Thread.Sleep(20);
                continue;
            }

            Assert.True(result.Refined);
            return;
        }

        Assert.Fail("could not isolate TextElements invocations from parallel tests");
    }

    [Fact]
    public void D2_Refine_AgreesWithLegacyGateAndBuild()
    {
        var samples = new[]
        {
            ("return oldValue;", "return newValue;"),
            ("alpha old", "completely unrelated"),
            ("alpha old", "completely unrelated alpha"),
            ("", "brand new"),
            ("identical", "identical"),
            ("a b c d e", "a x c d e"),
        };
        foreach (var (oldText, newText) in samples)
        {
            var result = IntralineDiffBuilder.Refine(oldText, newText);
            var shouldRefine = IntralineDiffBuilder.Similarity(oldText, newText) >= IntralineDiffBuilder.MinBlockSimilarity
                || IntralineDiffBuilder.HasMeaningfulAnchor(oldText, newText);
            if (!shouldRefine)
            {
                Assert.False(result.Refined, $"{oldText} | {newText}");
                continue;
            }

            var legacy = IntralineDiffBuilder.Build(oldText, newText);
            Assert.True(result.Refined, $"{oldText} | {newText}");
            Assert.True(legacy.Old.SequenceEqual(result.Old), $"{oldText} | {newText}");
            Assert.True(legacy.New.SequenceEqual(result.New), $"{oldText} | {newText}");
        }
    }

    [Fact]
    public void D2_BlockCache_ReusesRefinedResultAcrossRebuilds()
    {
        var cache = new DiffRefinementCache();
        var lines = new[]
        {
            new GitDiffLine(GitDiffLineKind.Removed, 1, null, "return oldValue;"),
            new GitDiffLine(GitDiffLineKind.Added, null, 1, "return newValue;"),
        };
        var options = new DiffRefineOptions(RefinementCache: cache);

        var first = DiffDocumentBuilders.BuildInlineRefined(lines, options);
        Assert.False(first.IntralineOmitted);
        Assert.NotEmpty(first.Lines[0].IntralineChanges!);
        Assert.Equal(1, cache.Count);

        var before = Interlocked.Read(ref IntralineDiffBuilder.TextElementsInvocations);
        var second = DiffDocumentBuilders.BuildInlineRefined(lines, options);
        var after = Interlocked.Read(ref IntralineDiffBuilder.TextElementsInvocations);

        // Fold-toggle style rebuild over identical block texts: cache hit, zero tokenization.
        Assert.Equal(0, after - before);
        Assert.Equal(first.Signature, second.Signature);
        Assert.Equal(first.FullText, second.FullText);
        Assert.Equal(first.Lines[0].IntralineChanges, second.Lines[0].IntralineChanges);
    }

    [Fact]
    public void D2_Cache_ClearedWhenFoldProjectionChanges()
    {
        var cache = new DiffRefinementCache();
        var lines = new[]
        {
            new GitDiffLine(GitDiffLineKind.Removed, 1, null, "value old;"),
            new GitDiffLine(GitDiffLineKind.Added, null, 1, "value new;"),
        };
        _ = DiffDocumentBuilders.BuildInlineRefined(lines, new DiffRefineOptions(RefinementCache: cache));
        Assert.Equal(1, cache.Count);

        cache.SetProjectionSignature(1);
        Assert.Equal(1, cache.Count); // same projection: no-op

        cache.SetProjectionSignature(2);
        Assert.Equal(0, cache.Count); // projection changed: cleared

        cache.SetProjectionSignature(3);
        cache.Invalidate();
        Assert.Equal(0, cache.Count);
    }

    // ------------------------------------------------------------------ D3

    private static List<GitDiffLine> FourRefinableBlocks()
    {
        var lines = new List<GitDiffLine>();
        for (var block = 0; block < 4; block++)
        {
            lines.Add(new GitDiffLine(GitDiffLineKind.Removed, 2 * block + 1, null, $"value old {block};"));
            lines.Add(new GitDiffLine(GitDiffLineKind.Added, null, 2 * block + 1, $"value new {block};"));
            lines.Add(new GitDiffLine(GitDiffLineKind.Context, 2 * block + 2, 2 * block + 2, $"context {block}"));
        }

        return lines;
    }

    [Fact]
    public void D3_ExhaustedBudget_SkipsRemainingBlocksAndReportsOmitted()
    {
        var budget = new IntralineDiffBudget(TimeSpan.Zero);

        var result = DiffDocumentBuilders.BuildInlineRefined(FourRefinableBlocks(), new DiffRefineOptions(Budget: budget));

        Assert.True(result.IntralineOmitted, "the build must report that character-level diff was omitted");
        Assert.True(budget.IsExhausted);
        Assert.All(result.Lines.Where(line => line.IsChange), line => Assert.Null(line.IntralineChanges));
    }

    [Fact]
    public void D3_FreshBudget_RefinesEveryBlock()
    {
        var budget = new IntralineDiffBudget(TimeSpan.FromSeconds(30));

        var result = DiffDocumentBuilders.BuildInlineRefined(FourRefinableBlocks(), new DiffRefineOptions(Budget: budget));

        Assert.False(result.IntralineOmitted);
        Assert.False(budget.IsExhausted);
        // 4 个替换块 × 两侧各一条变更行,全部精化。
        Assert.Equal(8, result.Lines.Count(line => line.IntralineChanges is { Count: > 0 }));
    }

    // ------------------------------------------------------------------ D4

    private static async IAsyncEnumerable<GitDiffEvent> EventsAsync(IReadOnlyList<GitDiffEvent> events)
    {
        foreach (var item in events)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static IReadOnlyList<GitDiffEvent> SampleDiffEvents() =>
    [
        new GitDiffMetadataEvent("src/A.cs", "src/A.cs", false, false, false),
        new GitDiffHunkEvent(10, 4, 10, 5, "@@ -10,4 +10,5 @@"),
        new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Context, 10, 10, " context-one")),
        new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Removed, 11, null, "removed-line")),
        new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Added, null, 11, "added-line 你好 🎉")),
        new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Added, null, 12, "")),
        new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Context, 12, 13, " context-two")),
        new GitDiffHunkEvent(50, 1, 50, 2, "@@ -50,1 +50,2 @@"),
        new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Removed, 50, null, "old")),
        new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Added, null, 50, "new")),
        new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Notice, null, null, "\\ No newline at end of file")),
        new GitDiffCompletedEvent(7, true, "boom"),
    ];

    [Fact]
    public async Task D4_CompactSpool_RoundTripsEveryEventKindByteIdentically()
    {
        var original = SampleDiffEvents();

        var source = await SpoolingDiffContentSource.CreateAsync(EventsAsync(original));
        try
        {
            var metadata = await source.GetMetadataAsync();
            Assert.Equal(8, metadata.TotalLines);
            // The sample completes with OutputLimitReached=true: the limit flag must round-trip.
            Assert.False(metadata.IsComplete);
            Assert.Equal(ReadOnlyContentTier.Summary, metadata.CapacityTier);
            Assert.Equal(2, metadata.Hunks.Count);
            Assert.Equal(1, metadata.Hunks[0].DisplayLine);
            Assert.Equal(6, metadata.Hunks[1].DisplayLine);
            Assert.Equal(10, metadata.Hunks[0].OldStart);
            Assert.Equal(50, metadata.Hunks[1].NewStart);
            Assert.Equal("@@ -10,4 +10,5 @@", metadata.Hunks[0].Header);

            var window = await source.ReadWindowAsync(new DocumentRange(1, 10_000), 10_000);
            // Compact format write → read back: the event stream is identical, in order.
            Assert.Equal(original, window.Events);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task D4_CompactSpool_ReconstructsIdenticalGitFileDiff()
    {
        const string diffText = """
            diff --git a/src/A.cs b/src/A.cs
            index 123abc..456def 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -10,3 +10,4 @@
             context-one
            -removed-line
            +added-line
            +another-added
             context-two
            @@ -50,1 +51,1 @@
            -old
            +new
            \ No newline at end of file
            """;

        var direct = GitDiffParser.Parse(diffText, "src/A.cs");

        var parser = new GitDiffStreamParser("src/A.cs", false);
        var events = diffText.Split('\n').Select(parser.Accept).SelectMany(item => item).ToList();
        events.Add(parser.Metadata);
        events.Add(new GitDiffCompletedEvent(0, false, null));

        var source = await SpoolingDiffContentSource.CreateAsync(EventsAsync(events));
        try
        {
            var window = await source.ReadWindowAsync(new DocumentRange(1, 10_000), 10_000);
            var readBack = RebuildModel(window.Events, "src/A.cs");

            Assert.Equal(direct.Path, readBack.Path);
            Assert.Equal(direct.OldPath, readBack.OldPath);
            Assert.Equal(direct.IsStaged, readBack.IsStaged);
            Assert.Equal(direct.IsBinary, readBack.IsBinary);
            Assert.Equal(direct.IsNewFile, readBack.IsNewFile);
            Assert.Equal(direct.Hunks.Count, readBack.Hunks.Count);
            for (var i = 0; i < direct.Hunks.Count; i++)
            {
                var a = direct.Hunks[i];
                var b = readBack.Hunks[i];
                Assert.Equal(a.OldStart, b.OldStart);
                Assert.Equal(a.OldCount, b.OldCount);
                Assert.Equal(a.NewStart, b.NewStart);
                Assert.Equal(a.NewCount, b.NewCount);
                Assert.Equal(a.Header, b.Header);
                Assert.Equal(a.Lines, b.Lines);
            }
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    private static GitFileDiff RebuildModel(IReadOnlyList<GitDiffEvent> events, string path)
    {
        var metadata = events.OfType<GitDiffMetadataEvent>().FirstOrDefault();
        var hunkEvents = events.OfType<GitDiffHunkEvent>().ToList();
        var hunks = new List<GitDiffHunk>();
        var cursor = 0;
        for (var i = 0; i < hunkEvents.Count; i++)
        {
            var hunk = hunkEvents[i];
            var end = events.Count;
            if (i + 1 < hunkEvents.Count)
            {
                for (var scan = cursor; scan < events.Count; scan++)
                {
                    if (ReferenceEquals(events[scan], hunkEvents[i + 1]))
                    {
                        end = scan;
                        break;
                    }
                }
            }
            var body = events.Skip(cursor).Take(end - cursor).OfType<GitDiffLineEvent>().Select(item => item.Line).ToList();
            cursor = end;
            var lines = new List<GitDiffLine> { new(GitDiffLineKind.HunkHeader, null, null, hunk.Header) };
            lines.AddRange(body);
            hunks.Add(new GitDiffHunk(hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount, string.Empty, lines));
        }

        return new GitFileDiff(
            metadata?.Path ?? path,
            metadata?.OldPath,
            metadata?.IsStaged ?? false,
            metadata?.IsBinary ?? false,
            metadata?.IsNewFile ?? false,
            hunks);
    }

    // ------------------------------------------------------------------ D5

    private static (List<DiffRenderLine> Lines, List<GitDiffHunk> Hunks) HunkDocument()
    {
        var lines = new List<DiffRenderLine>();
        var hunks = new List<GitDiffHunk>
        {
            new(10, 10, 10, 10, "@@ -10,10 +10,10 @@", []),
            new(500, 1, 500, 1, "@@ -500,1 +500,1 @@", []),
            new(900, 5, 900, 6, "@@ -900,5 +900,6 @@", []),
        };
        for (var hunk = 0; hunk < hunks.Count; hunk++)
        {
            var model = hunks[hunk];
            lines.Add(new DiffRenderLine(model.Header, GitDiffLineKind.HunkHeader, null, null, HunkIndex: hunk));
            for (var i = 0; i < model.OldCount; i++)
            {
                lines.Add(new DiffRenderLine($"line {model.OldStart + i}", GitDiffLineKind.Context, model.OldStart + i, model.NewStart + i, HunkIndex: hunk));
            }
        }

        return (lines, hunks);
    }

    [Fact]
    public void D5_HunkIndex_BinarySearchMatchesLinearScan()
    {
        var (lines, hunks) = HunkDocument();
        var index = DiffHunkIndex.Build(lines, hunks);

        Assert.Equal(3, index.Count);
        Assert.Equal(0, index.HunkStartDisplayLine(0));
        Assert.Equal(11, index.HunkStartDisplayLine(1));
        Assert.Equal(13, index.HunkStartDisplayLine(2));
        Assert.Equal(10, index.OldStart(0));
        Assert.Equal(900, index.NewStart(2));

        // Binary search vs a linear scan, over every display line (and the boundaries).
        for (var display = -1; display <= lines.Count; display++)
        {
            var expectedPrevious = -1;
            for (var h = 0; h < index.Count; h++)
            {
                if (index.HunkStartDisplayLine(h) <= display) expectedPrevious = h;
            }

            var expectedNext = index.Count;
            for (var h = 0; h < index.Count; h++)
            {
                if (index.HunkStartDisplayLine(h) > display)
                {
                    expectedNext = h;
                    break;
                }
            }

            Assert.True(expectedPrevious == index.PreviousHunkAtDisplayLine(display), $"display {display}");
            Assert.True(expectedNext == index.NextHunkAfterDisplayLine(display), $"display {display}");
        }

        // Old/new source-line containment via the sorted start arrays.
        Assert.Equal(0, index.HunkForOldLine(10));
        Assert.Equal(0, index.HunkForOldLine(19));
        Assert.Equal(-1, index.HunkForOldLine(20));
        Assert.Equal(1, index.HunkForOldLine(500));
        Assert.Equal(-1, index.HunkForOldLine(501));
        Assert.Equal(2, index.HunkForOldLine(904));
        Assert.Equal(-1, index.HunkForOldLine(905)); // old range [900, 905) — 905 is exclusive
        Assert.Equal(2, index.HunkForNewLine(905));  // new range [900, 906)
        Assert.Equal(-1, index.HunkForNewLine(906));
    }

    [Fact]
    public void D5_OverviewAndSticky_UseTheHunkIndex()
    {
        var (lines, hunks) = HunkDocument();
        var index = DiffHunkIndex.Build(lines, hunks);

        // Sticky header: the last hunk header at or before the viewport top (binary search).
        Assert.Equal(-1, DiffOverviewLayout.StickyHunkHeaderLine(index, -1));
        Assert.Equal(0, DiffOverviewLayout.StickyHunkHeaderLine(index, 0));
        Assert.Equal(0, DiffOverviewLayout.StickyHunkHeaderLine(index, 5));
        Assert.Equal(11, DiffOverviewLayout.StickyHunkHeaderLine(index, 11));
        Assert.Equal(11, DiffOverviewLayout.StickyHunkHeaderLine(index, 12));
        Assert.Equal(13, DiffOverviewLayout.StickyHunkHeaderLine(index, lines.Count - 1));

        // Per-scroll overview state: base colors once, HasCurrent re-stamped in O(buckets).
        var state = new DiffOverviewState(lines, 10, index);
        var header2 = index.HunkStartDisplayLine(1);
        var buckets = state.WithCurrent(header2);

        var expectedBucket = DiffOverviewLayout.BucketForLine(header2, 10, lines.Count);
        Assert.True(buckets[expectedBucket].HasCurrent);
        Assert.Equal(1, buckets.Count(bucket => bucket.HasCurrent));
        Assert.Equal(1, state.CurrentHunkAtDisplayLine(header2));
        Assert.Equal(-1, state.CurrentHunkAtDisplayLine(index.HunkStartDisplayLine(0) - 1));

        // Moving the current block re-stamps without touching the line list.
        var moved = state.WithCurrent(index.HunkStartDisplayLine(2));
        var movedBucket = DiffOverviewLayout.BucketForLine(index.HunkStartDisplayLine(2), 10, lines.Count);
        Assert.Equal(1, moved.Count(bucket => bucket.HasCurrent));
        Assert.True(moved[movedBucket].HasCurrent);
    }

    // ------------------------------------------------------------------ D6

    [Fact]
    public void D6_BuildResult_ExposesFullTextsSignatureAndCache()
    {
        var lines = FourRefinableBlocks();

        var first = DiffDocumentBuilders.BuildInlineRefined(lines);
        Assert.Equal(string.Join('\n', lines.Select(line => line.Text)), first.FullText);
        Assert.Equal(DiffDocumentBuilders.ComputeSignature(lines), first.Signature);

        // A different document version produces a different signature.
        var changed = lines.Select(line => line.Text == "value old 0;"
            ? line with { Text = "value changed 0;" }
            : line).ToList();
        Assert.NotEqual(first.Signature, DiffDocumentBuilders.ComputeSignature(changed));

        // Cached build result keyed by the input signature: the factory runs once.
        var cache = new DiffBuildCache();
        var builds = 0;
        DiffBuildResult GetOrBuild() => cache.GetOrPut(first.Signature, () =>
        {
            builds++;
            return new DiffBuildResult(first.Signature, DiffDocumentBuilders.BuildInlineRefined(lines), null);
        });

        var a = GetOrBuild();
        var b = GetOrBuild();
        Assert.Same(a, b);
        Assert.Equal(1, builds);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(first.Signature, out _));
        Assert.False(cache.TryGet(first.Signature + 1, out _));
    }

    // ------------------------------------------------------------------ D7

    private static List<DiffRenderLine> ContextRun(int runLength, bool changeBefore, bool changeAfter)
    {
        var lines = new List<DiffRenderLine>();
        if (changeBefore)
        {
            lines.Add(new DiffRenderLine("changed-before", GitDiffLineKind.Added, null, 1));
        }

        for (var i = 0; i < runLength; i++)
        {
            lines.Add(new DiffRenderLine($"context {i}", GitDiffLineKind.Context, i + 1, i + 1));
        }

        if (changeAfter)
        {
            lines.Add(new DiffRenderLine("changed-after", GitDiffLineKind.Removed, 2, null));
        }

        return lines;
    }

    [Fact]
    public void D7_FoldThresholds_EdgeAndMiddleRunsFollowVsCodeRule()
    {
        const int context = 3;

        // Middle run: folds at 2·context+1, not at 2·context.
        var middleFold = DiffContextProjection.Build(ContextRun(2 * context + 1, true, true));
        var placeholder = Assert.Single(middleFold, line => line.IsCollapsedContext);
        Assert.Equal(1, placeholder.HiddenLineCount);

        var middleShort = DiffContextProjection.Build(ContextRun(2 * context, true, true));
        Assert.DoesNotContain(middleShort, line => line.IsCollapsedContext);

        // Edge run (touches a document edge): folds once it can keep `context` on the change
        // side and hide a line. Length 5 hides one line; length 4 has nothing to hide.
        var edgeFold = DiffContextProjection.Build(ContextRun(context + 2, false, true));
        Assert.Single(edgeFold, line => line.IsCollapsedContext && line.HiddenLineCount == 1);

        var edgeTrailing = DiffContextProjection.Build(ContextRun(context + 2, true, false));
        Assert.Single(edgeTrailing, line => line.IsCollapsedContext);

        var edgeTiny = DiffContextProjection.Build(ContextRun(context + 1, false, true));
        Assert.DoesNotContain(edgeTiny, line => line.IsCollapsedContext);
        // 4 行上下文 + 1 行变更(尾部)= 5 行全部保持展开。
        Assert.Equal(context + 2, edgeTiny.Count(line => !line.IsCollapsedContext));
    }

    [Fact]
    public void D7_HiddenBlocksAreSkippedForRefinement()
    {
        var lines = new[]
        {
            new GitDiffLine(GitDiffLineKind.Removed, 1, null, "value old one;"),
            new GitDiffLine(GitDiffLineKind.Added, null, 1, "value new one;"),
            new GitDiffLine(GitDiffLineKind.Context, 2, 2, "gap"),
            new GitDiffLine(GitDiffLineKind.Removed, 3, null, "value old two;"),
            new GitDiffLine(GitDiffLineKind.Added, null, 3, "value new two;"),
        };

        // Fully hidden block 1 is skipped; block 2 is still refined.
        var hidden = DiffDocumentBuilders.BuildInlineRefined(lines,
            new DiffRefineOptions(HiddenOriginalIndexes: new HashSet<int> { 0, 1 }));
        Assert.Null(hidden.Lines[0].IntralineChanges);
        Assert.Null(hidden.Lines[1].IntralineChanges);
        Assert.NotNull(hidden.Lines[3].IntralineChanges);
        Assert.NotNull(hidden.Lines[4].IntralineChanges);

        // A partially hidden block is still displayed, so it is refined.
        var partial = DiffDocumentBuilders.BuildInlineRefined(lines,
            new DiffRefineOptions(HiddenOriginalIndexes: new HashSet<int> { 0 }));
        Assert.NotNull(partial.Lines[0].IntralineChanges);
        Assert.NotNull(partial.Lines[3].IntralineChanges);

        // Without any hidden lines both blocks refine (baseline).
        var full = DiffDocumentBuilders.BuildInlineRefined(lines);
        Assert.NotNull(full.Lines[0].IntralineChanges);
        Assert.NotNull(full.Lines[3].IntralineChanges);
    }

    // ------------------------------------------------------------------ D8

    private static async Task<SpoolingDiffContentSource> CreateTwoHundredFiftyLineSourceAsync()
    {
        var events = new List<GitDiffEvent>
        {
            new GitDiffMetadataEvent("big.txt", "big.txt", false, false, false),
        };
        var n = 0;
        for (var hunk = 0; hunk < 3; hunk++)
        {
            var start = hunk * 100 + 1;
            var count = hunk < 2 ? 100 : 50;
            events.Add(new GitDiffHunkEvent(start, count, start, count, $"@@ -{start},{count} +{start},{count} @@"));
            for (var i = 0; i < count; i++)
            {
                n++;
                events.Add(new GitDiffLineEvent(new GitDiffLine(
                    i % 7 == 0 ? GitDiffLineKind.Removed : GitDiffLineKind.Context,
                    start + i,
                    start + i,
                    $"line {n}")));
            }
        }

        events.Add(new GitDiffCompletedEvent(0, false, null));
        return await SpoolingDiffContentSource.CreateAsync(EventsAsync(events));
    }

    [Fact]
    public async Task D8_ReadWindow_ServesArbitraryWindowsIncludingLastPartial()
    {
        var source = await CreateTwoHundredFiftyLineSourceAsync();
        try
        {
            var metadata = await source.GetMetadataAsync();
            Assert.Equal(250, metadata.TotalLines);
            Assert.Equal(3, metadata.Hunks.Count);

            var full = await source.ReadWindowAsync(new DocumentRange(1, 10_000), 10_000);
            var fullLines = full.Events.OfType<GitDiffLineEvent>().Select(item => item.Line.Text).ToList();

            // First window: starts at 1, no previous.
            var first = await source.ReadWindowAsync(new DocumentRange(1, 100));
            Assert.Equal(1, first.StartLine);
            Assert.False(first.HasPrevious);
            Assert.True(first.HasNext);
            Assert.Equal(100, first.Events.OfType<GitDiffLineEvent>().Count());
            Assert.Equal(fullLines[..100], first.Events.OfType<GitDiffLineEvent>().Select(item => item.Line.Text).ToList());

            // Mid window: starts inside a hunk, hunk header not repeated.
            var mid = await source.ReadWindowAsync(new DocumentRange(120, 50));
            Assert.Equal(120, mid.StartLine);
            Assert.True(mid.HasPrevious);
            Assert.True(mid.HasNext);
            Assert.Equal(50, mid.Events.OfType<GitDiffLineEvent>().Count());
            Assert.Equal(fullLines[119..169], mid.Events.OfType<GitDiffLineEvent>().Select(item => item.Line.Text).ToList());

            // Last partial window via the explicit max-window overload (100k first window for a
            // 250-line diff, as the viewer's initial load would do).
            var last = await source.ReadWindowAsync(new DocumentRange(200, 100_000), 100_000);
            Assert.Equal(200, last.StartLine);
            // 尾部可能含额外的元数据行(如 "\ No newline"),行数以全量读回为准。
            Assert.Equal(fullLines.Count - 199, last.Events.OfType<GitDiffLineEvent>().Count());
            Assert.False(last.HasNext);
            Assert.Equal(fullLines[199..], last.Events.OfType<GitDiffLineEvent>().Select(item => item.Line.Text).ToList());
            Assert.Contains(last.Events, item => item is GitDiffCompletedEvent);

            // A window starting past the end clamps to the final line.
            var pastEnd = await source.ReadWindowAsync(new DocumentRange(999, 10));
            Assert.Equal(250, pastEnd.StartLine);
            Assert.Single(pastEnd.Events.OfType<GitDiffLineEvent>());
            Assert.False(pastEnd.HasNext);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task D8_PaginatingAllWindows_YieldsEveryLineExactlyOnce()
    {
        var source = await CreateTwoHundredFiftyLineSourceAsync();
        try
        {
            var full = await source.ReadWindowAsync(new DocumentRange(1, 10_000), 10_000);
            var fullLines = full.Events.OfType<GitDiffLineEvent>().Select(item => item.Line).ToList();

            var paged = new List<GitDiffLine>();
            for (var start = 1; start <= 250; start += 100)
            {
                var window = await source.ReadWindowAsync(new DocumentRange(start, 100));
                paged.AddRange(window.Events.OfType<GitDiffLineEvent>().Select(item => item.Line));
                if (!window.HasNext)
                {
                    break;
                }
            }

            Assert.Equal(fullLines, paged);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ D9

    private const string ChunkedSample = """
        diff --git a/src/A.cs b/src/A.cs
        index 123abc..456def 100644
        --- a/src/A.cs
        +++ b/src/A.cs
        @@ -10,3 +10,4 @@
         context-one
        -removed-line
        +added-line 你好
        +another-added
         context-two
        @@ -50,1 +51,1 @@
        -old
        +new
        \ No newline at end of file
        """;

    [Fact]
    public void D9_IncrementalParser_EqualsWholeLineParsing()
    {
        // Whole-line parse (the pre-existing entry point).
        var whole = new GitDiffStreamParser("src/A.cs", false);
        var wholeEvents = ChunkedSample.Split('\n').Select(whole.Accept).SelectMany(item => item).ToList();
        var wholeMetadata = whole.Metadata;

        // Chunked parse: feed arbitrary slices that split lines mid-token; the parser carries
        // the partial line in its reusable buffer across AcceptChunk calls.
        var chunked = new GitDiffStreamParser("src/A.cs", false);
        var chunkedEvents = new List<GitDiffEvent>();
        var start = 0;
        var chunkSize = 1;
        while (start < ChunkedSample.Length)
        {
            var end = Math.Min(ChunkedSample.Length, start + chunkSize);
            chunkedEvents.AddRange(chunked.AcceptChunk(ChunkedSample.AsSpan(start, end - start)));
            start = end;
            chunkSize = 1 + (chunkSize * 7) % 9; // 1..9, always crossing line boundaries somewhere
        }

        chunkedEvents.AddRange(chunked.Finish());
        var chunkedMetadata = chunked.Metadata;

        Assert.NotEmpty(wholeEvents);
        Assert.Equal(wholeEvents, chunkedEvents);
        Assert.Equal(wholeMetadata, chunkedMetadata);
    }

    [Fact]
    public void D9_IncrementalParser_HandlesCrLfAndMissingFinalNewline()
    {
        // CRLF text: the '\r' before each '\n' is part of the terminator on the chunk path.
        var crlf = ChunkedSample.Replace("\n", "\r\n");

        var wholeCrlf = new GitDiffStreamParser("src/A.cs", false);
        var wholeCrlfEvents = crlf.Split(["\r\n"], StringSplitOptions.None).Select(wholeCrlf.Accept).SelectMany(item => item).ToList();

        var chunkedCrlf = new GitDiffStreamParser("src/A.cs", false);
        var chunkedCrlfEvents = FeedInChunks(chunkedCrlf, crlf, initialChunkSize: 2, stride: 5);
        chunkedCrlfEvents.AddRange(chunkedCrlf.Finish());
        Assert.Equal(wholeCrlfEvents, chunkedCrlfEvents);

        // Missing final newline: the last line sits in the parser's buffer until Finish flushes
        // it, and the result must equal whole-line parsing of the same text.
        var noTrailing = ChunkedSample.TrimEnd('\n');

        var whole = new GitDiffStreamParser("src/A.cs", false);
        var wholeEvents = noTrailing.Split('\n').Select(whole.Accept).SelectMany(item => item).ToList();

        var chunked = new GitDiffStreamParser("src/A.cs", false);
        var chunkedEvents = FeedInChunks(chunked, noTrailing, initialChunkSize: 3, stride: 7);

        var beforeFinish = chunkedEvents.Count;
        chunkedEvents.AddRange(chunked.Finish());
        Assert.True(chunkedEvents.Count > beforeFinish,
            "the partial final line must be carried over and flushed by Finish");
        Assert.Contains(chunkedEvents, item => item is GitDiffLineEvent { Line.Kind: GitDiffLineKind.Notice });

        Assert.Equal(wholeEvents, chunkedEvents);
    }

    private static List<GitDiffEvent> FeedInChunks(GitDiffStreamParser parser, string text, int initialChunkSize, int stride)
    {
        var events = new List<GitDiffEvent>();
        var start = 0;
        var chunkSize = initialChunkSize;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + chunkSize);
            events.AddRange(parser.AcceptChunk(text.AsSpan(start, end - start)));
            start = end;
            chunkSize = 1 + (chunkSize * stride) % 9;
        }

        return events;
    }

    [Fact]
    public void D9_SingleByteChunks_ProdceOneEventPerLine()
    {
        var parser = new GitDiffStreamParser("a.txt", true);
        var events = new List<GitDiffEvent>();
        foreach (var character in "@@ -1,2 +1,2 @@\n-aa\n+bb\n cc")
        {
            events.AddRange(parser.AcceptChunk(character.ToString().AsSpan()));
        }

        events.AddRange(parser.Finish());

        Assert.Single(events.OfType<GitDiffHunkEvent>());
        var lines = events.OfType<GitDiffLineEvent>().Select(item => item.Line).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("aa", lines[0].Text);
        Assert.Equal("bb", lines[1].Text);
        Assert.Equal("cc", lines[2].Text); // context: the ' ' prefix char is stripped
        Assert.Equal(2, lines[2].OldLineNumber);
        Assert.Equal(2, lines[2].NewLineNumber);
        Assert.True(parser.Metadata.IsStaged);
    }
}
