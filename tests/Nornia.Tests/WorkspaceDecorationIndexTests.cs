using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>G6/P4 证据:资源管理器装饰刷新的节点解析必须走「父节点名字索引」,
/// 成本只与路径深度有关,与目录里有多少兄弟条目无关。病态工作区(单目录数百~数千条目)
/// 里,旧的线性兄弟扫描会让每一次 git 状态刷新把 UI 线程拖住。</summary>
public sealed class WorkspaceDecorationIndexTests : IDisposable
{
    private const int WideSiblingCount = 150;
    private readonly string _narrowRoot = Path.Combine(Path.GetTempPath(), $"nornia-deco-narrow-{Guid.NewGuid():N}");
    private readonly string _wideRoot = Path.Combine(Path.GetTempPath(), $"nornia-deco-wide-{Guid.NewGuid():N}");

    public WorkspaceDecorationIndexTests()
    {
        // 两棵树的目标路径深度完全相同(只有兄弟数量不同):a/b/c/d/<file>。
        BuildTree(_narrowRoot, siblings: 1);
        BuildTree(_wideRoot, siblings: WideSiblingCount);
    }

    public void Dispose()
    {
        foreach (var root in new[] { _narrowRoot, _wideRoot })
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static string Combine(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void BuildTree(string root, int siblings)
    {
        foreach (var directory in new[] { "a", "a/b", "a/b/c", "a/b/c/d" })
        {
            Directory.CreateDirectory(Combine(root, directory));
        }

        foreach (var file in new[] { "a/b/c/d/target.txt", "a/b/c/d/other.txt" })
        {
            File.WriteAllText(Combine(root, file), "x");
        }

        for (var i = 0; i < siblings; i++)
        {
            File.WriteAllText(Combine(root, $"a/b/c/d/sibling{i:D4}.txt"), "x");
        }
    }

    private static async Task<WorkspaceViewModel> OpenAsync(string root)
    {
        var viewModel = new WorkspaceViewModel(
            new FakeFolderPicker(), new FakeSettingsService(), new FakeProjectWorkspaceService(),
            new FakeUiLogService(), new FakeClipboardService());
        await viewModel.OpenWorkspaceAsync(root);
        return viewModel;
    }

    private static WorkspaceNode? Find(WorkspaceNode node, string relativePath)
    {
        if (string.Equals(node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (var child in node.Children)
        {
            if (child.IsPlaceholder) continue;
            if (string.Equals(child.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)) return child;
            if (child.IsDirectory && Find(child, relativePath) is { } match) return match;
        }

        return null;
    }

    /// <summary>逐级加载 a→b→c→d,让每一层的名字索引都真正建立起来。</summary>
    private static async Task LoadChainAsync(WorkspaceNode root)
    {
        foreach (var directory in new[] { "a", "a/b", "a/b/c", "a/b/c/d" })
        {
            await (Find(root, directory) ?? throw new InvalidOperationException($"目录 {directory} 未找到"))
                .LoadChildrenAsync();
        }
    }

    private static GitRepositoryStatus Unstaged(params string[] paths) => new(
        IsRepository: true,
        Branch: "main",
        Upstream: null,
        AheadCount: 0,
        BehindCount: 0,
        StagedChanges: [],
        UnstagedChanges: paths
            .Select(path => new GitFileChange(path, GitChangeStatus.Unmodified, GitChangeStatus.Modified))
            .ToArray());

    /// <summary>量测一次「应用新状态」做了多少段名字查找(诊断计数器是累计的,取差值)。</summary>
    private static int Measure(WorkspaceViewModel viewModel, params string[] paths)
    {
        var before = viewModel.DecorationSegmentLookups;
        viewModel.ApplyGitStatus(Unstaged(paths));
        return viewModel.DecorationSegmentLookups - before;
    }

    /// <summary>装饰是否命中该节点:文件带状态字母,目录带"子孙有更改"的点。
    /// (RefreshDecoration 对文件只写字母、HasChange 恒为 false,别拿目录的判据去断言文件。)</summary>
    private static bool IsDecorated(WorkspaceNode node) =>
        node.IsDirectory ? node.HasChange : node.StatusLetter.Length > 0;

    [Fact]
    public async Task DecorationResolution_CostsDepthNotSiblings()
    {
        var narrow = await OpenAsync(_narrowRoot);
        var wide = await OpenAsync(_wideRoot);
        await LoadChainAsync(narrow.RootNodes[0]);
        await LoadChainAsync(wide.RootNodes[0]);

        // 先建立一次装饰(两侧都真的命中目标节点),再换一条同深度路径量测解析成本。
        Measure(narrow, "a/b/c/d/target.txt");
        wide.ApplyGitStatus(Unstaged("a/b/c/d/target.txt"));
        Assert.True(IsDecorated(Find(wide.RootNodes[0], "a/b/c/d/target.txt")!),
            "宽目录树的装饰没有命中目标节点:解析根本没走到深层文件,后面的相等断言会退化成空转。");

        var narrowDelta = Measure(narrow, "a/b/c/d/other.txt");
        var wideDelta = Measure(wide, "a/b/c/d/other.txt");
        Assert.True(IsDecorated(Find(wide.RootNodes[0], "a/b/c/d/other.txt")!));

        // 150 个兄弟条目不产生任何额外查找:与深度成比例(一条路径一段一次),且与窄树完全相同。
        Assert.Equal(narrowDelta, wideDelta);
        Assert.True(wideDelta < WideSiblingCount,
            $"同深度路径在宽目录里花了 {wideDelta} 次查找,解析又变回了兄弟扫描。");
    }

    [Fact]
    public async Task ChildIndex_ResolvesFoldersLoadedAfterFirstStatus()
    {
        // 索引必须在懒加载补上子节点之后仍然可用:先有状态、后展开,是打开工作区的真实顺序。
        var viewModel = await OpenAsync(_wideRoot);
        var root = viewModel.RootNodes[0];
        viewModel.ApplyGitStatus(Unstaged("a/b/c/d/target.txt")); // 深层尚未加载 → 只能命中前缀

        await LoadChainAsync(root);
        var lookups = Measure(viewModel, "a/b/c/d/other.txt");

        Assert.True(IsDecorated(Find(root, "a/b/c/d/other.txt")!), "补加载之后的节点没被名字索引解析到。");
        Assert.True(lookups < WideSiblingCount, $"解析成本 {lookups} 次,已随兄弟数量增长。");
    }

    [Fact]
    public async Task ChildIndex_StaysInSyncWithIncrementalReconcile()
    {
        var viewModel = await OpenAsync(_narrowRoot);
        var root = viewModel.RootNodes[0];
        await LoadChainAsync(root);
        var directoryD = Find(root, "a/b/c/d")!;

        // 新增:磁盘上的新文件经增量同步进入 Children 后,必须立刻可被路径解析。
        File.WriteAllText(Path.Combine(directoryD.Path, "brandnew.txt"), "x");
        await directoryD.ReconcileChildrenAsync();
        Measure(viewModel, "a/b/c/d/brandnew.txt");
        var brandNew = Find(root, "a/b/c/d/brandnew.txt");
        Assert.NotNull(brandNew);
        Assert.True(IsDecorated(brandNew!), "增量同步新增的子节点没有进入名字索引。");

        // 删除:被移除的节点必须同时退出索引。索引若还指向那个已脱离树的节点,同名文件重建后
        // 路径解析会把装饰写给幽灵节点,界面上那个真正的新行永远不带徽章。
        File.Delete(Path.Combine(directoryD.Path, "brandnew.txt"));
        await directoryD.ReconcileChildrenAsync();
        Assert.DoesNotContain(directoryD.Children, child => child.Name == "brandnew.txt");
        Assert.Null(directoryD.FindChild("brandnew.txt"));

        File.WriteAllText(Path.Combine(directoryD.Path, "brandnew.txt"), "x");
        await directoryD.ReconcileChildrenAsync();
        var revived = directoryD.FindChild("brandnew.txt");
        Assert.NotNull(revived);
        Assert.NotSame(brandNew!, revived);
        Measure(viewModel, "a/b/c/d/brandnew.txt");
        Assert.Equal("M", revived!.StatusLetter);
    }
}
