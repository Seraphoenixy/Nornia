using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Services;
using Nornia.Project.Models;
using Nornia.Project.Services;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>
/// 编辑器区门面层跨组行为与工作区状态恢复:v1 扁平字段迁移、v2 布局树恢复、
/// 跨组去重激活、关闭组、Flush 写出 v2 布局并保留扁平字段。
/// </summary>
public sealed class EditorAreaRestoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-editor-restore-{Guid.NewGuid():N}");
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"nornia-ws-{Guid.NewGuid():N}");

    public EditorAreaRestoreTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
    }

    private string WriteFile(string name, string content = "hello")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static GitFileDiff SampleDiff() => new("src/A.cs", null, false, false, false,
    [
        new GitDiffHunk(1, 2, 1, 2, "@@ -1,2 +1,2 @@",
        [
            new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep"),
            new GitDiffLine(GitDiffLineKind.Added, null, 2, "added"),
        ]),
    ]);

    /// <summary>生产依赖链构造(真实状态存储写入临时文件)。</summary>
    private (EditorAreaViewModel Editor, FakeGitService Git, FakeProjectWorkspaceService Workspace,
        ApplicationStateStore Store, FakeSettingsService Settings) Create()
    {
        var git = new FakeGitService { DiffResult = SampleDiff() };
        var log = new FakeUiLogService();
        var settings = new FakeSettingsService();
        var workspace = new FakeProjectWorkspaceService
        {
            Current = new ProjectWorkspaceContext(
                new ProjectAsset(Guid.NewGuid(), "Test", _workspace, ProjectPathStatus.Available, 0, null, null,
                    EnvironmentHealthStatus.Unknown),
                _workspace, null),
        };
        var store = new ApplicationStateStore(Path.Combine(_tempDir, "state.json"));
        var editor = new EditorAreaViewModel(
            git, log, new FakeClipboardService(),
            CodeFileTypeRegistry.Instance, TextDocumentDecoder.Instance, CodeOutlineParser.Instance,
            TextSearchService.Instance, null,
            settings, workspace, store);
        return (editor, git, workspace, store, settings);
    }

    private static async Task ActivateWorkspaceAsync(FakeProjectWorkspaceService workspace) =>
        await workspace.ActivateAsync(workspace.Current!.ProjectPath);

    // ===== 跨组去重 / 激活 =====

    [Fact]
    public async Task OpenFile_WhenTabExistsInAnotherGroup_ActivatesOwningGroup()
    {
        var (editor, _, _, _, _) = Create();
        var path = WriteFile("a.txt");
        await editor.OpenFileAsync(path);
        var previousActive = editor.Groups.ActiveGroup;
        var tab = editor.SelectedTab!;
        editor.Groups.SplitActiveGroup(EditorSplitOrientation.Vertical);
        // 拆分把活动标签移入新组 → 该标签现在属于新组。
        var owning = editor.Groups.ActiveGroup;
        Assert.NotSame(previousActive, owning);
        Assert.Contains(tab, owning.Tabs);

        await editor.OpenFileAsync(path); // 再次打开 → 激活已有标签所在组

        Assert.Same(owning, editor.Groups.ActiveGroup);
        Assert.Same(tab, editor.SelectedTab);
        Assert.Single(editor.Groups.AllTabs, t => t.TabKey == tab.TabKey);
    }

    [Fact]
    public async Task CloseLastTabInGroup_RemovesGroup_KeepsAtLeastOne()
    {
        var (editor, _, _, _, _) = Create();
        await editor.OpenFileAsync(WriteFile("a.txt"));
        editor.SelectedTab!.IsPreview = false;
        var groupA = editor.Groups.ActiveGroup;
        editor.Groups.SplitActiveGroup(EditorSplitOrientation.Vertical); // tab 移入新组
        var groupB = editor.Groups.ActiveGroup;
        Assert.Equal(2, editor.Groups.GroupCount);

        editor.CloseTab(editor.SelectedTab); // 关闭 groupB 的唯一标签

        Assert.Equal(1, editor.Groups.GroupCount);
        Assert.Same(groupA, editor.Groups.ActiveGroup);
        Assert.Null(editor.SelectedTab);
    }

    [Fact]
    public async Task CloseAllTabs_ClearsEveryGroup_KeepsOneEmptyGroup()
    {
        var (editor, _, _, _, _) = Create();
        await editor.OpenFileAsync(WriteFile("a.txt"));
        var tab = editor.SelectedTab!;
        editor.Groups.SplitActiveGroup(EditorSplitOrientation.Vertical); // tab 移入新组
        var other = editor.Groups.ActiveGroup;
        await editor.OpenFileAsync(WriteFile("b.txt"));
        other.SelectedTab!.IsPreview = false;
        Assert.Equal(2, editor.Groups.GroupCount);

        editor.CloseAllTabsCommand.Execute(null);

        Assert.Equal(1, editor.Groups.GroupCount);
        Assert.Empty(editor.Groups.AllTabs);
        Assert.Null(editor.SelectedTab);
    }

    // ===== 组命令接线 =====

    [Fact]
    public async Task SplitRightCommand_SplitsGroupIntoTwo()
    {
        var (editor, _, _, _, _) = Create();
        await editor.OpenFileAsync(WriteFile("a.txt"));
        var group = editor.Groups.ActiveGroup;
        var tab = editor.SelectedTab!;

        group.SplitRightCommand.Execute(tab);

        Assert.Equal(2, editor.Groups.GroupCount);
        Assert.Equal(EditorSplitOrientation.Vertical, ((EditorSplitNode)editor.Groups.Root).Orientation);
        Assert.Contains(tab, editor.Groups.ActiveGroup.Tabs);
    }

    [Fact]
    public async Task GroupCloseTabCommand_ClosesThroughFacade()
    {
        var (editor, _, _, _, _) = Create();
        await editor.OpenFileAsync(WriteFile("a.txt"));
        var group = editor.Groups.ActiveGroup;
        var tab = editor.SelectedTab!;

        group.CloseTabCommand.Execute(tab);

        Assert.Empty(editor.OpenTabs);
        Assert.Null(editor.SelectedTab);
    }

    // ===== v1 迁移 =====

    [Fact]
    public async Task Restore_V1RecentTabs_MigratesToSingleGroup()
    {
        var (editor, _, workspace, store, _) = Create();
        var path = WriteFile("legacy.txt");
        var workspaceKey = workspace.Current!.ProjectPath;
        var v1 = new WorkspaceApplicationState(
            RecentTabs: [$"file:{path}", $"diff:src/A.cs"],
            ActiveEditor: $"file:{path}");
        await store.CommitAsync(new([new(ApplicationStateField.WorkspaceState, v1, workspaceKey)]));

        await ActivateWorkspaceAsync(workspace);

        Assert.Equal(1, editor.Groups.GroupCount);
        var tab = Assert.Single(editor.Groups.ActiveGroup.Tabs);
        Assert.Equal("file:" + path, tab.TabKey);
        // v1 的 diff 键无法恢复仓库路径 → 跳过;活动编辑器为遗留文件标签。
        Assert.Equal(tab, editor.SelectedTab);
    }

    [Fact]
    public async Task Restore_V1MissingFileTab_IsSkipped()
    {
        var (editor, _, workspace, store, _) = Create();
        var workspaceKey = workspace.Current!.ProjectPath;
        var v1 = new WorkspaceApplicationState(
            RecentTabs: [$"file:{Path.Combine(_tempDir, "gone.txt")}"]);
        await store.CommitAsync(new([new(ApplicationStateField.WorkspaceState, v1, workspaceKey)]));

        await ActivateWorkspaceAsync(workspace);

        Assert.Empty(editor.Groups.ActiveGroup.Tabs);
    }

    // ===== v2 布局恢复 =====

    [Fact]
    public async Task Restore_V2Layout_RestoresGroupsTabsAndSelection()
    {
        var (editor, _, workspace, store, _) = Create();
        var path = WriteFile("left.txt");
        var path2 = WriteFile("right.txt");
        var layout = new EditorLayoutState(
            Orientation: "vertical",
            Children:
            [
                new EditorLayoutState(GroupId: "left", Tabs:
                [
                    new EditorTabState($"file:{path}", IsPreview: true),
                    new EditorTabState("diff:src/A.cs", RepositoryPath: @"C:\repo", DiffPath: "src/A.cs",
                        IsStaged: true),
                ], ActiveTabKey: "diff:src/A.cs"),
                new EditorLayoutState(GroupId: "right", Tabs: [new EditorTabState($"file:{path2}")]),
            ],
            Weights: [0.3, 0.7],
            ActiveGroupId: "right");
        var workspaceKey = workspace.Current!.ProjectPath;
        await store.CommitAsync(new([new(ApplicationStateField.WorkspaceState,
            new WorkspaceApplicationState(EditorLayout: layout), workspaceKey)]));

        await ActivateWorkspaceAsync(workspace);

        Assert.Equal(2, editor.Groups.GroupCount);
        var ordered = editor.Groups.GroupsInLayoutOrder;
        var left = ordered[0];
        var right = ordered[1];
        Assert.Equal(2, left.Tabs.Count);
        var preview = Assert.IsType<FilePreviewTab>(left.Tabs[0]);
        Assert.True(preview.IsPreview);
        var diff = Assert.IsType<DiffTab>(left.Tabs[1]);
        Assert.Equal(@"C:\repo", diff.Request.RepositoryPath);
        Assert.True(diff.Request.IsStaged);
        Assert.Equal("src/A.cs", diff.Request.Path);
        Assert.Single(right.Tabs);
        // 活动组与活动标签恢复。
        Assert.Same(right, editor.Groups.ActiveGroup);
        Assert.Same(left.Tabs[1], left.SelectedTab);
    }

    [Fact]
    public async Task StartupAutoRestore_DoesNotReopenFileTabs()
    {
        var (editor, _, workspace, store, _) = Create();
        var path = WriteFile("left.txt");
        var layout = new EditorLayoutState(
            Orientation: "vertical",
            Children:
            [
                new EditorLayoutState(GroupId: "g1", Tabs: [new EditorTabState($"file:{path}")], ActiveTabKey: $"file:{path}"),
            ],
            Weights: [1.0],
            ActiveGroupId: "g1");
        var workspaceKey = workspace.Current!.ProjectPath;
        await store.CommitAsync(new([new(ApplicationStateField.WorkspaceState,
            new WorkspaceApplicationState(EditorLayout: layout), workspaceKey)]));

        // 启动自动恢复工作区:工作区上下文就位,但源码文件标签不回放。
        workspace.IsStartupAutoRestore = true;
        await ActivateWorkspaceAsync(workspace);

        Assert.Equal(1, editor.Groups.GroupCount);
        Assert.Empty(editor.Groups.AllTabs);

        // 用户随后主动激活同一工作区(标记已清除)→ 仍按保存布局恢复。
        workspace.IsStartupAutoRestore = false;
        await ActivateWorkspaceAsync(workspace);

        Assert.Single(editor.Groups.AllTabs);
        Assert.Contains(editor.Groups.AllTabs, tab => tab.TabKey == "file:" + path);
    }

    [Fact]
    public async Task Restore_V2Layout_MissingFileTab_SkippedWithLog()
    {
        var (editor, _, workspace, store, _) = Create();
        var layout = new EditorLayoutState(
            Orientation: "vertical",
            Children:
            [
                new EditorLayoutState(GroupId: "g1", Tabs: [new EditorTabState($"file:{Path.Combine(_tempDir, "gone.txt")}")]),
                new EditorLayoutState(GroupId: "g2"),
            ],
            Weights: [0.5, 0.5],
            ActiveGroupId: "g1");
        var workspaceKey = workspace.Current!.ProjectPath;
        await store.CommitAsync(new([new(ApplicationStateField.WorkspaceState,
            new WorkspaceApplicationState(EditorLayout: layout), workspaceKey)]));

        await ActivateWorkspaceAsync(workspace);

        var ordered = editor.Groups.GroupsInLayoutOrder;
        Assert.Equal(2, editor.Groups.GroupCount);
        Assert.Empty(ordered[0].Tabs);
        Assert.Empty(ordered[1].Tabs);
        Assert.Same(ordered[0], editor.Groups.ActiveGroup);
    }

    // ===== Flush 写出 v2 布局 =====

    [Fact]
    public async Task FlushAsync_WritesV2LayoutAndKeepsFlatFields()
    {
        var (editor, _, workspace, store, _) = Create();
        var path = WriteFile("flush.txt");
        await editor.OpenFileAsync(path);
        editor.SelectedTab!.IsPreview = false;
        editor.Groups.SplitActiveGroup(EditorSplitOrientation.Vertical);
        editor.Groups.ActiveGroup.SelectedTab!.IsPreview = false;
        await editor.OpenFileAsync(WriteFile("second.txt"));

        await editor.FlushAsync();

        var state = await store.LoadAsync();
        var saved = state.Workspaces!.Values.Single();
        Assert.NotNull(saved.EditorLayout);
        Assert.Equal("vertical", saved.EditorLayout!.Orientation);
        Assert.Equal(2, saved.EditorLayout.Children!.Count);
        // 扁平字段保留,便于旧版本降级启动。
        Assert.NotNull(saved.RecentTabs);
        Assert.True(saved.RecentTabs!.Count >= 2);
        Assert.NotNull(saved.ActiveEditor);
        Assert.Equal(ApplicationState.CurrentSchemaVersion, state.SchemaVersion);
    }

    [Fact]
    public async Task CloseActiveTab_SelectsNextMostRecentlyActive()
    {
        var (editor, _, _, _, _) = Create();
        // 逐个取消预览态使标签共存,建立 MRU [c, b, a]。
        await editor.OpenFileAsync(WriteFile("a.txt"));
        var a = editor.SelectedTab!;
        a.IsPreview = false;
        await editor.OpenFileAsync(WriteFile("b.txt"));
        var b = editor.SelectedTab!;
        b.IsPreview = false;
        await editor.OpenFileAsync(WriteFile("c.txt"));
        var c = editor.SelectedTab!;
        Assert.Equal([c, b, a], editor.Groups.ActiveGroup.MruEditors);

        // 重新激活 a → MRU [a, c, b];关闭 a → 回选最近使用的 c(而非标签条邻居 b)。
        editor.ActivateTab(a);
        Assert.Equal([a, c, b], editor.Groups.ActiveGroup.MruEditors);
        editor.CloseTabCommand.Execute(a);

        Assert.Same(c, editor.SelectedTab);
    }

    [Fact]
    public async Task SplitGroup_PersistsLayoutImmediately()
    {
        var (editor, _, workspace, store, _) = Create();
        await editor.OpenFileAsync(WriteFile("a.txt"));
        editor.SelectedTab!.IsPreview = false;
        // 尽早订阅并等待“含两个子节点的布局”落盘(忽略打开文件时已在途的一次性旧布局提交)。
        var persisted = new TaskCompletionSource<ApplicationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, args) =>
        {
            if (args.State.Workspaces!.Values.Any(saved => saved.EditorLayout?.Children?.Count == 2))
            {
                persisted.TrySetResult(args.State);
            }
        };

        editor.Groups.SplitActiveGroup(EditorSplitOrientation.Vertical);
        var state = await persisted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var saved = state.Workspaces!.Values.Single();
        Assert.NotNull(saved.EditorLayout);
        Assert.Equal(2, saved.EditorLayout!.Children!.Count);
    }

    [Fact]
    public async Task Restore_V2LayoutWithLiveTabs_ClosesExistingTabs_NoGhostTabs()
    {
        // 回归:恢复整体替换组树。旧实现直接丢弃现存组——其中的标签脱离任何组且未置
        // IsClosed,工作台条带留下永远无法关闭的幽灵标签。修复后旧标签按"真正关闭"处理。
        var (editor, _, workspace, store, _) = Create();
        var path = WriteFile("live.txt");
        await editor.OpenFileAsync(path);
        var liveTab = editor.SelectedTab!;
        liveTab.IsPreview = false;

        var layout = new EditorLayoutState(
            Orientation: "vertical",
            Children:
            [
                new EditorLayoutState(GroupId: "g1", Tabs: [new EditorTabState($"file:{path}")], ActiveTabKey: $"file:{path}"),
            ],
            Weights: [1.0],
            ActiveGroupId: "g1");
        var workspaceKey = workspace.Current!.ProjectPath;
        await store.CommitAsync(new([new(ApplicationStateField.WorkspaceState,
            new WorkspaceApplicationState(EditorLayout: layout), workspaceKey)]));

        await ActivateWorkspaceAsync(workspace);

        // 旧标签:置 IsClosed 且脱离任何组(投影可清理,载荷已释放)。
        Assert.True(liveTab.IsClosed);
        Assert.Null(editor.Groups.FindGroupContaining(liveTab));

        // 恢复的是新组新实例,同名文件不复活旧标签。
        Assert.Equal(1, editor.Groups.GroupCount);
        var restored = Assert.Single(editor.Groups.AllTabs);
        Assert.Equal("file:" + path, restored.TabKey);
        Assert.NotSame(liveTab, restored);
    }
}