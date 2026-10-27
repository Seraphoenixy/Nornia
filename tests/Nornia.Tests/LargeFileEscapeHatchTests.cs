using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>大文件逃生门("仍要尝试全文打开"):本标签全文上限临时抬升到窗口化上限并重载;
/// 已是全文/二进制/上限未抬升时为无操作。</summary>
public sealed class LargeFileEscapeHatchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-escape-{Guid.NewGuid():N}");

    public LargeFileEscapeHatchTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task OpenFullAnyway_RaisesTheTabLimitToTheWindowedBoundary()
    {
        // 小上限注入:窗口化路径与生产 8MB/32MB 阈值完全一致,不必写 8.6MB 临时文件。
        var path = Path.Combine(_directory, "escape.md");
        await File.WriteAllTextAsync(path, "# Big\n\n" + new string('x', 2048));
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance)
        {
            ContentLimits = new ReadOnlyContentLimits(1024, 64 * 1024, ReadOnlyContentCapacity.FullDiffLines, ReadOnlyContentCapacity.WindowedDiffLines),
        };
        await tab.LoadAsync();

        Assert.Equal(ReadOnlyContentTier.Windowed, tab.CapacityTier);
        Assert.True(tab.CanOpenFullAnyway);

        tab.OpenFullAnywayCommand.Execute(null);

        // 上限抬升立即生效(窗口化上限 64KB > 文件 2KB → 重载后为全文);重载异步完成。
        Assert.Equal(64L * 1024, tab.ContentLimits.FullSourceBytes);
        Assert.True(tab.IsLoading);
    }

    [Fact]
    public async Task OpenFullAnyway_IgnoresFullTierTabs()
    {
        var path = Path.Combine(_directory, "small.txt");
        await File.WriteAllTextAsync(path, "small");
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();

        Assert.Equal(ReadOnlyContentTier.Full, tab.CapacityTier);
        Assert.False(tab.CanOpenFullAnyway);

        var before = tab.ContentLimits;
        tab.OpenFullAnywayCommand.Execute(null);

        Assert.Equal(before, tab.ContentLimits); // 无操作:不抬升、不重载
    }

    [Fact]
    public void OpenFullAnyway_IgnoresUnloadedTabs()
    {
        var path = Path.Combine(_directory, "never-loaded.txt");
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);

        // 未装载(CapacityTier 默认 Full)→ 不触发重载,无异常。
        tab.OpenFullAnywayCommand.Execute(null);
        Assert.False(tab.IsLoading);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
