using System.Collections.ObjectModel;
using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Project.Models;
using Nornia.Project.Services;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>设置实时生效:设置保存后(经 ISettingsSession.Changed)立即作用于当前工作台
/// —— 编辑器/ Diff 已打开标签、标签上限、资源管理器紧凑文件夹、Git 自动刷新与恢复上次工作区。
/// 配置解析、保存与作用域优先级不变。</summary>
public sealed class SettingsLiveEffectTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-live-settings-{Guid.NewGuid():N}");

    public SettingsLiveEffectTests() => Directory.CreateDirectory(_tempDir);

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

    private string Write(string name, string content = "content")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // 60s:设置变更经 ScopedSettingsService 的 watch 通道多跳异步投递(提交 → 通道 →
    // watch 续延 → 重读快照 → Changed → 应用),每跳都依赖线程池线程。2 核 CI 上全量
    // 套件并行(1000+ 测试 + 真实 shell 进程)会把链路拉长到远超短预算——实测 15s 在
    // 满负载下必然超时(条件本身最终都成立,只是晚到),60s 提供足量余量。
    private static async Task WaitUntilAsync(Func<bool> condition, string failure, int timeoutMs = 60_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(condition(), failure);
    }

    private static async Task SetAsync<T>(FakeSettingsService settings, SettingKey<T> key, T value)
    {
        var baseline = await settings.GetSnapshotAsync(new SettingsContext());
        var result = await settings.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User, key, value));
        // 提交失败(如瞬时文件锁导致 FileError)必须在此可见,否则订阅者收不到变更,
        // 失败会被错误地表现为下游"设置未应用"的超时。
        Assert.True(result.Status == SettingsCommitStatus.Success,
            $"设置提交失败: {result.Status} {result.ErrorMessage} (key={key.Id})");
    }

    private static GitFileDiff SampleDiff() => new("src/A.cs", null, false, false, false,
    [
        new GitDiffHunk(1, 2, 1, 2, "@@ -1,2 +1,2 @@",
        [
            new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep"),
            new GitDiffLine(GitDiffLineKind.Added, null, 2, "added"),
        ]),
    ]);

    /// <summary>生产依赖链编辑器(真实 ScopedSettingsService 会话,经 Changed 实时应用)。</summary>
    private (EditorAreaViewModel Editor, FakeSettingsService Settings) CreateEditor()
    {
        var settings = new FakeSettingsService();
        var git = new FakeGitService { Status = new GitRepositoryStatus(false, null, null, 0, 0, [], []), DiffResult = SampleDiff() };
        var editor = new EditorAreaViewModel(
            git, new FakeUiLogService(), new FakeClipboardService(),
            CodeFileTypeRegistry.Instance, TextDocumentDecoder.Instance, CodeOutlineParser.Instance,
            TextSearchService.Instance, null,
            settings, new FakeProjectWorkspaceService(), new FakeApplicationStateStore());
        return (editor, settings);
    }

    // ===== 编辑器:标签上限 / 阅读选项实时生效 =====

    [Fact]
    public async Task EditorLimitReduction_EvictsOnlyPreviewTabs()
    {
        var (editor, settings) = CreateEditor();
        // 打开 11 个常驻 + 1 个预览(上限 30 默认不回收);再把上限降到合法区间内(10)。
        for (var i = 0; i < 11; i++)
        {
            await editor.OpenFileAsync(Write($"p{i}.txt"), permanent: true);
        }

        var previewPath = Write("preview.txt");
        await editor.OpenFileAsync(previewPath); // 预览标签
        editor.SelectedTab = editor.Groups.AllTabs.First(); // 选中常驻标签
        Assert.Equal(12, editor.Groups.AllTabs.Count());

        await SetAsync(settings, BuiltInSettingsCatalog.EditorLimitEnabled, true);
        await SetAsync(settings, BuiltInSettingsCatalog.EditorLimitValue, 10);

        await WaitUntilAsync(() => editor.Groups.AllTabs.Count() == 11, "降低标签上限后未回收预览标签");
        Assert.DoesNotContain(editor.Groups.AllTabs, tab => tab.Path == previewPath);
        Assert.DoesNotContain(editor.Groups.AllTabs, tab => tab is FilePreviewTab { IsPreview: true });
    }

    [Fact]
    public async Task EditorLimitReduction_DoesNotClosePermanentTabs()
    {
        var (editor, settings) = CreateEditor();
        var a = Write("a.txt");
        var b = Write("b.txt");
        var c = Write("c.txt");
        await editor.OpenFileAsync(a, permanent: true);
        await editor.OpenFileAsync(b, permanent: true);
        await editor.OpenFileAsync(c, permanent: true);
        Assert.Equal(3, editor.Groups.AllTabs.Count());

        await SetAsync(settings, BuiltInSettingsCatalog.EditorLimitValue, 10);

        // 全部为常驻标签:上限降低只回收预览标签,常驻标签一个也不关闭。
        await WaitUntilAsync(() => editor.Groups.AllTabs.Count() == 3, "上限降低误关闭了常驻标签");
    }

    [Fact]
    public async Task EditorReadingOptions_ApplyToOpenTabsImmediately()
    {
        var (editor, settings) = CreateEditor();
        var file = Write("a.cs", "class A { }");
        await editor.OpenFileAsync(file);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.False(tab.WordWrap);
        Assert.True(tab.ShowStickyScroll);
        Assert.True(tab.RulerColumns is [80]); // 默认 "80" 已在打开时实时应用

        await SetAsync(settings, BuiltInSettingsCatalog.EditorWordWrap, "on");
        await SetAsync(settings, BuiltInSettingsCatalog.StickyScroll, false);
        await SetAsync(settings, BuiltInSettingsCatalog.EditorRulers, "80,120");

        await WaitUntilAsync(() => tab.WordWrap && !tab.ShowStickyScroll && tab.RulerColumns is [80, 120],
            "已打开标签未立即应用新的阅读选项");
    }

    [Fact]
    public async Task DiffOptions_ApplyToOpenDiffTabsImmediately()
    {
        var (editor, settings) = CreateEditor();
        await editor.OpenDiffAsync(new GitDiffRequest(_tempDir, "a.cs", IsStaged: false, IsUntracked: false));
        var tab = editor.Groups.AllTabs.OfType<DiffTab>().Single();
        Assert.True(tab.DiffMode == GitDiffMode.Inline);

        await SetAsync(settings, BuiltInSettingsCatalog.DiffSideBySide, true);
        await SetAsync(settings, BuiltInSettingsCatalog.DiffIgnoreTrimWhitespace, true);
        await SetAsync(settings, BuiltInSettingsCatalog.DiffNarrowInline, false);

        await WaitUntilAsync(() => tab.DiffMode == GitDiffMode.SideBySide
            && tab.IgnoreTrimWhitespace && !tab.UseInlineWhenNarrow,
            "已打开的 Diff 标签未立即应用新的阅读选项");
    }

    // ===== 资源管理器:紧凑文件夹投影 =====

    private (WorkspaceViewModel Workspace, FakeSettingsService Settings) CreateWorkspace()
    {
        var settings = new FakeSettingsService();
        var workspace = new WorkspaceViewModel(new FakeFolderPicker(), settings,
            new FakeProjectWorkspaceService(), new FakeUiLogService(), new FakeClipboardService());
        return (workspace, settings);
    }

    [Fact]
    public async Task ExplorerCompactFolders_MergesSingleChildFolderChain()
    {
        // 树:root/src/nested/a.cs + root/top.txt —— src 只含一个子目录 nested。
        var root = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(Path.Combine(root, "src", "nested"));
        File.WriteAllText(Path.Combine(root, "src", "nested", "a.cs"), "x");
        File.WriteAllText(Path.Combine(root, "top.txt"), "y");

        var (workspace, settings) = CreateWorkspace();
        await workspace.ActivateAsync(); // 绑定设置(ExplorerCompactFolders 默认开启)
        await WaitUntilAsync(() => workspace.CompactFoldersEnabled, "紧凑文件夹设置未应用");

        await workspace.OpenWorkspaceAsync(root);
        var src = workspace.RootNodes[0].Children.Single(node => node.Name == "src");
        await workspace.ExpandAllCommand.ExecuteAsync(src);

        var merged = workspace.WorkspaceTreeRows.OfType<WorkspaceFolderRow>()
            .SingleOrDefault(row => row.DisplayName == "src / nested");
        Assert.NotNull(merged);
        Assert.DoesNotContain(workspace.WorkspaceTreeRows, row => row is WorkspaceFolderRow { Name: "src" });
    }

    [Fact]
    public async Task ExplorerCompactFolders_Off_ShowsSeparateRows()
    {
        var root = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(Path.Combine(root, "src", "nested"));
        File.WriteAllText(Path.Combine(root, "src", "nested", "a.cs"), "x");

        var (workspace, settings) = CreateWorkspace();
        await workspace.ActivateAsync();
        await WaitUntilAsync(() => workspace.CompactFoldersEnabled, "紧凑文件夹设置未应用");
        await SetAsync(settings, BuiltInSettingsCatalog.ExplorerCompactFolders, false);
        await WaitUntilAsync(() => !workspace.CompactFoldersEnabled, "紧凑文件夹开关未生效");

        await workspace.OpenWorkspaceAsync(root);
        var src = workspace.RootNodes[0].Children.Single(node => node.Name == "src");
        await workspace.ExpandAllCommand.ExecuteAsync(src);

        Assert.Contains(workspace.WorkspaceTreeRows, row => row is WorkspaceFolderRow { DisplayName: "src" });
        Assert.Contains(workspace.WorkspaceTreeRows, row => row is WorkspaceFolderRow { DisplayName: "nested" });
        Assert.DoesNotContain(workspace.WorkspaceTreeRows, row => row is WorkspaceFolderRow { DisplayName: "src / nested" });
    }

    // ===== Git:自动刷新开关实时附加/解除仓库监视器 =====

    [Fact]
    public async Task GitAutoRefresh_Toggle_AttachesAndDetachesRepositoryWatcher()
    {
        var git = new FakeGitService { Status = new GitRepositoryStatus(true, "main", null, 0, 0, [], []), DiffResult = SampleDiff() };
        var settings = new FakeSettingsService();
        var watcher = new FakeGitRepositoryWatcher();
        var viewModel = new GitViewModel(
            git, new FakeFolderPicker { Result = _tempDir }, new FakeConfirmationService(),
            new FakeProjectCatalog(), new EditorAreaViewModel(git, new FakeUiLogService()),
            new FakeUiLogService(), new FakeClipboardService(), watcher,
            new FakeProjectWorkspaceService(), new FakeApplicationStateStore(), settings);

        await viewModel.ActivateAsync(); // 绑定作用域设置会话(自动刷新默认开启)
        viewModel.RepositoryPath = _tempDir;
        await viewModel.RefreshCommand.ExecuteAsync(null); // 打开仓库 → 附加监视器
        await WaitUntilAsync(() => watcher.AttachedPaths.Contains(_tempDir), "仓库激活后未附加监视器");

        await SetAsync(settings, BuiltInSettingsCatalog.GitAutoRefresh, false);
        await WaitUntilAsync(() => watcher.DetachCalls >= 1, "关闭自动刷新后未解除监视器");

        await SetAsync(settings, BuiltInSettingsCatalog.GitAutoRefresh, true);
        await WaitUntilAsync(() => watcher.AttachedPaths.Count >= 2, "重新开启自动刷新后未重新附加监视器");
    }

    // ===== 恢复上次工作区:会话中开关实时语义 =====

    [Fact]
    public async Task RestoreLastWorkspace_ToggledOnAfterStartup_RestoresRecentWorkspace()
    {
        var settings = new FakeSettingsService();
        await SetAsync(settings, BuiltInSettingsCatalog.RestoreLastWorkspace, false);
        var state = new FakeApplicationStateStore { State = new ApplicationState { LastWorkspace = _tempDir } };
        var service = new ProjectWorkspaceService(new FakeProjectCatalog(), settings, state);

        await service.EnsureInitializedAsync();
        Assert.Null(service.Current); // 启动时开关关闭 → 不自动恢复

        await SetAsync(settings, BuiltInSettingsCatalog.RestoreLastWorkspace, true);

        await WaitUntilAsync(() => service.Current?.ProjectPath == _tempDir,
            "会话中打开「恢复上次工作区」后未立即恢复最近工作区");
        Assert.True(service.IsStartupAutoRestore);
    }

    [Fact]
    public async Task RestoreLastWorkspace_ToggledOff_KeepsCurrentWorkspace()
    {
        var settings = new FakeSettingsService();
        var state = new FakeApplicationStateStore { State = new ApplicationState { LastWorkspace = _tempDir } };
        var service = new ProjectWorkspaceService(new FakeProjectCatalog(), settings, state);

        await service.EnsureInitializedAsync(); // 默认开启 → 启动即恢复
        Assert.NotNull(service.Current);

        await SetAsync(settings, BuiltInSettingsCatalog.RestoreLastWorkspace, false);
        await WaitUntilAsync(() => service.Current?.ProjectPath == _tempDir, "关闭开关不应关闭已打开的工作区");

        // 再次打开开关:已有工作区 → 不重复恢复(不产生新上下文)。
        var before = service.Current;
        await SetAsync(settings, BuiltInSettingsCatalog.RestoreLastWorkspace, true);
        await WaitUntilAsync(() => ReferenceEquals(before, service.Current), "重新打开开关不应产生新工作区上下文");
    }
}