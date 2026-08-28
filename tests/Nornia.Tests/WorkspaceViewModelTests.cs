using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Covers the VS Code-style git decorations on the explorer tree (status letters on files,
/// changed dots on folders) and reveal-in-explorer from the source-control view.</summary>
public sealed class WorkspaceViewModelTests : IDisposable
{
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), $"nornia-workspace-{Guid.NewGuid():N}");

    public WorkspaceViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspacePath, "src"));
        Directory.CreateDirectory(Path.Combine(_workspacePath, "notes"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp workspace.
        }
    }

    private static GitRepositoryStatus RepoStatus() => new(
        IsRepository: true,
        Branch: "main",
        Upstream: "origin/main",
        AheadCount: 0,
        BehindCount: 0,
        StagedChanges: [new GitFileChange("src/B.cs", GitChangeStatus.Added, GitChangeStatus.Unmodified)],
        UnstagedChanges:
        [
            new GitFileChange("src/A.cs", GitChangeStatus.Unmodified, GitChangeStatus.Modified),
            new GitFileChange("notes/todo.txt", GitChangeStatus.Unmodified, GitChangeStatus.Untracked)
        ]);

    private async Task<WorkspaceViewModel> CreateAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspacePath, "src", "A.cs"), "a");
        await File.WriteAllTextAsync(Path.Combine(_workspacePath, "src", "B.cs"), "b");
        await File.WriteAllTextAsync(Path.Combine(_workspacePath, "notes", "todo.txt"), "todo");
        var viewModel = new WorkspaceViewModel(new FakeFolderPicker(), new FakeSettingsService(), new FakeProjectWorkspaceService(), new FakeUiLogService(), new FakeClipboardService());
        await viewModel.OpenWorkspaceAsync(_workspacePath);
        return viewModel;
    }

    private static WorkspaceNode? FindNode(WorkspaceNode root, string relativePath)
    {
        if (string.Equals(root.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var match = FindNode(child, relativePath);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    [Fact]
    public async Task ApplyGitStatus_SetsFileLettersAndFolderDots()
    {
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        var src = root.Children.First(child => child.Name == "src");
        await src.LoadChildrenAsync();
        var notes = root.Children.First(child => child.Name == "notes");
        await notes.LoadChildrenAsync();

        viewModel.ApplyGitStatus(RepoStatus());

        Assert.Equal("M", FindNode(root, "src/A.cs")?.StatusLetter);
        Assert.Equal("A", FindNode(root, "src/B.cs")?.StatusLetter);
        Assert.Equal("A", FindNode(root, "notes/todo.txt")?.StatusLetter); // 未跟踪文件与更改树一致:渲染为 "A"
        Assert.Equal("M", FindNode(root, "src")?.StatusLetter); // M outranks staged A in the folder
        Assert.Equal("M", FindNode(root, string.Empty)?.StatusLetter); // M outranks untracked A at root
        Assert.True(FindNode(root, "src")?.HasChange);
        Assert.True(FindNode(root, "notes")?.HasChange);
        Assert.True(FindNode(root, string.Empty)?.HasChange); // workspace root is dirty
    }

    [Fact]
    public async Task ApplyGitStatus_NotARepository_ClearsDecorations()
    {
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        var src = root.Children.First(child => child.Name == "src");
        await src.LoadChildrenAsync();

        viewModel.ApplyGitStatus(RepoStatus());
        Assert.Equal("M", FindNode(root, "src/A.cs")?.StatusLetter);

        viewModel.ApplyGitStatus(GitRepositoryStatus.NotARepository);

        Assert.Equal(string.Empty, FindNode(root, "src/A.cs")?.StatusLetter);
        Assert.False(FindNode(root, "src")?.HasChange);
    }

    [Fact]
    public async Task NodesLoadedAfterRefresh_PickUpCurrentDecorations()
    {
        var viewModel = await CreateAsync();
        viewModel.ApplyGitStatus(RepoStatus());

        // Nodes created after the refresh still resolve the current status map.
        var src = viewModel.RootNodes[0].Children.First(child => child.Name == "src");
        await src.LoadChildrenAsync();

        Assert.Equal("M", FindNode(viewModel.RootNodes[0], "src/A.cs")?.StatusLetter);
    }

    [Fact]
    public async Task GetGitInfo_ReportsStagedAndUntrackedFlags()
    {
        var viewModel = await CreateAsync();
        viewModel.ApplyGitStatus(RepoStatus());

        var staged = viewModel.GetGitInfo("src/B.cs");
        Assert.NotNull(staged);
        Assert.Equal('A', staged.Value.Letter);
        Assert.True(staged.Value.IsStaged);

        var untracked = viewModel.GetGitInfo("notes/todo.txt");
        Assert.NotNull(untracked);
        Assert.Equal('A', untracked.Value.Letter); // 字母渲染为 "A"(与更改树一致);差异请求仍由 IsUntracked 标志驱动
        Assert.True(untracked.Value.IsUntracked);

        Assert.Null(viewModel.GetGitInfo("src/unknown.cs"));
    }

    [Fact]
    public async Task RevealNodeAsync_ExpandsAncestorsAndSelectsNode()
    {
        var viewModel = await CreateAsync();

        var revealed = await viewModel.RevealNodeAsync("src/A.cs");

        Assert.True(revealed);
        var src = viewModel.RootNodes[0].Children.First(child => child.Name == "src");
        Assert.True(src.IsExpanded);
        var node = src.Children.First(child => child.Name == "A.cs");
        Assert.True(node.IsSelected);
    }

    [Fact]
    public async Task RevealNodeAsync_MissingPath_ReturnsFalse()
    {
        var viewModel = await CreateAsync();

        Assert.False(await viewModel.RevealNodeAsync("src/does-not-exist.cs"));
        Assert.False(await viewModel.RevealNodeAsync(string.Empty));
    }

    [Fact]
    public async Task OpenNode_OnDirectory_DoesNotOpenFileAndChildrenLoadOnExpansion()
    {
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        var src = root.Children.First(child => child.Name == "src");
        Assert.False(src.IsExpanded);
        Assert.False(src.IsLoaded);

        // VS Code explorer behavior: selecting a folder row does not open anything; expanding it
        // (row click or chevron) is what loads children.
        viewModel.OpenNodeCommand.Execute(src);

        Assert.False(src.IsLoaded);
        Assert.False(src.IsExpanded);

        src.IsExpanded = true;
        // 展开触发是 fire-and-forget,枚举已改异步:测试加入进行中的同一次加载。
        await src.LoadChildrenAsync();

        Assert.True(src.IsLoaded);
        Assert.Contains(src.Children, child => child.Name == "A.cs");
    }

    [Fact]
    public async Task OpenTreeFilePermanentCommand_RaisesPermanentOpenEvent_NotPreviewOpen()
    {
        // VS Code:单击开预览,双击开常驻标签。
        var viewModel = await CreateAsync();
        var src = viewModel.RootNodes[0].Children.First(child => child.Name == "src");
        await src.LoadChildrenAsync();
        var node = src.Children.First(child => child.Name == "A.cs");

        var permanent = new List<string>();
        var preview = new List<string>();
        viewModel.FileOpenPermanentRequested += (_, path) => permanent.Add(path);
        viewModel.FileOpenRequested += (_, path) => preview.Add(path);

        viewModel.OpenTreeFilePermanentCommand.Execute(new WorkspaceFileRow(node, depth: 1));

        Assert.Equal([Path.Combine(_workspacePath, "src", "A.cs")], permanent);
        Assert.Empty(preview);
    }

    // ===== V6/G9: 装饰差集定向刷新 =====

    private sealed class ChangeCounter
    {
        public int Count;
    }

    private static ChangeCounter CountNodePropertyChanges(WorkspaceNode node, string propertyName)
    {
        var counter = new ChangeCounter();
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == propertyName) counter.Count++;
        };
        return counter;
    }

    [Fact]
    public async Task ApplyGitStatus_UnchangedMap_SkipsDecorationRefreshEntirely()
    {
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        var src = root.Children.First(child => child.Name == "src");
        await src.LoadChildrenAsync();
        var aNode = FindNode(root, "src/A.cs")!;
        var observed = CountNodePropertyChanges(aNode, nameof(WorkspaceNode.StatusLetter));

        viewModel.ApplyGitStatus(RepoStatus());
        Assert.True(observed.Count > 0, "首次应用应刷新装饰");

        var eventsAfterFirst = observed.Count;
        viewModel.ApplyGitStatus(RepoStatus()); // 相同的状态 map(静默刷新场景)

        Assert.Equal(eventsAfterFirst, observed.Count); // 整轮跳过:节点不再收到刷新通知
    }

    [Fact]
    public async Task ApplyGitStatus_ChangedPath_RefreshesAffectedAndAncestorsOnly()
    {
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        var src = root.Children.First(child => child.Name == "src");
        await src.LoadChildrenAsync();
        var notes = root.Children.First(child => child.Name == "notes");
        await notes.LoadChildrenAsync();
        var aNode = FindNode(root, "src/A.cs")!;
        var bNode = FindNode(root, "src/B.cs")!;
        var srcNode = FindNode(root, "src")!;
        var todoNode = FindNode(root, "notes/todo.txt")!;

        // 第一次应用:全部路径都受影响。
        viewModel.ApplyGitStatus(RepoStatus());

        // 第二次:只有 src/B.cs 的字母变化 → 只有 B.cs(及祖先链)需要重刷,
        // A.cs 与 notes/todo.txt 的字母保持正确且不被再次通知。
        var aEvents = CountNodePropertyChanges(aNode, nameof(WorkspaceNode.StatusLetter));
        var bEvents = CountNodePropertyChanges(bNode, nameof(WorkspaceNode.StatusLetter));
        var todoEvents = CountNodePropertyChanges(todoNode, nameof(WorkspaceNode.StatusLetter));
        var srcEvents = CountNodePropertyChanges(srcNode, nameof(WorkspaceNode.StatusLetter));
        var changed = RepoStatus() with
        {
            StagedChanges = [new GitFileChange("src/B.cs", GitChangeStatus.Modified, GitChangeStatus.Unmodified)],
        };
        viewModel.ApplyGitStatus(changed);

        Assert.Equal("M", bNode.StatusLetter); // 受影响路径已刷新(GitChangeStatus.Modified → 'M')
        Assert.Equal("M", srcNode.StatusLetter); // 祖先链(src 目录点)保持正确(M 仍压倒其它状态)
        Assert.Equal("M", aNode.StatusLetter); // 未受影响路径的字母仍然正确(只是未被再次通知)
        Assert.Equal("A", todoNode.StatusLetter);
        Assert.Equal(0, aEvents.Count);
        Assert.Equal(0, todoEvents.Count);
        Assert.True(bEvents.Count >= 1);
        // src 目录两次应用的最高优先级字母都是 'M' → 值未变,不产生事件(正确);其正确性
        // 由上面的字母断言覆盖。祖先链刷新在路径"移除"(如 NotARepository)场景触发。
        Assert.Equal(0, srcEvents.Count);
    }

    [Fact]
    public async Task ApplyGitStatus_AfterWorkspaceReopen_RepaintsEvenWhenMapIsUnchanged()
    {
        var viewModel = await CreateAsync();
        var root = viewModel.RootNodes[0];
        var src = root.Children.First(child => child.Name == "src");
        await src.LoadChildrenAsync();

        viewModel.ApplyGitStatus(RepoStatus());
        Assert.Equal("M", FindNode(root, "src/A.cs")?.StatusLetter);
        Assert.Equal("M", src.StatusLetter);

        // 重新打开同一工作区:树整体重建、状态源清空,但 git 随后发布的状态 map 与上次
        // 完全相同。旧实现因 map 相等整轮跳过,新树节点永远缺装饰;修复后必须全量重刷。
        await viewModel.OpenWorkspaceAsync(_workspacePath);
        var newRoot = viewModel.RootNodes[0];
        var newSrc = newRoot.Children.First(child => child.Name == "src");
        Assert.Equal(string.Empty, newSrc.StatusLetter); // 重开后源已清空,旧装饰不存活
        viewModel.ApplyGitStatus(RepoStatus());

        Assert.Equal("M", newSrc.StatusLetter); // 已加载节点按相同 map 重新上色
        await newSrc.LoadChildrenAsync();
        Assert.Equal("M", FindNode(newRoot, "src/A.cs")?.StatusLetter); // 懒加载节点取当前 map
        Assert.Equal("M", FindNode(newRoot, string.Empty)?.StatusLetter);
    }
}
