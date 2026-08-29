using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Git.Parsing;
using System.Text;

namespace Nornia.Tests;

// 与 PresentationStagingTests 同集合串行,避免 TextMateSharp 首次编译/原生初始化的并行首启竞态。
[Collection("TextMate")]
public sealed class LanguagePresentationTests
{
    [Fact]
    public async Task XamlComments_KeepCommentHighlightingForTextAndAcrossLines()
    {
        const string xaml = "<Grid><!-- same line --></Grid>\n<!-- first line\nsecond line -->\n<TextBlock />";
        var type = CodeFileTypeRegistry.Instance.FromPath("View.xaml");
        var snapshot = await CodePresentationService.Instance.AnalyzeAsync(xaml, type, 1);

        static CodeTokenSpan TokenAt(CodePresentationSnapshot result, int line, int column) =>
            result.Tokens.Where(token => token.Line == line && token.Start <= column && token.Start + token.Length > column).Last();

        Assert.Equal(CodeTokenKind.Comment, TokenAt(snapshot, 1, 12).Kind); // same-line body
        Assert.Equal(CodeTokenKind.Comment, TokenAt(snapshot, 2, 8).Kind);  // opening-line body
        Assert.Equal(CodeTokenKind.Comment, TokenAt(snapshot, 3, 2).Kind);  // continuation body
        Assert.Equal(CodeTokenRole.Comment, TokenTheme.Resolve(TokenAt(snapshot, 3, 2).Scopes)!.Role);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesTextMateTokensAndKeepsVersion()
    {
        var type = CodeFileTypeRegistry.Instance.FromPath("Demo.cs");
        var snapshot = await CodePresentationService.Instance.AnalyzeAsync("// note\npublic class Demo { string name = \"n\"; }", type, 37);

        Assert.Equal(37, snapshot.DocumentVersion);
        Assert.Contains(snapshot.Tokens, token => token.Kind == CodeTokenKind.Comment);
        Assert.Contains(snapshot.Tokens, token => token.Kind == CodeTokenKind.Keyword);
        Assert.NotEmpty(snapshot.Outline);
        Assert.NotEmpty(snapshot.LineDensity);
    }

    [Fact]
    public async Task Tokens_CarryOriginalScopeStacks_IncludingPlain()
    {
        // 与 AnalyzeAsync_UsesTextMateTokensAndKeepsVersion 同一类:TextMateSharp 首次编译 +
        // Oniguruma native 初始化在并行首启下会竞态(tokenize 抛异常被兜底吞掉,返回空集合),
        // 因此 tokenizer 用例放在同一类内顺序执行。
        var type = CodeFileTypeRegistry.Instance.FromPath("Demo.cs");
        var snapshot = await CodePresentationService.Instance.AnalyzeAsync("// note\npublic class Demo { string name = \"n\"; }\n", type, 5);

        Assert.Contains(snapshot.Tokens, token => token.Scopes is { Count: > 0 });
        Assert.Contains(snapshot.Tokens, token => token.Scopes is { } scopes &&
            scopes.Any(scope => scope.Contains("comment", StringComparison.OrdinalIgnoreCase)));
        // Plain token(如标识符)也被保留,主题规则(粗/斜体)可作用其上。
        Assert.Contains(snapshot.Tokens, token => token.Kind == CodeTokenKind.Plain && token.Scopes is { Count: > 0 });
    }

    [Fact]
    public async Task AnalyzeAsync_PrepresentsByLineIndexes_OnWorker()
    {
        var type = CodeFileTypeRegistry.Instance.FromPath("Demo.c");
        const string code = "int main(void)\n{\n    int x = 1;\n    return x;\n}\n";
        var snapshot = await CodePresentationService.Instance.AnalyzeAsync(code, type, 7);

        Assert.True(snapshot.Tokens.Count > 0, "C 源码应产生 token(否则无法校验索引)");
        Assert.NotNull(snapshot.TokensByLine);
        Assert.NotNull(snapshot.MinimapKindByLine);

        // 行划分与旧 UI 线程 GroupBy 语义一致:同样的行集合、同样的行内 token 顺序。
        Assert.Equal(
            snapshot.Tokens.Select(token => token.Line).Distinct().OrderBy(line => line),
            snapshot.TokensByLine!.Keys.OrderBy(line => line));
        foreach (var line in snapshot.TokensByLine!.Keys)
        {
            Assert.Equal(
                snapshot.Tokens.Where(token => token.Line == line).Select(token => (token.Start, token.Length, token.Kind)),
                snapshot.TokensByLine[line].Select(token => (token.Start, token.Length, token.Kind)));
        }

        // minimap 色带 = 该行第一个 token 的类别(列表序,与旧 UI 逻辑一致)。
        Assert.Equal(snapshot.MinimapKindByLine!.Keys.OrderBy(line => line),
            snapshot.Tokens.Select(token => token.Line).Distinct().OrderBy(line => line));
        foreach (var line in snapshot.MinimapKindByLine!.Keys)
        {
            Assert.Equal(snapshot.Tokens.First(token => token.Line == line).Kind, snapshot.MinimapKindByLine[line]);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_CacheHit_SharesPrecomputedIndexes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nornia-presentation-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Demo.cs");
        try
        {
            await File.WriteAllTextAsync(path, "public class Demo { }");
            var type = CodeFileTypeRegistry.Instance.FromPath(path);
            var identity = CodePresentationCacheIdentity.FromFile(path, "utf-8", 1);
            var text = await File.ReadAllTextAsync(path);

            var first = await CodePresentationService.Instance.AnalyzeAsync(text, type, 1, identity);
            var second = await CodePresentationService.Instance.AnalyzeAsync(text, type, 2, identity);

            // 命中缓存:预计算行索引是同一实例(worker 只建一次),版本按调用方刷新。
            Assert.Same(first.TokensByLine, second.TokensByLine);
            Assert.Same(first.MinimapKindByLine, second.MinimapKindByLine);
            Assert.Equal(2, second.DocumentVersion);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("README.md", CodeOutlineKind.Markdown)]
    [InlineData("pipeline.yml", CodeOutlineKind.Indentation)]
    [InlineData("app.ts", CodeOutlineKind.Brace)]
    public void Registry_MapsExpandedStructuralLanguages(string path, CodeOutlineKind expected)
    {
        Assert.Equal(expected, CodeFileTypeRegistry.Instance.FromPath(path).OutlineKind);
    }

    [Fact]
    public void MarkdownOutlineAndFolding_AgreeOnHeadings()
    {
        const string text = "# Top\nintro\n## Child\nbody\n";
        var outline = CodeOutlineParser.Instance.Parse(text, CodeOutlineKind.Markdown);
        var folds = CodeFoldingStrategy.Instance.FindSections(text, CodeFileTypeRegistry.Instance.FromPath("a.md"));

        Assert.Equal(2, outline.Count);
        Assert.Contains(folds, fold => fold.StartLine == 1);
    }

    [Fact]
    public void GrammarCatalog_UsesDistinctTraceableScopes()
    {
        var profiles = BuiltInGrammarCatalog.Instance.Profiles;
        Assert.True(profiles.Count >= 20);
        Assert.Equal(profiles.Count, profiles.Select(profile => profile.LanguageId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(profiles, profile => Assert.StartsWith("TextMateSharp.Grammars@2.0.4:", profile.GrammarResource, StringComparison.Ordinal));
    }
}

public sealed class DiffPresentationTests
{
    [Fact]
    public void BuildInline_AddsBoundedTokenChangesToReplacementPairs()
    {
        var lines = DiffDocumentBuilders.BuildInline([
            new GitDiffLine(GitDiffLineKind.Removed, 4, null, "return oldValue;"),
            new GitDiffLine(GitDiffLineKind.Added, null, 4, "return newValue;"),
        ]);

        Assert.NotEmpty(lines[0].IntralineChanges!);
        Assert.NotEmpty(lines[1].IntralineChanges!);
    }

    [Fact]
    public void ContextProjection_CollapsesLongUnchangedRunWithoutChangingSourceIndices()
    {
        var lines = Enumerable.Range(1, 12).Select(line => new DiffRenderLine($"line {line}", GitDiffLineKind.Context, line, line)).ToArray();
        var display = DiffContextProjection.Build(lines);

        var placeholder = Assert.Single(display, line => line.IsCollapsedContext);
        Assert.Equal(3, placeholder.OriginalIndex);
        Assert.Equal(6, placeholder.HiddenLineCount);
    }

    [Fact]
    public void SideBySideCollapseProjection_MatchesInlinePlaceholderText()
    {
        var rows = Enumerable.Range(1, 12)
            .Select(line => new GitSideBySideRow(line, GitDiffLineKind.Context, $"line {line}",
                line, GitDiffLineKind.Context, $"line {line}"))
            .ToList();
        rows.Insert(12, new GitSideBySideRow(null, GitDiffLineKind.None, string.Empty,
            7, GitDiffLineKind.Added, "added"));

        var inlineSource = rows.Select(row => row.NewKind == GitDiffLineKind.Added
            ? new GitDiffLine(GitDiffLineKind.Added, null, row.NewLineNumber, row.NewText)
            : new GitDiffLine(GitDiffLineKind.Context, row.OldLineNumber, row.NewLineNumber, row.NewText));
        var inline = DiffContextProjection.Build(DiffDocumentBuilders.BuildInline(inlineSource));
        var side = DiffDocumentBuilders.BuildSideBySide(rows);
        var sideMask = DiffDocumentBuilders.BuildSideBySideCollapseSource(side.Old, side.New);
        var sideDisplay = DiffContextProjection.Build(sideMask);

        var inlineText = inline.Where(line => line.IsCollapsedContext).Select(line => line.Text).ToArray();
        var sideText = sideDisplay.Where(line => line.IsCollapsedContext).Select(line => line.Text).ToArray();
        Assert.Equal(inlineText, sideText);
    }

    [Fact]
    public void SideBySideCollapseProjection_DoesNotCountHunkSpacerAsContext()
    {
        static GitDiffHunk Hunk(int start) => new(
            start, 10, start, 10, $"@@ -{start},10 +{start},10 @@",
            new[] { new GitDiffLine(GitDiffLineKind.HunkHeader, null, null, $"@@ -{start},10 +{start},10 @@") }
                .Concat(Enumerable.Range(start, 10)
                    .Select(line => new GitDiffLine(GitDiffLineKind.Context, line, line, $"line {line}")))
                .ToArray());

        var file = new GitFileDiff("sample.cs", null, false, false, false, new[] { Hunk(1), Hunk(20) });
        var inline = DiffContextProjection.Build(
            DiffDocumentBuilders.BuildInline(file.Hunks.SelectMany(hunk => hunk.Lines)));
        var side = DiffDocumentBuilders.BuildSideBySide(file.ToSideBySideRows());
        var sideMask = DiffDocumentBuilders.BuildSideBySideCollapseSource(side.Old, side.New);
        var sideDisplay = DiffContextProjection.Build(sideMask);

        Assert.Equal(
            inline.Where(line => line.IsCollapsedContext).Select(line => line.Text).ToArray(),
            sideDisplay.Where(line => line.IsCollapsedContext).Select(line => line.Text).ToArray());
    }

    [Fact]
    public void Overview_MapsChangesAcrossEntireDocument()
    {
        var lines = Enumerable.Range(0, 100).Select(index => new DiffRenderLine("x", index == 5 ? GitDiffLineKind.Added : index == 90 ? GitDiffLineKind.Removed : GitDiffLineKind.Context, null, null)).ToArray();
        var buckets = DiffOverviewLayout.Build(lines, 10, 5);

        Assert.True(buckets[0].HasAdded);
        Assert.True(buckets[9].HasRemoved);
        Assert.True(buckets[0].HasCurrent);
        Assert.Equal(50, DiffOverviewLayout.DocumentLineFromY(50, 100, 100));
    }

    [Fact]
    public void CapacityPolicy_UsesThreeStableTiers()
    {
        Assert.Equal(ReadOnlyContentTier.Full, ReadOnlyContentCapacity.ForSource(ReadOnlyContentCapacity.FullSourceBytes));
        Assert.Equal(ReadOnlyContentTier.Windowed, ReadOnlyContentCapacity.ForSource(ReadOnlyContentCapacity.FullSourceBytes + 1));
        Assert.Equal(ReadOnlyContentTier.Summary, ReadOnlyContentCapacity.ForDiff(ReadOnlyContentCapacity.WindowedDiffLines + 1));
    }

    [Fact]
    public void IntralineDiff_UsesUnicodeTextElementBoundaries()
    {
        var result = IntralineDiffBuilder.Build("a👩‍💻e\u0301z", "a👨‍💻e\u0301z");
        var old = Assert.Single(result.Old);
        var changed = "a👩‍💻e\u0301z".Substring(old.Start, old.Length);
        Assert.Equal("👩‍💻", changed);
    }

    [Fact]
    public void IntralineDiff_UsesWordAndIdentifierBoundaries()
    {
        var result = IntralineDiffBuilder.Build("return oldValue;", "return newValue;");

        var old = Assert.Single(result.Old);
        var @new = Assert.Single(result.New);
        Assert.Equal("oldValue", "return oldValue;".Substring(old.Start, old.Length));
        Assert.Equal("newValue", "return newValue;".Substring(@new.Start, @new.Length));
    }

    [Fact]
    public void IntralineDiff_DropsIsolatedWhitespaceChanges()
    {
        var result = IntralineDiffBuilder.Build("value = 1", "value  = 1");

        Assert.Empty(result.Old);
        Assert.Empty(result.New);
    }

    [Fact]
    public void IntralineDiff_MergesWhitespaceIntoAdjacentContentChange()
    {
        var result = IntralineDiffBuilder.Build("call old", "call  new");

        var old = Assert.Single(result.Old);
        var @new = Assert.Single(result.New);
        Assert.Equal(" old", "call old".Substring(old.Start, old.Length));
        Assert.Equal("  new", "call  new".Substring(@new.Start, @new.Length));
    }

    [Fact]
    public void IntralineDiff_PreservesPunctuationWithoutExpandingTheWholeLine()
    {
        var result = IntralineDiffBuilder.Build("value = 1;", "value = 2;");

        var old = Assert.Single(result.Old);
        var @new = Assert.Single(result.New);
        Assert.Equal("1", "value = 1;".Substring(old.Start, old.Length));
        Assert.Equal("2", "value = 2;".Substring(@new.Start, @new.Length));
    }

    [Fact]
    public void IntralineDiff_MergesShortAdjacentChangesIntoOneRange()
    {
        var result = IntralineDiffBuilder.Build("call old,old!", "call new,new!");

        var old = Assert.Single(result.Old);
        var @new = Assert.Single(result.New);
        Assert.Equal("old,old", "call old,old!".Substring(old.Start, old.Length));
        Assert.Equal("new,new", "call new,new!".Substring(@new.Start, @new.Length));
    }

    [Fact]
    public void IntralineDiff_RangesAreSortedNonOverlappingAndWithinBounds()
    {
        const string oldText = "alpha old,old; tail";
        const string newText = "alpha new,new; head";
        var result = IntralineDiffBuilder.Build(oldText, newText);

        static void AssertRanges(string text, IReadOnlyList<DiffIntralineChange> ranges)
        {
            var previousEnd = 0;
            foreach (var range in ranges)
            {
                Assert.True(range.Start >= previousEnd);
                Assert.True(range.Length > 0);
                Assert.True(range.Start + range.Length <= text.Length);
                previousEnd = range.Start + range.Length;
            }
        }

        AssertRanges(oldText, result.Old);
        AssertRanges(newText, result.New);
    }

    [Fact]
    public void BuildInline_MultilineReplacementPairingIsMonotonic()
    {
        var lines = DiffDocumentBuilders.BuildInline([
            new GitDiffLine(GitDiffLineKind.Removed, 1, null, "alpha old"),
            new GitDiffLine(GitDiffLineKind.Removed, 2, null, "beta old"),
            new GitDiffLine(GitDiffLineKind.Added, null, 1, "alpha new"),
            new GitDiffLine(GitDiffLineKind.Added, null, 2, "beta new"),
        ]);

        var alphaOld = Assert.Single(lines[0].IntralineChanges!);
        var alphaNew = Assert.Single(lines[2].IntralineChanges!);
        var betaOld = Assert.Single(lines[1].IntralineChanges!);
        var betaNew = Assert.Single(lines[3].IntralineChanges!);
        Assert.Equal("old", "alpha old".Substring(alphaOld.Start, alphaOld.Length));
        Assert.Equal("new", "alpha new".Substring(alphaNew.Start, alphaNew.Length));
        Assert.Equal("old", "beta old".Substring(betaOld.Start, betaOld.Length));
        Assert.Equal("new", "beta new".Substring(betaNew.Start, betaNew.Length));
    }

    [Fact]
    public void BuildInline_RefinesOneToManyReplacementAsOneMappedBlock()
    {
        var lines = DiffDocumentBuilders.BuildInline([
            new GitDiffLine(GitDiffLineKind.Removed, 12, null,
                "return (ToRanges(oldElements, prefix, oldCount), ToRanges(newElements, prefix, newCount));"),
            new GitDiffLine(GitDiffLineKind.Added, null, 12, "return ("),
            new GitDiffLine(GitDiffLineKind.Added, null, 13,
                "    NormalizeRanges(oldElements, prefix, AllChangedFlags(oldCount)),"),
            new GitDiffLine(GitDiffLineKind.Added, null, 14,
                "    NormalizeRanges(newElements, prefix, AllChangedFlags(newCount)));"),
        ]);

        Assert.NotNull(lines[0].IntralineChanges);
        Assert.Contains(lines.Skip(2), line => line.IntralineChanges is { Count: > 0 });
    }

    [Fact]
    public void BuildInline_RefinesWordChangeWhenReplacementBlockGrows()
    {
        var lines = DiffDocumentBuilders.BuildInline([
            new GitDiffLine(GitDiffLineKind.Removed, 11, null,
                "/// <summary>Bounded character diff for replacement pairs. It runs only inside Git-provided change"),
            new GitDiffLine(GitDiffLineKind.Removed, 12, null,
                "/// runs, so it never changes hunk context or source line-number authority.</summary>"),
            new GitDiffLine(GitDiffLineKind.Added, null, 11,
                "/// <summary>Bounded token diff for replacement pairs. It runs only inside Git-provided change"),
            new GitDiffLine(GitDiffLineKind.Added, null, 12,
                "/// runs, so it never changes hunk context or source line-number authority. Tokens are deliberately"),
            new GitDiffLine(GitDiffLineKind.Added, null, 13,
                "/// coarser than characters: identifiers/words and whitespace runs are compared as units while"),
            new GitDiffLine(GitDiffLineKind.Added, null, 14,
                "/// punctuation and symbols retain their own boundaries. Raw ranges are normalized afterward so"),
            new GitDiffLine(GitDiffLineKind.Added, null, 15,
                "/// short adjacent changes become one readable, non-overlapping range.</summary>"),
        ]);

        var old = Assert.Single(lines[0].IntralineChanges!);
        var @new = Assert.Single(lines[2].IntralineChanges!);
        Assert.Equal("character", lines[0].Text.Substring(old.Start, old.Length));
        Assert.Equal("token", lines[2].Text.Substring(@new.Start, @new.Length));
    }

    [Fact]
    public void BuildInline_DoesNotRefineLowSimilarityReplacement()
    {
        var lines = DiffDocumentBuilders.BuildInline([
            new GitDiffLine(GitDiffLineKind.Removed, 1, null, "alpha old"),
            new GitDiffLine(GitDiffLineKind.Added, null, 1, "completely unrelated"),
        ]);

        Assert.Null(lines[0].IntralineChanges);
        Assert.Null(lines[1].IntralineChanges);
    }

    [Fact]
    public void IntralineDiff_UsesWholeLineFallbackForOversizedInput()
    {
        var oldText = new string('a', IntralineDiffBuilder.MaxComparableCharacters / 2 + 1);
        var newText = new string('b', IntralineDiffBuilder.MaxComparableCharacters / 2 + 1);

        var result = IntralineDiffBuilder.Build(oldText, newText);

        Assert.Empty(result.Old);
        Assert.Empty(result.New);
    }

    [Fact]
    public void DisplayMap_ExpandsOnlyOwningContextRegion()
    {
        var lines = Enumerable.Range(1, 20).Select(line => new DiffRenderLine($"line {line}", GitDiffLineKind.Context, line, line)).ToArray();
        var map = new DiffDisplayMap(lines);
        Assert.True(map.EnsureVisible(10) >= 0);
        Assert.Equal(10, map.OriginalIndexFromDisplay(map.DisplayIndexFromOriginal(10)));
    }

    [Fact]
    public void StreamParser_PreservesGitLineNumbersIncrementally()
    {
        var parser = new GitDiffStreamParser("a.cs", false);
        var events = new[] { "@@ -4,2 +8,2 @@", "-old", "+new", " same" }
            .SelectMany(parser.Accept).ToArray();
        var lines = events.OfType<GitDiffLineEvent>().Select(item => item.Line).ToArray();
        Assert.Equal(4, lines[0].OldLineNumber);
        Assert.Equal(8, lines[1].NewLineNumber);
        Assert.Equal((5, 9), (lines[2].OldLineNumber, lines[2].NewLineNumber));
    }
}

public sealed class WindowedContentSourceTests
{
    [Fact]
    public async Task FileSource_ReadsGlobalWindowAndSearchesAcrossIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nornia-source-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, string.Join("\r\n", Enumerable.Range(1, 900).Select(index => $"line {index}")), new UTF8Encoding(true));
        try
        {
            await using var source = new FileReadOnlyDocumentSource(path);
            var metadata = await source.GetMetadataAsync();
            var window = await source.ReadWindowAsync(new DocumentRange(513, 20));
            Assert.Equal(900, metadata.TotalLines);
            Assert.Equal(513, window.StartLine);
            Assert.StartsWith("line 513", window.Text, StringComparison.Ordinal);
            var matches = new List<DocumentSearchMatch>();
            await foreach (var match in source.SearchAsync("line 899")) matches.Add(match);
            Assert.Equal(899, Assert.Single(matches).Line);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSource_PreservesUtf16AndGb18030WindowBoundaries()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (var encoding in new Encoding[] { new UnicodeEncoding(false, true), Encoding.GetEncoding(54936) })
        {
            var path = Path.Combine(Path.GetTempPath(), $"nornia-encoded-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, string.Join("\n", Enumerable.Range(1, 700).Select(index => $"第 {index} 行")), encoding);
            try
            {
                await using var source = new FileReadOnlyDocumentSource(path);
                var window = await source.ReadWindowAsync(new DocumentRange(513, 10));
                Assert.StartsWith("第 513 行", window.Text, StringComparison.Ordinal);
                Assert.DoesNotContain('\uFFFD', window.Text);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task DiffSpool_BuildsHunkIndexAndReleasesSource()
    {
        static async IAsyncEnumerable<GitDiffEvent> Events()
        {
            yield return new GitDiffHunkEvent(1, 1, 1, 1, "@@ -1 +1 @@");
            yield return new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Removed, 1, null, "old"));
            yield return new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Added, null, 1, "new"));
            yield return new GitDiffCompletedEvent(0, false);
            await Task.CompletedTask;
        }

        var source = await SpoolingDiffContentSource.CreateAsync(Events());
        Assert.Single((await source.GetMetadataAsync()).Hunks);
        Assert.NotEmpty((await source.ReadWindowAsync(new DocumentRange(1, 10))).Events);
        await source.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await source.GetMetadataAsync());
    }
}
