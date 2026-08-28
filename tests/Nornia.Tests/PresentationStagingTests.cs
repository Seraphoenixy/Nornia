using System.Collections.Concurrent;
using System.Text;
using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>源码面"缺省色 → 正确高亮"闪帧修复的定向测试:首屏部分快照(IsComplete=false)
/// 先于完整快照发布、grammar 存储每 scope 只编译一次、标签级发布时序。与
/// LanguagePresentationTests 同集合串行:TextMateSharp 首次编译 + Oniguruma 原生初始化在并行
/// 首启下会竞态(tokenize 抛异常被兜底吞掉,返回空集合)。</summary>
[Collection("TextMate")]
public sealed class PresentationStagingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-staging-{Guid.NewGuid():N}");

    public PresentationStagingTests() => Directory.CreateDirectory(_directory);

    private static string BuildLargeCSharp(int lines)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            builder.AppendLine($"public class C{i} {{ public int F{i}() => {i}; }}");
        }

        return builder.ToString();
    }

    [Fact]
    public async Task AnalyzeTokensAsync_PublishesFirstBatchPartialBeforeComplete()
    {
        var type = CodeFileTypeRegistry.Instance.FromPath("Demo.cs");
        var published = new ConcurrentQueue<CodePresentationSnapshot>();

        // null: 服务级测试不挂文件缓存身份(与 4 参数 diff 重载同语义)。
        var complete = await CodePresentationService.Instance.AnalyzeTokensAsync(
            BuildLargeCSharp(300), type, 11, null!, published.Enqueue);

        var partial = Assert.Single(published);
        Assert.False(partial.IsComplete);
        Assert.True(complete.IsComplete);
        Assert.NotEmpty(partial.Tokens);
        Assert.True(partial.Tokens.All(token => token.Line <= CodePresentationService.FirstBatchLines),
            "部分快照应只覆盖首屏行");
        Assert.True(complete.Tokens.Count >= partial.Tokens.Count);
        Assert.True(complete.Tokens.Any(token => token.Line > CodePresentationService.FirstBatchLines),
            "完整快照应覆盖全文");
        Assert.NotNull(partial.TokensByLine);
        Assert.Same(partial.LineDensity, complete.LineDensity); // 全文密度复用,不重算
    }

    [Fact]
    public async Task AnalyzeTokensAsync_SmallDocument_SkipsPartial()
    {
        var type = CodeFileTypeRegistry.Instance.FromPath("Demo.cs");
        var published = new ConcurrentQueue<CodePresentationSnapshot>();

        // null: 服务级测试不挂文件缓存身份(与 4 参数 diff 重载同语义)。
        var complete = await CodePresentationService.Instance.AnalyzeTokensAsync(
            "public class A { }", type, 5, null!, published.Enqueue);

        Assert.Empty(published);
        Assert.True(complete.IsComplete);
    }

    [Fact]
    public async Task GrammarStore_CompilesEachScopeOnce_AndSharesAcrossSessions()
    {
        var store = new TextMateGrammarStore();
        var service = new CodePresentationService(store);
        var type = CodeFileTypeRegistry.Instance.FromPath("Demo.cs");

        var first = await service.AnalyzeAsync("public class A { }\n", type, 1);
        var second = await service.AnalyzeAsync("public class B { }\n", type, 2);

        Assert.True(store.HasGrammar("source.cs"));
        Assert.Equal(1, store.LoadCountFor("source.cs")); // 仅编译一次,第二次命中缓存
        Assert.Contains(first.Tokens, token => token.Kind == CodeTokenKind.Keyword);
        Assert.Contains(second.Tokens, token => token.Kind == CodeTokenKind.Keyword);
    }

    [Fact]
    public async Task FilePreviewTab_PublishesPartialSnapshotBeforeCompleteOnLoad()
    {
        var path = Path.Combine(_directory, "staged.cs");
        await File.WriteAllTextAsync(path, BuildLargeCSharp(300));
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);

        var published = new List<CodePresentationSnapshot>();
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilePreviewTab.Presentation))
            {
                published.Add(tab.Presentation);
            }
        };

        await tab.LoadAsync();

        Assert.True(published.Count >= 2, $"应至少发布部分与完整两次快照: {published.Count}");
        Assert.False(published[0].IsComplete, "首次发布应是首屏部分快照");
        Assert.True(published[^1].IsComplete, "末次发布应是完整快照");
        Assert.True(published[0].TokensByLine is { } byLine && byLine.Keys.Max() <= CodePresentationService.FirstBatchLines);
        Assert.True(published[^1].TokensByLine is { } fullByLine && fullByLine.Keys.Max() > CodePresentationService.FirstBatchLines);
        tab.ReleaseResources();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
