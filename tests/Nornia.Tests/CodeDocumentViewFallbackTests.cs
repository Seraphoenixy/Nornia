using Nornia.Desktop.Code;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

/// <summary>编辑器不挂载 AvalonEdit 内置定义,文本高亮完全由 TextMate token colorizer(行索引)
/// 提供——避免打开文件时“默认上色 → 文本高亮”的闪变。无 token 的纯文本/未分词行显示为纯文本。
/// 首屏增量快照(IsComplete=false)已覆盖首个可见屏幕,因此不会出现整篇缺省色。</summary>
public sealed class CodeDocumentViewFallbackTests
{
    [Fact]
    public void EditorNeverUsesBuiltInAvalonEditHighlighting()
    {
        object? initial = null;
        object? duringPartial = null;
        object? afterComplete = null;

        WpfStaContext.Run(() =>
        {
            var view = new CodeDocumentView();
            view.HighlightingName = "C#";
            view.SourceText = "public class A { }";
            initial = view.Editor.SyntaxHighlighting; // 初始:不挂内置定义

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

        Assert.Null(initial);       // 初始:无内置上色
        Assert.Null(duringPartial); // 部分快照:仍无内置上色(token 由 colorizer 绘制)
        Assert.Null(afterComplete); // 完整快照:语义配色接管
    }

    [Fact]
    public void EmptySnapshotKeepsSyntaxHighlightingNull()
    {
        object? afterEmpty = null;

        WpfStaContext.Run(() =>
        {
            var view = new CodeDocumentView();
            view.HighlightingName = "C#";
            view.PresentationSnapshot = CodePresentationSnapshot.Empty(1); // 释放/初始态
            afterEmpty = view.Editor.SyntaxHighlighting;
        });

        Assert.Null(afterEmpty);
    }
}