using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Services;
using Nornia.Project.Models;
using Nornia.Project.Services;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Markdown 大纲与预览双向同步:统一逻辑位置状态(锚点 + 标题行号)在源码光标、
/// 预览滚动(经 SetMarkdownHeading 入口)和大纲点击之间收敛,并随阅读状态持久化/恢复。</summary>
public sealed class MarkdownOutlineSyncTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-sync-{Guid.NewGuid():N}");

    public MarkdownOutlineSyncTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private const string Document = """
        # Root

        text

        ## Alpha

        text

        ### Deep

        text

        ## Beta
        """;

    private async Task<FilePreviewTab> CreateTabAsync(string name = "sync.md")
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, Document);
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();
        return tab;
    }

    private static CodeSymbolNode FindNode(CodeSymbolDocument document, string name) =>
        document.All.Single(node => node.Name == name);

    // ===== 源码光标 → 统一逻辑位置 =====

    [Fact]
    public async Task CaretOnHeadingLine_SyncsAnchorLineAndBreadcrumb()
    {
        var tab = await CreateTabAsync();

        tab.UpdateCaretLine(9); // "### Deep" 所在行

        Assert.Equal("deep", tab.MarkdownAnchor);
        Assert.Equal(9, tab.MarkdownHeadingLine);
        Assert.Equal(["Root", "Alpha", "Deep"], tab.ActiveSymbolPath.Select(node => node.Name).ToArray());
        Assert.True(FindNode(tab.SymbolDocument, "Deep").IsActive);
    }

    [Fact]
    public async Task CaretAboveFirstHeading_KeepsPreviousLogicalPosition()
    {
        var path = Path.Combine(_directory, "preamble.md");
        await File.WriteAllTextAsync(path, "preamble text\n\n# First\n\ntext\n");
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();

        tab.UpdateCaretLine(3); // "First" 标题行:建立逻辑位置
        Assert.Equal("first", tab.MarkdownAnchor);
        Assert.Equal(3, tab.MarkdownHeadingLine);

        tab.UpdateCaretLine(1); // 光标行上方没有任何标题 → 保留上次逻辑位置
        Assert.Equal("first", tab.MarkdownAnchor);
        Assert.Equal(3, tab.MarkdownHeadingLine);
    }

    // ===== 预览滚动入口(SetMarkdownHeading)→ 大纲高亮 + 父级展开 + 面包屑 =====

    [Fact]
    public async Task SetMarkdownHeading_HighlightsNode_AndExpandsCollapsedAncestors()
    {
        var tab = await CreateTabAsync();
        var document = tab.SymbolDocument;
        var alpha = FindNode(document, "Alpha");
        var deep = FindNode(document, "Deep");
        alpha.IsExpanded = false; // 模拟用户折叠了父级

        tab.SetMarkdownHeading("deep", 9);

        Assert.True(alpha.IsExpanded); // 活动节点的所有父级自动展开
        Assert.True(deep.IsActive);
        Assert.Equal("deep", tab.MarkdownAnchor);
        Assert.Equal(9, tab.MarkdownHeadingLine);
        Assert.Equal(["Root", "Alpha", "Deep"], tab.ActiveSymbolPath.Select(node => node.Name).ToArray());
    }

    [Fact]
    public async Task SetMarkdownHeading_WithoutHeading_DoesNotOverwriteLogicalPosition()
    {
        var tab = await CreateTabAsync();
        tab.SetMarkdownHeading("alpha", 5);

        tab.SetMarkdownHeading(null, 0); // 无标题滚动(文档顶部上方)不覆盖

        Assert.Equal("alpha", tab.MarkdownAnchor);
        Assert.Equal(5, tab.MarkdownHeadingLine);
    }

    // ===== 大纲点击 → 统一逻辑位置 =====

    [Fact]
    public async Task JumpToSymbol_WritesLogicalPositionAndHighlights()
    {
        var tab = await CreateTabAsync();
        var deep = FindNode(tab.SymbolDocument, "Deep");

        tab.JumpToSymbolCommand.Execute(deep);

        Assert.Equal("deep", tab.MarkdownAnchor);
        Assert.Equal(9, tab.MarkdownHeadingLine);
        Assert.True(deep.IsActive);
        Assert.Equal(["Root", "Alpha", "Deep"], tab.ActiveSymbolPath.Select(node => node.Name).ToArray());
    }

    // ===== 模式切换:逻辑位置不丢失 =====

    [Fact]
    public async Task ModeSwitch_PreservesLogicalPosition_AndExposesTargetLine()
    {
        var tab = await CreateTabAsync();
        tab.SetMarkdownHeading("alpha", 5);

        tab.ShowMarkdownSourceCommand.Execute(null);
        Assert.Equal(MarkdownViewMode.Source, tab.MarkdownMode);
        Assert.False(tab.IsMarkdownRendered);
        // 预览 → 源码:视图按 MarkdownHeadingLine 恢复光标;状态本身保留。
        Assert.Equal("alpha", tab.MarkdownAnchor);
        Assert.Equal(5, tab.MarkdownHeadingLine);

        tab.ShowMarkdownPreviewCommand.Execute(null);
        // 源码模式已释放渲染产物:切回预览需重新解析(异步)。
        Assert.True(await WaitForAsync(() => tab.MarkdownRenderResult is not null),
            "切回预览后应在超时内完成重新解析");
        Assert.True(tab.IsMarkdownRendered);
        Assert.Equal("alpha", tab.MarkdownAnchor);
        Assert.Equal(5, tab.MarkdownHeadingLine);
    }

    /// <summary>轮询等待条件成立(异步解析完成判定,上限 10 秒)。</summary>
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

    // ===== 阅读状态持久化:重启恢复锚点/行号/模式 =====

    private sealed class RestoreFixture : IDisposable
    {
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-md-restore-{Guid.NewGuid():N}");
        private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"nornia-md-ws-{Guid.NewGuid():N}");

        public EditorAreaViewModel Editor { get; }
        public FakeProjectWorkspaceService Workspace { get; }
        public ApplicationStateStore Store { get; }
        public string WorkspaceKey { get; }
        public string WorkspacePath => _workspace;

        public RestoreFixture()
        {
            Directory.CreateDirectory(_tempDir);
            Directory.CreateDirectory(_workspace);
            Workspace = new FakeProjectWorkspaceService
            {
                Current = new ProjectWorkspaceContext(
                    new ProjectAsset(Guid.NewGuid(), "Test", _workspace, ProjectPathStatus.Available, 0, null, null,
                        EnvironmentHealthStatus.Unknown),
                    _workspace, null),
            };
            Store = new ApplicationStateStore(Path.Combine(_tempDir, "state.json"));
            WorkspaceKey = Workspace.Current!.ProjectPath;
            Editor = new EditorAreaViewModel(
                new FakeGitService(), new FakeUiLogService(), new FakeClipboardService(),
                CodeFileTypeRegistry.Instance, TextDocumentDecoder.Instance, CodeOutlineParser.Instance,
                TextSearchService.Instance, null,
                new FakeSettingsService(), Workspace, Store);
        }

        public async Task ActivateAsync() => await Workspace.ActivateAsync(Workspace.Current!.ProjectPath);

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task OpenFile_RestoresMarkdownAnchorLineModeAndOffset()
    {
        using var fixture = new RestoreFixture();
        var path = Path.Combine(fixture.WorkspacePath, "restore.md");
        await File.WriteAllTextAsync(path, Document);
        await fixture.ActivateAsync();

        var reading = new EditorReadingState(
            VerticalOffset: 40, CaretLine: 9, CaretColumn: 3,
            MarkdownMode: MarkdownViewMode.Source.ToString(), MarkdownVerticalOffset: 220,
            MarkdownAnchor: "alpha", MarkdownHeadingLine: 5);
        await fixture.Store.CommitAsync(new([new(ApplicationStateField.WorkspaceReadingState, reading,
            fixture.WorkspaceKey, path)]));

        await fixture.Editor.OpenFileAsync(path);

        var tab = Assert.IsType<FilePreviewTab>(fixture.Editor.SelectedTab);
        Assert.Equal("alpha", tab.MarkdownAnchor);
        Assert.Equal(5, tab.MarkdownHeadingLine);
        Assert.Equal(MarkdownViewMode.Source, tab.MarkdownMode);
        Assert.Equal(220, tab.MarkdownVerticalOffset);
        Assert.NotNull(tab.ViewState);
        Assert.Equal(9, tab.ViewState!.CaretLine);
    }

    [Fact]
    public async Task FlushAsync_PersistsMarkdownAnchorLineAndMode()
    {
        using var fixture = new RestoreFixture();
        var path = Path.Combine(fixture.WorkspacePath, "flush.md");
        await File.WriteAllTextAsync(path, Document);
        await fixture.ActivateAsync();

        await fixture.Editor.OpenFileAsync(path);
        var tab = Assert.IsType<FilePreviewTab>(fixture.Editor.SelectedTab);
        tab.ViewState = new EditorViewState(220, 5, 1); // 模拟视图捕获的源码位置
        tab.MarkdownAnchor = "alpha";
        tab.MarkdownHeadingLine = 5;
        tab.MarkdownVerticalOffset = 120;
        tab.MarkdownMode = MarkdownViewMode.Rendered;

        await fixture.Editor.FlushAsync();

        var state = await fixture.Store.LoadAsync();
        var saved = state.Workspaces!.Values.Single().SafeReadingStates[path];
        Assert.Equal("alpha", saved.MarkdownAnchor);
        Assert.Equal(5, saved.MarkdownHeadingLine);
        Assert.Equal(MarkdownViewMode.Rendered.ToString(), saved.MarkdownMode);
        Assert.Equal(120, saved.MarkdownVerticalOffset);
        Assert.Equal(5, saved.CaretLine);
    }

    [Fact]
    public void EditorReadingState_OmitsEmptyAnchorWhenSerializingRoundTrip()
    {
        var reading = new EditorReadingState(MarkdownVerticalOffset: 80, MarkdownAnchor: null, MarkdownHeadingLine: 0);
        var json = System.Text.Json.JsonSerializer.Serialize(reading);
        var back = System.Text.Json.JsonSerializer.Deserialize<EditorReadingState>(json);

        Assert.Equal(80, back!.MarkdownVerticalOffset);
        Assert.Null(back.MarkdownAnchor);
        Assert.Equal(0, back.MarkdownHeadingLine);
    }
}
