using Nornia.Desktop.Code;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

/// <summary>编辑器内置兜底策略:部分快照(首屏未分词完,IsComplete=false)期间保留 AvalonEdit
/// 内置定义(首屏之外的行保持回退色,不闪"缺省色 → 正确高亮");完整快照到达后才关闭,
/// 语义配色全面接管。</summary>
public sealed class CodeDocumentViewFallbackTests
{
    [Fact]
    public void PartialSnapshotKeepsBuiltInFallback_CompleteSnapshotDisablesIt()
    {
        object? duringPartial = null;
        object? afterComplete = null;

        WpfStaContext.Run(() =>
        {
            var view = new CodeDocumentView();
            view.HighlightingName = "C#";
            view.SourceText = "public class A { }";
            Assert.NotNull(view.Editor.SyntaxHighlighting); // 初始:内置 C# 兜底就位

            var partial = new CodePresentationSnapshot(
                1,
                [new CodeTokenSpan(1, 0, 6, CodeTokenKind.Keyword, ["keyword", "other", "storage", "type", "modifier"])],
                [],
                [],
                [18],
                null,
                null)
            { IsComplete = false };
            view.PresentationSnapshot = partial;
            duringPartial = view.Editor.SyntaxHighlighting;

            var complete = partial with { DocumentVersion = 2, IsComplete = true };
            view.PresentationSnapshot = complete;
            afterComplete = view.Editor.SyntaxHighlighting;
        });

        Assert.NotNull(duringPartial); // 部分快照:内置兜底保留(未分词行不退回纯缺省色)
        Assert.Null(afterComplete);   // 完整快照:语义配色接管
    }

    [Fact]
    public void EmptySnapshotKeepsBuiltInFallback()
    {
        object? afterEmpty = null;

        WpfStaContext.Run(() =>
        {
            var view = new CodeDocumentView();
            view.HighlightingName = "C#";
            view.PresentationSnapshot = CodePresentationSnapshot.Empty(1); // 释放/初始态
            afterEmpty = view.Editor.SyntaxHighlighting;
        });

        Assert.NotNull(afterEmpty);
    }
}
