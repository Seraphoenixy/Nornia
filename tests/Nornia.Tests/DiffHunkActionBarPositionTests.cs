using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;
using Nornia.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Nornia.Tests;

/// <summary>Diff hunk 操作条(还原块/暂存块/取消暂存块悬浮按钮)的绘制位置回归:
/// 渲染真实 DiffDocumentView,经 InputManager 注入 MouseDevice 位置驱动悬停,
/// 断言操作条落在行号 gutter 内、随 hunk/滚动/分隔条布局联动,并且一个 hunk 内的
/// 多个连续变更块各自锚定自己的工具条。进程内注入不依赖物理光标与 OS 消息投递。</summary>
[Collection("WpfStaSequential")]
public sealed class DiffHunkActionBarPositionTests
{
    private readonly ITestOutputHelper _output;

    public DiffHunkActionBarPositionTests(ITestOutputHelper output) => _output = output;

    private static GitFileDiff BuildTwoHunkDiff() => new(
        "a.cs", null, false, false, false,
    [
        new GitDiffHunk(1, 10, 1, 10, "@@ -1,10 +1,10 @@",
        [
            new GitDiffLine(GitDiffLineKind.HunkHeader, null, null, "@@ -1,10 +1,10 @@"),
            new GitDiffLine(GitDiffLineKind.Context, 1, 1, "c1"),
            new GitDiffLine(GitDiffLineKind.Context, 2, 2, "c2"),
            new GitDiffLine(GitDiffLineKind.Context, 3, 3, "c3"),
            new GitDiffLine(GitDiffLineKind.Removed, 4, null, "old1"),
            new GitDiffLine(GitDiffLineKind.Added, null, 4, "new1"),
            new GitDiffLine(GitDiffLineKind.Context, 5, 5, "c4"),
            new GitDiffLine(GitDiffLineKind.Context, 6, 6, "c5"),
            new GitDiffLine(GitDiffLineKind.Context, 7, 7, "c6"),
        ]),
        new GitDiffHunk(11, 10, 11, 10, "@@ -11,10 +11,10 @@",
        [
            new GitDiffLine(GitDiffLineKind.HunkHeader, null, null, "@@ -11,10 +11,10 @@"),
            new GitDiffLine(GitDiffLineKind.Context, 11, 11, "d1"),
            new GitDiffLine(GitDiffLineKind.Context, 12, 12, "d2"),
            new GitDiffLine(GitDiffLineKind.Context, 13, 13, "d3"),
            new GitDiffLine(GitDiffLineKind.Removed, 14, null, "old2"),
            new GitDiffLine(GitDiffLineKind.Added, null, 14, "new2"),
            new GitDiffLine(GitDiffLineKind.Context, 15, 15, "d4"),
            new GitDiffLine(GitDiffLineKind.Context, 16, 16, "d5"),
            new GitDiffLine(GitDiffLineKind.Context, 17, 17, "d6"),
        ]),
    ]);

    private static IReadOnlyList<DiffRenderLine> GetDisplayLines(DiffDocumentView view, string fieldName)
    {
        var field = typeof(DiffDocumentView).GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (IReadOnlyList<DiffRenderLine>)field!.GetValue(view)!;
    }

    /// <summary>与视图 FindVisibleDisplayBounds 相同的算法(独立重写):显示行区间与可见
    /// visual line 的交集边界(视口坐标)。</summary>
    private static (double Top, double Bottom)? VisibleBlockBounds(TextView textView,
        IReadOnlyList<DiffRenderLine> lines, int blockStart, int blockEnd)
    {
        if (blockStart < 0 || blockEnd < 0)
        {
            return null;
        }

        var visible = textView.VisualLines.Where(line =>
            line.LastDocumentLine.LineNumber >= blockStart + 1
            && line.FirstDocumentLine.LineNumber <= blockEnd + 1).ToArray();
        if (visible.Length == 0)
        {
            return null;
        }

        return (visible.Min(line => line.VisualTop - textView.ScrollOffset.Y),
            visible.Max(line => line.VisualTop + line.Height - textView.ScrollOffset.Y));
    }

    private static void PumpUntil(Func<bool> condition, int maxRounds = 60)
    {
        for (var i = 0; i < maxRounds && !condition(); i++)
        {
            WpfStaContext.PumpQueue();
        }
    }

    private static (DiffTab Tab, DiffDocumentView View, Window Window) ShowDiff(GitDiffMode mode, double height, GitFileDiff? diff = null)
    {
        var git = new FakeGitService { DiffResult = diff ?? BuildTwoHunkDiff() };
        var tab = new DiffTab(git, new GitDiffRequest(@"C:\repo", "a.cs", IsStaged: false, IsUntracked: false),
            new FakeUiLogService(), new FakeClipboardService())
        {
            DiffMode = mode,
            IsContextCollapsed = false,
        };
        var view = new DiffDocumentView { DataContext = tab };
        var window = new Window
        {
            Content = view,
            Width = 900,
            Height = height,
            Left = 10,
            Top = 10,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false,
            // 置顶:WM_MOUSEMOVE 只投递给光标处的顶层窗口,被遮挡时 MouseDevice 拿不到
            // 真实光标位置,悬停几何断言会全部失效。
            Topmost = true,
        };
        window.Show();
        window.UpdateLayout();
        PumpUntil(() => tab.IsLoaded && view.InlineEditor.Document is { LineCount: > 0 });
        window.UpdateLayout();
        WpfStaContext.PumpQueue();
        return (tab, view, window);
    }

    /// <summary>把指针"悬停"到指定显示行(viewport 内)的中心:经视图的测试接缝注入指针
    /// 位置,再向 pane 派发 PreviewMouseMove 走真实处理器链路。完全不依赖物理光标与 OS
    /// 消息投递(真实桌面上用户操作会和 SetCursorPos 竞争,无法作为回归基线)。</summary>
    private void HoverRow(DiffDocumentView view, TextEditor editor, int firstChangeRow, string linesField)
    {
        var textView = editor.TextArea.TextView;
        Assert.True(GetDisplayLines(view, linesField)[firstChangeRow].IsChange, $"row {firstChangeRow} 不是变更行");
        var visualLine = textView.VisualLines.FirstOrDefault(line =>
            line.FirstDocumentLine.LineNumber <= firstChangeRow + 1 && line.LastDocumentLine.LineNumber >= firstChangeRow + 1);
        Assert.True(visualLine is not null, $"row {firstChangeRow} 没有对应的 visual line");
        var viewportY = visualLine.VisualTop - textView.ScrollOffset.Y;
        var rowCenterY = viewportY + visualLine.Height / 2;
        var editorPoint = new Point(editor.ActualWidth / 2, rowCenterY);

        view.PointerPositionOverride = _ => editorPoint;
        try
        {
            var args = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = UIElement.PreviewMouseMoveEvent,
            };
            var pane = editor == view.InlineEditor ? (IInputElement)view.InlinePane : view.SideBySidePane;
            pane.RaiseEvent(args);
            WpfStaContext.PumpQueue();
            WpfStaContext.PumpQueue();
        }
        finally
        {
            view.PointerPositionOverride = null;
        }

        _output.WriteLine($"hover row {firstChangeRow}: viewportY={viewportY:0.##}");
    }

    /// <summary>git 会把间距不超过 2×上下文行的多处修改合并成一个 hunk:两个变更块必须各自
    /// 锚定自己的工具条,而不是共用整个 hunk 的居中位置,否则用户无法分辨当前操作作用于哪处。
    /// </summary>
    private static GitFileDiff BuildTwoBlockOneHunkDiff() => new(
        "a.cs", null, false, false, false,
    [
        new GitDiffHunk(1, 11, 1, 11, "@@ -1,11 +1,11 @@",
        [
            new GitDiffLine(GitDiffLineKind.HunkHeader, null, null, "@@ -1,11 +1,11 @@"),
            new GitDiffLine(GitDiffLineKind.Context, 1, 1, "c1"),
            new GitDiffLine(GitDiffLineKind.Context, 2, 2, "c2"),
            new GitDiffLine(GitDiffLineKind.Context, 3, 3, "c3"),
            new GitDiffLine(GitDiffLineKind.Removed, 4, null, "old1"),
            new GitDiffLine(GitDiffLineKind.Added, null, 4, "new1"),
            new GitDiffLine(GitDiffLineKind.Context, 5, 5, "c4"),
            new GitDiffLine(GitDiffLineKind.Context, 6, 6, "c5"),
            new GitDiffLine(GitDiffLineKind.Context, 7, 7, "c6"),
            new GitDiffLine(GitDiffLineKind.Removed, 8, null, "old2"),
            new GitDiffLine(GitDiffLineKind.Added, null, 8, "new2"),
            new GitDiffLine(GitDiffLineKind.Context, 9, 9, "c7"),
            new GitDiffLine(GitDiffLineKind.Context, 10, 10, "c8"),
            new GitDiffLine(GitDiffLineKind.Context, 11, 11, "c9"),
        ]),
    ]);

    [Fact]
    public void HunkActionBar_AnchorsToHoveredBlock_WhenOneHunkHasTwoBlocks()
    {
        WpfStaContext.Run(() =>
        {
            var (tab, view, window) = ShowDiff(GitDiffMode.Inline, height: 340, diff: BuildTwoBlockOneHunkDiff());
            try
            {
                var textView = view.InlineEditor.TextArea.TextView;
                var lines = GetDisplayLines(view, "_inlineLines");
                // 两处修改在同一个 hunk 内:行 4-5 是块 1,行 9-10 是块 2。
                Assert.Equal(0, lines[4].HunkIndex);
                Assert.True(lines[4].IsChange && lines[5].IsChange && !lines[6].IsChange);
                Assert.True(lines[9].IsChange && lines[10].IsChange);

                HoverRow(view, view.InlineEditor, firstChangeRow: 4, "_inlineLines");
                var top1 = Canvas.GetTop(view.InlineHunkActionBar);
                AssertBarAnchoredToRange("块1", view.InlineHunkActionBar, textView, lines, 4, 5, 2,
                    (view.InlineRestoreHunkButton, view.InlineStageHunkButton, view.InlineUnstageHunkButton));

                HoverRow(view, view.InlineEditor, firstChangeRow: 9, "_inlineLines");
                var top2 = Canvas.GetTop(view.InlineHunkActionBar);
                AssertBarAnchoredToRange("块2", view.InlineHunkActionBar, textView, lines, 9, 10, 2,
                    (view.InlineRestoreHunkButton, view.InlineStageHunkButton, view.InlineUnstageHunkButton));

                Assert.True(top2 - top1 > 10,
                    $"两个变更块的工具条必须落在各自位置(块1 top={top1:0.##},块2 top={top2:0.##})——共用整个 hunk 的居中位置会让用户无法分辨操作目标");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SideBySideHunkActionBar_AnchorsToHoveredBlock_WhenOneHunkHasTwoBlocks()
    {
        WpfStaContext.Run(() =>
        {
            var (tab, view, window) = ShowDiff(GitDiffMode.SideBySide, height: 420, diff: BuildTwoBlockOneHunkDiff());
            try
            {
                var pane = view.SideBySidePane;
                var newLines = GetDisplayLines(view, "_newLines");
                var changedRows = newLines.Select((line, index) => (line, index))
                    .Where(entry => entry.line.IsChange)
                    .Select(entry => entry.index)
                    .ToArray();
                Assert.True(changedRows.Length >= 2, "并排投影应有两个变更块");

                // 并排投影里一个修改对是一行:按视图 ExpandChangeBlock 的规则扩展块区间。
                static (int Start, int End) Expand(IReadOnlyList<DiffRenderLine> lines, int anchor)
                {
                    var start = anchor;
                    while (start > 0 && lines[start - 1].IsChange) start--;
                    var end = anchor;
                    while (end + 1 < lines.Count && lines[end + 1].IsChange) end++;
                    return (start, end);
                }

                var left = view.NewEditor.TransformToAncestor(pane).Transform(new Point(0, 0)).X + 2.5;
                var buttons = (view.SideRestoreHunkButton, view.SideStageHunkButton, view.SideUnstageHunkButton);
                var (blockStart1, blockEnd1) = Expand(newLines, changedRows[0]);
                HoverRow(view, view.NewEditor, blockStart1, "_newLines");
                var top1 = Canvas.GetTop(view.SideHunkActionBar);
                AssertBarAnchoredToRange("并排块1", view.SideHunkActionBar, view.NewEditor.TextArea.TextView, newLines,
                    blockStart1, blockEnd1, left, buttons);

                var (blockStart2, blockEnd2) = Expand(newLines, changedRows[^1]);
                HoverRow(view, view.NewEditor, blockStart2, "_newLines");
                var top2 = Canvas.GetTop(view.SideHunkActionBar);
                AssertBarAnchoredToRange("并排块2", view.SideHunkActionBar, view.NewEditor.TextArea.TextView, newLines,
                    blockStart2, blockEnd2, left, buttons);

                Assert.True(top2 - top1 > 10,
                    $"并排布局下两个变更块的工具条也必须分开(块1 top={top1:0.##},块2 top={top2:0.##})");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InlineHunkActionBar_CentersOnHoveredHunk_AcrossScrollPositions()
    {
        WpfStaContext.Run(() =>
        {
            var (tab, view, window) = ShowDiff(GitDiffMode.Inline, height: 260);
            try
            {
                var textView = view.InlineEditor.TextArea.TextView;
                var lines = GetDisplayLines(view, "_inlineLines");

                // 悬停第一个 hunk(变更行 4-5)。
                HoverRow(view, view.InlineEditor, firstChangeRow: 4, "_inlineLines");
                AssertBarCenteredOnHunk("hunk1", view.InlineHunkActionBar, textView, lines, lines[4].HunkIndex,
                    expectedLeft: 2, configuredButtons: (view.InlineRestoreHunkButton, view.InlineStageHunkButton, view.InlineUnstageHunkButton));

                // 滚动后再悬停第二个 hunk:坐标换算必须跟随滚动偏移。
                view.InlineEditor.ScrollToVerticalOffset(textView.DefaultLineHeight * 8);
                WpfStaContext.PumpQueue();
                Assert.True(textView.ScrollOffset.Y > 0, "窗口太小未能产生滚动,探针前提失效");
                HoverRow(view, view.InlineEditor, firstChangeRow: 13, "_inlineLines");
                AssertBarCenteredOnHunk("hunk2(滚动后)", view.InlineHunkActionBar, textView, lines, lines[13].HunkIndex,
                    expectedLeft: 2, configuredButtons: (view.InlineRestoreHunkButton, view.InlineStageHunkButton, view.InlineUnstageHunkButton));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SideBySideHunkActionBar_AlignsWithNewEditorGutter_AfterSplitterDrag()
    {
        WpfStaContext.Run(() =>
        {
            var (tab, view, window) = ShowDiff(GitDiffMode.SideBySide, height: 420);
            try
            {
                var pane = view.SideBySidePane;
                var newLines = GetDisplayLines(view, "_newLines");
                var firstChangeRow = newLines.Select((line, index) => (line, index)).First(entry => entry.line.IsChange).index;

                // 初始等分列:操作条应在 new 编辑器 gutter(new 编辑器起点 + 2.5px)。
                HoverRow(view, view.NewEditor, firstChangeRow, "_newLines");
                var initialLeft = view.NewEditor.TransformToAncestor(pane).Transform(new Point(0, 0)).X + 2.5;
                AssertBarCenteredOnHunk("并排 hunk1", view.SideHunkActionBar, view.NewEditor.TextArea.TextView, newLines,
                    newLines[firstChangeRow].HunkIndex, expectedLeft: initialLeft,
                    configuredButtons: (view.SideRestoreHunkButton, view.SideStageHunkButton, view.SideUnstageHunkButton));

                // 拖动分隔条(旧列固定 300):操作条必须跟随 new 编辑器的实际起点,
                // 而不是"画布中点 + 5px"的等分假设,否则会漂浮在代码文本上。
                pane.ColumnDefinitions[0].Width = new GridLength(300);
                window.UpdateLayout();
                WpfStaContext.PumpQueue();

                HoverRow(view, view.NewEditor, firstChangeRow, "_newLines");
                var draggedLeft = view.NewEditor.TransformToAncestor(pane).Transform(new Point(0, 0)).X + 2.5;
                Assert.True(Math.Abs(draggedLeft - initialLeft) > 10, "分隔条拖动未生效,探针前提失效");
                AssertBarCenteredOnHunk("并排 hunk1(分隔条拖动后)", view.SideHunkActionBar, view.NewEditor.TextArea.TextView, newLines,
                    newLines[firstChangeRow].HunkIndex, expectedLeft: draggedLeft,
                    configuredButtons: (view.SideRestoreHunkButton, view.SideStageHunkButton, view.SideUnstageHunkButton));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private void AssertBarCenteredOnHunk(string phase, Border bar, TextView textView,
        IReadOnlyList<DiffRenderLine> lines, int hunkIndex, double expectedLeft,
        (Button Restore, Button Stage, Button Unstage) configuredButtons)
    {
        var start = -1;
        var end = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].HunkIndex != hunkIndex)
            {
                continue;
            }

            if (lines[index].IsChange)
            {
                start = start < 0 ? index : Math.Min(start, index);
                end = index;
            }
        }

        Assert.True(start >= 0, $"{phase}: hunk {hunkIndex} 没有变更行");
        AssertBarAnchoredToRange(phase, bar, textView, lines, start, end, expectedLeft, configuredButtons);
    }

    private void AssertBarAnchoredToRange(string phase, Border bar, TextView textView,
        IReadOnlyList<DiffRenderLine> lines, int blockStart, int blockEnd, double expectedLeft,
        (Button Restore, Button Stage, Button Unstage) configuredButtons)
    {
        Assert.Equal(Visibility.Visible, bar.Visibility);
        // 未暂存 diff:还原 + 暂存可用,取消暂存隐藏。
        Assert.Equal(Visibility.Visible, configuredButtons.Restore.Visibility);
        Assert.Equal(Visibility.Visible, configuredButtons.Stage.Visibility);
        Assert.Equal(Visibility.Collapsed, configuredButtons.Unstage.Visibility);

        var left = Canvas.GetLeft(bar);
        var top = Canvas.GetTop(bar);
        var bounds = VisibleBlockBounds(textView, lines, blockStart, blockEnd);
        Assert.True(bounds is not null, $"{phase}: 显示行 [{blockStart},{blockEnd}] 没有可见行");
        var expectedTop = Math.Max(2, (bounds!.Value.Top + bounds.Value.Bottom - bar.ActualHeight) / 2);
        _output.WriteLine($"[{phase}] bar left={left:0.##} top={top:0.##} expected left={expectedLeft:0.##} top={expectedTop:0.##}");
        Assert.True(Math.Abs(left - expectedLeft) < 1.0, $"{phase}: 操作条 left={left:0.##} 应在 gutter({expectedLeft:0.##})");
        Assert.True(Math.Abs(top - expectedTop) < 1.5, $"{phase}: 操作条 top={top:0.##} 应居中于目标块 [{blockStart},{blockEnd}]({expectedTop:0.##})");
    }
}
