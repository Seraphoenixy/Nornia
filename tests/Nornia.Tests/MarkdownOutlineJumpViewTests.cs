using Nornia.Desktop.Code;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;

namespace Nornia.Tests;

/// <summary>大纲点击 → Markdown 预览同步跳转的端到端集成测试:走真实 FilePreviewView 装配
/// (JumpToSymbolCommand → GoToPositionRequested → MarkdownView 锚点跳转),使用真实
/// Nornia.Desktop.App 资源字典(离线布局,无需窗口)。文档足够长以保证预览可滚动。</summary>
public sealed class MarkdownOutlineJumpViewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-jump-{Guid.NewGuid():N}");

    public MarkdownOutlineJumpViewTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // 每节约 8 行:渲染高度远超 520px 视口,底部标题的跳转目标可被 ScrollViewer 到达。
    private const string Document = """
        # Root

        line one
        line two
        line three
        line four
        line five
        line six

        ## Alpha

        line one
        line two
        line three
        line four
        line five
        line six

        ### Deep

        line one
        line two
        line three
        line four
        line five
        line six

        ## Beta

        line one
        line two
        line three
        line four
        line five
        line six

        ## Gamma

        line one
        line two
        line three
        line four
        line five
        line six
        """;

    [Fact]
    public async Task OutlineClick_InRenderedMode_PreviewJumpsToHeading()
    {
        var path = Path.Combine(_directory, "jump.md");
        await File.WriteAllTextAsync(path, Document);
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();
        Assert.NotNull(tab.MarkdownRenderResult);
        Assert.True(tab.IsMarkdownRendered);
        var gamma = tab.SymbolDocument.All.Single(node => node.Name == "Gamma");
        var gammaLine = gamma.Range.StartLine;

        bool rendered = false;
        double offsetBefore = 0, offsetAfter = 0;
        MarkdownHeading? currentHeading = null;

        WpfStaContext.Run(() =>
        {
            var view = new FilePreviewView();
            view.DataContext = tab;
            Layout(view);
            PumpQueue();
            Layout(view);
            PumpQueue();

            rendered = tab.IsMarkdownRendered;
            offsetBefore = view.MarkdownViewControl.VerticalOffset;

            // 大纲树节点点击的真实命令路径。
            tab.JumpToSymbolCommand.Execute(gamma);

            // 双泵:延迟滚动生效 + 跳转回调/节流重算执行。
            PumpQueue();
            PumpQueue();

            currentHeading = view.MarkdownViewControl.CurrentHeading;
            offsetAfter = view.MarkdownViewControl.VerticalOffset;
        });

        Assert.True(rendered);
        // 逻辑位置已由大纲点击写入。
        Assert.Equal("gamma", tab.MarkdownAnchor);
        Assert.Equal(gammaLine, tab.MarkdownHeadingLine);
        // 预览实际滚动到目标标题,并报告当前标题(而非停留在文档顶部)。
        Assert.True(offsetAfter > offsetBefore, $"预览未滚动: {offsetBefore} → {offsetAfter}");
        Assert.NotNull(currentHeading);
        Assert.Equal("gamma", currentHeading!.Anchor);
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(960, 520));
        element.Arrange(new Rect(0, 0, 960, 520));
    }

    private static void PumpQueue() => WpfStaContext.PumpQueue();
}
