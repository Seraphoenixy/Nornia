using Nornia.Desktop.Services;
using Nornia.Desktop.Views.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nornia.Tests;

[Collection("WpfStaSequential")]
public sealed class TerminalSurfaceRenderingTests
{
    [Fact]
    public void PlainText_ProducesVisiblePixelsAboveTheTerminalBackground()
    {
        WpfStaContext.Run(() =>
        {
            const int width = 320;
            // Cover all ten model rows so the assertion isolates text drawing from viewport
            // resizing/scrolling behavior.
            const int height = 200;
            var session = new TerminalSession(new ShellProfile("test", "Test", "test.exe"), "", null);
            session.AttachFallbackScreen(columns: 40, rows: 10);
            session.Screen!.FeedText("visible terminal output");

            var surface = new TerminalSurfaceControl { Session = session };
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);
            var background = pixels.AsSpan(0, 4).ToArray();
            var changedPixels = 0;
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                if (!pixels.AsSpan(offset, 4).SequenceEqual(background))
                {
                    changedPixels++;
                }
            }

            Assert.True(changedPixels > 20, $"终端文本没有产生可见像素，非背景像素数：{changedPixels}");
        });
    }
}
