using Nornia.Desktop;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Covers the unified mixed tab strip: content-page tabs (环境管理 / 项目管理 / 设置)
/// and file/diff document tabs share one strip. Page tabs are prebuilt but only join the strip on
/// first navigation; view pages (资源管理器 / 源代码管理) never produce tabs and clear any page-tab
/// selection. Also covers neighbour selection on close, Ctrl+W, Ctrl+PageUp/PageDown wrapping,
/// close-all (with page-tab re-open) and editor projection dedupe.</summary>
public sealed class WorkbenchViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-workbench-{Guid.NewGuid():N}");

    public WorkbenchViewModelTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    // ===== Fixtures =====

    /// <summary>轻量内容页(构造内容页标签无需完整环境/项目依赖链)。</summary>
    private sealed class StubContentPage(string title, IUiLogService logService) : PageViewModel(title, logService);

    private sealed record Fixture(
        WorkbenchViewModel Workbench,
        EditorAreaViewModel Editor,
        StubContentPage Environment,
        StubContentPage Projects,
        StubContentPage Settings,
        GitViewModel GitPage);

    private Fixture CreateWorkbench()
    {
        var log = new FakeUiLogService();
        var editor = new EditorAreaViewModel(new FakeGitService(), log);
        var environment = new StubContentPage("环境管理", log);
        var projects = new StubContentPage("项目管理", log);
        var settings = new StubContentPage("设置", log);
        var gitPage = new GitViewModel(new FakeGitService(), new FakeFolderPicker(),
            new FakeConfirmationService(), new FakeProjectCatalog(), editor, log, new FakeClipboardService(),
            new FakeGitRepositoryWatcher(), new FakeProjectWorkspaceService(), new FakeApplicationStateStore(),
            new FakeSettingsService());
        var navigationItems = new List<NavigationItem>
        {
            new("Nav_Environment", Codicons.Dashboard, environment, NavigationTargets.Dashboard),
            new("Nav_Explorer", Codicons.Files, gitPage, NavigationTargets.Git), // 视图页:预建时被排除
            new("Nav_Projects", Codicons.FolderLibrary, projects, NavigationTargets.Projects),
            new("Nav_Settings", Codicons.Settings, settings, NavigationTargets.Settings),
        };
        return new Fixture(new WorkbenchViewModel(editor, navigationItems), editor, environment, projects, settings, gitPage);
    }

    private static async Task<string> WriteFileAsync(string dir, string name, string content = "hello")
    {
        var path = Path.Combine(dir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    /// <summary>逐个打开文件并取消预览态——预览替换语义下,不取消预览态则多个文档标签无法共存。</summary>
    private async Task OpenRegularFilesAsync(EditorAreaViewModel editor, params string[] names)
    {
        foreach (var name in names)
        {
            await editor.OpenFileAsync(await WriteFileAsync(_tempDir, name));
            editor.SelectedTab!.IsPreview = false;
        }
    }

    /// <summary>混合条带:[环境管理] 页面标签 + a.txt / b.txt / c.txt 文档标签,选中 c.txt。</summary>
    private async Task<Fixture> CreateMixedStripAsync()
    {
        var fixture = CreateWorkbench();
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);
        await OpenRegularFilesAsync(fixture.Editor, "a.txt", "b.txt", "c.txt");
        return fixture;
    }

    // ===== Startup & page tabs =====

    [Fact]
    public void Startup_StripIsEmpty_PageTabsPrebuiltButNotJoined()
    {
        var fixture = CreateWorkbench();

        Assert.Empty(fixture.Workbench.Tabs);
        Assert.False(fixture.Workbench.HasTabs);
        Assert.Null(fixture.Workbench.SelectedTab);
    }

    [Fact]
    public void OpenOrActivatePage_ContentPage_CreatesAndActivatesSingleTab()
    {
        var fixture = CreateWorkbench();

        fixture.Workbench.OpenOrActivatePage(fixture.Environment);

        var tab = Assert.Single(fixture.Workbench.Tabs);
        var pageTab = Assert.IsType<PageWorkbenchTab>(tab);
        Assert.Equal("环境管理", pageTab.Title);
        Assert.Same(fixture.Environment, pageTab.Content);
        Assert.Same(pageTab, fixture.Workbench.SelectedTab);
    }

    [Fact]
    public void OpenOrActivatePage_ContentPage_ReusesExistingTab()
    {
        var fixture = CreateWorkbench();
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);
        var first = fixture.Workbench.SelectedTab;

        fixture.Workbench.OpenOrActivatePage(fixture.Projects);
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);

        Assert.Equal(2, fixture.Workbench.Tabs.Count);
        Assert.Same(first, fixture.Workbench.SelectedTab);
        Assert.Single(fixture.Workbench.Tabs, tab => tab.Title == "环境管理");
    }

    [Fact]
    public void MixedTabContextCommands_IgnorePageTabsWithoutTypeErrors()
    {
        var fixture = CreateWorkbench();
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);
        var pageTab = Assert.IsType<PageWorkbenchTab>(fixture.Workbench.SelectedTab);

        // The unified tab context menu is available for both page and editor tabs. Editor-only
        // actions must be safe no-ops for a page tab instead of passing an incompatible runtime
        // parameter to a strongly typed generated command.
        fixture.Workbench.SplitEditorTabRightCommand.Execute(pageTab);
        fixture.Workbench.SplitEditorTabDownCommand.Execute(pageTab);
        fixture.Workbench.RevealEditorTabCommand.Execute(pageTab);
        fixture.Workbench.OpenEditorTabExternallyCommand.Execute(pageTab);

        Assert.Single(fixture.Workbench.Tabs);
        Assert.Same(pageTab, fixture.Workbench.SelectedTab);
    }

    [Fact]
    public void OpenOrActivatePage_ViewPage_IsNoOp()
    {
        var fixture = CreateWorkbench();
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);

        fixture.Workbench.OpenOrActivatePage(fixture.GitPage);

        // 视图页不创建、不激活、不选中。
        Assert.Single(fixture.Workbench.Tabs);
        Assert.Equal("环境管理", fixture.Workbench.SelectedTab?.Title);
    }

    // ===== View-page normalisation =====

    [Fact]
    public async Task ClearNonDocumentSelection_KeepsActiveDocument()
    {
        var fixture = CreateWorkbench();
        await OpenRegularFilesAsync(fixture.Editor, "a.txt");
        var document = Assert.IsType<EditorWorkbenchTab>(fixture.Workbench.SelectedTab);

        fixture.Workbench.ClearNonDocumentSelection();

        Assert.Same(document, fixture.Workbench.SelectedTab);
    }

    [Fact]
    public async Task ClearNonDocumentSelection_ClearsPageTabSelection()
    {
        var fixture = await CreateMixedStripAsync();
        fixture.Workbench.OpenOrActivatePage(fixture.Environment); // 页面标签选中

        fixture.Workbench.ClearNonDocumentSelection();

        Assert.Null(fixture.Workbench.SelectedTab);
        // 条带不丢标签,活动文档仍在编辑器中。
        Assert.Equal(4, fixture.Workbench.Tabs.Count);
        Assert.Equal(3, fixture.Editor.OpenTabs.Count);
    }

    // ===== Mixed strip: close behaviour =====

    [Fact]
    public async Task CloseActiveTab_SelectsLeftNeighbourAtStripEnd()
    {
        var fixture = await CreateMixedStripAsync();
        Assert.Equal("c.txt", fixture.Workbench.SelectedTab?.Title);

        fixture.Workbench.CloseActiveTabCommand.Execute(null); // 关 c.txt → 无右邻 → 左邻 b.txt

        Assert.Equal("b.txt", fixture.Workbench.SelectedTab?.Title);
        Assert.DoesNotContain(fixture.Workbench.Tabs, tab => tab.Title == "c.txt");
    }

    [Fact]
    public async Task CloseActiveTab_MiddleTabSelectsRightNeighbour()
    {
        var fixture = await CreateMixedStripAsync();
        fixture.Workbench.OpenOrActivateTab(fixture.Workbench.Tabs[2]); // 选中 b.txt

        fixture.Workbench.CloseActiveTabCommand.Execute(null);

        Assert.Equal("c.txt", fixture.Workbench.SelectedTab?.Title);
    }

    [Fact]
    public async Task CloseAllTabs_EmptiesStripAndEditor_PageTabReopensOnNavigation()
    {
        var fixture = await CreateMixedStripAsync();

        fixture.Workbench.CloseAllTabsCommand.Execute(null);

        Assert.Empty(fixture.Workbench.Tabs);
        Assert.False(fixture.Workbench.HasTabs);
        Assert.Null(fixture.Workbench.SelectedTab);
        Assert.Empty(fixture.Editor.OpenTabs);

        // 再次导航 → 预建的页面标签重新加入条带。
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);
        Assert.Single(fixture.Workbench.Tabs);
        Assert.Equal("环境管理", fixture.Workbench.SelectedTab?.Title);
    }

    [Fact]
    public async Task CloseOtherTabs_AppliesToPageTabsToo()
    {
        var fixture = await CreateMixedStripAsync();
        var keep = fixture.Workbench.Tabs.Single(tab => tab is PageWorkbenchTab);

        fixture.Workbench.CloseOtherTabsCommand.Execute(keep);

        // 页面标签无特例:其余文档标签全部关闭。
        Assert.Single(fixture.Workbench.Tabs);
        Assert.Same(keep, fixture.Workbench.SelectedTab);
        Assert.Empty(fixture.Editor.OpenTabs);
    }

    // ===== Adjacent cycling (Ctrl+PageUp / PageDown) =====

    [Fact]
    public async Task GoToAdjacentTab_WrapsAroundTheMixedStrip()
    {
        var fixture = await CreateMixedStripAsync();
        fixture.Workbench.OpenOrActivateTab(fixture.Workbench.Tabs[0]); // [环境管理] 选中

        fixture.Workbench.GoToAdjacentTabCommand.Execute(1);
        Assert.Equal("a.txt", fixture.Workbench.SelectedTab?.Title);
        fixture.Workbench.GoToAdjacentTabCommand.Execute(1);
        Assert.Equal("b.txt", fixture.Workbench.SelectedTab?.Title);
        fixture.Workbench.GoToAdjacentTabCommand.Execute(1);
        Assert.Equal("c.txt", fixture.Workbench.SelectedTab?.Title);
        fixture.Workbench.GoToAdjacentTabCommand.Execute(1); // 回绕到页面标签
        Assert.Equal("环境管理", fixture.Workbench.SelectedTab?.Title);
        fixture.Workbench.GoToAdjacentTabCommand.Execute(-1); // 反向回绕到 c.txt
        Assert.Equal("c.txt", fixture.Workbench.SelectedTab?.Title);
    }

    // ===== Editor projection =====

    [Fact]
    public async Task SameFile_ReusesEditorTab()
    {
        var fixture = CreateWorkbench();
        var path = await WriteFileAsync(_tempDir, "a.txt");

        await fixture.Editor.OpenFileAsync(path);
        await fixture.Editor.OpenFileAsync(path);

        Assert.Single(fixture.Workbench.Tabs.OfType<EditorWorkbenchTab>());
        Assert.Single(fixture.Editor.OpenTabs);
    }

    [Fact]
    public async Task EditorTabClose_RemovesFromStripAndEditor()
    {
        var fixture = CreateWorkbench();
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "a.txt"));

        var editorTab = Assert.Single(fixture.Workbench.Tabs.OfType<EditorWorkbenchTab>());
        fixture.Workbench.CloseTabCommand.Execute(editorTab);

        Assert.Empty(fixture.Workbench.Tabs.OfType<EditorWorkbenchTab>());
        Assert.Empty(fixture.Editor.OpenTabs);
    }

    // ===== 页面标签条投影(与编辑器组分离渲染) =====

    [Fact]
    public void PageTabs_ContainsOnlyPageTabs_InOpenOrder()
    {
        var fixture = CreateWorkbench();
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);
        fixture.Workbench.OpenOrActivatePage(fixture.Projects);

        Assert.Equal(2, fixture.Workbench.PageTabs.Count);
        Assert.All(fixture.Workbench.PageTabs, tab => Assert.IsType<PageWorkbenchTab>(tab));
        Assert.Equal("环境管理", fixture.Workbench.PageTabs[0].Title);
        Assert.Equal("项目管理", fixture.Workbench.PageTabs[1].Title);
        Assert.True(fixture.Workbench.HasPageTabs);
    }

    [Fact]
    public async Task PageTabs_DropsClosedPageTabs()
    {
        var fixture = CreateWorkbench();
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);
        fixture.Workbench.OpenOrActivatePage(fixture.Projects);
        var projectsTab = fixture.Workbench.Tabs.Single(tab => tab.Title == "项目管理");

        fixture.Workbench.CloseTabCommand.Execute(projectsTab);

        Assert.Single(fixture.Workbench.PageTabs);
        Assert.Equal("环境管理", fixture.Workbench.PageTabs[0].Title);
    }

    [Fact]
    public void SelectedPageTab_ReflectsPageSelectionOnly()
    {
        var fixture = CreateWorkbench();
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);
        var pageTab = fixture.Workbench.PageTabs.Single();

        Assert.Same(pageTab, fixture.Workbench.SelectedPageTab);
        Assert.True(fixture.Workbench.IsPageTabSelected);
        Assert.Same(fixture.Environment, fixture.Workbench.SelectedPageContent);
    }

    [Fact]
    public async Task SelectedPageTab_ClearsWhenDocumentSelected()
    {
        var fixture = CreateWorkbench();
        fixture.Workbench.OpenOrActivatePage(fixture.Environment);
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "a.txt"));

        Assert.Null(fixture.Workbench.SelectedPageTab);
        Assert.False(fixture.Workbench.IsPageTabSelected);
        // 编辑器组对 Workbench 可见(视图渲染源)。
        Assert.NotNull(fixture.Workbench.Groups);
    }

    // ===== 顶层文件标签栏(跨编辑器组) =====

    [Fact]
    public async Task EditorTabs_ProjectsAllGroupsInGlobalOpenOrder()
    {
        var fixture = CreateWorkbench();
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "a.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;

        var first = fixture.Editor.Groups.ActiveGroup;
        var second = fixture.Editor.Groups.SplitGroup(first, EditorSplitOrientation.Vertical);
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "b.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;

        fixture.Editor.Groups.ActiveGroup = first;
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "c.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;

        Assert.Equal(2, fixture.Editor.Groups.GroupCount);
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" },
            fixture.Workbench.EditorTabs.Select(tab => tab.Title));
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" },
            fixture.Workbench.Tabs.OfType<EditorWorkbenchTab>().Select(tab => tab.Title));
        Assert.Contains(fixture.Editor.Groups.AllTabs, tab => tab.Name == "a.txt");
        Assert.Contains(fixture.Editor.Groups.AllTabs, tab => tab.Name == "b.txt");
        Assert.Contains(fixture.Editor.Groups.AllTabs, tab => tab.Name == "c.txt");
    }

    [Fact]
    public async Task SelectingEditorTab_ActivatesItsOwningGroup()
    {
        var fixture = CreateWorkbench();
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "a.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;
        var first = fixture.Editor.Groups.ActiveGroup;
        var second = fixture.Editor.Groups.SplitGroup(first, EditorSplitOrientation.Vertical);

        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "b.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;
        var target = fixture.Workbench.EditorTabs.Single(tab => tab.Title == "a.txt");

        fixture.Workbench.SelectedEditorTab = target;

        Assert.Same(first, fixture.Editor.Groups.ActiveGroup);
        Assert.Same(target.EditorTab, first.SelectedTab);
        Assert.NotSame(second, fixture.Editor.Groups.ActiveGroup);
    }

    [Fact]
    public async Task SwitchingGroups_DoesNotReorderEditorTabs()
    {
        var fixture = CreateWorkbench();
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "a.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;
        var first = fixture.Editor.Groups.ActiveGroup;
        var second = fixture.Editor.Groups.SplitGroup(first, EditorSplitOrientation.Vertical);
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "b.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;

        var expected = fixture.Workbench.EditorTabs.Select(tab => tab.Title).ToArray();
        fixture.Editor.Groups.ActiveGroup = first;
        fixture.Editor.Groups.ActiveGroup = second;
        fixture.Editor.Groups.ActiveGroup = first;

        Assert.Equal(expected, fixture.Workbench.EditorTabs.Select(tab => tab.Title));
    }

    [Fact]
    public async Task MoveEditorTab_ReordersGlobalStripWithoutMovingGroupMembership()
    {
        var fixture = CreateWorkbench();
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "a.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;
        var first = fixture.Editor.Groups.ActiveGroup;
        var second = fixture.Editor.Groups.SplitGroup(first, EditorSplitOrientation.Vertical);
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "b.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;

        var a = fixture.Workbench.EditorTabs.Single(tab => tab.Title == "a.txt");
        var b = fixture.Workbench.EditorTabs.Single(tab => tab.Title == "b.txt");
        fixture.Workbench.MoveEditorTabCommand.Execute(new MoveTabArgs(1, 0));

        Assert.Equal(new[] { "b.txt", "a.txt" }, fixture.Workbench.EditorTabs.Select(tab => tab.Title));
        Assert.Same(first, fixture.Editor.Groups.FindGroupContaining(a.EditorTab));
        Assert.Same(second, fixture.Editor.Groups.FindGroupContaining(b.EditorTab));
    }

    [Fact]
    public async Task MovingTabAcrossGroups_PreservesGlobalTabOrder()
    {
        var fixture = CreateWorkbench();
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "a.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;
        var first = fixture.Editor.Groups.ActiveGroup;
        fixture.Editor.Groups.SplitGroup(first, EditorSplitOrientation.Vertical,
            fixture.Editor.SelectedTab);

        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "b.txt"));
        fixture.Editor.SelectedTab!.IsPreview = false;
        var b = fixture.Workbench.EditorTabs.Single(tab => tab.Title == "b.txt");
        fixture.Editor.Groups.MoveTabToGroup(b.EditorTab, first);

        Assert.Equal(new[] { "a.txt", "b.txt" }, fixture.Workbench.EditorTabs.Select(tab => tab.Title));
        Assert.Same(first, fixture.Editor.Groups.FindGroupContaining(b.EditorTab));
        Assert.DoesNotContain(fixture.Workbench.EditorTabs, tab => tab.EditorTab.IsClosed);
    }

    [Fact]
    public async Task PreviewReplacedByNextOpen_LeavesNoGhostTabInStrip()
    {
        // 回归:预览槽替换若只做裸 Remove(不置 IsClosed),被替换的预览标签会残留在工作台
        // 条带里——它已不属于任何编辑器组,点关闭时 CloseTab 找不到所属组而静默失败,
        // 导致除最新打开的标签外其余文件标签永远无法关闭。
        var fixture = CreateWorkbench();
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "a.txt"));
        var replaced = Assert.IsType<FilePreviewTab>(fixture.Editor.SelectedTab);
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "b.txt"));

        // 条带只剩 b.txt:被替换的 a.txt 预览不留幽灵标签。
        var editorTabs = fixture.Workbench.Tabs.OfType<EditorWorkbenchTab>().ToArray();
        Assert.Equal(new[] { "b.txt" }, editorTabs.Select(tab => tab.Title));
        Assert.DoesNotContain(fixture.Workbench.Tabs,
            tab => tab is EditorWorkbenchTab { EditorTab: var et } && ReferenceEquals(et, replaced));

        // 被替换的预览按真正的关闭处理:置 IsClosed 且离组。
        Assert.True(replaced.IsClosed);
        Assert.Null(fixture.Editor.Groups.FindGroupContaining(replaced));
    }

    [Fact]
    public async Task GhostTabWithoutOwningGroup_IsClosableAndClearedFromStrip()
    {
        // 回归/兜底:已脱离任何组但未置 IsClosed 的标签(旧版布局替换遗留的幽灵标签)仍投影在
        // 工作台条带里;点关闭必须真正生效(补齐 IsClosed/释放)并移除条带项,而不是静默失败
        // 永远残留。
        var fixture = CreateWorkbench();
        await fixture.Editor.OpenFileAsync(await WriteFileAsync(_tempDir, "a.txt"));
        var ghost = fixture.Editor.SelectedTab!;
        ghost.IsPreview = false;

        // 模拟遗留:裸离组(不置 IsClosed),条带投影仍在。
        fixture.Editor.Groups.ActiveGroup.Tabs.Remove(ghost);
        var workbenchGhost = fixture.Workbench.Tabs.OfType<EditorWorkbenchTab>()
            .Single(tab => ReferenceEquals(tab.EditorTab, ghost));

        fixture.Workbench.CloseTabCommand.Execute(workbenchGhost);

        Assert.True(ghost.IsClosed);
        Assert.Null(fixture.Editor.Groups.FindGroupContaining(ghost));
        Assert.DoesNotContain(fixture.Workbench.Tabs,
            tab => tab is EditorWorkbenchTab { EditorTab: var et } && ReferenceEquals(et, ghost));
        Assert.DoesNotContain(fixture.Workbench.EditorTabs,
            tab => ReferenceEquals(tab.EditorTab, ghost));
    }
}
