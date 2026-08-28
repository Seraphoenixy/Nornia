using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

/// <summary>MarkdownPreviewView 双向同步的 STA 定向测试:滚动 → 当前标题(视口顶部附近规则)、
/// 锚点/行跳转与反馈回路抑制、无标题文档安全忽略。控件经离线布局(无需真实窗口)。</summary>
public sealed class MarkdownPreviewViewSyncTests
{
    // 末尾刻意保留多段正文:让可滚动范围足够把任意目标标题滚进 24px 顶部判定边距,
    // 避免"滚到某标题附近"被 ScrollViewer 的偏移钳制破坏。
    private const string Document = """
        # First Heading

        some text under first

        ## Second Heading

        some text under second

        ### Third Heading

        final text

        trailing paragraph one

        trailing paragraph two

        trailing paragraph three
        """;

    private static void PumpQueue()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
        Dispatcher.PushFrame(frame);
    }

    private static void Layout(FrameworkElement element)
    {
        // 视口刻意小于文档高度(80px):离线布局下 ScrollViewer 才会产生可滚动范围,
        // 各标题的"顶部附近"滚动目标才不会因钳制而失效。
        element.Measure(new Size(480, 80));
        element.Arrange(new Rect(0, 0, 480, 80));
    }

    /// <summary>标题在视图坐标系中的 Y(经渲染器挂零尺寸标记,与预览视图同一几何来源)。</summary>
    private static double TopY(MarkdownPreviewView view, string wpfName)
    {
        var paragraph = Assert.IsType<Paragraph>(MarkdownPreviewView.FindNamedBlock(view.Document!, wpfName)!);
        var marker = Assert.IsAssignableFrom<FrameworkElement>(paragraph.Tag!);
        return marker.TransformToVisual(view).Transform(new Point(0, 0)).Y;
    }

    /// <summary>创建已布局、已载入渲染结果的预览视图;返回 (视图, 标题按行序)。</summary>
    private static async Task<(MarkdownPreviewView View, MarkdownHeading[] Headings)> CreateLaidOutViewAsync()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync(Document, "C:\\docs\\sync.md");
        MarkdownPreviewView? view = null;
        WpfStaContext.Run(() =>
        {
            view = new MarkdownPreviewView();
            Layout(view);
            view.RenderResult = result;
            PumpQueue();
            Layout(view); // 文档进入文档树后重新布局,标题位置标记就位
            PumpQueue();
        });
        return (view!, result.Headings.ToArray());
    }

    [Fact]
    public async Task Scrolling_UpdatesCurrentHeading_NearTopRule()
    {
        var (view, headings) = await CreateLaidOutViewAsync();
        var (first, second, third) = (headings[0], headings[1], headings[2]);
        var reported = new List<MarkdownHeading?>();

        WpfStaContext.Run(() =>
        {
            view.CurrentHeadingChanged += (_, heading) => reported.Add(heading);
            var topOfSecond = TopY(view, second.DocumentName);
            var topOfThird = TopY(view, third.DocumentName);

            // 离线环境滚动是延迟应用的:第一次泵驱动滚动生效并排队节流重算,
            // 第二次泵驱动重算执行。每步固定双泵。
            view.VerticalOffset = topOfThird - 8; // 第三标题进入视口顶部附近
            PumpQueue();
            PumpQueue();
            Assert.Same(third, view.CurrentHeading);

            // 回滚到文档顶部:顶部兜底规则 → 第一个标题即当前标题。
            view.VerticalOffset = 0;
            PumpQueue();
            PumpQueue();
            Assert.Same(first, view.CurrentHeading);

            view.VerticalOffset = topOfSecond - 8; // 第二标题进入视口顶部附近
            PumpQueue();
            PumpQueue();
            Assert.Same(second, view.CurrentHeading);

            view.VerticalOffset = topOfThird - 8;
            PumpQueue();
            PumpQueue();
            Assert.Same(third, view.CurrentHeading);

            // 滚过最后一个标题:当前标题保持最后一级(不回落)。
            view.VerticalOffset = topOfThird + 200;
            PumpQueue();
            PumpQueue();
            Assert.Same(third, view.CurrentHeading);
        });

        Assert.Contains(first, reported);
        Assert.Contains(second, reported);
        Assert.Contains(third, reported);
    }

    [Fact]
    public async Task ScrollToAnchor_JumpsToHeading_AndPublishesCurrentHeadingWithoutScrollEcho()
    {
        var (view, headings) = await CreateLaidOutViewAsync();
        var (second, third) = (headings[1], headings[2]);
        var reported = new List<MarkdownHeading?>();

        WpfStaContext.Run(() =>
        {
            view.CurrentHeadingChanged += (_, heading) => reported.Add(heading);

            // 先滚到第三标题,让第二标题移出视口顶部(双泵:滚动生效 + 节流重算)。
            view.VerticalOffset = TopY(view, third.DocumentName) - 8;
            PumpQueue();
            PumpQueue();
            Assert.Same(third, view.CurrentHeading);
            var offsetBefore = view.VerticalOffset;

            view.ScrollToAnchor(second.Anchor);
            PumpQueue();
            PumpQueue();

            Assert.Same(second, view.CurrentHeading);
            Assert.True(view.VerticalOffset < offsetBefore); // 回滚到第二标题
            // 最后一个事件必须是跳转目标本身(滚动回声被抑制,不产生重复同步)。
            Assert.Equal(second, reported.Last());
        });
    }

    [Fact]
    public async Task ScrollToLine_JumpsToNearestHeadingAtOrAboveLine()
    {
        var (view, headings) = await CreateLaidOutViewAsync();
        var (first, second) = (headings[0], headings[1]);

        WpfStaContext.Run(() =>
        {
            view.ScrollToLine(second.Line + 3); // 第二标题行之后 → 落到 Second
            PumpQueue();
            PumpQueue();
            Assert.Same(second, view.CurrentHeading);

            view.ScrollToLine(first.Line); // 第一标题行 → 落到 First
            PumpQueue();
            PumpQueue();
            Assert.Same(first, view.CurrentHeading);
        });
    }

    [Fact]
    public async Task UnknownAnchor_IsSafelyIgnored()
    {
        var (view, _) = await CreateLaidOutViewAsync();

        WpfStaContext.Run(() =>
        {
            var before = view.CurrentHeading; // 文档顶部 → 第一个标题
            Assert.False(view.ScrollToAnchorSafe("does-not-exist")); // 锚点失效 → 报告 false
            PumpQueue();
            Assert.Equal(before, view.CurrentHeading); // 安全忽略,当前标题不变
            view.ScrollToAnchor("does-not-exist"); // 不抛异常
            PumpQueue();
            Assert.Equal(before, view.CurrentHeading);
        });
    }

    /// <summary>滚轮 → 预览内容滚动回归:.NET 10 的 ScrollViewer 内建滚轮处理是每事件固定
    /// 48px(ScrollViewer._mouseWheelDelta 常量,只看 delta 符号、忽略幅值),预览滚轮
    /// 体感不灵敏;视图在隧道路径接管后按硬件 delta 像素比例滚动(MarkdownPreviewView
    /// .OnPreviewMouseWheel)。断言:偏移恰好随 delta 位移(而非 48px 固定步)且事件被
    /// 标记已处理(抑制内层固定步路径)。</summary>
    [Fact]
    public async Task WheelScrolls_ByPixelDelta_RatherThanFixedStep()
    {
        var (view, _) = await CreateLaidOutViewAsync();

        WpfStaContext.Run(() =>
        {
            // Mouse.PrimaryDevice 依赖输入子系统(需 PresentationSource):显示 1×1 窗口
            // 初始化(测试 App 为 OnExplicitShutdown,关窗不退出)。
            var window = new Window { Width = 1, Height = 1, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
            window.Show();
            PumpQueue();
            try
            {
                var device = Mouse.PrimaryDevice;
                Assert.NotNull(device);

                var maxOffset = view.ExtentHeight - view.ViewportHeight;
                Assert.True(maxOffset >= 120, $"文档可滚动范围应足以区分 120/48 步长,实际 {maxOffset}");

                view.VerticalOffset = maxOffset - 120;
                PumpQueue();

                // 负 delta = 滚轮向下 = 偏移增大:恰好 +120(像素比例),而非固定 +48。
                var args = new MouseWheelEventArgs(device, 0, -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                };
                view.RaiseEvent(args);
                PumpQueue();

                Assert.True(args.Handled); // 接管生效:内层固定 48px 步长路径被抑制
                Assert.Equal(maxOffset, view.VerticalOffset);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DocumentWithoutHeadings_CurrentHeadingStaysNull_AndPositionReportsNoAnchor()
    {
        var result = await MarkdownPreviewService.Instance.ParseAsync("plain text\n\nmore text\n", "C:\\docs\\plain.md");
        MarkdownPreviewView? view = null;

        WpfStaContext.Run(() =>
        {
            view = new MarkdownPreviewView();
            Layout(view);
            view.RenderResult = result;
            PumpQueue();
            Layout(view);
            PumpQueue();

            view.VerticalOffset = 10;
            PumpQueue();
            Assert.Null(view.CurrentHeading);
            var position = view.GetLogicalPosition();
            Assert.Null(position.Anchor);
            Assert.Equal(0, position.Line);
            Assert.True(position.VerticalOffset >= 0);
        });
    }
}
