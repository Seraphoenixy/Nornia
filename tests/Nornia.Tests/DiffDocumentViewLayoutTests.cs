using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

/// <summary>回归:diff 单栏/并排视图的滚动条被右缘概览轨道盖住、无法拖动。
/// 三个编辑器右缘固定让出 16px(轨道 12px + 间距 4px),垂直滚动条完整落在轨道左侧:
/// 轨道左缘与编辑器右缘对齐,不再遮挡滚动条滑块/轨道。</summary>
public sealed class DiffDocumentViewLayoutTests
{
    [Fact]
    public void EditorsReserveRailSpace_RailIsFlushWithEditorRightEdge()
    {
        WpfStaContext.Run(() =>
        {
            var view = new DiffDocumentView();
            var host = new Window { Content = view, Width = 720, Height = 420, ShowInTaskbar = false };
            host.Show();
            try
            {
                host.UpdateLayout();

                Assert.Equal(new Thickness(0, 0, 16, 0), view.InlineEditor.Margin);
                Assert.Equal(new Thickness(0, 0, 16, 0), view.OldEditor.Margin);
                Assert.Equal(new Thickness(0, 0, 16, 0), view.NewEditor.Margin);

                // 轨道占用宽度(12px 宽 + 4px 右距)== 编辑器让出的右缘空间。
                Assert.Equal(16, view.OverviewCanvas.Width + view.OverviewCanvas.Margin.Right);
                Assert.Equal(16, view.OldOverviewCanvas.Width + view.OldOverviewCanvas.Margin.Right);
                Assert.Equal(16, view.NewOverviewCanvas.Width + view.NewOverviewCanvas.Margin.Right);

                // 轨道左缘与编辑器右缘贴合:滚动条(位于编辑器右缘内侧)不再被覆盖。
                AssertFlush(view.InlinePane, view.InlineEditor, view.OverviewCanvas);
                AssertFlush(view.SideBySidePane, view.OldEditor, view.OldOverviewCanvas);
                AssertFlush(view.SideBySidePane, view.NewEditor, view.NewOverviewCanvas);
            }
            finally
            {
                // 已加载视图持有 ThemeEvents 强订阅;断言失败也必须卸载,避免跨测试类泄漏。
                host.Close();
            }
        });
    }

    [Fact]
    public void ScrollBarHitArea_IsNotCoveredByOverviewRail()
    {
        WpfStaContext.Run(() =>
        {
            var view = new DiffDocumentView();
            var host = new Window { Content = view, Width = 720, Height = 420, ShowInTaskbar = false };
            host.Show();
            try
            {
                // 足够多的内容让垂直滚动条真正出现。
                var lines = new string[400];
                for (var i = 0; i < lines.Length; i++)
                {
                    lines[i] = $"line {i}";
                }

                view.InlineEditor.Document = new TextDocument(string.Join('\n', lines));
                host.UpdateLayout();

                var bar = FindVerticalScrollBar(view.InlineEditor);
                Assert.NotNull(bar);
                Assert.True(bar!.ActualHeight > 0, "单栏编辑器应显示垂直滚动条");

                var mid = bar.TransformToVisual(view.InlinePane).Transform(new Point(bar.ActualWidth / 2, bar.ActualHeight / 2));
                var hit = view.InlinePane.InputHitTest(mid) as DependencyObject;
                Assert.NotNull(hit);
                Assert.False(IsAncestorOrSelf(view.OverviewCanvas, hit), "概览轨道不得覆盖滚动条命中区");
                Assert.True(IsAncestorOrSelf(bar, hit), "滚动条命中区应落在滚动条自身");
            }
            finally
            {
                // 已加载视图持有 ThemeEvents 强订阅;断言失败也必须卸载,避免跨测试类泄漏。
                host.Close();
            }
        });
    }

    private static void AssertFlush(Grid pane, FrameworkElement editor, Canvas rail)
    {
        var editorRight = editor.TransformToVisual(pane).Transform(new Point(editor.ActualWidth, 0)).X;
        var railLeft = rail.TransformToVisual(pane).Transform(new Point(0, 0)).X;
        Assert.InRange(railLeft - editorRight, -0.5, 0.5);
    }

    private static bool IsAncestorOrSelf(DependencyObject ancestor, DependencyObject? node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static ScrollBar? FindVerticalScrollBar(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollBar bar && bar.Orientation == Orientation.Vertical)
            {
                return bar;
            }

            var found = FindVerticalScrollBar(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
