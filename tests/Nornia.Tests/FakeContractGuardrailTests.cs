using System.Reflection;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

/// <summary>接口契约护栏。测试项目用 30+ 个手写替身替代 55 个测试文件里的真实实现 ——
/// 这是有意的选择(不启动真实进程/WPF 窗口),但它有一个隐性风险:
/// **接口新增带默认实现(DIM)的成员时,任何手写替身都不会编译报错**,测试会静默跑在默认实现上。
/// 本项目对 `IProcessRunner`/`IGitService` 的流式方法**刻意**用了 DIM(由既有非流式方法回放,
/// 以免新增流式能力破坏既有 Fake,见 docs/design.md §13.6);因此正确的护栏不是"禁止 DIM",
/// 而是"默认成员的集合必须恰好等于已登记的那一份" —— 新增 DIM 必须是有意的决定,
/// 并在此处补一行带理由的登记。</summary>
public sealed class FakeContractGuardrailTests
{
    /// <summary>已登记的默认接口成员（<c>接口.成员</c>）。新增条目必须说明为什么该成员适合提供默认实现。</summary>
    private static readonly string[] ExpectedDefaultInterfaceMembers =
    [
        // IDesktopNavigationService：字符串上下文重载转发到结构化重载，纯语法糖。
        "IDesktopNavigationService.Navigate",
        // IGitService：流式 diff / 提交 diff / 部分读取 / 初始化。默认实现由既有非流式方法回放，
        // 使新增流式能力不破坏既有手写 Fake（IGitService 的 Fake 多达数个）。
        "IGitService.GetDiffRevisionWithBlobsAsync",
        // IGitService.GetStatusAsync(int maximumEntries,...):条目上限重载由无上限重载回放。默认实现
        // 会忽略上限,所以任何驱动降级 UI 的 Fake 必须显式覆写它(FakeGitService 已覆写并记录上限,
        // 由 ScmLargeChangesDegradationTests 断言上限真的传到服务)。
        "IGitService.GetStatusAsync",
        "IGitService.InitializeRepositoryAsync",
        "IGitService.StreamCommitFileDiffAsync",
        "IGitService.StreamDiffAsync",
        // IProcessRunner：流式与原始字节输出的默认实现，同样由 RunAsync 结果回放。
        // RunBoundedAsync 的默认体保留上限但不终止子进程(对替身等价:替身本就没有真实管道)。
        "IProcessRunner.RunBoundedAsync",
        "IProcessRunner.RunRawAsync",
        "IProcessRunner.RunShellAsync",
        "IProcessRunner.StreamLinesAsync",
        "IProcessRunner.StreamLinesWithTextFallbackAsync",
        // IUiDispatcher：带优先级的重载转发到无优先级重载。优先级只是调度提示，
        // 简单实现（含各测试替身）忽略它是正确行为，因此适合默认实现而非强制每个替身实现一遍。
        "IUiDispatcher.BeginInvoke",
        "IUiDispatcher.InvokeAsync",
    ];

    [Fact]
    public void DefaultInterfaceMembers_AreExactlyTheRegisteredSet()
    {
        // 同一程序集可能经不同路径被枚举两次,按成员名去重(集合语义才是这里的断言对象)。
        var actual = CollectDefaultInterfaceMembers()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expected = ExpectedDefaultInterfaceMembers.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.True(
            actual.SequenceEqual(expected),
            "接口默认实现集合发生了变化。新增的默认成员不会让任何手写替身编译失败,请确认这是有意的,"
            + "并在 FakeContractGuardrailTests.ExpectedDefaultInterfaceMembers 里补充登记与理由。"
            + Environment.NewLine + "实际: " + string.Join(", ", actual)
            + Environment.NewLine + "登记: " + string.Join(", ", expected));
    }

    [Fact]
    public void GuardrailItself_ActuallySeesTheFakes()
    {
        // 防止采集器退化成空转:测试项目里必须真的存在实现了 Nornia 接口的替身类型。
        var testAssembly = typeof(FakeContractGuardrailTests).Assembly;
        var norniaInterfaces = LoadNorniaAssemblies(testAssembly)
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsInterface)
            .ToHashSet();

        var implementations = testAssembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface && !type.IsGenericTypeDefinition)
            .Count(type => type.GetInterfaces().Any(norniaInterfaces.Contains));

        Assert.True(implementations > 20, $"只找到 {implementations} 个替身,护栏可能已经失效。");
    }

    /// <summary>反射采集所有 Nornia 接口中**带方法体**的成员（方法/属性访问器/事件访问器）。</summary>
    private static IEnumerable<string> CollectDefaultInterfaceMembers()
    {
        var testAssembly = typeof(FakeContractGuardrailTests).Assembly;
        foreach (var contract in LoadNorniaAssemblies(testAssembly)
                     .Distinct()
                     .SelectMany(assembly => assembly.GetTypes())
                     .Where(type => type.IsInterface))
        {
            foreach (var method in contract.GetMethods())
            {
                // 属性/事件访问器由下面的属性/事件枚举覆盖,避免重复登记。
                if (method.IsSpecialName)
                {
                    continue;
                }

                if (!method.IsAbstract)
                {
                    yield return $"{contract.Name}.{method.Name}";
                }
            }

            foreach (var property in contract.GetProperties())
            {
                var accessors = new[] { property.GetMethod, property.SetMethod };
                if (accessors.Any(accessor => accessor is { IsAbstract: false }))
                {
                    yield return $"{contract.Name}.{property.Name}";
                }
            }

            foreach (var @event in contract.GetEvents())
            {
                var accessors = new[] { @event.AddMethod, @event.RemoveMethod };
                if (accessors.Any(accessor => accessor is { IsAbstract: false }))
                {
                    yield return $"{contract.Name}.{@event.Name}";
                }
            }
        }
    }

    private static IEnumerable<Assembly> LoadNorniaAssemblies(Assembly testAssembly)
    {
        yield return testAssembly;
        yield return typeof(CoreRuntime).Assembly;
        foreach (var name in testAssembly.GetReferencedAssemblies()
                     .Where(reference => reference.Name?.StartsWith("Nornia.", StringComparison.Ordinal) == true)
                     .Select(reference => reference.Name!))
        {
            Assembly? loaded = null;
            try
            {
                loaded = Assembly.Load(name);
            }
            catch (FileNotFoundException)
            {
                // 引用但不随测试输出的程序集(例如 Desktop)忽略即可。
            }

            if (loaded is not null)
            {
                yield return loaded;
            }
        }
    }
}
