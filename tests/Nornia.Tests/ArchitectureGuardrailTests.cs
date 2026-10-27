using System.Text;
using System.Text.RegularExpressions;

namespace Nornia.Tests;

/// <summary>
/// 架构约束守卫(纯源码文本断言,不需要 WPF/STA,也不依赖运行时行为)。
///
/// 这些约束此前只写在 <c>AGENTS.md</c> 里,结果是「文档说有、代码里已经破了几处」——
/// 例如 ViewModel 直接读 <c>Application.Current.Dispatcher</c>(还用完全限定名绕过了 grep)、
/// 版本号用字典序取最新(9 → 10 跨版本即错)。文档拦不住新代码,测试可以。
///
/// 断言的是"不得再出现"的结构性事实,而不是某段实现细节,因此不会因为重构而误报;
/// 若确需放行,必须在下面的白名单里显式登记并写明理由。
/// </summary>
public sealed class ArchitectureGuardrailTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    /// <summary>ViewModel 层禁止直接触达的 WPF 调度器成员。前缀断言用 <c>(?&lt;![\w.])</c> 做词首限制,
    /// 否则 <c>DispatcherPriority</c> 会命中自家的 <c>UiDispatcherPriority</c> 而误报。</summary>
    private static readonly string[] BannedDispatcherTokens =
    [
        @"(?<![\w.])Application\.Current",
        @"(?<![\w.])Dispatcher\.CurrentDispatcher",
        @"(?<![\w.])DispatcherTimer",
        @"(?<![\w.])DispatcherPriority",
    ];

    /// <summary>已登记豁免(相对仓库根的文件路径 → 理由)。当前为空:VM 层已全部改走
    /// <c>IUiDispatcher</c>/<c>IUiTimer</c>。新增条目会被下面的用例显式校验,
    /// 留下"必须解释为什么"的门槛。</summary>
    private static readonly Dictionary<string, string> DispatcherAllowList = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ViewModels_ReachTheUiThreadOnlyThroughIUiDispatcher()
    {
        var directory = Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels");
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
            if (DispatcherAllowList.ContainsKey(relative))
            {
                continue;
            }

            var code = StripComments(File.ReadAllText(path));
            foreach (var token in BannedDispatcherTokens)
            {
                if (Regex.IsMatch(code, token))
                {
                    offenders.Add($"{relative}: {token}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "ViewModel 必须经 IUiDispatcher/IUiTimer 回到 UI 线程,禁止直接触达 WPF 调度器。违规:\n  "
            + string.Join("\n  ", offenders)
            + "\n(确需豁免时在 ArchitectureGuardrailTests.DispatcherAllowList 登记并写明理由。)");
    }

    [Fact]
    public void VersionSelection_NeverOrdersVersionStringsLexically()
    {
        // 版本号是结构化数据: "9.0.1" 字典序大于 "10.0.0",用字符串序取最新会选错实例。
        // 统一入口是 Nornia.Core.Models.VersionOrder(Comparer / MaxByVersion)。
        var offenders = new List<string>();

        foreach (var path in EnumerateProductionSources())
        {
            var relative = Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
            if (relative.EndsWith("VersionOrder.cs", StringComparison.Ordinal))
            {
                continue; // 比较器自身的实现
            }

            var lines = StripComments(File.ReadAllText(path)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("OrderBy", StringComparison.Ordinal))
                {
                    continue;
                }

                // 只关心"排序键是版本、且没有显式给出版本序比较器"的写法。
                if (!line.Contains(".Version", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains("VersionOrder.Comparer", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{relative}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "版本排序必须显式传 VersionOrder.Comparer(否则用的是字典序)。违规:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void SidebarViewsAreLocatedByDataTemplate_NotByTypeSwitchesInTheWindow()
    {
        // 视图定位必须由 App.xaml 的隐式 DataTemplate 承担:新增一个带侧栏的页面 = 加一个模板
        // (1 处改动),而不是回去改窗口里的类型判断链(此前是 3 处)。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var window = StripComments(File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml.cs")));

        var sidebarPages = new (string PageViewModel, string View)[]
        {
            ("EnvironmentManagementViewModel", "EnvironmentSidebarView"),
            ("ExplorerPageViewModel", "ExplorerSidebarView"),
            ("SearchViewModel", "SearchSidebarView"),
            ("GitViewModel", "GitView"),
            ("ProjectsViewModel", "ProjectsSidebarView"),
            ("SettingsEditorViewModel", "SettingsSidebarView"),
        };

        foreach (var (pageViewModel, view) in sidebarPages)
        {
            Assert.Contains($"<DataTemplate DataType=\"{{x:Type vm:{pageViewModel}}}\">", app);
            Assert.Contains($"<views:{view} />", app);

            // 窗口 code-behind 不得再持有"页 VM 类型 → 视图类型"的映射。
            Assert.DoesNotContain($"page is {pageViewModel}", window);
            Assert.DoesNotContain($"new {view}", window);
            Assert.DoesNotContain($"Build<{view}>", window);
        }
    }

    [Fact]
    public void RefreshActiveView_IsAPageResponsibilityNotAWindowTypeSwitch()
    {
        // Ctrl+R / 命令面板的"刷新当前页"曾经是窗口里对 4 个具体 VM 类型的 switch。
        var window = StripComments(File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml.cs")));

        Assert.Contains("RefreshActiveViewAsync()", window);
        Assert.DoesNotContain("ScanCommand.ExecuteAsync(null)", window);
        Assert.DoesNotContain("CheckCommand.ExecuteAsync(null)", window);
    }

    [Fact]
    public void ProductionSources_AreActuallyEnumerated()
    {
        // 防止上面的守卫因为"目录/文件名改了"而静默变成空转(这正是这类文本守卫最容易退化的方式)。
        var sources = EnumerateProductionSources().ToArray();
        Assert.True(sources.Length > 100, $"只枚举到 {sources.Length} 个生产源码文件,守卫可能已空转。");

        var viewModels = Directory.EnumerateFiles(
            Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels"), "*.cs", SearchOption.AllDirectories).ToArray();
        Assert.True(viewModels.Length > 10, $"只枚举到 {viewModels.Length} 个 ViewModel 文件,守卫可能已空转。");
    }

    /// <summary>资源管理器按路径解析节点必须走名字索引,不得退回线性兄弟扫描。
    /// 一次 git 状态刷新要解析"每个变化路径 + 其全部祖先",线性扫描的成本是
    /// O(路径数 × 深度 × 兄弟数)——那正是大仓库里更改栏卡顿连带资源管理器卡顿的来源。</summary>
    [Fact]
    public void ExplorerPathResolution_UsesChildIndex_NotLinearSiblingScan()
    {
        var path = Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/WorkspaceViewModel.cs");
        var code = StripComments(File.ReadAllText(path));
        Assert.True(code.Length > 20_000, $"读到的 WorkspaceViewModel 只有 {code.Length} 字符,守卫可能已空转。");

        var offenders = new List<string>();
        if (code.Contains("Children.FirstOrDefault(", StringComparison.Ordinal)) offenders.Add("Children.FirstOrDefault(");
        if (code.Contains("string.Equals(child.Name", StringComparison.Ordinal)) offenders.Add("string.Equals(child.Name");
        Assert.True(offenders.Count == 0,
            "按路径解析子节点必须用 WorkspaceNode.FindChild(名字索引),不得线性扫描兄弟。违规:\n  "
            + string.Join("\n  ", offenders));

        // 同一守卫的另一半:索引确实被使用(而不是删掉了扫描也没接索引)。
        Assert.True(code.Contains("FindChild(", StringComparison.Ordinal),
            "WorkspaceNode.FindChild 未被调用:索引被删除后守卫会空转。");
    }

    /// <summary>SCM 树行的层级导线必须按深度共享,不得每行分配数组:每轮刷新上万次小数组分配
    /// 之外,数组按引用参与 record 等值比较,会让"无变化"的发布永远判成有变化(整表 Reset)。</summary>
    [Fact]
    public void ScmTreeRows_ShareGuideLinesByDepth_NotPerRowArrays()
    {
        var path = Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/GitViewModel.cs");
        var code = StripComments(File.ReadAllText(path));
        Assert.True(code.Length > 50_000, $"读到的 GitViewModel 只有 {code.Length} 字符,守卫可能已空转。");

        // 「= 」带尾空格:表达式体写法 `AncestorGuideLefts => GuideLeftsFor(Depth)` 里的 "=>"
        // 不能被误判成「逐行赋值」。回归形态是重新变成可写属性并在行构造里赋值。
        Assert.False(code.Contains("AncestorGuideLefts = ", StringComparison.Ordinal),
            "SCM 树行不得再逐行赋值导线数组(必须按深度共享)。");
        Assert.True(code.Contains("AncestorGuideLefts => GuideLeftsFor(Depth)", StringComparison.Ordinal),
            "SCM 树行的导线未按深度共享,守卫可能已空转。");
    }

    /// <summary>git 状态读取的整条链路(进程输出 → 解析 → 发布)不得把续延排回 UI 上下文:
    /// 每个 await 都要 ConfigureAwait(false),进程启动要落到线程池。几十 MB porcelain 输出的
    /// 解析回到主线程,就是"打开工作区整个界面一起卡"的直接原因。</summary>
    [Fact]
    public void GitStatusPipeline_DoesNotMarshallParsingBackToTheUiContext()
    {
        var servicePath = Path.Combine(RepoRoot, "src/Nornia.Git/GitService.cs");
        var serviceCode = StripComments(File.ReadAllText(servicePath));
        var statusCore = ExtractMethodBody(serviceCode, "private async Task<GitRepositoryStatus> GetStatusCoreAsync");
        Assert.True(statusCore.Contains("ConfigureAwait(false)", StringComparison.Ordinal),
            "GetStatusCoreAsync 必须 ConfigureAwait(false),否则解析在 UI 线程续延。");
        Assert.True(statusCore.Contains("RunGitBoundedAsync", StringComparison.Ordinal),
            "GetStatusCoreAsync 应经由统一的有界 git 进程入口,守卫可能已空转。");

        // 光有 ConfigureAwait(false) 不够: uncontended 的闸门 await 与 Process.Start 都会同步跑在
        // 调用方线程上,所以子进程必须由 Task.Run 启动。
        var bounded = ExtractMethodBody(serviceCode, "private Task<ProcessResult> RunGitBoundedAsync");
        Assert.True(bounded.Contains("Task.Run", StringComparison.Ordinal),
            "有界 git 读取必须在线程池上启动子进程,否则 UI 线程仍要承担 Process.Start。");
        Assert.True(bounded.Contains("stopAtOutputLimit: true", StringComparison.Ordinal),
            "RunGitBoundedAsync 不再是「命中上限即终止子进程」的读取,守卫可能已空转。");

        var runnerPath = Path.Combine(RepoRoot, "src/Nornia.Core/Services/ProcessRunner.cs");
        var runnerCode = StripComments(File.ReadAllText(runnerPath));
        var runCore = ExtractMethodBody(runnerCode, "private async Task<ProcessResult> RunAsyncCore");
        Assert.True(runCore.Contains("ConfigureAwait(false)", StringComparison.Ordinal),
            "ProcessRunner.RunAsyncCore 必须 ConfigureAwait(false)。");
        Assert.True(runCore.Contains("WaitForExitAsync", StringComparison.Ordinal),
            "RunAsyncCore 结构已变,请同步更新本守卫。");
    }

    /// <summary>取出从某个方法签名开始到下一个成员声明为止的源码片段,用于做局部文本断言。
    /// (不能截到"下一个空行"——方法体里就有空行。)</summary>
    private static string ExtractMethodBody(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到方法 {signature},守卫可能已空转。");

        var end = code.Length;
        foreach (var boundary in new[] { "\n    private ", "\n    public ", "\n    internal ", "\n    /// <summary>", "\n}" })
        {
            var index = code.IndexOf(boundary, start + signature.Length, StringComparison.Ordinal);
            if (index >= 0 && index < end)
            {
                end = index;
            }
        }

        var body = code[start..end];
        Assert.True(body.Length > 200, $"{signature} 只截到 {body.Length} 字符,守卫可能已空转。");
        return body;
    }

    private static IEnumerable<string> EnumerateProductionSources() =>        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>去掉注释后再做文本断言:实现注释里经常成段解释"为什么不用 Application.Current",
    /// 那些说明不应该被判成违规。行尾注释只认"前面是空白"的 <c>//</c>,避免砍掉字符串里的
    /// <c>http://</c>。</summary>
    private static string StripComments(string source)
    {
        var builder = new StringBuilder(source.Length);
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
            {
                builder.Append('\n');
                continue;
            }

            var index = line.IndexOf("//", StringComparison.Ordinal);
            while (index > 0 && !char.IsWhiteSpace(line[index - 1]))
            {
                index = line.IndexOf("//", index + 2, StringComparison.Ordinal);
            }

            builder.Append(index > 0 ? line[..index] : line).Append('\n');
        }

        return builder.ToString();
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nornia.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到 Nornia.sln:无法定位仓库根,架构守卫无法运行。");
    }
}
