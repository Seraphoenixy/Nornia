using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Covers the shared editor area: explorer file opens become read-only preview tabs and
/// source-control selections become diff tabs in the same tab strip.</summary>
public sealed class EditorAreaViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-editor-{Guid.NewGuid():N}");

    public EditorAreaViewModelTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

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

    private static GitFileDiff SampleDiff() => new(
        "src/A.cs",
        null,
        false,
        false,
        false,
        [
            new GitDiffHunk(1, 2, 1, 2, "@@ -1,2 +1,2 @@",
            [
                new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep"),
                new GitDiffLine(GitDiffLineKind.Added, null, 2, "added")
            ])
        ]);

    private (EditorAreaViewModel Editor, FakeGitService Git) Create()
    {
        var git = new FakeGitService { DiffResult = SampleDiff() };
        return (new EditorAreaViewModel(git, new FakeUiLogService()), git);
    }

    private (EditorAreaViewModel Editor, FakeClipboardService Clipboard) CreateWithClipboard()
    {
        var git = new FakeGitService { DiffResult = SampleDiff() };
        var clipboard = new FakeClipboardService();
        return (new EditorAreaViewModel(git, new FakeUiLogService(), clipboard), clipboard);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(condition(), failure);
    }

    [Fact]
    public async Task OpenFileAsync_CreatesPreviewTabAndLoadsContent()
    {
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(file, "hello");

        await editor.OpenFileAsync(file);

        var preview = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal("a.txt", preview.Name);
        Assert.Equal("hello", preview.Content);
        Assert.Equal(string.Empty, preview.Notice);
    }

    [Fact]
    public async Task OpenFileAsync_Permanent_OpensRegularTab_NotReplacedByNextOpen()
    {
        // 双击语义:常驻打开不进预览槽,后续文件打开不会替换它。
        var (editor, _) = Create();
        var a = Path.Combine(_tempDir, "a.txt");
        var b = Path.Combine(_tempDir, "b.txt");
        await File.WriteAllTextAsync(a, "hello");
        await File.WriteAllTextAsync(b, "world");

        await editor.OpenFileAsync(a, permanent: true);
        var tabA = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.False(tabA.IsPreview);

        await editor.OpenFileAsync(b);

        Assert.Same(tabA, editor.OpenTabs.Single(tab => tab.Path == a));
        Assert.False(tabA.IsPreview);
        Assert.Contains(editor.OpenTabs, tab => tab.Path == b);
    }

    [Fact]
    public async Task OpenFileAsync_Permanent_PromotesAlreadyOpenPreview()
    {
        // 双击再次打开已打开的预览 → 就地转正(同一标签,不新增标签)。
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(file, "hello");

        await editor.OpenFileAsync(file);
        var tabA = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.True(tabA.IsPreview);

        await editor.OpenFileAsync(file, permanent: true);

        Assert.Same(tabA, Assert.Single(editor.OpenTabs));
        Assert.False(tabA.IsPreview);
    }

    [Fact]
    public async Task OpenFileAsync_LargeFileShowsPreviewNotice()
    {
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "big.txt");
        var chunk = new string('x', 4096);
        using (var writer = new StreamWriter(file, append: false))
        {
            for (var i = 0; i < 2100; i++)
            {
                await writer.WriteAsync(chunk);
            }
        }

        await editor.OpenFileAsync(file);

        var preview = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.Contains("超过 8 MB", preview.Notice);
        Assert.NotEmpty(preview.Content);
        Assert.True(preview.Content.Length <= FileReadOnlyDocumentSource.MaximumWindowCharacters);
        Assert.Equal(ReadOnlyContentTier.Windowed, preview.CapacityTier);
    }

    [Fact]
    public async Task OpenFileAsync_WhenOutlineParsingFails_ShowsNoticeInsteadOfFaulting()
    {
        var git = new FakeGitService { DiffResult = SampleDiff() };
        var editor = new EditorAreaViewModel(
            git,
            new FakeUiLogService(),
            new FakeClipboardService(),
            CodeFileTypeRegistry.Instance,
            TextDocumentDecoder.Instance,
            new ThrowingOutlineParser());
        var file = Path.Combine(_tempDir, "broken.cs");
        await File.WriteAllTextAsync(file, "class C { }");

        await editor.OpenFileAsync(file);

        var preview = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.StartsWith("无法预览文件：", preview.Notice);
        Assert.Empty(preview.Content);
    }

    private sealed class ThrowingOutlineParser : ICodeOutlineParser
    {
        public IReadOnlyList<CodeOutlineEntry> Parse(string text, CodeOutlineKind kind) =>
            throw new ArgumentException("invalid outline");
    }

    [Fact]
    public async Task OpenDiffAsync_CreatesDiffTabAndLoadsLines()
    {
        var (editor, _) = Create();

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", IsStaged: false, IsUntracked: false));

        var diff = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal("src/A.cs", diff.Path);
        Assert.Contains(diff.DiffLines, line => line.Kind == GitDiffLineKind.Added && line.Text == "added");
        Assert.NotEmpty(diff.SideBySideRows);
        Assert.True(diff.HasDiff);
        Assert.Equal("src/A.cs", diff.DiffTitle);
    }

    [Fact]
    public async Task RepositoryChange_ReloadsOnlyMatchingOpenDiff()
    {
        var git = new FakeGitService { DiffResult = SampleDiff() };
        var watcher = new FakeGitRepositoryWatcher();
        var editor = new EditorAreaViewModel(git, new FakeUiLogService(),
            repositoryWatcher: watcher);

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false));
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/B.cs", false, false));
        var initialRequests = git.DiffRequests.Count;

        git.DiffRevision = "revision-2";
        watcher.RaiseChangesDetected(changedPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src/A.cs" });

        await WaitUntilAsync(() => git.DiffRequests.Count > initialRequests, "同路径 Diff 未自动刷新");
        Assert.Equal(initialRequests + 1, git.DiffRequests.Count);
        Assert.Equal("src/A.cs", git.DiffRequests[^1].Path);
    }

    [Fact]
    public async Task IndexChange_ReloadsOnlyChangedOpenDiffRevision()
    {
        var git = new FakeGitService { DiffResult = SampleDiff() };
        git.DiffRevisions["src/A.cs"] = "a-1";
        git.DiffRevisions["src/B.cs"] = "b-1";
        var watcher = new FakeGitRepositoryWatcher();
        var editor = new EditorAreaViewModel(git, new FakeUiLogService(),
            repositoryWatcher: watcher);

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", true, false));
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/B.cs", true, false));
        var initialRequests = git.DiffRequests.Count;

        git.DiffRevisions["src/A.cs"] = "a-2";
        watcher.RaiseChangesDetected(indexChanged: true);

        await WaitUntilAsync(() => git.DiffRequests.Count > initialRequests, "index 变化后 Diff 未自动刷新");
        Assert.Equal(initialRequests + 1, git.DiffRequests.Count);
        Assert.Equal("src/A.cs", git.DiffRequests[^1].Path);
    }

    [Fact]
    public async Task RepositoryChange_DoesNotReloadCommitDiff()
    {
        var git = new FakeGitService { DiffResult = SampleDiff(), CommitFileDiff = SampleDiff() };
        var watcher = new FakeGitRepositoryWatcher();
        var editor = new EditorAreaViewModel(git, new FakeUiLogService(),
            repositoryWatcher: watcher);

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false, "a".PadRight(40, '0')));
        var initialRequests = git.DiffRequests.Count;
        watcher.RaiseChangesDetected(
            changedPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src/A.cs" },
            indexChanged: true,
            headOrRefsChanged: true);

        await Task.Delay(100);
        Assert.Equal(initialRequests, git.DiffRequests.Count);
    }

    [Fact]
    public async Task DiffTab_HunkOperation_ReloadsAndRaisesMutationEvent()
    {
        var git = new FakeGitService { DiffResult = SampleDiff() };
        var tab = new DiffTab(git, new GitDiffRequest(@"C:\repo", "src/A.cs", false, false),
            new FakeUiLogService(), new FakeClipboardService());
        var completed = 0;
        tab.HunkMutationCompleted += (_, _) => completed++;

        await tab.LoadAsync();
        Assert.NotEmpty(tab.Hunks);
        Assert.True(await tab.ApplyHunkAsync(0, GitHunkOperation.Stage));

        var operation = Assert.Single(git.HunkOperations);
        Assert.Equal(GitHunkOperation.Stage, operation.Operation);
        Assert.Equal("src/A.cs", operation.Path);
        Assert.Equal(1, completed);
        Assert.True(tab.IsLoaded);
    }

    [Fact]
    public async Task DiffTab_RestoreHunk_ConfirmsOncePerTab()
    {
        var git = new FakeGitService { DiffResult = SampleDiff() };
        var confirmation = new FakeConfirmationService();
        var tab = new DiffTab(git, new GitDiffRequest(@"C:\repo", "src/A.cs", false, false),
            new FakeUiLogService(), new FakeClipboardService(), confirmation);

        await tab.LoadAsync();
        Assert.True(await tab.ApplyHunkAsync(0, GitHunkOperation.Restore));
        Assert.True(await tab.ApplyHunkAsync(0, GitHunkOperation.Restore));

        Assert.Equal(1, confirmation.Calls);
        Assert.Equal(2, git.HunkOperations.Count);
        Assert.All(confirmation.Requests, request => Assert.Contains("无法撤销", request.Message));
    }

    [Fact]
    public async Task EditorArea_ForwardsSuccessfulDiffHunkMutation()
    {
        var (editor, _) = Create();
        GitDiffRequest? raised = null;
        editor.DiffMutationCompleted += (_, request) => raised = request;

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false));
        var tab = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.True(await tab.ApplyHunkAsync(0, GitHunkOperation.Stage));

        Assert.NotNull(raised);
        Assert.Equal("src/A.cs", raised!.Path);
        Assert.False(raised.IsStaged);
    }

    [Fact]
    public async Task OpenDiffAsync_WithCommitHash_UsesCommitFileDiff()
    {
        var (editor, git) = Create();
        git.CommitFileDiff = SampleDiff();
        var hash = "b".PadRight(40, '0');

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "docs/readme.md", false, false, hash));

        var diff = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.Contains("提交 b000000", diff.DiffTitle); // "b".PadRight(40, '0') → short hash b000000
        Assert.Contains("docs/readme.md", diff.DiffTitle);
        Assert.Contains(diff.DiffLines, line => line.Kind == GitDiffLineKind.Added);
        Assert.Equal((hash, "docs/readme.md"), Assert.Single(git.CommitFileDiffRequests));
    }

    [Theory]
    [InlineData(false, false, null, DiffTabStatus.Modified, "M", "未暂存")]
    [InlineData(true, false, null, DiffTabStatus.Staged, "S", "已暂存")]
    [InlineData(false, true, null, DiffTabStatus.Untracked, "U", "未跟踪")]
    [InlineData(false, false, "a1234567890", DiffTabStatus.Commit, "C", "提交")]
    public async Task OpenDiffAsync_ExposesSemanticTabStatus(
        bool staged,
        bool untracked,
        string? commitHash,
        DiffTabStatus expectedStatus,
        string expectedMarker,
        string expectedSource)
    {
        var (editor, git) = Create();
        git.CommitFileDiff = SampleDiff();

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", staged, untracked, commitHash));

        var diff = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal(expectedStatus, diff.TabStatus);
        Assert.Equal(expectedMarker, diff.TabStatusMarker);
        Assert.Contains(expectedSource, diff.TabStatusToolTip);
    }

    [Fact]
    public async Task OpenSameTabKey_ReusesExistingTab()
    {
        var (editor, _) = Create();
        var request = new GitDiffRequest(@"C:\repo", "src/A.cs", false, false);

        await editor.OpenDiffAsync(request);
        await editor.OpenDiffAsync(request);

        Assert.Single(editor.OpenTabs);
    }

    [Fact]
    public void GitDiffRequest_TabKey_DistinguishesWorkingTreeSides()
    {
        // 暂存/未暂存/未跟踪是同一文件的三个不同文档:键必须区分侧别,否则跨侧重开会
        // 激活错误侧的旧标签,布局恢复也会因重复键丢掉其中一个。
        var unstaged = new GitDiffRequest(@"C:\repo", "src/A.cs", false, false);
        var staged = new GitDiffRequest(@"C:\repo", "src/A.cs", IsStaged: true, IsUntracked: false);
        var untracked = new GitDiffRequest(@"C:\repo", "src/A.cs", false, IsUntracked: true);
        var commit = new GitDiffRequest(@"C:\repo", "src/A.cs", false, false, "a".PadRight(40, '0'));

        Assert.Equal("diff:src/A.cs:w", unstaged.TabKey);
        Assert.Equal("diff:src/A.cs:s", staged.TabKey);
        Assert.Equal("diff:src/A.cs:u", untracked.TabKey);
        Assert.Equal($"diff:{commit.CommitHash}:src/A.cs", commit.TabKey);
        Assert.NotEqual(unstaged.TabKey, staged.TabKey);
        Assert.NotEqual(unstaged.TabKey, untracked.TabKey);
    }

    [Fact]
    public async Task OpenDiffAsync_SameFileOtherSide_CreatesSeparateTab()
    {
        var (editor, _) = Create();

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", IsStaged: false, IsUntracked: false));
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", IsStaged: true, IsUntracked: false));

        var tabs = editor.OpenTabs.OfType<DiffTab>().ToArray();
        Assert.Equal(2, tabs.Length);
        Assert.Contains(tabs, tab => tab.Request.IsStaged);
        Assert.Contains(tabs, tab => !tab.Request.IsStaged);
    }

    private static GitFileDiff EmptyDiff(string path) => new(path, null, false, false, false, []);

    [Fact]
    public async Task OpenDiffAsync_RequestedSideEmpty_FallsBackToOtherSide()
    {
        // 状态快照滞后(刷新未落地 / 请求构建后更改被外部暂存):请求的未暂存侧 git diff 为空,
        // 标签必须采用已暂存侧的内容,而不是显示"该文件没有可显示的差异"。
        var git = new FakeGitService
        {
            DiffResultBySide = new Dictionary<(bool Staged, bool Untracked), GitFileDiff?>
            {
                [(false, false)] = EmptyDiff("src/A.cs"),
                [(true, false)] = SampleDiff(),
            },
        };
        var editor = new EditorAreaViewModel(git, new FakeUiLogService());

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", IsStaged: false, IsUntracked: false));

        var tab = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.True(tab.IsLoaded);
        Assert.True(tab.HasDiff);
        Assert.Contains(tab.DiffLines, line => line.Kind == GitDiffLineKind.Added && line.Text == "added");
        Assert.Equal("已暂存", tab.SourceLabel);
        Assert.Equal(DiffTabStatus.Staged, tab.TabStatus);
        Assert.Equal("S", tab.TabStatusMarker);
    }

    [Fact]
    public async Task OpenDiffAsync_BothSidesEmpty_StaysOnRequestedSide()
    {
        var git = new FakeGitService
        {
            DiffResultBySide = new Dictionary<(bool Staged, bool Untracked), GitFileDiff?>
            {
                [(false, false)] = EmptyDiff("src/A.cs"),
                [(true, false)] = EmptyDiff("src/A.cs"),
            },
        };
        var editor = new EditorAreaViewModel(git, new FakeUiLogService());

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", IsStaged: false, IsUntracked: false));

        var tab = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.True(tab.IsLoaded);
        Assert.True(tab.IsEmptyDiff);
        Assert.Equal("未暂存", tab.SourceLabel);
        Assert.Equal(DiffTabStatus.Modified, tab.TabStatus);
    }

    [Fact]
    public async Task OpenDiffAsync_BinaryRequestedSide_DoesNotFallBackToOtherSide()
    {
        var binary = new GitFileDiff("src/A.cs", null, false, IsBinary: true, IsNewFile: false, []);
        var git = new FakeGitService
        {
            DiffResultBySide = new Dictionary<(bool Staged, bool Untracked), GitFileDiff?>
            {
                [(false, false)] = binary,
                [(true, false)] = SampleDiff(),
            },
        };
        var editor = new EditorAreaViewModel(git, new FakeUiLogService());

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", IsStaged: false, IsUntracked: false));

        var tab = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal("未暂存", tab.SourceLabel);
        Assert.Empty(tab.DiffLines);
        Assert.Single(git.DiffRequests);
    }

    [Fact]
    public async Task ApplyHunk_FollowsFallbackSide()
    {
        // 请求说未暂存、回退采用了已暂存侧内容:块操作必须作用到已暂存侧(git 调用 staged=true),
        // 且"暂存"操作对已暂存侧禁用、"取消暂存"可用。
        var git = new FakeGitService
        {
            DiffResultBySide = new Dictionary<(bool Staged, bool Untracked), GitFileDiff?>
            {
                [(false, false)] = EmptyDiff("src/A.cs"),
                [(true, false)] = SampleDiff(),
            },
        };
        var tab = new DiffTab(git, new GitDiffRequest(@"C:\repo", "src/A.cs", IsStaged: false, IsUntracked: false),
            new FakeUiLogService(), new FakeClipboardService());

        await tab.LoadAsync();
        Assert.True(tab.HasDiff);
        Assert.False(tab.CanApplyHunk(0, GitHunkOperation.Stage));
        Assert.True(tab.CanApplyHunk(0, GitHunkOperation.Unstage));
        Assert.True(await tab.ApplyHunkAsync(0, GitHunkOperation.Unstage));

        var operation = git.HunkOperations[^1];
        Assert.True(operation.Staged);
        Assert.Equal(GitHunkOperation.Unstage, operation.Operation);
    }

    [Fact]
    public async Task FileAndDiffTabs_CoexistIndependently()
    {
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(file, "hello");

        await editor.OpenFileAsync(file);
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false));

        Assert.Equal(2, editor.OpenTabs.Count);
        Assert.Contains(editor.OpenTabs, tab => tab is FilePreviewTab);
        Assert.Contains(editor.OpenTabs, tab => tab is DiffTab);
    }

    [Fact]
    public async Task MaxOpenTabs_EvictsOldest()
    {
        var (editor, _) = Create();

        for (var i = 1; i <= 31; i++)
        {
            await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", $"file{i}.cs", false, false));
        }

        Assert.Equal(30, editor.OpenTabs.Count);
        Assert.DoesNotContain(editor.OpenTabs, tab => tab.Name == "file1.cs");
        Assert.Contains(editor.OpenTabs, tab => tab.Name == "file31.cs");
    }

    [Fact]
    public async Task OpenDiffAsync_Preview_ReplacesSelectedFilePreview()
    {
        // 更改页单击的预览 diff 与文件预览共用同一预览槽:打开即驱逐当前选中的文件预览。
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(file, "a");

        await editor.OpenFileAsync(file);
        var filePreview = Assert.Single(editor.OpenTabs);
        Assert.True(filePreview.IsPreview);

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false, IsPreview: true));

        var diffPreview = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.True(diffPreview.IsPreview);
        Assert.Same(diffPreview, editor.SelectedTab);
    }

    [Fact]
    public async Task OpenDiffAsync_Preview_IsReplacedByNextFileOpen()
    {
        // 反向同样成立:预览 diff 之后打开的文件预览会驱逐它(每组至多一个预览)。
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "b.txt");
        await File.WriteAllTextAsync(file, "b");

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false, IsPreview: true));
        var diffPreview = Assert.Single(editor.OpenTabs);
        Assert.True(diffPreview.IsPreview);

        await editor.OpenFileAsync(file);

        var filePreview = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.Same(filePreview, editor.SelectedTab);
        Assert.NotSame(diffPreview, filePreview);
    }

    [Fact]
    public async Task OpenDiffAsync_NonPreview_DoesNotReplaceSelectedPreview()
    {
        // 非预览 diff(历史页/右键"打开 diff")不进预览槽:已有预览保留,两者并存。
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(file, "a");

        await editor.OpenFileAsync(file);
        var filePreview = Assert.Single(editor.OpenTabs);
        Assert.True(filePreview.IsPreview);

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false));

        Assert.Equal(2, editor.OpenTabs.Count);
        Assert.Same(filePreview, editor.OpenTabs.Single(tab => tab.Path == file));
        Assert.False(editor.SelectedTab!.IsPreview);
    }

    [Fact]
    public async Task CloseTab_RemovesAndMovesSelection()
    {
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "a.txt");
        var file2 = Path.Combine(_tempDir, "b.txt");
        await File.WriteAllTextAsync(file, "a");
        await File.WriteAllTextAsync(file2, "b");

        await editor.OpenFileAsync(file);
        editor.SelectedTab!.IsPreview = false; // 取消预览态:a 保留,避免被下一个预览替换
        await editor.OpenFileAsync(file2);
        var first = editor.OpenTabs[0];
        Assert.Equal(file, first.Path);

        editor.CloseTabCommand.Execute(first);

        Assert.Single(editor.OpenTabs);
        Assert.Equal(file2, editor.SelectedTab?.Path);
    }

    [Fact]
    public async Task CloseAllTabs_ClearsEditor()
    {
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(file, "a");

        await editor.OpenFileAsync(file);
        editor.CloseAllTabsCommand.Execute(null);

        Assert.Empty(editor.OpenTabs);
        Assert.Null(editor.SelectedTab);
        Assert.False(editor.HasSelectedTab);
    }

    [Fact]
    public async Task ToggleActiveDiffMode_FlipsOnlyDiffTab()
    {
        var (editor, _) = Create();
        var file = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(file, "a");
        await editor.OpenFileAsync(file);
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false));

        var diff = Assert.IsType<DiffTab>(editor.OpenTabs.First(tab => tab is DiffTab));
        Assert.True(diff.IsSideBySideDiff);

        editor.ToggleActiveDiffModeCommand.Execute(null);

        Assert.True(diff.IsInlineDiff);
        Assert.False(diff.IsSideBySideDiff);
    }

    // ===== Editor copy commands (复制选中 / 复制全部 diff, 复制标签路径) =====

    [Fact]
    public async Task CopySelectedDiffLines_PrefixesEachRowWithDiffMarker()
    {
        var (editor, clipboard) = CreateWithClipboard();
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false));
        var diff = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.False(diff.CopySelectedDiffLinesCommand.CanExecute(null));

        diff.SelectedDiffLines.Add(diff.DiffLines.First(line => line.Kind == GitDiffLineKind.Context));
        diff.SelectedDiffLines.Add(diff.DiffLines.First(line => line.Kind == GitDiffLineKind.Added));
        Assert.True(diff.CopySelectedDiffLinesCommand.CanExecute(null));

        diff.CopySelectedDiffLinesCommand.Execute(null);
        Assert.Equal($" keep{Environment.NewLine}+added", clipboard.LastText);
    }

    [Fact]
    public async Task CopyAllDiffLines_JoinsTheWholeDiff()
    {
        var (editor, clipboard) = CreateWithClipboard();
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false));
        var diff = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));

        diff.CopyAllDiffLinesCommand.Execute(null);
        Assert.Equal($" keep{Environment.NewLine}+added", clipboard.LastText);
    }

    [Fact]
    public async Task CopySelectedTabPath_UsesSelectedTabPath()
    {
        var (editor, clipboard) = CreateWithClipboard();
        Assert.False(editor.CopySelectedTabPathCommand.CanExecute(null)); // no tab yet

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "docs/readme.md", false, false));
        Assert.True(editor.CopySelectedTabPathCommand.CanExecute(null));

        editor.CopySelectedTabPathCommand.Execute(null);
        Assert.Equal("docs/readme.md", clipboard.LastText);
    }

    [Fact]
    public async Task CloseSelectedTab_DisabledWithoutSelection()
    {
        var (editor, _) = Create();
        Assert.False(editor.CloseSelectedTabCommand.CanExecute(null));

        var file = Path.Combine(_tempDir, "a.txt");
        await File.WriteAllTextAsync(file, "a");
        await editor.OpenFileAsync(file);
        Assert.True(editor.CloseSelectedTabCommand.CanExecute(null));

        editor.CloseSelectedTabCommand.Execute(null);
        Assert.False(editor.CloseSelectedTabCommand.CanExecute(null));
        Assert.Empty(editor.OpenTabs);
    }

    [Fact]
    public async Task GoToAdjacentTab_WrapsAroundTheStrip()
    {
        var (editor, _) = Create();
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "a.cs", false, false));
        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "b.cs", false, false));
        editor.SelectedTab = editor.OpenTabs[0];

        editor.GoToAdjacentTabCommand.Execute(1);
        Assert.Equal("b.cs", editor.SelectedTab?.Name);

        editor.GoToAdjacentTabCommand.Execute(1);
        Assert.Equal("a.cs", editor.SelectedTab?.Name);

        editor.GoToAdjacentTabCommand.Execute(-1);
        Assert.Equal("b.cs", editor.SelectedTab?.Name);
    }
}
