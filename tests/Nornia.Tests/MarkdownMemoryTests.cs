using Nornia.Desktop.Code;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nornia.Tests;

/// <summary>Markdown 图片缓存单测:有界 LRU + 嵌入引用计数(解码需 STA,统一走共享
/// <see cref="WpfStaContext"/>)。每个测试使用独立临时目录,互不串扰。</summary>
public sealed class MarkdownImageCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-imgcache-{Guid.NewGuid():N}");
    private readonly string[] _pngs;

    public MarkdownImageCacheTests()
    {
        _pngs = Enumerable.Range(0, 4).Select(i => Path.Combine(_directory, $"img{i}.png")).ToArray();
        Directory.CreateDirectory(_directory);
        WpfStaContext.Run(() =>
        {
            foreach (var path in _pngs)
            {
                CreatePng(path);
            }
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>16×16 纯色 PNG(字节估算 = 16×16×4 = 1024/张)。</summary>
    private static void CreatePng(string path)
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

    [Fact]
    public void GetOrLoad_CachesDecodedBitmap_SharedInstance()
    {
        WpfStaContext.Run(() =>
        {
            var cache = new MarkdownImageCache();
            var a = cache.GetOrLoad(_pngs[0]);
            var b = cache.GetOrLoad(_pngs[0]);
            Assert.NotNull(a);
            Assert.True(ReferenceEquals(a, b));
            Assert.True(a!.IsFrozen);
            Assert.Equal((1, 0), (cache.Snapshot().Entries, cache.Snapshot().Referenced));
        });
    }

    [Fact]
    public void AcquireRelease_TracksReferences_OverReleaseIsNoop()
    {
        WpfStaContext.Run(() =>
        {
            var cache = new MarkdownImageCache();
            cache.GetOrLoad(_pngs[0]);
            cache.Acquire(_pngs[0]);
            cache.Acquire(_pngs[0]);
            Assert.Equal(2, cache.Snapshot().Referenced);
            cache.Release(_pngs[0]);
            Assert.Equal(1, cache.Snapshot().Referenced);
            cache.Release(_pngs[0]);
            Assert.Equal(0, cache.Snapshot().Referenced);
            cache.Release(_pngs[0]); // 过量释放:保持 0,不抛
            cache.Release(_pngs[1]); // 从未登记的路径:空操作
            Assert.Equal(0, cache.Snapshot().Referenced);
        });
    }

    [Fact]
    public void Eviction_ByEntryCap_FollowsLruOrder_EvictedEntryIsReDecoded()
    {
        WpfStaContext.Run(() =>
        {
            var cache = new MarkdownImageCache(maxEntries: 3);
            var img0 = cache.GetOrLoad(_pngs[0]);
            var img1 = cache.GetOrLoad(_pngs[1]);
            var img2 = cache.GetOrLoad(_pngs[2]);
            cache.Acquire(_pngs[2]); // 被引用者不可逐出
            Assert.All([img0, img1, img2], Assert.NotNull);
            cache.GetOrLoad(_pngs[0]); // 再次访问 img0 → LRU 序变为 [1, 2, 0]
            cache.GetOrLoad(_pngs[3]); // 超限 → 逐出最久未用的零引用 img1
            Assert.Equal(3, cache.Snapshot().Entries);
            Assert.Same(img0, cache.GetOrLoad(_pngs[0]));   // img0 仍在缓存
            Assert.NotSame(img1, cache.GetOrLoad(_pngs[1])); // img1 被逐出 → 重新解码(实例不同)
        });
    }

    [Fact]
    public void Eviction_ByByteCap_DropsOldestUnreferenced()
    {
        WpfStaContext.Run(() =>
        {
            // 先探测单张实际估算字节(不硬编码像素格式假设),再按其 3 倍设上限。
            var probe = new MarkdownImageCache();
            Assert.NotNull(probe.GetOrLoad(_pngs[0]));
            var perEntry = probe.Snapshot().Bytes;
            Assert.True(perEntry > 0);

            var cache = new MarkdownImageCache(maxEntries: 100, maxBytes: perEntry * 3);
            var first = cache.GetOrLoad(_pngs[0]);
            cache.GetOrLoad(_pngs[1]);
            cache.GetOrLoad(_pngs[2]);
            Assert.Equal(3, cache.Snapshot().Entries);
            Assert.Equal(perEntry * 3, cache.Snapshot().Bytes);
            cache.GetOrLoad(_pngs[3]); // 超上限 → 逐出最久未用的 img0,回到 3 张
            Assert.Equal(3, cache.Snapshot().Entries);
            Assert.Equal(perEntry * 3, cache.Snapshot().Bytes);
            Assert.NotSame(first, cache.GetOrLoad(_pngs[0]));
        });
    }

    [Fact]
    public void Eviction_AllReferenced_AllowsOverflow()
    {
        WpfStaContext.Run(() =>
        {
            var cache = new MarkdownImageCache(maxEntries: 2);
            cache.GetOrLoad(_pngs[0]);
            cache.Acquire(_pngs[0]);
            cache.GetOrLoad(_pngs[1]);
            cache.Acquire(_pngs[1]);
            cache.GetOrLoad(_pngs[2]); // 全部被引用 → 允许溢出(可见文档不可失图)
            Assert.Equal(3, cache.Snapshot().Entries);
            Assert.Equal(2, cache.Snapshot().Referenced);
        });
    }

    [Fact]
    public void Clear_DropsAllEntries()
    {
        WpfStaContext.Run(() =>
        {
            var cache = new MarkdownImageCache();
            cache.GetOrLoad(_pngs[0]);
            cache.Acquire(_pngs[0]);
            cache.Clear();
            Assert.Equal((0, 0, 0L), cache.Snapshot());
        });
    }

    [Fact]
    public void GetOrLoad_MissingFile_ReturnsNull_ReleaseOfUnknownPathIsNoop()
    {
        WpfStaContext.Run(() =>
        {
            var cache = new MarkdownImageCache();
            var missing = Path.Combine(_directory, "missing.png");
            Assert.Null(cache.GetOrLoad(missing));
            cache.Release(missing); // 不抛
            Assert.Equal(0, cache.Snapshot().Entries);
        });
    }
}

/// <summary>状态栏提示单行压缩:多行异常消息不得破坏单行 TextBlock 布局。</summary>
public sealed class MarkdownNoticeFormattingTests
{
    [Fact]
    public void SingleLineMessage_MultiLineMessage_CollapsesToSingleLine()
    {
        var ex = new InvalidOperationException("line one\r\nline two\nline three\tline four");
        Assert.Equal("line one line two line three line four", FilePreviewTab.SingleLineMessage(ex));
    }

    [Fact]
    public void SingleLineMessage_BlankMessage_FallsBackToPlaceholder()
    {
        Assert.Equal("未知错误", FilePreviewTab.SingleLineMessage(new InvalidOperationException("   ")));
        Assert.Equal("未知错误", FilePreviewTab.SingleLineMessage(new InvalidOperationException(string.Empty)));
    }

    [Fact]
    public void SingleLineMessage_OverlongMessage_TruncatedWithEllipsis()
    {
        var ex = new InvalidOperationException(new string('x', 300));
        var result = FilePreviewTab.SingleLineMessage(ex);
        Assert.Equal(121, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void SingleLineMessage_SingleLineMessage_KeptVerbatim()
    {
        var ex = new FileNotFoundException("找不到文件", "C:\\a\\b.md");
        Assert.Equal("找不到文件", FilePreviewTab.SingleLineMessage(ex));
    }
}

/// <summary>标签级释放与模式独占驻留(VM 层,无视图):关闭释放全部 Markdown 产物、
/// 源码模式释放渲染产物且标题同步不中断、切回预览重新解析、渲染后 AST 分离、
/// 主题变化触发重新解析。</summary>
public sealed class MarkdownTabReleaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-tabrel-{Guid.NewGuid():N}");

    public MarkdownTabReleaseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // 行号:1 # Root · 3 ## Child · 5 ### Deep · 7 ## Sibling · 9 # Other
    private const string Document = """
        # Root

        ## Child

        ### Deep

        ## Sibling

        # Other
        """;

    private async Task<FilePreviewTab> LoadTabAsync(string name)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, Document);
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();
        return tab;
    }

    [Fact]
    public async Task ReleaseResources_FreesAllMarkdownArtifacts()
    {
        var tab = await LoadTabAsync("release.md");
        Assert.NotNull(tab.MarkdownRenderResult);
        Assert.NotEmpty(tab.MarkdownHeadings);
        tab.SetMarkdownHeading("deep", 5);
        tab.MarkdownVerticalOffset = 123;

        tab.ReleaseResources();

        Assert.Null(tab.MarkdownRenderResult);
        Assert.Empty(tab.MarkdownHeadings);
        Assert.Equal(string.Empty, tab.MarkdownAnchor);
        Assert.Equal(0, tab.MarkdownHeadingLine);
        Assert.Equal(0, tab.MarkdownVerticalOffset);
        Assert.Equal(string.Empty, tab.MarkdownRenderNotice);
        Assert.Equal(string.Empty, tab.Content);
        Assert.False(tab.IsMarkdownRendered);
    }

    [Fact]
    public async Task SourceModeReleasesRenderResult_CaretSyncSurvives_PreviewRebuilds()
    {
        var tab = await LoadTabAsync("mode.md");
        Assert.NotNull(tab.MarkdownRenderResult);
        Assert.True(tab.IsMarkdownRendered);

        tab.ShowMarkdownSourceCommand.Execute(null);
        Assert.Equal(MarkdownViewMode.Source, tab.MarkdownMode);
        Assert.False(tab.IsMarkdownRendered);
        Assert.Null(tab.MarkdownRenderResult); // 渲染产物(AST/FlowDocument 数据源)已释放
        Assert.NotEmpty(tab.MarkdownHeadings);  // 标题列表常驻

        // 源码模式下光标→逻辑位置同步不受渲染产物释放影响。
        tab.UpdateCaretLine(5);
        Assert.Equal("deep", tab.MarkdownAnchor);
        Assert.Equal(5, tab.MarkdownHeadingLine);

        // 切回预览:重新解析(异步),阅读位置保留。
        tab.ShowMarkdownPreviewCommand.Execute(null);
        Assert.True(await WaitForAsync(() => tab.MarkdownRenderResult is not null),
            "切回预览后应在超时内完成重新解析");
        Assert.True(tab.IsMarkdownRendered);
        Assert.Equal("deep", tab.MarkdownAnchor);
        Assert.Equal(5, tab.MarkdownHeadingLine);
        tab.ReleaseResources();
    }

    [Fact]
    public async Task DetachMarkdownDocument_FreesAstKeepsHeadingsAndRenderedState()
    {
        var tab = await LoadTabAsync("detach.md");
        Assert.NotNull(tab.MarkdownRenderResult);
        var result = tab.MarkdownRenderResult!;
        Assert.NotNull(result.Document);

        tab.DetachMarkdownDocument();

        Assert.Null(result.Document);
        Assert.Same(result, tab.MarkdownRenderResult); // 结果对象仍在使用,仅 AST 分离
        Assert.NotEmpty(result.Headings);
        Assert.True(tab.IsMarkdownRendered);
        tab.ReleaseResources();
    }

    [Fact]
    public async Task ThemeChange_InRenderedMode_ReparsesWithFreshResult()
    {
        var tab = await LoadTabAsync("theme.md");
        tab.IsActive = true; // 主题重建仅针对活动标签
        Assert.NotNull(tab.MarkdownRenderResult);
        var first = tab.MarkdownRenderResult!;
        tab.DetachMarkdownDocument(); // 模拟视图渲染完成后的分离

        ThemeEvents.Raise(AppTheme.Light);

        MarkdownRenderResult? rebuilt = null;
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            if (tab.MarkdownRenderResult is { } candidate && !ReferenceEquals(candidate, first))
            {
                rebuilt = candidate;
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(rebuilt);
        Assert.NotNull(rebuilt!.Document); // 新解析的 AST 就绪(视图会渲染后再次分离)
        tab.ReleaseResources(); // 解除主题事件订阅
    }

    [Fact]
    public async Task InactiveTab_DoesNotReparseOnThemeChange()
    {
        var tab = await LoadTabAsync("theme-inactive.md");
        tab.IsActive = false;
        Assert.NotNull(tab.MarkdownRenderResult);
        var first = tab.MarkdownRenderResult!;

        ThemeEvents.Raise(AppTheme.Light);
        await Task.Delay(200); // 给(不该发生的)重建留时间

        Assert.Same(first, tab.MarkdownRenderResult);
        tab.ReleaseResources();
    }

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
}

/// <summary>视图级集成(共享 STA 上下文 + 真实 FilePreviewView):渲染完成即分离 AST、
/// 源码模式清空 FlowDocument 并释放图片引用、切回预览重建并恢复位置、关闭标签
/// 释放文档与图片引用。</summary>
public sealed class MarkdownPreviewMemoryViewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-memview-{Guid.NewGuid():N}");
    private string _pngPath = string.Empty;

    public MarkdownPreviewMemoryViewTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // 同一图片嵌入两处 → 缓存引用计数 2。
    private const string ImageDocument = """
        # Root

        ![pic](img.png)

        ![pic again](img.png)

        line one
        line two
        line three
        line four

        ## Section

        line one
        line two
        line three
        line four
        """;

    // 每节约 8 行,保证可滚动到 Gamma。
    private const string ScrollDocument = """
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
    public async Task RenderDetach_SourceModeReleasesFlowDocAndImages_SwitchBackRebuilds()
    {
        _pngPath = Path.Combine(_directory, "img.png");
        WpfStaContext.Run(() => CreatePng(_pngPath));
        var path = Path.Combine(_directory, "images.md");
        await File.WriteAllTextAsync(path, ImageDocument);
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        // 预热契约:图片缓存条目在加载期由解析 worker 预解码产生(首帧渲染不再同步解码大位图),
        // 基线必须取在加载之前;引用计数仍由渲染期 Acquire/Release 驱动。
        var baseline = MarkdownImageCache.Instance.Snapshot();
        await tab.LoadAsync();

        Assert.NotNull(tab.MarkdownRenderResult);
        Assert.Equal(2, tab.MarkdownRenderResult!.ImagePaths.Count); // 每处嵌入一条(同一文件两条)

        FlowDocument? docRendered = null, docSource = null, docRebuilt = null;

        WpfStaContext.Run(() =>
        {
            var view = new FilePreviewView();
            view.DataContext = tab;
            Layout(view);
            PumpQueue();
            Layout(view);
            PumpQueue();

            // 首次渲染完成:FlowDocument 在视图,AST 已分离。
            docRendered = view.MarkdownViewControl.Document;
            Assert.NotNull(docRendered);
            Assert.NotNull(tab.MarkdownRenderResult);
            Assert.Null(tab.MarkdownRenderResult!.Document);
            Assert.Same(tab.MarkdownRenderResult, view.MarkdownViewControl.RenderedResult);

            // 图片引用:同一图片嵌入两处 → 2 个引用(M6 预热即 Acquire;按路径精确断言,
            // 与并行测试的全局快照差分解耦)。
            var imageKey = tab.MarkdownRenderResult!.ImagePaths[0];
            Assert.True(MarkdownImageCache.Instance.ContainsKey(imageKey));
            Assert.Equal(2, MarkdownImageCache.Instance.ReferenceCount(imageKey));

            // 切源码:FlowDocument 清空,图片引用归零(条目可留缓存作零引用)。
            tab.ShowMarkdownSourceCommand.Execute(null);
            PumpQueue();
            docSource = view.MarkdownViewControl.Document;
            Assert.Null(docSource);
            Assert.Equal(0, MarkdownImageCache.Instance.ReferenceCount(imageKey));

            // 切回预览:重新解析 + 渲染(异步,泵驱动),引用恢复。
            tab.ShowMarkdownPreviewCommand.Execute(null);
            WaitUntil(() => tab.MarkdownRenderResult is not null);
            PumpQueue();
            PumpQueue();
            docRebuilt = view.MarkdownViewControl.Document;
            Assert.NotNull(docRebuilt);
            var imageKeyRebuilt = tab.MarkdownRenderResult!.ImagePaths[0];
            Assert.Equal(2, MarkdownImageCache.Instance.ReferenceCount(imageKeyRebuilt));

            tab.ReleaseResources();
            PumpQueue();
        });

        Assert.NotNull(docRendered);
        Assert.Null(docSource);
        Assert.NotNull(docRebuilt);
        Assert.NotSame(docRendered, docRebuilt); // 重建的是新 FlowDocument
    }

    [Fact]
    public async Task ModeSwitch_PreservesReadingPosition_OnSwitchBack()
    {
        var path = Path.Combine(_directory, "scroll.md");
        await File.WriteAllTextAsync(path, ScrollDocument);
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();
        var gamma = tab.SymbolDocument.All.Single(node => node.Name == "Gamma");

        double offsetAtGamma = 0, offsetAfterSwitchBack = 0;
        string? headingBefore = null, headingAfter = null;

        WpfStaContext.Run(() =>
        {
            var view = new FilePreviewView();
            view.DataContext = tab;
            Layout(view);
            PumpQueue();
            Layout(view);
            PumpQueue();

            tab.JumpToSymbolCommand.Execute(gamma);
            PumpQueue();
            PumpQueue();
            headingBefore = view.MarkdownViewControl.CurrentHeading?.Anchor;
            offsetAtGamma = view.MarkdownViewControl.VerticalOffset;
            Assert.Equal("gamma", headingBefore);
            Assert.True(offsetAtGamma > 0);

            // 预览 → 源码:文档清空(位置状态保留在标签)。
            tab.ShowMarkdownSourceCommand.Execute(null);
            PumpQueue();
            Assert.Null(view.MarkdownViewControl.Document);

            // 源码 → 预览:重建后按 锚点→行号→偏移 恢复。
            tab.ShowMarkdownPreviewCommand.Execute(null);
            WaitUntil(() => tab.MarkdownRenderResult is not null);
            PumpQueue();
            PumpQueue();
            Assert.NotNull(view.MarkdownViewControl.Document);
            headingAfter = view.MarkdownViewControl.CurrentHeading?.Anchor;
            offsetAfterSwitchBack = view.MarkdownViewControl.VerticalOffset;

            tab.ReleaseResources();
        });

        Assert.Equal("gamma", headingAfter);
        Assert.True(offsetAfterSwitchBack > 0, $"切回预览后应恢复滚动位置: {offsetAfterSwitchBack}");
        Assert.True(Math.Abs(offsetAfterSwitchBack - offsetAtGamma) < 48,
            $"恢复位置应接近原位置: {offsetAtGamma} → {offsetAfterSwitchBack}");
    }

    [Fact]
    public async Task ReleaseResources_WithBoundView_ClearsFlowDocumentAndImageRefs()
    {
        // 文档内嵌图片名为 img.png,图片文件必须同名。
        _pngPath = Path.Combine(_directory, "img.png");
        WpfStaContext.Run(() => CreatePng(_pngPath));
        var path = Path.Combine(_directory, "close.md");
        await File.WriteAllTextAsync(path, ImageDocument);
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();

        var baseline = (Entries: 0, Referenced: 0, Bytes: 0L);
        FlowDocument? before = null;
        bool cleared = false;

        WpfStaContext.Run(() =>
        {
            var view = new FilePreviewView();
            baseline = MarkdownImageCache.Instance.Snapshot();
            view.DataContext = tab;
            Layout(view);
            PumpQueue();
            Layout(view);
            PumpQueue();

            before = view.MarkdownViewControl.Document;
            Assert.NotNull(before);
            var imageKey = tab.MarkdownRenderResult!.ImagePaths[0];
            Assert.Equal(2, MarkdownImageCache.Instance.ReferenceCount(imageKey));

            tab.ReleaseResources(); // 关闭:渲染产物 + 图片引用整体释放
            PumpQueue();
            cleared = view.MarkdownViewControl.Document is null;
            Assert.Equal(0, MarkdownImageCache.Instance.ReferenceCount(imageKey));
        });

        Assert.True(cleared);
    }

    private static void CreatePng(string path)
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

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(960, 520));
        element.Arrange(new Rect(0, 0, 960, 520));
    }

    private static void PumpQueue() => WpfStaContext.PumpQueue();

    /// <summary>STA 泵驱动等待异步解析/渲染完成(续帖排入 Dispatcher 队列)。</summary>
    private static void WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            PumpQueue();
            Thread.Sleep(10);
        }

        Assert.True(condition(), "等待异步 Markdown 解析/渲染超时");
    }
}
