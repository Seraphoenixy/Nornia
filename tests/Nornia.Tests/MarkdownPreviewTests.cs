using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Nornia.Desktop.Code;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfList = System.Windows.Documents.List;
using WpfListItem = System.Windows.Documents.ListItem;
using WpfTable = System.Windows.Documents.Table;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfRun = System.Windows.Documents.Run;

namespace Nornia.Tests;

public sealed class MarkdownPreviewServiceTests
{
    [Fact]
    public async Task ParseAsync_RecognizesCommonBlocksAndMath()
    {
        var source = """
            # Notes

            A formula $x^2 + y^2$ and a table:

            | A | B |
            | - | - |
            | 1 | 2 |

            $$
            \\frac{1}{\\sqrt{2}}
            $$
            """;

        var result = await MarkdownPreviewService.Instance.ParseAsync(source, "C:\\docs\\README.md");

        Assert.Contains(result.Headings, heading => heading.Text == "Notes" && heading.Anchor == "notes");
        Assert.NotNull(result.Document);
        var document = result.Document;
        Assert.Contains(document.Descendants<MathInline>(), _ => true);
        Assert.Contains(document.Descendants<MathBlock>(), _ => true);
        Assert.Contains(document.Descendants<Markdig.Extensions.Tables.Table>(), _ => true);
    }

    [Fact]
    public async Task ParseAsync_ExpandsTexStyleDelimitersWithoutTouchingCodeFences()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync("""
            Inline \(a+b\) and:

            \[
            c^2
            \]

            ```text
            \(not math\)
            ```
            """, "C:\\docs\\README.md");

        Assert.NotNull(result.Document);
        var document = result.Document;
        Assert.Single(document.Descendants<MathInline>());
        Assert.Single(document.Descendants<MathBlock>());
        Assert.Contains(document.Descendants<FencedCodeBlock>(), block =>
            string.Join("\n", block.Lines.Lines.Select(line => line.ToString())).Contains("not math", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WpfRenderer_ProducesDocumentBlocksOnStaThread()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync(
            "# 1-文档信息\n\nText with $x^2$.", "C:\\docs\\README.md");
        Assert.Equal("1-文档信息", result.Headings[0].Anchor);
        Assert.Equal("md_heading_0", result.Headings[0].DocumentName);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(MarkdownWpfRenderer.Render(result).Blocks.Count);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(await completion.Task > 0);
    }

    [Fact]
    public async Task WpfRenderer_LongDocumentCanBeAppendedInBoundedBatches()
    {
        var source = string.Join("\n\n", Enumerable.Range(1, 96).Select(index => $"Paragraph {index}"));
        var result = await MarkdownPreviewService.Instance.ParseAsync(source, "C:\\docs\\long.md");
        var completion = new TaskCompletionSource<(bool InitialCompleted, int InitialCount, int FinalCount)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var session = MarkdownWpfRenderer.BeginRender(result);
                var initialCompleted = session.AppendBatch(8);
                var initialCount = session.Document.Blocks.Count;
                while (!session.AppendBatch(16))
                {
                }

                completion.SetResult((initialCompleted, initialCount, session.Document.Blocks.Count));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var resultState = await completion.Task;
        Assert.False(resultState.InitialCompleted);
        Assert.InRange(resultState.InitialCount, 1, 8);
        Assert.True(resultState.FinalCount > resultState.InitialCount);
    }

    [Fact]
    public async Task WpfRenderer_ListsPreserveOrderedStartNestedStructureAndLeftAlignment()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync("""
            3. outer one
            4. outer two
               1. nested one
                  - deeply nested
            """, "C:\\docs\\lists.md");
        var completion = new TaskCompletionSource<(TextAlignment Alignment, TextMarkerStyle Marker,
            int Start, int OuterCount, int NestedCount, int DeepCount, double MarkerOffset)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var document = MarkdownWpfRenderer.Render(result);
                var outer = document.Blocks.OfType<WpfList>().Single();
                var nested = outer.ListItems.OfType<WpfListItem>().ElementAt(1).Blocks.OfType<WpfList>().Single();
                var deep = nested.ListItems.OfType<WpfListItem>().Single().Blocks.OfType<WpfList>().Single();
                completion.SetResult((document.TextAlignment, outer.MarkerStyle, outer.StartIndex,
                    outer.ListItems.Count, nested.ListItems.Count, deep.ListItems.Count, outer.MarkerOffset));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var state = await completion.Task;
        Assert.Equal(TextAlignment.Left, state.Alignment);
        Assert.Equal(TextMarkerStyle.Decimal, state.Marker);
        Assert.Equal(3, state.Start);
        Assert.Equal(2, state.OuterCount);
        Assert.Equal(1, state.NestedCount);
        Assert.Equal(1, state.DeepCount);
        Assert.Equal(6, state.MarkerOffset);
    }

    [Fact]
    public async Task MarkdownPreviewView_FindsHeadingsInsideNestedListBlocks()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync(
            "- ## Nested heading", "C:\\docs\\nested-heading.md");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var document = MarkdownWpfRenderer.Render(result);
                completion.SetResult(MarkdownPreviewView.FindNamedBlock(
                    document, result.Headings.Single().DocumentName) is WpfParagraph);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(await completion.Task);
    }

    [Fact]
    public async Task WpfRenderer_TableUsesMarkdownColumnAlignmentAndLeftDefault()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync("""
            | left | center | right | default |
            | :--- | :---: | ---: | --- |
            | a | b | c | d |
            """, "C:\\docs\\table.md");
        var completion = new TaskCompletionSource<(TextAlignment TableAlignment, TextAlignment[] Cells, TextAlignment[] Paragraphs)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var document = MarkdownWpfRenderer.Render(result);
                var table = document.Blocks.OfType<WpfTable>().Single();
                var cells = table.RowGroups[0].Rows[1].Cells.Select(cell => cell.TextAlignment).ToArray();
                var paragraphs = table.RowGroups[0].Rows[1].Cells
                    .SelectMany(cell => cell.Blocks.OfType<WpfParagraph>())
                    .Select(paragraph => paragraph.TextAlignment)
                    .ToArray();
                completion.SetResult((table.TextAlignment, cells, paragraphs));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var state = await completion.Task;
        Assert.Equal(TextAlignment.Left, state.TableAlignment);
        Assert.Equal([TextAlignment.Left, TextAlignment.Center, TextAlignment.Right, TextAlignment.Left], state.Cells);
        Assert.Equal([TextAlignment.Left, TextAlignment.Center, TextAlignment.Right, TextAlignment.Left], state.Paragraphs);
    }

    [Fact]
    public async Task WpfRenderer_RendersIndentedCodeEntitiesAndReadOnlyTaskChecks()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync(
            "Entity &copy; &amp;\n\n    INDENTED_CODE\n\n- [ ] todo\n- [x] done",
            "C:\\docs\\syntax.md");
        var completion = new TaskCompletionSource<(string Text, bool[] Checks)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var document = MarkdownWpfRenderer.Render(result);
                var text = string.Concat(document.Blocks.OfType<WpfParagraph>()
                    .SelectMany(paragraph => paragraph.Inlines.OfType<WpfRun>())
                    .Select(run => run.Text));
                var checks = document.Blocks.OfType<WpfList>().Single().ListItems
                    .SelectMany(item => item.Blocks.OfType<WpfParagraph>())
                    .SelectMany(paragraph => paragraph.Inlines.OfType<InlineUIContainer>())
                    .Select(container => ((WpfCheckBox)container.Child).IsChecked == true)
                    .ToArray();
                completion.SetResult((text, checks));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var state = await completion.Task;
        Assert.Contains("Entity © &", state.Text, StringComparison.Ordinal);
        Assert.Contains("INDENTED_CODE", state.Text, StringComparison.Ordinal);
        Assert.Equal([false, true], state.Checks);
    }

    [Fact]
    public async Task WpfRenderer_RendersEnabledContainerBlocksAndFootnotesAsBlocks()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync("""
            ---
            title: preview
            ---

            > [!NOTE]
            > alert body

            ::: note
            container body
            :::

            Body[^1]

            [^1]: footnote body
            """, "C:\\docs\\extensions.md");
        var completion = new TaskCompletionSource<(string Body, string Footnote, int Sections)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var document = MarkdownWpfRenderer.Render(result);
                var body = string.Concat(document.Blocks.OfType<WpfParagraph>()
                    .SelectMany(paragraph => paragraph.Inlines.OfType<WpfRun>())
                    .Select(run => run.Text));
                var footnote = string.Concat(document.Blocks.OfType<Section>()
                    .SelectMany(section => section.Blocks.OfType<WpfParagraph>())
                    .SelectMany(paragraph => paragraph.Inlines.OfType<WpfRun>())
                    .Select(run => run.Text));
                completion.SetResult((body, footnote, document.Blocks.OfType<Section>().Count()));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var state = await completion.Task;
        Assert.Contains("[NOTE] alert body", state.Body, StringComparison.Ordinal);
        Assert.Contains("container body", state.Body, StringComparison.Ordinal);
        Assert.Contains("1. footnote body", state.Footnote, StringComparison.Ordinal);
        Assert.Equal(1, state.Sections);
    }

    [Fact]
    public async Task WpfRenderer_RendersSafeInlineHtmlWithoutExecutingUnknownTags()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync(
            "HTML <strong>bold</strong> <em>italic</em> <sub>x</sub><br />next <script>raw</script>",
            "C:\\docs\\html.md");
        var completion = new TaskCompletionSource<(bool Bold, bool Italic, bool Subscript, int Breaks, string Raw)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var paragraph = MarkdownWpfRenderer.Render(result).Blocks.OfType<WpfParagraph>().Single();
                var spans = paragraph.Inlines.OfType<Span>().ToArray();
                var raw = string.Concat(paragraph.Inlines.OfType<WpfRun>().Select(run => run.Text));
                completion.SetResult((
                    spans.Any(span => span.FontWeight == FontWeights.Bold
                        && span.Inlines.OfType<WpfRun>().Any(run => run.Text == "bold")),
                    spans.Any(span => span.FontStyle == FontStyles.Italic
                        && span.Inlines.OfType<WpfRun>().Any(run => run.Text == "italic")),
                    spans.Any(span => span.BaselineAlignment == BaselineAlignment.Subscript
                        && span.Inlines.OfType<WpfRun>().Any(run => run.Text == "x")),
                    paragraph.Inlines.OfType<LineBreak>().Count(),
                    raw));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var state = await completion.Task;
        Assert.True(state.Bold);
        Assert.True(state.Italic);
        Assert.True(state.Subscript);
        Assert.Equal(1, state.Breaks);
        Assert.Contains("<script>raw</script>", state.Raw, StringComparison.Ordinal);
    }

    /// <summary>渲染围栏代码块段落的全部 Run 文本(拼接);按内容特征定位代码段落,
    /// 不依赖字体资源——回归锚点:Markdig 1.x 的 CodeBlockLines 恒空(正文丢失)与
    /// Lines 底层容量数组的尾部填充(多余空行)都会让拼接文本偏离源码。</summary>
    private static string RenderCodeParagraphText(string source)
    {
        var result = MarkdownPreviewService.Instance.ParseAsync(source, "C:\\docs\\code.md").GetAwaiter().GetResult();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var doc = MarkdownWpfRenderer.Render(result);
                var code = doc.Blocks.OfType<WpfParagraph>().Single(p =>
                    string.Concat(p.Inlines.OfType<WpfRun>().Select(r => r.Text))!.Contains("CODE_MARKER", StringComparison.Ordinal));
                completion.SetResult(string.Concat(code.Inlines.OfType<WpfRun>().Select(r => r.Text)));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.GetAwaiter().GetResult();
    }

    [Fact]
    public void WpfRenderer_FencedCodeBlock_RendersEveryLine_ExactlyOnce()
    {
        var text = RenderCodeParagraphText("""
            # T

            ```csharp
            CODE_MARKER
            namespace Demo
            {
                class C
            }
            ```

            After.
            """);

        // info 行 + 全部代码行,逐字节一致;尾部不得出现容量数组填充的空行。
        Assert.Equal("csharp\nCODE_MARKER\nnamespace Demo\n{\n    class C\n}", text);
    }

    [Fact]
    public void WpfRenderer_EmptyFencedCodeBlock_RendersInfoOnlyWithoutThrowing()
    {
        var text = RenderCodeParagraphText("```CODE_MARKER\n```\n");

        // 空围栏:只有 info 行,不崩溃(Markdig 1.x 空代码块的 Lines 底层数组为 null)。
        Assert.Equal("CODE_MARKER\n", text);
    }

    [Fact]
    public void WpfRenderer_FencedCodeBlock_CrlfSource_RendersWithoutCarriageReturns()
    {
        var text = RenderCodeParagraphText("```CODE_MARKER\r\nLine A\r\nLine B\r\n```\r\n");

        // Windows CRLF 文件:行内容不得残留 \r。
        Assert.Equal("CODE_MARKER\nLine A\nLine B", text);
    }

    [Fact]
    public void WpfRenderer_FencedCodeBlock_TrailingBlankLineInsideFence_PreservedOnce()
    {
        var text = RenderCodeParagraphText("```CODE_MARKER\r\nLine A\n\n```\n");

        // 围栏内真实的尾部空行保留一行(不丢、不补)。
        Assert.Equal("CODE_MARKER\nLine A\n", text);
    }

}

/// <summary>统一标题模型:ATX + Setext、代码围栏排除、重复锚点后缀、层级栈父子树。</summary>
public sealed class MarkdownHeadingModelTests
{
    [Fact]
    public void ExtractHeadings_RecognizesAtxAndSetext_IgnoresFencedPseudoHeadings()
    {
        var source = """
            # Root

            ## Alpha

            ### Deep

            ## Beta

            Gamma
            =====

            Delta
            -----

            ```text
            # Fenced pseudo heading
            ## Still fenced
            ```

            Done
            """;

        var headings = MarkdownHeadingModel.ExtractHeadings(source);

        Assert.Equal(["Root", "Alpha", "Deep", "Beta", "Gamma", "Delta"],
            headings.Select(item => item.Text).ToArray());
        Assert.Equal([1, 2, 3, 2, 1, 2], headings.Select(item => item.Level).ToArray());
        Assert.Equal([1, 3, 5, 7, 9, 12], headings.Select(item => item.Line).ToArray());
        Assert.DoesNotContain(headings, item => item.Text.Contains("Fenced", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractHeadings_DuplicateTitlesKeepSuffixAnchors()
    {
        var headings = MarkdownHeadingModel.ExtractHeadings("# Intro\n\nA\n\n# Intro\n\nB\n\n# Intro\n");

        Assert.Equal(["intro", "intro-1", "intro-2"], headings.Select(item => item.Anchor).ToArray());
        Assert.Equal(["md_heading_0", "md_heading_1", "md_heading_2"],
            headings.Select(item => item.DocumentName).ToArray());
    }

    [Fact]
    public void SymbolDocument_BuildsLevelStackTree_WithSkippedAndConsecutiveLevels()
    {
        var source = """
            # One

            ### Skipped to three

            ## Two

            ## Three

            # Four
            """;

        var document = CodeSymbolAnalyzer.Instance.Analyze(source, CodeOutlineKind.Markdown);

        // 根 = 全部 H1;H2/H3 按层级栈挂到最近的更高层级父级(跳级 H1 → H3 直挂 H1)。
        Assert.Equal(["One", "Four"], document.Roots.Select(node => node.Name).ToArray());
        var one = document.Roots[0];
        Assert.Equal(["Skipped to three", "Two", "Three"], one.Children.Select(node => node.Name).ToArray());
        Assert.Null(document.Roots[1].Parent); // Four 无父
        var two = one.Children[1];
        Assert.Equal("One", two.Parent?.Name);
        Assert.Empty(two.Children); // 连续同级("Two" 与 "Three")互不为父
    }

    [Fact]
    public async Task FilePreviewTab_OutlineMatchesRenderedHeadings_WithSetextAndFences()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nornia-md-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "README.md");
            await File.WriteAllTextAsync(path, """
                # Root

                ```text
                # Fenced
                ```

                Setext One
                ==========

                ## Child
                """);
            var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);

            await tab.LoadAsync();

            var rendered = tab.MarkdownRenderResult?.Headings.Select(item => item.Text).ToArray() ?? [];
            var outline = tab.SymbolDocument.All.Where(node => node.ShowInOutline).Select(node => node.Name).ToArray();
            Assert.Equal(rendered, outline);
            Assert.Equal(["Root", "Setext One", "Child"], rendered);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}

public sealed class MarkdownFilePreviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-markdown-{Guid.NewGuid():N}");

    public MarkdownFilePreviewTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task FilePreviewTab_MarkdownDefaultsToRenderedAndCanSwitchToSource()
    {
        var path = Path.Combine(_directory, "README.md");
        await File.WriteAllTextAsync(path,
            "# Root\n\n## Child\n\n### Deep\n\n## Sibling\n\n# Other\n\n$x^2$");
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);

        await tab.LoadAsync();

        Assert.True(tab.IsMarkdown);
        Assert.NotNull(tab.MarkdownRenderResult);
        Assert.True(tab.IsMarkdownRendered);
        Assert.Equal(2, tab.SymbolRoots.Count);
        Assert.Equal("Root", tab.SymbolRoots[0].Name);
        Assert.Equal(["Child", "Sibling"], tab.SymbolRoots[0].Children.Select(node => node.Name).ToArray());
        Assert.Equal("Deep", tab.SymbolRoots[0].Children[0].Children.Single().Name);
        tab.ShowMarkdownSourceCommand.Execute(null);
        Assert.Equal(MarkdownViewMode.Source, tab.MarkdownMode);
        Assert.False(tab.IsMarkdownRendered);
        // 源码模式已释放渲染产物:切回预览需重新解析(异步)。
        Assert.Null(tab.MarkdownRenderResult);
        Assert.NotEmpty(tab.MarkdownHeadings); // 标题列表常驻,光标同步不依赖渲染产物
        tab.ShowMarkdownPreviewCommand.Execute(null);
        Assert.True(await WaitForAsync(() => tab.MarkdownRenderResult is not null),
            "切回预览后应在超时内完成重新解析");
        Assert.True(tab.IsMarkdownRendered);
    }

    /// <summary>闪帧修复:渲染模式的 Markdown 在加载前/结果就绪期间源码面都保持隐藏(中性空白
    /// 而非"源码 + 内置语法高亮"闪帧);源码模式与手动切换时源码面可见性随之收敛。</summary>
    [Fact]
    public async Task FilePreviewTab_RenderedMarkdown_HidesSourceSurfaceUntilSourceMode()
    {
        var path = Path.Combine(_directory, "surface.md");
        await File.WriteAllTextAsync(path, "# Root\n\nbody");
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);

        // 加载前:默认渲染模式已决定源码面隐藏(加载窗口不显示源码)。
        Assert.False(tab.ShowSourceSurface);

        await tab.LoadAsync();
        Assert.True(tab.IsMarkdownRendered);
        Assert.False(tab.ShowSourceSurface);

        tab.ShowMarkdownSourceCommand.Execute(null);
        Assert.True(tab.ShowSourceSurface);
        tab.ShowMarkdownPreviewCommand.Execute(null);
        Assert.True(await WaitForAsync(() => tab.MarkdownRenderResult is not null),
            "切回预览后应在超时内完成重新解析");
        Assert.False(tab.ShowSourceSurface);
    }

    /// <summary>窗口阅读的大 Markdown 不产生渲染预览:模式显式落到源码,源码面(含提示)可见,
    /// 无渲染产物驻留。</summary>
    [Fact]
    public async Task FilePreviewTab_WindowedMarkdown_FallsBackToSourceMode()
    {
        var path = Path.Combine(_directory, "big.md");
        using (var writer = new StreamWriter(path, append: false))
        {
            writer.WriteLine("# Big");
            for (var i = 0; i < 2100; i++)
            {
                writer.WriteLine(new string('x', 4096));
            }
        }
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();

        Assert.Equal(ReadOnlyContentTier.Windowed, tab.CapacityTier);
        Assert.Equal(MarkdownViewMode.Source, tab.MarkdownMode);
        Assert.True(tab.ShowSourceSurface);
        Assert.Null(tab.MarkdownRenderResult);
        Assert.NotEmpty(tab.Notice); // 窗口阅读提示在可见的源码面上
    }

    /// <summary>二进制 .md 走空态:模式落到源码,空态提示(EmptyStateControl 位于源码面内)可见。</summary>
    [Fact]
    public async Task FilePreviewTab_BinaryMarkdown_FallsBackToSourceMode()
    {
        var path = Path.Combine(_directory, "binary.md");
        await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x03, 0x04]);
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();

        Assert.True(tab.IsBinary);
        Assert.Equal(MarkdownViewMode.Source, tab.MarkdownMode);
        Assert.True(tab.ShowSourceSurface);
        Assert.NotEmpty(tab.Notice);
    }

    /// <summary>轮询等待条件成立(异步解析/渲染完成判定,上限 10 秒)。</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + (long)timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
