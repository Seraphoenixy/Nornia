using System.Runtime.ExceptionServices;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using Nornia.Desktop.Code;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

/// <summary>
/// 折叠栏接线护栏(STA,不渲染):验证 AvalonEdit 的 <c>FoldingManager.Install</c> 会自动把默认
/// <c>FoldingMargin</c> 插入 <c>LeftMargins</c>(旧式三角折叠栏的根因),CodeDocumentView 的清理
/// 必须移除它;并验证 <c>FoldGutterMargin</c> 的 region provider 从折叠管理器投影出 C# 几何
/// (含折叠态)。真实渲染(chevron 像素 + hover 显隐)已由一次性 HwndSource 探针验证。
/// </summary>
public sealed class FoldGutterMarginTests
{
    [Fact]
    public void FoldingManagerInstall_AddsBuiltInMargin_ThatMustBeRemoved()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var doc = new TextDocument("class A {\n    void M() {\n    }\n}\n");
                var textArea = new TextArea { Document = doc };

                // Install 前无任何边栏;Install 会自动插入旧式 FoldingMargin —— 必须移除,
                // 否则“原来的旧折叠栏”仍可见。
                Assert.Empty(textArea.LeftMargins);
                var manager = FoldingManager.Install(textArea);
                Assert.Contains(textArea.LeftMargins, margin => margin is FoldingMargin);

                // 复刻 CodeDocumentView.SetFoldingSections 的清理:移除 Install 自动插入的 margin。
                foreach (var margin in textArea.LeftMargins.OfType<FoldingMargin>().ToArray())
                {
                    textArea.LeftMargins.Remove(margin);
                }

                Assert.DoesNotContain(textArea.LeftMargins, margin => margin is FoldingMargin);

                // 折叠:class A(1-4)展开、void M(2-3)折叠。
                manager.UpdateFoldings(new[]
                {
                    new NewFolding(doc.GetLineByNumber(1).EndOffset, doc.GetLineByNumber(4).Offset) { Name = "…" },
                    new NewFolding(doc.GetLineByNumber(2).EndOffset, doc.GetLineByNumber(3).Offset) { Name = "…", DefaultClosed = true },
                }, 0);

                var allFoldings = manager.AllFoldings.ToArray();
                Assert.Equal(doc.GetLineByNumber(1).EndOffset, allFoldings[0].StartOffset);
                Assert.Equal(doc.GetLineByNumber(4).Offset, allFoldings[0].EndOffset);
                Assert.Equal(doc.GetLineByNumber(2).EndOffset, allFoldings[1].StartOffset);
                Assert.Equal(doc.GetLineByNumber(3).Offset, allFoldings[1].EndOffset);

                // FoldGutterMargin 接线:region provider 从管理器投影出期望的 C# 折叠几何。
                var gutter = new FoldGutterMargin(() => BuildRegions(manager, doc), _ => { }, _ => { });
                textArea.LeftMargins.Add(gutter);
                var regions = BuildRegions(manager, doc);
                Assert.Equal(2, regions.Count);
                Assert.Contains(regions, region => region.StartLine == 1 && region.EndLine == 4 && !region.IsCollapsed);
                Assert.Contains(regions, region => region.StartLine == 2 && region.EndLine == 3 && region.IsCollapsed);

                // 位置热区:未悬停也始终绘制(全透明),折叠列**显式固定占位**(Width/MinWidth=18),
                // 与可见内容无关,宽度不会塌缩为 0。
                Assert.False(gutter.Revealing);
                Assert.True(gutter.IsHitTestVisible);
                Assert.Equal(18, gutter.Width);
                Assert.Equal(18, gutter.MinWidth);
                gutter.Measure(new Size(18, 100));
                Assert.Equal(18, gutter.DesiredSize.Width);
                gutter.Revealing = true;
                gutter.Revealing = true; // 幂等,不重复重绘/不抛错
                Assert.True(gutter.Revealing);
                gutter.Revealing = false;
                Assert.False(gutter.Revealing);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void MouseClick_TogglesOnlyWhenMouseUpStaysOnPressedLine()
    {
        var state = new FoldGutterMouseState();
        state.Press(1);
        Assert.True(state.TryRelease(1, out var pressedLine));
        Assert.Equal(1, pressedLine);

        state.Press(1);
        Assert.False(state.TryRelease(2, out pressedLine));
        Assert.Equal(1, pressedLine);

        state.Press(0);
        Assert.False(state.TryRelease(1, out _));
    }

    /// <summary>与 CodeDocumentView.BuildCurrentFoldRegions 相同的投影逻辑(margin 的 provider)。</summary>
    private static IReadOnlyList<FoldRegion> BuildRegions(FoldingManager manager, TextDocument document)
    {
        var items = new List<(int Start, int End, bool Collapsed)>();
        foreach (var folding in manager.AllFoldings)
        {
            var startOffset = Math.Clamp(folding.StartOffset, 0, document.TextLength);
            var endOffset = Math.Clamp(folding.EndOffset, startOffset, document.TextLength);
            var startLine = document.GetLineByOffset(startOffset).LineNumber;
            var endLine = document.GetLineByOffset(endOffset).LineNumber;
            if (endLine > startLine)
            {
                items.Add((startLine, endLine, folding.IsFolded));
            }
        }

        return FoldingRegions.Build(items);
    }
}
