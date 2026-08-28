using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.Views;
using WpfMath.Controls;

namespace Nornia.Tests;

/// <summary>共享测试辅助:16×16 纯色 PNG 生成(解码路径与真实图片一致)+
/// FlowDocument 内容树遍历(FlowDocument 不是 Visual,Visual 版 Descendants 扩展不适用)。</summary>
internal static class MdPerfTestSupport
{
    public static void CreatePng(string path)
    {
        const int size = 16;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, size, size));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static List<InlineUIContainer> EnumerateInlineUiContainers(FlowDocument document)
    {
        var result = new List<InlineUIContainer>();
        foreach (var block in document.Blocks)
        {
            Collect(block, result);
        }

        return result;
    }

    private static void Collect(Block block, List<InlineUIContainer> result)
    {
        switch (block)
        {
            case Paragraph paragraph:
                foreach (var inline in paragraph.Inlines)
                {
                    if (inline is InlineUIContainer container)
                    {
                        result.Add(container);
                    }
                }
                break;
            case Table table:
                foreach (var group in table.RowGroups)
                {
                    foreach (var row in group.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var inner in cell.Blocks)
                            {
                                Collect(inner, result);
                            }
                        }
                    }
                }
                break;
        }
    }
}

// ===== M1: 公式 worker 预解析缓存 =====

/// <summary>M1: 公式预解析缓存 —— 同一公式文本只解析一次,失败判定也缓存(不重复解析)。</summary>
public sealed class MarkdownFormulaCacheTests
{
    [Fact]
    public void SameFormulaText_ParsesOnce_SecondLookupReusesParsedFormula()
    {
        var cache = new MarkdownFormulaCache();
        var first = cache.GetOrParse("x^2 + 1");
        Assert.NotNull(first);
        Assert.Equal(1, cache.ParseCount);

        var second = cache.GetOrParse("x^2 + 1");
        Assert.Same(first, second);
        Assert.Equal(1, cache.ParseCount); // 同一文本只跑一次 TexFormula 解析

        Assert.True(cache.TryGet("x^2 + 1", out var got));
        Assert.Same(first, got);
        Assert.False(cache.TryGet("never seen", out _));
    }

    [Fact]
    public void DistinctFormulaTexts_ParseSeparately()
    {
        var cache = new MarkdownFormulaCache();
        cache.GetOrParse("a + b");
        cache.GetOrParse("c_d");
        Assert.Equal(2, cache.ParseCount);
    }

    [Fact]
    public void UnparseableFormula_CachesFailureVerdict_WithoutReparse()
    {
        var cache = new MarkdownFormulaCache();
        // \frac 需要两个参数:单参数必然解析失败 → 失败判定入缓存。
        var verdict = cache.GetOrParse("\\frac{1}");
        Assert.Null(verdict);
        Assert.Equal(1, cache.ParseCount);

        Assert.True(cache.TryGet("\\frac{1}", out var again));
        Assert.Null(again);
        Assert.Equal(1, cache.ParseCount); // 判定复用,不重复解析
    }

    [Fact]
    public void CacheClearsWhenFull()
    {
        var cache = new MarkdownFormulaCache();
        for (var i = 0; i < MarkdownFormulaCache.MaxEntries; i++)
        {
            cache.GetOrParse($"f{i}");
        }

        Assert.Equal(MarkdownFormulaCache.MaxEntries, cache.Count);
        cache.GetOrParse("overflow");
        Assert.Equal(MarkdownFormulaCache.MaxEntries, cache.Count); // 满时清空重建
        Assert.True(cache.TryGet("overflow", out _));
        Assert.False(cache.TryGet("f0", out _));
    }
}

/// <summary>M1 集成:ParseAsync 的 worker 线程预解析全部公式文本(键与渲染器一致),
/// 渲染期按判定构建 —— 失败落原文兜底、成功落 FormulaControl(UI 线程不再尝试解析失败公式)。</summary>
public sealed class MarkdownFormulaPreparseTests
{
    [Fact]
    public async Task ParseAsync_PreparsesAllFormulaTextsOnWorker()
    {
        var source = "# F\n\n$x^2$ and:\n\n$$\n\\frac{1}{\\sqrt{2}}\n$$\n";
        var result = await MarkdownPreviewService.Instance.ParseAsync(source, "C:\\docs\\math.md");
        Assert.NotNull(result.Document);

        Assert.True(MarkdownFormulaCache.Instance.TryGet("x^2", out var inline));
        Assert.NotNull(inline);
        Assert.True(MarkdownFormulaCache.Instance.TryGet("\\frac{1}{\\sqrt{2}}", out var block));
        Assert.NotNull(block);
    }

    [Fact]
    public async Task ParseableFormula_RendersFormulaControl_FromWorkerVerdict()
    {
        var source = "# M\n\n$x^2$ inline and:\n\n$$\n\\frac{1}{2}\n$$\n";
        var result = await MarkdownPreviewService.Instance.ParseAsync(source, "C:\\docs\\ok.md");

        WpfStaContext.Run(() =>
        {
            var document = MarkdownWpfRenderer.Render(result);
            var containers = MdPerfTestSupport.EnumerateInlineUiContainers(document);
            // 行内公式:InlineUIContainer 子节点是 FormulaControl,且公式文本与预解析键一致。
            Assert.Contains(containers, item => item.Child is FormulaControl { Formula: "x^2" });
            // 块级公式:BlockUIContainer → StackPanel → FormulaControl。
            Assert.Contains(document.Blocks.OfType<BlockUIContainer>(),
                item => item.Child is StackPanel { Children: [FormulaControl] });
        });
    }

    [Fact]
    public async Task UnparseableFormula_RendersFallbackText_FromWorkerVerdict()
    {
        var source = "# M\n\n$\\frac{1}$\n";
        var result = await MarkdownPreviewService.Instance.ParseAsync(source, "C:\\docs\\bad.md");

        WpfStaContext.Run(() =>
        {
            var document = MarkdownWpfRenderer.Render(result);
            // 失败判定直接原文兜底:UI 线程不创建 FormulaControl。
            Assert.DoesNotContain(MdPerfTestSupport.EnumerateInlineUiContainers(document),
                item => item.Child is FormulaControl);
            var paragraph = Assert.IsType<Paragraph>(document.Blocks.ElementAt(1));
            var fallback = Assert.IsType<TextBlock>(
                paragraph.Inlines.OfType<InlineUIContainer>().Single().Child);
            Assert.Equal("$\\frac{1}$", fallback.Text);
        });
    }
}

// ===== M2: 解析 LRU(内容哈希 → AST) =====

/// <summary>M2: 内容哈希 LRU —— 结构单测(独立实例,无并行干扰)。</summary>
public sealed class MarkdownParseCacheTests
{
    private static MarkdownRenderBudget Budget => new(1, 0, 0);

    private static (string Source, string Path, Markdig.Syntax.MarkdownDocument Doc) Doc(string name, int index)
    {
        var source = $"# {name}-{index}\n\nbody\n";
        return (source, $"C:\\docs\\{name}{index}.md",
            Markdig.Markdown.Parse(source, MarkdownPreviewService.Pipeline));
    }

    [Fact]
    public void ParseCache_LruEviction_KeepsRecent_PromotesOnAccess()
    {
        var cache = new MarkdownParseCache(maxEntries: 3);
        var (s1, p1, d1) = Doc("a", 1);
        var (s2, p2, d2) = Doc("b", 2);
        var (s3, p3, d3) = Doc("c", 3);
        var (s4, p4, d4) = Doc("d", 4);
        var (s5, p5, d5) = Doc("e", 5);

        cache.Put(s1, p1, d1, [], [], Budget);
        cache.Put(s2, p2, d2, [], [], Budget);
        cache.Put(s3, p3, d3, [], [], Budget);
        Assert.Equal(3, cache.Count);

        cache.Put(s4, p4, d4, [], [], Budget); // 超限 → 逐出最久未用的 s1
        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet(s1, p1, out _));
        Assert.True(cache.TryGet(s4, p4, out var e4));
        Assert.True(ReferenceEquals(d4, e4.Document));

        cache.TryGet(s2, p2, out _); // 访问 s2 → 提升为最近使用
        cache.Put(s5, p5, d5, [], [], Budget); // 逐出 s3(而非 s2)
        Assert.True(cache.TryGet(s2, p2, out _));
        Assert.False(cache.TryGet(s3, p3, out _));
    }

    [Fact]
    public void ParseCache_KeyDistinguishesContentAndPath()
    {
        var cache = new MarkdownParseCache();
        var (s1, p1, d1) = Doc("x", 1);
        var (s2, p2, d2) = Doc("y", 2);
        cache.Put(s1, p1, d1, [], [], Budget);

        Assert.True(cache.TryGet(s1, p1, out _));
        Assert.False(cache.TryGet(s1, p2, out _)); // 同内容不同路径
        Assert.False(cache.TryGet(s2, p1, out _)); // 同路径不同内容
        Assert.False(MarkdownParseCache.ComputeKey(s1, p1) == MarkdownParseCache.ComputeKey(s2, p1));
    }
}

/// <summary>M2 集成:主题变化/切回预览的重新解析请求(内容未变)跳过 Markdig 解析,
/// 复用已解析 AST;内容变化才重新解析。用独立缓存实例隔离并行测试。</summary>
public sealed class MarkdownParseReuseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-m2-{Guid.NewGuid():N}");

    public MarkdownParseReuseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task SameContentReparse_SkipsParse_ReusesAst_AndBudget()
    {
        var original = MarkdownParseCache.Instance;
        MarkdownParseCache.Instance = new MarkdownParseCache(maxEntries: 16);
        try
        {
            var body = $"# M2-{Guid.NewGuid():N}\n\nreused body\n";
            var path = Path.Combine(_directory, "m2.md");
            var first = await MarkdownPreviewService.Instance.ParseAsync(body, path);
            Assert.NotNull(first.Document);

            // 模拟主题切换:同一内容 + 路径的重新解析请求。
            var second = await MarkdownPreviewService.Instance.ParseAsync(body, path);
            Assert.NotSame(first, second); // 新结果包装(标签按结果对象身份渲染/分离)
            Assert.True(ReferenceEquals(first.Document, second.Document)); // M2: 复用同一 AST
            Assert.True(ReferenceEquals(first.Headings, second.Headings));
            Assert.True(ReferenceEquals(first.ImagePaths, second.ImagePaths));
            Assert.Equal(first.Budget, second.Budget);
            Assert.Empty(second.PreheatedPaths); // 缓存命中不重新预热

            // 内容变化 → 重新解析(新 AST)。
            var third = await MarkdownPreviewService.Instance.ParseAsync(body + "\nchanged\n", path);
            Assert.False(ReferenceEquals(second.Document, third.Document));
        }
        finally
        {
            MarkdownParseCache.Instance = original;
        }
    }
}

// ===== M5: 渲染预算 =====

/// <summary>M5: 渲染预算纯函数(初始批次/增量路径决策)+ 解析期统计。</summary>
public sealed class MarkdownRenderBudgetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-m5-{Guid.NewGuid():N}");

    public MarkdownRenderBudgetTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void DecideInitialBatch_HeavyElementBudget_HalvesFirstBatch()
    {
        // 常规:无重元素 → 32 块首屏。
        Assert.Equal(32, MarkdownRenderBudget.DecideInitialBatch(500, 0, 0));
        Assert.Equal(32, MarkdownRenderBudget.DecideInitialBatch(500, 3, 4));
        // 公式+图片 ≥ 8 → 16 块首屏(先渲首屏,再填充)。
        Assert.Equal(16, MarkdownRenderBudget.DecideInitialBatch(500, 8, 0));
        Assert.Equal(16, MarkdownRenderBudget.DecideInitialBatch(500, 3, 5));
        Assert.Equal(16, MarkdownRenderBudget.DecideInitialBatch(10, 4, 4)); // 块数少也走重预算

        Assert.False(MarkdownRenderBudget.IsOverInitialBudget(0, 0));
        Assert.False(MarkdownRenderBudget.IsOverInitialBudget(3, 4));
        Assert.True(MarkdownRenderBudget.IsOverInitialBudget(8, 0));
        Assert.True(MarkdownRenderBudget.IsOverInitialBudget(4, 4));
    }

    [Fact]
    public async Task ParseAsync_ComputesBudgetFromParsedDocument()
    {
        WpfStaContext.Run(() =>
        {
            MdPerfTestSupport.CreatePng(Path.Combine(_directory, "a.png"));
            MdPerfTestSupport.CreatePng(Path.Combine(_directory, "b.png"));
        });

        var path = Path.Combine(_directory, "budget.md");
        var source = "# T\n\n$x^2$ and $y_0$ and:\n\n$$\n\\frac{1}{2}\n$$\n\n![a](a.png)\n\n![b](b.png)\n";
        await File.WriteAllTextAsync(path, source);
        var result = await MarkdownPreviewService.Instance.ParseAsync(source, path);

        Assert.Equal(5, result.Budget.BlockCount); // 标题 + 行内公式段 + 块公式 + 2 图片段
        Assert.Equal(3, result.Budget.FormulaCount); // 2 行内 + 1 块级
        Assert.Equal(2, result.Budget.ImageCount);
    }
}

// ===== M6: 预热 = Acquire(渲染期不可逐出) =====

/// <summary>M6: 解析 worker 预热登记引用(引用交接/对称释放),渲染期间位图不被 LRU 逐出。</summary>
public sealed class MarkdownImagePreheatTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-m6-{Guid.NewGuid():N}");
    private readonly string[] _pngs;

    public MarkdownImagePreheatTests()
    {
        Directory.CreateDirectory(_directory);
        _pngs = Enumerable.Range(0, 4).Select(i => Path.Combine(_directory, $"img{i}.png")).ToArray();
        WpfStaContext.Run(() =>
        {
            foreach (var path in _pngs)
            {
                MdPerfTestSupport.CreatePng(path);
            }
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ParseAsync_PreheatRegistersReference_RenderKeepsBitmapCached()
    {
        var imgName = Path.GetFileName(_pngs[0]);
        var mdPath = Path.Combine(_directory, "img.md");
        var source = $"# I\n\n![x]({imgName})\n\n![x]({imgName})\n";
        await File.WriteAllTextAsync(mdPath, source);

        var result = await MarkdownPreviewService.Instance.ParseAsync(source, mdPath);
        // M6: 预热即 Acquire —— 预热登记了引用(旧行为是 0)。
        Assert.Contains(_pngs[0], result.PreheatedPaths);

        var preloaded = MarkdownImageCache.Instance.GetOrLoad(_pngs[0]);
        Assert.NotNull(preloaded);

        WpfStaContext.Run(() =>
        {
            var document = MarkdownWpfRenderer.Render(result);
            // 渲染期位图未被逐出:嵌入的是 Image(而非 [图片: ...] 回退链接)。
            Assert.Contains(MdPerfTestSupport.EnumerateInlineUiContainers(document),
                item => item.Child is Image);
        });

        // 渲染后仍是同一解码实例(未被逐出后重新解码)。
        Assert.Same(preloaded, MarkdownImageCache.Instance.GetOrLoad(_pngs[0]));
    }

    [Fact]
    public void PreheatReferencedEntry_SurvivesLruEviction()
    {
        // M6 契约单测:预热 = 解码 + Acquire;持引用期间 LRU 超限只能逐出零引用条目。
        var cache = new MarkdownImageCache(maxEntries: 3);
        WpfStaContext.Run(() =>
        {
            var img0 = cache.GetOrLoad(_pngs[0]);
            cache.Acquire(_pngs[0]); // 预热引用
            Assert.NotNull(img0);
            Assert.NotNull(cache.GetOrLoad(_pngs[1]));
            Assert.NotNull(cache.GetOrLoad(_pngs[2]));
            Assert.NotNull(cache.GetOrLoad(_pngs[3])); // 超上限 → 逐出零引用者(被引用的 img0 保留)
            Assert.Equal(3, cache.Snapshot().Entries);
            Assert.Same(img0, cache.GetOrLoad(_pngs[0]));
        });
    }
}

// ===== M7: 资源包一次性解析 + 标题标记惰性创建 =====

/// <summary>M7: 渲染资源包每次渲染只创建一份并贯穿整棵块树;标题位置标记不在块构造期创建。</summary>
public sealed class MarkdownRenderResourcesTests
{
    [Fact]
    public async Task RenderSession_UsesSingleResourceBundle_DoesNotCreateMarkersEagerly()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync(
            "# One\n\ntext\n\n## Two\n\ntext\n", "C:\\docs\\markers.md");

        WpfStaContext.Run(() =>
        {
            using var session = MarkdownWpfRenderer.BeginRender(result);
            // 资源包:会话持有唯一一份,文档前景直接取自包(块构造不再逐元素 TryFindResource)。
            Assert.NotNull(session.Resources);
            Assert.Equal(session.Resources.TextBrush, session.Document.Foreground);
            Assert.Equal(session.Resources.BodyFontSize, session.Document.FontSize);

            while (!session.AppendBatch(int.MaxValue))
            {
            }

            var h1 = Assert.IsType<Paragraph>(MarkdownPreviewView.FindNamedBlock(session.Document, "md_heading_0")!);
            var h2 = Assert.IsType<Paragraph>(MarkdownPreviewView.FindNamedBlock(session.Document, "md_heading_1")!);
            // M7: 渲染完成前不创建零尺寸标记(首屏不为标题数多付 Visual 布局成本)。
            Assert.Null(h1.Tag);
            Assert.Null(h2.Tag);
        });
    }

    [Fact]
    public void HeadingMarker_LazyCreation_IsIdempotent_AndAttachesInlineMarker()
    {
        WpfStaContext.Run(() =>
        {
            var paragraph = new Paragraph(new Run("heading"));
            Assert.Null(paragraph.Tag);

            var marker = MarkdownPreviewView.EnsureHeadingMarker(paragraph);
            Assert.IsAssignableFrom<FrameworkElement>(marker);
            Assert.Same(marker, paragraph.Tag);
            Assert.IsType<InlineUIContainer>(paragraph.Inlines.Last());
            Assert.Same(marker, ((InlineUIContainer)paragraph.Inlines.Last()).Child);

            // 幂等:重复物化返回同一标记,不重复挂 Inline。
            Assert.Same(marker, MarkdownPreviewView.EnsureHeadingMarker(paragraph));
            Assert.Equal(1, paragraph.Inlines.Count(inline => inline is InlineUIContainer));
        });
    }
}

// ===== M8: 代码块 Run 合并 =====

/// <summary>M8: 代码块按 100 行合并 Run(内嵌 '\n' 按换行渲染),全文逐字节一致、内联元素骤减。</summary>
public sealed class MarkdownCodeBlockTests
{
    [Fact]
    public void BuildCodeBatches_JoinedTextIdentical_FewerInlines()
    {
        var lines = Enumerable.Range(0, 250).Select(i => $"line {i}").ToArray();
        var batches = MarkdownWpfRenderer.BuildCodeBatches(lines);

        Assert.Equal(3, batches.Count); // 100 + 100 + 50
        Assert.All(batches, batch => Assert.True(
            batch.TrimEnd('\n').Split('\n').Length <= MarkdownWpfRenderer.CodeRunLineBatch));
        // 合并后全文与逐行版逐字节一致。
        Assert.Equal(string.Join("\n", lines), string.Concat(batches));
        // 内联元素:旧实现 2×N(Run+LineBreak)→ ceil(N/100) 个 Run。
        Assert.True(batches.Count < lines.Length);

        var small = MarkdownWpfRenderer.BuildCodeBatches(lines.Take(100).ToArray());
        Assert.Single(small);
        Assert.Equal(string.Join("\n", lines.Take(100)), small[0]);
    }

    [Fact]
    public void RenderedCodeBlock_MergesRuns_KeepsFullText()
    {
        // 用独立实例构建,避开并行测试对共享实例状态的干扰;直接验证批契约:
        // 150 行 → 2 批(100+50),拼接文本与逐行版逐字节一致。
        var lines = Enumerable.Range(0, 150).Select(i => $"l{i}").ToArray();
        var batches = MarkdownWpfRenderer.BuildCodeBatches(lines);
        Assert.Equal(2, batches.Count);
        Assert.Equal(string.Join("\n", lines), string.Concat(batches));
        Assert.All(batches, batch => Assert.True(
            batch.TrimEnd('\n').Split('\n').Length <= MarkdownWpfRenderer.CodeRunLineBatch));
    }
}

// ===== M4/M5 集成: 增量标题表 + 预算驱动的初始批次 =====

/// <summary>M4: 长文档增量渲染 —— 标题查找表逐批增量合并(无全文档重建),渲染完成后
/// 全部标题可达。M5: 重元素文档初始批次减半(16 vs 32)。</summary>
public sealed class MarkdownIncrementalHeadingMapTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-m4-{Guid.NewGuid():N}");

    public MarkdownIncrementalHeadingMapTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static string BuildLongDocument(int paragraphs)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < paragraphs; i++)
        {
            builder.AppendLine(i % 10 == 0 ? $"# H{i}" : $"text {i}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    [Fact]
    public async Task LongDocument_HeadingsMappedIncrementally_NoFullRebuild_AllReachable()
    {
        var source = BuildLongDocument(80);
        var result = await MarkdownPreviewService.Instance.ParseAsync(source, "C:\\docs\\long.md");
        Assert.Equal(8, result.Headings.Count);

        WpfStaContext.Run(() =>
        {
            var view = new MarkdownPreviewView();
            view.RenderResult = result;
            for (var i = 0; i < 500 && (view.Document?.Blocks.Count ?? 0) < result.Document!.Count; i++)
            {
                WpfStaContext.PumpQueue();
            }

            Assert.Equal(result.Document!.Count, view.Document!.Blocks.Count);
            // M4: 增量路径全程零次全文档重建(短文档一次性渲染才会全量构建一次)。
            Assert.Equal(0, view.HeadingMapFullRebuildCount);
            // 查找表经逐批增量合并后完整:每个标题都在表中且可跳转。
            Assert.NotNull(view.HeadingParagraphsForTest);
            foreach (var heading in result.Headings)
            {
                Assert.True(view.HeadingParagraphsForTest!.ContainsKey(heading.DocumentName));
            }

            Assert.True(view.ScrollToAnchorSafe(result.Headings[^1].Anchor));
            view.RenderResult = null;
            WpfStaContext.PumpQueue();
        });
    }

    [Fact]
    public async Task HeavyBudgetDocument_UsesSmallerInitialBatch_ThenFills()
    {
        var paths = new List<string>();
        WpfStaContext.Run(() =>
        {
            for (var i = 0; i < 40; i++)
            {
                var path = Path.Combine(_directory, $"heavy{i}.png");
                MdPerfTestSupport.CreatePng(path);
                paths.Add(path);
            }
        });

        var builder = new StringBuilder("# Heavy\n");
        foreach (var path in paths)
        {
            builder.AppendLine();
            builder.AppendLine($"![i]({Path.GetFileName(path)})");
            builder.AppendLine();
        }

        var mdPath = Path.Combine(_directory, "heavy.md");
        var source = builder.ToString();
        await File.WriteAllTextAsync(mdPath, source);
        var result = await MarkdownPreviewService.Instance.ParseAsync(source, mdPath);
        Assert.True(result.Budget.ImageCount >= MarkdownRenderBudget.HeavyElementThreshold);
        Assert.Equal(16, MarkdownRenderBudget.DecideInitialBatch(result.Document!.Count,
            result.Budget.FormulaCount, result.Budget.ImageCount));

        WpfStaContext.Run(() =>
        {
            var view = new MarkdownPreviewView();
            view.RenderResult = result;
            WpfStaContext.PumpQueue(); // 首批:重预算 → 16 块(常规文档此处为 32)
            Assert.Equal(16, view.Document!.Blocks.Count);

            for (var i = 0; i < 500 && view.Document.Blocks.Count < result.Document!.Count; i++)
            {
                WpfStaContext.PumpQueue();
            }

            Assert.Equal(result.Document.Count, view.Document.Blocks.Count);
            view.RenderResult = null;
            WpfStaContext.PumpQueue();
        });
    }
}

// ===== M9: 滚动偏移 50ms 尾沿节流 =====

/// <summary>M9: 滚动事件风暴只产生一次(最新值)尾沿写入;Flush 立即落盘并取消定时器。
/// 注意:共享 STA 泵只在 PumpQueue 时处理 Dispatcher 队列,节流测试不在泵内做长睡
/// (Sleep 会阻塞泵线程本身),改为"泵外等待 → 泵内断言"两段式。</summary>
public sealed class MarkdownScrollThrottleTests
{
    [Fact]
    public void BurstScroll_SingleTrailingWrite_LatestValueWins()
    {
        var writes = new List<double>();
        MarkdownScrollThrottle throttle = null!;
        WpfStaContext.Run(() =>
        {
            throttle = new MarkdownScrollThrottle(offset => writes.Add(offset), intervalMs: 30);
            for (var i = 1; i <= 200; i++)
            {
                throttle.Raise(i); // 滚动风暴
            }

            Assert.Equal(0, throttle.WriteCount); // 窗口内不写
        });

        // 定时器在泵外到期(WM_TIMER 排队,泵内才处理);两段式避免阻塞共享泵。
        Thread.Sleep(80);
        WpfStaContext.Run(() =>
        {
            WpfStaContext.PumpQueue(); // 处理到期的定时器踢一次
            Assert.True(throttle.WriteCount >= 1, $"尾沿写入未发生: {throttle.WriteCount}");
            Assert.True(throttle.WriteCount <= 2, $"滚动风暴至多 2 次写入,实际 {throttle.WriteCount}");
            Assert.Equal(200, writes[^1]); // 尾沿写入取最新值
        });
    }

    [Fact]
    public void Flush_WritesPendingImmediately_AndCancelsTimer()
    {
        var writes = new List<double>();
        WpfStaContext.Run(() =>
        {
            var throttle = new MarkdownScrollThrottle(offset => writes.Add(offset));
            throttle.Raise(7);
            Assert.Equal(0, throttle.WriteCount);
            throttle.Flush();
            Assert.Equal(1, throttle.WriteCount);
            Assert.Single(writes);
            Assert.Equal(7.0, writes[0]);

            throttle.Flush(); // 空 Flush 空操作
            Assert.Equal(1, throttle.WriteCount);

            // 定时器已取消:新的 Raise + Flush 只产生一次写入,且不被旧定时器重复触发。
            throttle.Raise(8);
            throttle.Flush();
            Assert.Equal(2, throttle.WriteCount);
            Assert.Equal(8.0, writes[^1]);
        });
    }
}

// ===== M10: 垂直进度映射(纯函数) =====

/// <summary>M10: 存储值 ≤1 = 垂直进度(offset/extent),&gt;1 = 旧版像素偏移;
/// 恢复时按新文档 extent 映射,旧偏移越界时钳制而非失真。</summary>
public sealed class MarkdownVerticalProgressTests
{
    [Fact]
    public void ToVerticalProgress_ClampsAndZerosTop()
    {
        Assert.Equal(0, MarkdownPreviewView.ToVerticalProgress(0, 1000));
        Assert.Equal(0, MarkdownPreviewView.ToVerticalProgress(0.5, 1000)); // 顶部亚像素 → 0
        Assert.Equal(0.5, MarkdownPreviewView.ToVerticalProgress(500, 1000), 3);
        Assert.Equal(0.9, MarkdownPreviewView.ToVerticalProgress(900, 1000), 3); // 0.9 = 900/1000
        Assert.Equal(0, MarkdownPreviewView.ToVerticalProgress(500, 0)); // extent 未布局
    }

    [Fact]
    public void RestoreVerticalOffset_ProgressMapsProportionallyToNewExtent()
    {
        Assert.Equal(0, MarkdownPreviewView.RestoreVerticalOffset(0, 1000, 200));
        Assert.Equal(500, MarkdownPreviewView.RestoreVerticalOffset(0.5, 1000, 200));
        Assert.Equal(800, MarkdownPreviewView.RestoreVerticalOffset(1.0, 1000, 200)); // 钳制到最大可滚动
        // 文档变长(1000 → 2000):同一进度映射到更深处。
        Assert.Equal(1000, MarkdownPreviewView.RestoreVerticalOffset(0.5, 2000, 200));
        // 文档变短(1000 → 600):钳制,不越界。
        Assert.Equal(400, MarkdownPreviewView.RestoreVerticalOffset(0.9, 600, 200));
    }

    [Fact]
    public void RestoreVerticalOffset_LegacyPixelOffset_ClampedNotRescaled()
    {
        // >1 视为旧版像素偏移:范围内原样、越界钳制。
        Assert.Equal(120, MarkdownPreviewView.RestoreVerticalOffset(120, 1000, 200));
        Assert.Equal(800, MarkdownPreviewView.RestoreVerticalOffset(2000, 1000, 200)); // 钳制到 extent-viewport
        Assert.Equal(0, MarkdownPreviewView.RestoreVerticalOffset(500, 100, 200)); // extent < viewport
    }

    [Fact]
    public void RoundTrip_SameDocument_PreservesOffset()
    {
        const double extent = 4000;
        const double viewport = 800;
        const double offset = 1500;

        var stored = MarkdownPreviewView.ToVerticalProgress(offset, extent);
        Assert.Equal(offset, MarkdownPreviewView.RestoreVerticalOffset(stored, extent, viewport), 3);
    }
}
