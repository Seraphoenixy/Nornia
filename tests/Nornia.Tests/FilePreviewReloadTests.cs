using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Coverage for the source-view external-change auto refresh: <see cref="FilePreviewTab"/>
/// reload pipeline (version gate, transient-failure preservation, FileChangeNotice) and the
/// <see cref="EditorAreaViewModel"/> watcher wiring (open / close / inactive-tab / restore).</summary>
public sealed class FilePreviewReloadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-reload-{Guid.NewGuid():N}");

    public FilePreviewReloadTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp directory.
        }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private (EditorAreaViewModel Editor, FakeFileContentWatcher Watcher) Create()
    {
        var watcher = new FakeFileContentWatcher();
        return (new EditorAreaViewModel(new FakeGitService(), new FakeUiLogService(), watcher), watcher);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(condition(), failure);
    }

    /// <summary>带短暂重试的写入:在途异步解码可能短暂独占文件(只共享读),冲突等待其完成。</summary>
    private static async Task WriteWithRetryAsync(string path, string content)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(content);
                }

                return;
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(5);
            }
        }
    }

    [Fact]
    public async Task ExternalWrite_ReloadsOpenTabContent()
    {
        var (editor, watcher) = Create();
        var file = Write("a.txt", "v1");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.Contains(Path.GetFullPath(file), watcher.Watched); // 打开即注册监听

        File.WriteAllText(file, "v2");
        watcher.Raise(file);

        await WaitUntilAsync(() => tab.Content == "v2", "外部写入后内容未自动刷新");
        Assert.Equal("v2", tab.Content);
        Assert.Empty(tab.Notice);
        Assert.Empty(tab.FileChangeNotice);
    }

    [Fact]
    public async Task Reload_UpdatesInactiveTabToo()
    {
        var (editor, watcher) = Create();
        var a = Write("a.txt", "aa");
        var b = Write("b.txt", "bb");
        await editor.OpenFileAsync(a, permanent: true); // 常驻标签,避免被 b 的预览槽替换
        await editor.OpenFileAsync(b);
        var tabA = Assert.IsType<FilePreviewTab>(editor.OpenTabs.Single(tab => tab.Path == a));
        Assert.NotSame(tabA, editor.SelectedTab); // b 是活动标签,A 是非活动标签

        File.WriteAllText(a, "aa-v2");
        watcher.Raise(a);

        await WaitUntilAsync(() => tabA.Content == "aa-v2", "非活动标签未随外部变更刷新");
    }

    [Fact]
    public async Task Reload_RaisesContentUpdatingBeforePublishing()
    {
        var (editor, watcher) = Create();
        var file = Write("a.txt", "v1");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        var capturedBeforeReplacement = 0;
        tab.ContentUpdating += (_, _) =>
        {
            capturedBeforeReplacement++;
            Assert.Equal("v1", tab.Content); // 事件必须在内容替换前触发
        };

        File.WriteAllText(file, "v2");
        await tab.ReloadAsync(); // 直接走管线,确定性等待

        Assert.Equal("v2", tab.Content);
        Assert.Equal(1, capturedBeforeReplacement);
        Assert.Contains(watcher.Watched, watched => watched == Path.GetFullPath(file));
    }

    [Fact]
    public async Task Reload_FileDeleted_KeepsOldContent_ShowsNotice_ThenRestores()
    {
        var (editor, watcher) = Create();
        var file = Write("a.txt", "v1");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));

        File.Delete(file);
        watcher.Raise(file);

        await WaitUntilAsync(() => tab.FileChangeNotice.Length > 0, "文件被删除后未显示提示");
        Assert.Equal("v1", tab.Content); // 旧内容保留
        Assert.Empty(tab.Notice); // 独立提示,不把源码区切到空态

        File.WriteAllText(file, "v3");
        watcher.Raise(file);

        await WaitUntilAsync(() => tab.Content == "v3" && tab.FileChangeNotice.Length == 0,
            "文件恢复后未自动刷新并清除提示");
        Assert.Empty(tab.Notice);
    }

    [Fact]
    public async Task RapidExternalChanges_ApplyLatestContentOnly()
    {
        var (editor, watcher) = Create();
        var file = Write("a.txt", "v1");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));

        // 快速连续保存 + 事件:版本门闩保证最终只应用最后一次内容,旧解码不覆盖新内容。
        // 写入可能撞上仍在途的异步解码(File.ReadAllBytesAsync 仅共享读):短暂冲突时重试,
        // 与生产路径的"暂时不可读 → 自动重试"容错一致。
        for (var i = 2; i <= 5; i++)
        {
            await WriteWithRetryAsync(file, $"v{i}");
            watcher.Raise(file);
        }

        await WaitUntilAsync(() => tab.Content == "v5", "最终内容必须是最后一次写入");
        Assert.Equal("v5", tab.Content);
    }

    [Fact]
    public async Task Reload_RebuildsSearchOutlineAndFoldState()
    {
        var (editor, watcher) = Create();
        var file = Write("code.cs", "class A\n{\n    void M() { }\n}\n");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        tab.SearchText = "void"; // 活动查找:重载后必须按新文档重算
        Assert.True(tab.MatchCount > 0);

        File.WriteAllText(file, "class A\n{\n    void M() { }\n    void N() { }\n}\n");
        watcher.Raise(file);

        // 内容发布先于派生工作(查找/大纲/折叠):等待重载管线把派生状态重建完成。
        await WaitUntilAsync(() => tab.Content.Contains("void N") && tab.OutlineEntries.Count >= 2,
            "重载后内容或大纲未重建");
        Assert.True(tab.MatchCount > 0, "重载后查找结果失效");
        Assert.NotEmpty(tab.FoldSections);
    }

    [Fact]
    public async Task Reload_UpdatesMarkdownSourceAndPreview()
    {
        var (editor, watcher) = Create();
        var file = Write("doc.md", "# Title\n\nold paragraph");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.True(tab.IsMarkdown);

        File.WriteAllText(file, "# New Title\n\nnew paragraph");
        watcher.Raise(file);

        // 内容发布先于 Markdown 重建(标题/渲染产物):等待标题列表按新内容重建。
        await WaitUntilAsync(() => tab.MarkdownHeadings.Any(heading => heading.Text == "New Title"),
            "Markdown 标题未随重载重建");
        Assert.Contains("new paragraph", tab.Content);
        // 渲染预览产物在重载后重建(默认渲染模式)。
        if (tab.MarkdownRenderResult is not null)
        {
            Assert.True(tab.MarkdownRenderNotice.Length == 0);
        }
    }

    [Fact]
    public async Task CloseTab_UnwatchesAndClosedTabDoesNotReload()
    {
        var (editor, watcher) = Create();
        var file = Write("a.txt", "v1");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));

        editor.CloseTab(tab);

        Assert.Contains(Path.GetFullPath(file), watcher.Unwatched); // 关闭即取消监听
        Assert.Empty(editor.OpenTabs);
        File.WriteAllText(file, "v2");
        watcher.Raise(file); // 无打开标签 → 无重载
        await Task.Delay(100);
        Assert.Empty(tab.Content); // 资源已释放,闭后标签不再被写回
    }

    [Fact]
    public async Task Reload_UpdatesStatusBarMetadata()
    {
        var (editor, watcher) = Create();
        var file = Write("a.txt", "one\ntwo\n");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        var initialLines = tab.LineCount;

        File.WriteAllText(file, "one\ntwo\nthree\nfour\nfive\n");
        watcher.Raise(file);

        await WaitUntilAsync(() => tab.LineCount > initialLines, "重载后行数未更新");
        Assert.NotEmpty(tab.EncodingName);
        Assert.NotEmpty(tab.FileSizeText);
    }

    [Fact]
    public async Task Reload_LargeFileWindowed_StaysWindowedAndKeepsWindow()
    {
        var (editor, watcher) = Create();
        var file = Path.Combine(_tempDir, "big.txt");
        var chunk = new string('x', 4096);
        using (var writer = new StreamWriter(file, append: false))
        {
            for (var i = 0; i < 2100; i++)
            {
                writer.WriteLine(chunk);
            }
        }

        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal(ReadOnlyContentTier.Windowed, tab.CapacityTier);

        // 用新首行重写大文件(仍超过 8MB):重载必须重建窗口索引并保留当前阅读窗口位置。
        using (var writer = new StreamWriter(file, append: false))
        {
            writer.WriteLine("MARKER");
            for (var i = 1; i < 2100; i++)
            {
                writer.WriteLine(chunk);
            }
        }

        watcher.Raise(file);

        await WaitUntilAsync(() => tab.Content.StartsWith("MARKER", StringComparison.Ordinal), "窗口模式重载后未显示新内容");
        Assert.Equal(ReadOnlyContentTier.Windowed, tab.CapacityTier);
        Assert.Equal(1, tab.WindowStartLine);
    }
}