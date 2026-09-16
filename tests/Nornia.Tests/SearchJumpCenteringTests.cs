using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;
using System.Windows;

namespace Nornia.Tests;

/// <summary>搜索结果点击跳转:目标行显示在编辑器视口垂直中间位置。
/// 关键时序:OpenFileAtAsync 在 LoadAsync 完成后立即执行跳转命令——此时文档可能尚未装进
/// 编辑器(绑定未落位)或已装入但视图尚未首次布局。跳转必须挂起/延迟居中,否则会塌缩到
/// 第 1 行或贴顶(点击跳转目标必须显示在屏幕中间)。</summary>
[Collection("WpfStaSequential")]
public sealed class SearchJumpCenteringTests
{
    private const int TargetLine = 120;
    private static readonly string Lines = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i}"));

    [Fact]
    public async Task SearchResultJump_BeforeFirstLayout_CentersTargetLine()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nornia-search-jump-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "big.txt");
            await File.WriteAllTextAsync(path, Lines);
            var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
            await tab.LoadAsync();

            double actualOffset = -1, expectedOffset = -1, lineHeight = 1;
            int caretLine = -1, documentLines = -1, tabLineCount = -1;

            WpfStaContext.Run(() =>
            {
                var view = new FilePreviewView();
                view.DataContext = tab;
                tabLineCount = tab.LineCount;

                // 真实搜索结果路径:OpenFileAtAsync 在内容发布后、视图首次布局前执行跳转。
                tab.GoToLineInput = $"{TargetLine}:1";
                tab.GoToLineCommand.Execute(null);

                Layout(view);
                PumpQueue();
                Layout(view);
                PumpQueue();

                var codeView = view.CodeViewControl;
                var textView = codeView.Editor.TextArea.TextView;
                lineHeight = Math.Max(1, textView.DefaultLineHeight);
                // 像素精确居中:目标行顶的像素坐标 - 半视口(容差 1.5 行,覆盖视口高与文本高的小差异)。
                expectedOffset = Math.Max(0, (TargetLine - 1) * lineHeight - (textView.ActualHeight - lineHeight) / 2);
                actualOffset = codeView.CaptureViewState().VerticalOffset;
                caretLine = codeView.Editor.TextArea.Caret.Line;
                documentLines = codeView.Document.LineCount;
            });

            Assert.True(caretLine == TargetLine && documentLines > 0 && tabLineCount >= TargetLine,
                $"跳转未生效: caretLine={caretLine}, documentLines={documentLines}, tabLineCount={tabLineCount}");
            Assert.True(Math.Abs(actualOffset - expectedOffset) <= lineHeight * 1.5,
                $"搜索结果跳转未居中: 偏移={actualOffset:F1}, 期望(居中)={expectedOffset:F1}");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>最小机制:跳转先于文档装入(空文档)时挂起,文档装入后落位并居中。</summary>
    [Fact]
    public void JumpBeforeDocumentInstall_ParksAndCenters()
    {
        int caretAfterPark = -1, caretAfterInstall = -1;
        double offset = -1, expected = -1, tolerance = 1;
        WpfStaContext.Run(() =>
        {
            var codeView = new CodeDocumentView();
            codeView.JumpToPosition(120, 1, true);
            caretAfterPark = codeView.Editor.TextArea.Caret.Line;
            codeView.SourceText = Lines;
            caretAfterInstall = codeView.Editor.TextArea.Caret.Line;

            // 布局 + 泵,让延迟居中(布局后的 Loaded 槽)落地。
            Layout(codeView);
            PumpQueue();
            Layout(codeView);
            PumpQueue();
            offset = codeView.CaptureViewState().VerticalOffset;
            var textView = codeView.Editor.TextArea.TextView;
            var lineHeight = Math.Max(1, textView.DefaultLineHeight);
            tolerance = lineHeight * 1.5;
            expected = Math.Max(0, (TargetLine - 1) * lineHeight - (textView.ActualHeight - lineHeight) / 2);
        });

        Assert.True(caretAfterPark == 1 && caretAfterInstall == TargetLine,
            $"挂起失败: park后光标={caretAfterPark}, 装入后光标={caretAfterInstall}");
        Assert.True(Math.Abs(offset - expected) <= tolerance,
            $"装入落位未居中: 偏移={offset:F1}, 期望(居中)={expected:F1}");
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(960, 520));
        element.Arrange(new Rect(0, 0, 960, 520));
    }

    private static void PumpQueue() => WpfStaContext.PumpQueue();
}
