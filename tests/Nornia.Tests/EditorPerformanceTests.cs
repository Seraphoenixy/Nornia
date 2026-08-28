using System.Collections.Concurrent;
using ICSharpCode.AvalonEdit.Document;
using Nornia.Desktop.Code;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

/// <summary>
/// 性能改进(见 docs/performance-review.md §2.1)的纯逻辑回归测试:
/// - E2: <see cref="FoldingRegions"/> 二分 + 父链查找 —— 与旧线性扫描参考实现逐行差分;
/// - E8: <see cref="TokenTheme"/> 并发缓存 —— 多线程解析结果稳定且与单线程参考一致;
/// - E9/E4: <see cref="CodeDocumentView"/> SetDocument 等值短路 + TextDocument 实例连续
///   (WpfStaContext 共享 STA 夹具,与 CodeDocumentViewFallbackTests 同款);
/// - E3: 阅读高亮扫描窗口外扩/夹取纯函数 <c>ExpandScanWindow</c>;
/// - E7: TextMate 分词超长行(> MaxTokenizedLineLength)跳过。
/// 渲染路径项(E1 迷你地图拆分 / E5 光标合帧 / E6 行号 FormattedText 缓存)依赖
/// WPF 渲染线程时序,不在此处断言。
/// </summary>
public sealed class EditorPerformanceTests
{
    // ===== E2: 折叠几何二分查找 =====

    /// <summary>生成随机良好嵌套的折叠段(顶层互不相交,子段严格位于父段内部)。</summary>
    private static List<(int Start, int End, bool Collapsed)> GenerateNestedRegions(int seed)
    {
        var rng = new Random(seed);
        var result = new List<(int, int, bool)>();
        EmitBlock(result, 1, 0, 3, rng);
        return result;

        void EmitBlock(List<(int Start, int End, bool Collapsed)> target, int pos, int depth, int maxDepth, Random r)
        {
            var guard = 0;
            while (guard++ < 500)
            {
                if (depth >= maxDepth || r.NextDouble() >= 0.6)
                {
                    pos += r.Next(0, 3); // 无区域的间隙
                    if (r.NextDouble() < 0.25)
                    {
                        return;
                    }

                    continue;
                }

                var start = pos;
                var end = start + r.Next(2, 12);
                target.Add((start, end, r.NextDouble() < 0.5));
                if (depth + 1 < maxDepth && r.NextDouble() < 0.5)
                {
                    EmitBlock(target, start + 1, depth + 1, maxDepth, r);
                }

                pos = end;
                if (r.NextDouble() < 0.25)
                {
                    return;
                }
            }
        }
    }

    // ---- 旧线性扫描参考实现(与被测方法的旧版本逐字一致) ----

    private static bool ReferenceIsRegionHidden(IReadOnlyList<FoldRegion> regions, FoldRegion region)
    {
        foreach (var other in regions)
        {
            if (other.IsCollapsed && other.StartLine < region.StartLine && region.EndLine <= other.EndLine)
            {
                return true;
            }
        }

        return false;
    }

    private static FoldRegion? ReferenceFindStartVisibleAtLine(IReadOnlyList<FoldRegion> regions, int line)
    {
        FoldRegion? best = null;
        foreach (var region in regions)
        {
            if (region.StartLine != line || region.EndLine <= line || ReferenceIsRegionHidden(regions, region))
            {
                continue;
            }

            if (best is null || region.Level < best.Level)
            {
                best = region;
            }
        }

        return best;
    }

    private static FoldRegion? ReferenceFindAtLine(IReadOnlyList<FoldRegion> regions, int line)
    {
        FoldRegion? deepest = null;
        foreach (var region in regions)
        {
            if (region.ContainsLine(line) && (deepest is null || region.Level > deepest.Level))
            {
                deepest = region;
            }
        }

        return deepest;
    }

    private static FoldRegion? ReferenceFindCollapsedContaining(IReadOnlyList<FoldRegion> regions, int line)
    {
        FoldRegion? deepest = null;
        foreach (var region in regions)
        {
            if (region.IsCollapsed && region.StartLine < line && line <= region.EndLine &&
                (deepest is null || region.Level > deepest.Level))
            {
                deepest = region;
            }
        }

        return deepest;
    }

    [Fact]
    public void Folding_FindStartVisibleAtLine_MatchesLinearReference_OnRandomNestedRegions()
    {
        for (var seed = 1; seed <= 12; seed++)
        {
            var regions = FoldingRegions.Build(GenerateNestedRegions(seed));
            var maxLine = regions.Count == 0 ? 0 : regions.Max(region => region.EndLine);
            for (var line = 1; line <= maxLine; line++)
            {
                Assert.Same(
                    ReferenceFindStartVisibleAtLine(regions, line),
                    FoldingRegions.FindStartVisibleAtLine(regions, line));
            }
        }
    }

    [Fact]
    public void Folding_FindAtLine_MatchesLinearReference_OnRandomNestedRegions()
    {
        for (var seed = 1; seed <= 12; seed++)
        {
            var regions = FoldingRegions.Build(GenerateNestedRegions(seed));
            var maxLine = regions.Count == 0 ? 0 : regions.Max(region => region.EndLine);
            for (var line = 1; line <= maxLine; line++)
            {
                Assert.Same(
                    ReferenceFindAtLine(regions, line),
                    FoldingRegions.FindAtLine(regions, line));
            }
        }
    }

    [Fact]
    public void Folding_FindCollapsedContaining_MatchesLinearReference_OnRandomNestedRegions()
    {
        for (var seed = 1; seed <= 12; seed++)
        {
            var regions = FoldingRegions.Build(GenerateNestedRegions(seed));
            var maxLine = regions.Count == 0 ? 0 : regions.Max(region => region.EndLine);
            for (var line = 1; line <= maxLine; line++)
            {
                Assert.Same(
                    ReferenceFindCollapsedContaining(regions, line),
                    FoldingRegions.FindCollapsedContaining(regions, line));
            }
        }
    }

    [Fact]
    public void Folding_IsRegionHidden_And_IsLineHiddenInCollapsed_MatchLinearReference()
    {
        for (var seed = 1; seed <= 8; seed++)
        {
            var regions = FoldingRegions.Build(GenerateNestedRegions(seed));
            var maxLine = regions.Count == 0 ? 0 : regions.Max(region => region.EndLine);

            for (var line = 1; line <= maxLine; line++)
            {
                Assert.True(
                    (ReferenceFindCollapsedContaining(regions, line) is not null) ==
                    FoldingRegions.IsLineHiddenInCollapsed(regions, line),
                    $"seed={seed} line={line}");
            }

            foreach (var region in regions)
            {
                Assert.True(
                    ReferenceIsRegionHidden(regions, region) == FoldingRegions.IsRegionHidden(regions, region),
                    $"seed={seed} region=({region.StartLine},{region.EndLine})");
            }
        }
    }

    [Fact]
    public void Folding_DeepNesting_ReturnsExpectedInnermostRegion()
    {
        // 5000 层嵌套:二分 + 父链必须与线性参考一致(深层命中不依赖全量扫描)。
        var items = new List<(int, int, bool)>();
        for (var i = 0; i < 5000; i++)
        {
            items.Add((i + 1, 5000 - i, i % 3 == 0));
        }

        var regions = FoldingRegions.Build(items);
        var line = 2500;
        Assert.Same(ReferenceFindAtLine(regions, line), FoldingRegions.FindAtLine(regions, line));
        Assert.Same(ReferenceFindCollapsedContaining(regions, line), FoldingRegions.FindCollapsedContaining(regions, line));
        // 第 2500 行的最内层区域是 (2500, 2501),深度 2500。
        Assert.Equal(2500, FoldingRegions.FindAtLine(regions, line)!.Level);
    }

    // ===== E8: TokenTheme 并发缓存 =====

    [Fact]
    public void TokenTheme_Resolve_ConcurrentAccess_ReturnsStableResults()
    {
        var scopeSets = new[]
        {
            new[] { "keyword.control.cs" },
            new[] { "comment.line.cs" },
            new[] { "string.quoted.cs", "punctuation.definition.cs" },
            new[] { "variable.other.readwrite.cs" }, // 未命中规则 → null(否定缓存)
            new[] { "markup.heading.1.markdown" },
        };
        var expected = scopeSets.Select(TokenTheme.Resolve).ToArray();

        var failures = new ConcurrentBag<Exception>();
        Parallel.For(0, 8, worker =>
        {
            try
            {
                for (var i = 0; i < 3000; i++)
                {
                    var index = (worker + i) % scopeSets.Length;
                    var style = TokenTheme.Resolve(scopeSets[index]);
                    var reference = expected[index];
                    if (reference is null)
                    {
                        Assert.Null(style);
                        continue;
                    }

                    Assert.NotNull(style);
                    Assert.Equal(reference!.Role, style!.Role);
                    Assert.Equal(reference.Bold, style.Bold);
                    Assert.Equal(reference.Italic, style.Italic);
                    Assert.Equal(reference.Underline, style.Underline);
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);
    }

    // ===== E9/E4: SetDocument 等值短路 + 文档实例连续 =====

    [Fact]
    public void Document_SetDocument_KeepsTextDocumentInstanceAcrossReplacements()
    {
        TextDocument? initial = null;
        TextDocument? afterFirst = null;
        TextDocument? afterSecond = null;

        WpfStaContext.Run(() =>
        {
            var view = new CodeDocumentView();
            initial = view.Document;
            view.SourceText = "class A { }";
            afterFirst = view.Document;
            view.SourceText = "class B { }\nvoid M() { }";
            afterSecond = view.Document;

            // 窗口翻页/换文档不再 new TextDocument:同一实例上 Replace,
            // 折叠管理器绑定与 TextSegmentCollection 跨刷新保持有效。
            // (TextDocument 只能在持有线程访问,断言放进 STA 闭包内。)
            Assert.Same(initial, afterFirst);
            Assert.Same(initial, afterSecond);
            Assert.Equal("class B { }\nvoid M() { }", afterSecond!.Text);
        });
    }

    [Fact]
    public void Document_SetDocument_EqualTextIsNoOp_WithoutDocumentChanged()
    {
        var changed = 0;
        var initial = new TextDocument("public class A { }");
        TextDocument? current = null;
        string? text = null;

        WpfStaContext.Run(() =>
        {
            var view = new CodeDocumentView();
            initial = view.Document;
            view.SourceText = "public class A { }";
            view.DocumentChanged += (_, _) => changed++;

            // 内容相等(不同字符串实例,同长度)→ 全比较后短路,不重建、不触发事件。
            view.SourceText = "public class A { }";
            // 同一字符串实例 → 引用短路。
            var sameReference = view.SourceText;
            view.SourceText = sameReference;

            current = view.Document;
            text = view.SourceText;
        });

        Assert.Equal(0, changed);
        Assert.Same(initial, current);
        Assert.Equal("public class A { }", text);
    }

    // ===== E3: 扫描窗口纯函数 =====

    [Theory]
    [InlineData(1, 50, 1000, 100, 1, 150)]     // 顶部:下限夹到 1
    [InlineData(950, 1000, 1000, 100, 850, 1000)] // 底部:上限夹到 lineCount
    [InlineData(200, 300, 1000, 100, 100, 400)]   // 中段:两侧外扩
    [InlineData(10, 10, 1000, 100, 1, 110)]       // 单行可见:外扩后仍受限
    [InlineData(5, 2, 100, 3, 2, 5)]              // 退化输入 first>last:按区间夹取
    public void ReadingHighlights_ExpandScanWindow_ClampsToDocument(
        int first, int last, int lineCount, int margin, int expectedStart, int expectedEnd)
    {
        var (start, end) = CodeDocumentView.ExpandScanWindow(first, last, lineCount, margin);
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Fact]
    public void ReadingHighlights_ExpandScanWindow_EmptyDocumentDegeneratesGracefully()
    {
        var (start, end) = CodeDocumentView.ExpandScanWindow(1, 1, 0, 100);
        Assert.Equal(1, start);
        Assert.Equal(1, end);
    }

    // ===== E7: 超长行跳过 TextMate 分词 =====

    [Fact]
    public void Tokenizer_LinesLongerThanMaxTokenizedLineLength_ProduceNoTokens()
    {
        Assert.Equal(2000, TextMateLanguageTokenizer.MaxTokenizedLineLength);

        var longLine = new string('x', 3000); // > 2000 → 必须跳过
        var text = longLine + "\nint y = 1;"; // 无尾随换行,避免产生第 3 行
        var fileType = CodeFileTypeRegistry.Instance.FromExtension(".cs");

        var tokens = new TextMateLanguageTokenizer().Tokenize(text, fileType);

        // 超长行不产生任何 token(native grammar 不可用时整体为空,同样满足)。
        Assert.DoesNotContain(tokens, token => token.Line == 1);
        // 正常短行不受影响(若分词可用则必须有 token;不可用时整个列表为空)。
        if (tokens.Count > 0)
        {
            Assert.DoesNotContain(tokens, token => token.Line != 2);
        }
    }
}
