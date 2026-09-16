using Nornia.Desktop.Code;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        double headingTopInViewport = 0, hostViewport = 0, hostExtent = 0;
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

            // 点击跳转行为:目标标题显示在屏幕中间——标题段落顶边应落在预览视口垂直中心线上。
            var host = FindFirstScrollViewer(view.MarkdownViewControl);
            var gammaHeading = tab.MarkdownRenderResult!.Headings.Single(h => h.Anchor == "gamma");
            var paragraph = view.MarkdownViewControl.HeadingParagraphsForTest![gammaHeading.DocumentName];
            // 段落是 ContentElement 没有几何 API,按视图同款方式经零尺寸位置标记取几何。
            var marker = Assert.IsAssignableFrom<FrameworkElement>(paragraph.Tag);
            headingTopInViewport = marker.TransformToVisual(host).Transform(new Point(0, 0)).Y;
            hostViewport = host.ViewportHeight;
            hostExtent = host.ExtentHeight;
        });

        Assert.True(rendered);
        // 逻辑位置已由大纲点击写入。
        Assert.Equal("gamma", tab.MarkdownAnchor);
        Assert.Equal(gammaLine, tab.MarkdownHeadingLine);
        // 预览实际滚动到目标标题,并报告当前标题(而非停留在文档顶部)。
        Assert.True(offsetAfter > offsetBefore, $"预览未滚动: {offsetBefore} → {offsetAfter}");
        Assert.NotNull(currentHeading);
        Assert.Equal("gamma", currentHeading!.Anchor);
        // 跳转目标居中(文档边界钳制):最终偏移必须等于 clamp(标题内容坐标 − 视口/2, 0, extent − 视口)。
        // 标题在文档底部时中心不可达,贴住文档末端仍是正确居中。
        var contentY = offsetAfter + headingTopInViewport;
        var expectedOffset = Math.Clamp(contentY - hostViewport / 2, 0, Math.Max(0, hostExtent - hostViewport));
        Assert.True(Math.Abs(offsetAfter - expectedOffset) <= 3.0,
            $"跳转目标未居中: 偏移={offsetAfter:F1}, 期望={expectedOffset:F1} (标题内容坐标={contentY:F1}, 视口={hostViewport:F1}, extent={hostExtent:F1})");
    }

    private static ScrollViewer FindFirstScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            try
            {
                return FindFirstScrollViewer(child);
            }
            catch (InvalidOperationException)
            {
                // 继续兄弟分支。
            }
        }

        throw new InvalidOperationException("未找到 ScrollViewer。");
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(960, 520));
        element.Arrange(new Rect(0, 0, 960, 520));
    }

    private static void PumpQueue() => WpfStaContext.PumpQueue();
}
