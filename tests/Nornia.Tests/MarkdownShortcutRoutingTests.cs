using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;
using Nornia.Desktop.Views;

namespace Nornia.Tests;

/// <summary>Markdown 渲染态键盘焦点与编辑器快捷键路由的定向测试。</summary>
public sealed class MarkdownShortcutRoutingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nornia-md-shortcuts-{Guid.NewGuid():N}");

    public MarkdownShortcutRoutingTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void MarkdownPreview_IsKeyboardFocusable()
    {
        WpfStaContext.Run(() =>
        {
            var view = new MarkdownPreviewView();

            Assert.True(view.Focusable);
            Assert.True(view.IsTabStop);
        });
    }

    [Fact]
    public async Task F3_FromRenderedMarkdown_IsHandledByFilePreview()
    {
        var path = Path.Combine(_directory, "shortcut.md");
        await File.WriteAllTextAsync(path, "# Root\n\nsearchable text");
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();

        var handled = false;
        WpfStaContext.Run(() =>
        {
            var view = new FilePreviewView { DataContext = tab };
            using var source = new HwndSource(new HwndSourceParameters("MarkdownShortcutTest")
            {
                Width = 960,
                Height = 520,
            });
            source.RootVisual = view;
            Layout(view);
            WpfStaContext.PumpQueue();

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F3)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            view.MarkdownViewControl.RaiseEvent(args);
            handled = args.Handled;
        });

        Assert.True(handled);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(960, 520));
        element.Arrange(new Rect(0, 0, 960, 520));
    }
}
