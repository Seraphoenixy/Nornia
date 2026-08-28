using Nornia.Desktop;
using Nornia.Desktop.Code;

namespace Nornia.Tests;

/// <summary>
/// Guards the vscode-main-aligned highlight/folding internals:
/// - TokenTheme resolves scope stacks with last-match-wins rules shared by the code reader
///   (CodeToken* palette) for both the code and diff readers, including font styles;
/// - FoldingRegions derives nesting/parent/level and answers hit-testing for the fold gutter;
/// - the folding strategy handles #region markers + a region cap;
/// - the tokenizer keeps the original scope stacks on every token (including Plain).
/// </summary>
public sealed class TokenThemeTests
{
    [Theory]
    [InlineData(new[] { "comment.line.double-slash.cs" }, CodeTokenRole.Comment)]
    [InlineData(new[] { "string.quoted.double.cs" }, CodeTokenRole.String)]
    [InlineData(new[] { "constant.numeric.decimal.cs" }, CodeTokenRole.Number)]
    [InlineData(new[] { "keyword.control.cs" }, CodeTokenRole.Keyword)]
    [InlineData(new[] { "entity.name.type.cs" }, CodeTokenRole.Type)]
    [InlineData(new[] { "entity.name.function.cs" }, CodeTokenRole.Function)]
    [InlineData(new[] { "meta.tag.xml" }, CodeTokenRole.Tag)]
    [InlineData(new[] { "entity.other.attribute-name.cs" }, CodeTokenRole.Attribute)]
    [InlineData(new[] { "markup.underline.link.markdown" }, CodeTokenRole.Link)]
    [InlineData(new[] { "meta.preprocessor.c.cs" }, CodeTokenRole.Preprocessor)]
    [InlineData(new[] { "punctuation.definition.comment.cs" }, CodeTokenRole.Punctuation)]
    [InlineData(new[] { "markup.heading.1.markdown" }, CodeTokenRole.Type)]
    [InlineData(new[] { "markup.fenced_code.block.markdown" }, CodeTokenRole.String)]
    [InlineData(new[] { "markup.raw.block.markdown" }, CodeTokenRole.String)]
    [InlineData(new[] { "meta.embedded.block.csharp" }, CodeTokenRole.String)]
    public void Resolve_MapsScopeFragmentsToRoles(string[] scopes, CodeTokenRole expected)
    {
        var style = TokenTheme.Resolve(scopes);
        Assert.NotNull(style);
        Assert.Equal(expected, style!.Role);
    }

    [Fact]
    public void Resolve_AppliesFontStylesPerRule()
    {
        var comment = TokenTheme.Resolve(["comment.line.cs"]);
        Assert.True(comment!.Italic);
        Assert.False(comment.Bold);

        var bold = TokenTheme.Resolve(["markup.bold.markdown"]);
        Assert.True(bold!.Bold);
        Assert.Null(bold.Role); // 不改前景色

        var link = TokenTheme.Resolve(["markup.underline.link.markdown"]);
        Assert.True(link!.Underline);
        Assert.Equal(CodeTokenRole.Link, link.Role);

        var heading = TokenTheme.Resolve(["markup.heading.1.markdown"]);
        Assert.True(heading!.Bold);
        Assert.Equal(CodeTokenRole.Type, heading.Role);
    }

    [Fact]
    public void Resolve_LastMatchWins_KeywordOverDefault()
    {
        // "default" 规则先于 "keyword":冲突时关键字获胜(vscode TMTheme last-match-wins)。
        var style = TokenTheme.Resolve(["keyword.control.default.cs"]);
        Assert.Equal(CodeTokenRole.Keyword, style!.Role);
        Assert.False(style.Bold);
    }

    [Fact]
    public void Resolve_NullOrEmptyAndUnmatchedScopes_ReturnNull()
    {
        Assert.Null(TokenTheme.Resolve(null));
        Assert.Null(TokenTheme.Resolve([]));
        Assert.Null(TokenTheme.Resolve(["variable.other.readwrite.cs"]));
        Assert.Null(TokenTheme.ResolveFragment("StrangeDecoration"));
    }

    [Fact]
    public void ResolveFragment_DiffColorNamesMapThroughSharedRules()
    {
        Assert.Equal(CodeTokenRole.Preprocessor, TokenTheme.ResolveFragment("Preprocessor")!.Role);
        Assert.Equal(CodeTokenRole.Punctuation, TokenTheme.ResolveFragment("Operator")!.Role);
        Assert.Equal(CodeTokenRole.Default, TokenTheme.ResolveFragment("Default")!.Role);
    }

    [Theory]
    [InlineData(CodeTokenRole.Comment, "CodeTokenCommentBrush", "CodeTokenCommentBrush")]
    [InlineData(CodeTokenRole.Function, "CodeTokenFunctionBrush", "CodeTokenFunctionBrush")]
    [InlineData(CodeTokenRole.Link, "CodeTokenLinkBrush", "CodeTokenLinkBrush")]
    [InlineData(CodeTokenRole.Punctuation, null, null)]
    [InlineData(CodeTokenRole.Default, null, null)]
    public void PaletteBridges_ReaderAndDiffUseCodeToken(CodeTokenRole role, string? reader, string? diff)
    {
        Assert.Equal(reader, TokenTheme.CodeReaderBrushName(role));
        Assert.Equal(diff, TokenTheme.DiffBrushName(role));
    }

    [Fact]
    public void Resolve_IsCachedAndStable()
    {
        var first = TokenTheme.Resolve(["entity.name.function.cs"]);
        var second = TokenTheme.Resolve(["entity.name.function.cs"]);
        Assert.Same(first, second);
    }
}

public sealed class FoldingRegionsTests
{
    private static IReadOnlyList<FoldRegion> ClassicNesting() => FoldingRegions.Build(
    [
        (1, 6, false), // class A
        (2, 5, false), // void M
        (3, 4, false), // if
    ]);

    [Fact]
    public void Build_ComputesParentsAndLevels()
    {
        var regions = ClassicNesting();
        Assert.Equal(3, regions.Count);
        Assert.Equal((1, 6, -1, 1), (regions[0].StartLine, regions[0].EndLine, regions[0].ParentIndex, regions[0].Level));
        Assert.Equal((2, 5, 0, 2), (regions[1].StartLine, regions[1].EndLine, regions[1].ParentIndex, regions[1].Level));
        Assert.Equal((3, 4, 1, 3), (regions[2].StartLine, regions[2].EndLine, regions[2].ParentIndex, regions[2].Level));
    }

    [Fact]
    public void Build_SortsUnsortedInputAndHandlesSameStart()
    {
        // 乱序输入 → 排序后右嵌套;等起点外层(End 更大)先入栈。
        var regions = FoldingRegions.Build([(4, 5, false), (1, 3, false), (1, 6, false)]);
        Assert.Equal(1, regions[0].StartLine);
        Assert.Equal(6, regions[0].EndLine);
        Assert.Equal(1, regions[1].StartLine);
        Assert.Equal(3, regions[1].EndLine);
        Assert.Equal(0, regions[1].ParentIndex);
        Assert.Equal(2, regions[1].Level);
        Assert.Equal(4, regions[2].StartLine);
        Assert.Equal(0, regions[2].ParentIndex);
        Assert.Equal(2, regions[2].Level);
    }

    [Fact]
    public void Build_EmptyInput_YieldsEmptyModel()
    {
        Assert.Empty(FoldingRegions.Build([]));
    }

    [Fact]
    public void Build_IgnoresInvalidAndKeepsAdjacentRangesIndependent()
    {
        var regions = FoldingRegions.Build([(0, 2, false), (1, 3, false), (4, 6, false), (7, 7, false)]);

        Assert.Equal(2, regions.Count);
        Assert.Equal((1, 3, -1), (regions[0].StartLine, regions[0].EndLine, regions[0].ParentIndex));
        Assert.Equal((4, 6, -1), (regions[1].StartLine, regions[1].EndLine, regions[1].ParentIndex));
        Assert.Equal(1, FoldingRegions.FindStartVisibleAtLine(regions, 1)!.StartLine);
        Assert.Equal(4, FoldingRegions.FindStartVisibleAtLine(regions, 4)!.StartLine);
    }

    [Fact]
    public void FindAtLine_ReturnsInnermostContainingRegion()
    {
        var regions = ClassicNesting();
        Assert.Equal(3, FoldingRegions.FindAtLine(regions, 3)!.Level);
        Assert.Equal(2, FoldingRegions.FindAtLine(regions, 5)!.Level);
        Assert.Equal(1, FoldingRegions.FindAtLine(regions, 6)!.Level);
        Assert.Null(FoldingRegions.FindAtLine(regions, 7));
    }

    [Fact]
    public void FindStartVisibleAtLine_ReturnsOutermostAndHidesCollapsedNested()
    {
        var expanded = ClassicNesting();
        var outer = FoldingRegions.FindStartVisibleAtLine(expanded, 1);
        Assert.Equal(1, outer!.StartLine);
        Assert.Equal(6, outer.EndLine);

        var nested = FoldingRegions.FindStartVisibleAtLine(expanded, 2);
        Assert.Equal(5, nested!.EndLine);

        // 外层折叠 → 内层区域被遮挡,不再渲染 chevron。
        var collapsedOuter = FoldingRegions.Build([(1, 6, true), (2, 5, false)]);
        Assert.Null(FoldingRegions.FindStartVisibleAtLine(collapsedOuter, 2));
    }

    [Fact]
    public void IsRegionHidden_DetectsCollapsedAncestors()
    {
        var collapsedOuter = FoldingRegions.Build([(1, 6, true), (2, 5, false)]);
        Assert.True(FoldingRegions.IsRegionHidden(collapsedOuter, collapsedOuter[1]));
        Assert.False(FoldingRegions.IsRegionHidden(collapsedOuter, collapsedOuter[0]));
    }

    [Fact]
    public void FindCollapsedContaining_ReturnsInnermostCollapsedRange()
    {
        var regions = FoldingRegions.Build([(1, 10, false), (2, 5, true), (6, 9, false)]);
        var hit = FoldingRegions.FindCollapsedContaining(regions, 3);
        Assert.Equal(2, hit!.StartLine);
        Assert.Equal(5, hit.EndLine);
        Assert.Null(FoldingRegions.FindCollapsedContaining(regions, 7)); // 该行未被折叠区间覆盖

        // 外层折叠时,深层未折叠内容仍被外层遮挡命中。
        var outerCollapsed = FoldingRegions.Build([(1, 10, true), (6, 9, false)]);
        Assert.Equal(1, FoldingRegions.FindCollapsedContaining(outerCollapsed, 7)!.StartLine);
    }
}

public sealed class FoldingStrategyExtrasTests
{
    private static CodeFileType BraceType() => new("csharp", "C#", "C#", Codicons.File, CodeOutlineKind.CSharp);

    [Fact]
    public void RegionMarkers_ProduceSectionsInBraceLanguages()
    {
        const string source = """
            class A
            {
                #region PublicSurface
                public void M()
                {
                }
                #endregion
            }
            """;

        var sections = CodeFoldingStrategy.Instance.FindSections(source, BraceType());

        Assert.Contains(sections, section => section.StartLine == 3 && section.EndLine == 7); // #region..#endregion
        Assert.Contains(sections, section => section.StartLine == 2 && section.EndLine == 8); // class braces
    }

    [Fact]
    public void UnclosedRegion_ProducesNoSection()
    {
        const string source = "#region NeverClosed\nvoid M() { }\n";

        var sections = CodeFoldingStrategy.Instance.FindSections(source, BraceType());

        Assert.DoesNotContain(sections, section => section.StartLine == 1);
    }

    [Fact]
    public void RegionCount_IsCapped()
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 5001; i++)
        {
            builder.Append("{\n}\n");
        }

        var sections = CodeFoldingStrategy.Instance.FindSections(builder.ToString(), BraceType());

        Assert.Equal(5000, sections.Count);
    }
}

public sealed class TokenizerScopeCaptureTests
{
    // 注意:scope 捕获断言在 LanguagePresentationTests.Tokens_CarryOriginalScopeStacks_IncludingPlain
    // 中(同一类内顺序执行)。TextMateSharp 首次编译 + Oniguruma native 初始化在并行首启下会
    // 竞态(tokenize 抛异常被兜底吞掉),两个 tokenizer 用例不得跨类并行。
}
