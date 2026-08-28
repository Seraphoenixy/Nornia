using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace Nornia.Desktop.Behaviors;

/// <summary>
/// High-DPI helpers for the custom non-client title bar. WPF measures in device-independent
/// pixels, but <see cref="WindowChrome"/> reports caption and resize-border values to the OS in
/// physical pixels, so they must be re-scaled whenever the effective DPI changes or the window is
/// moved between monitors with different scaling.
/// </summary>
public static class DpiHelper
{
    /// <summary>Device-independent baseline values the title bar is authored against (96 DPI).
    /// <para>The caption hit band must end WELL ABOVE the workbench content: the secondary sidebar
    /// (环境/源代码管理/… view titles with their icon buttons) starts immediately below the 35-unit
    /// window title strip. With a 35-unit caption, rounding at fractional DPIs pushed the HTCAPTION
    /// band down over the sidebar title-bar buttons — clicks there were swallowed by the OS caption
    /// (drag + double-click maximize-toggle) instead of reaching the buttons. The visible strip keeps
    /// its drag / double-click behavior through the WPF handler on the title-bar Border.</para></summary>
    private const int BaseCaptionHeight = 18;
    private const double BaseResizeBorder = 5.0;
    // Window controls are intentionally compact: the glyph is 12 DIPs and the button only adds
    // a small hit-test margin. The old 46x34 system-sized boxes made the controls look oversized
    // even after the glyph FontSize was reduced.
    private const int BaseSystemButtonWidth = 32;
    private const int BaseSystemButtonHeight = 28;

    public static double Scale(Visual relativeTo) =>
        VisualTreeHelper.GetDpi(relativeTo).DpiScaleY;

    /// <summary>Re-apply DPI-correct WindowChrome values to a custom title-bar window.</summary>
    public static void ApplyWindowChrome(Window window)
    {
        var scale = Scale(window);
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = CaptionHeight(scale),
            ResizeBorderThickness = new Thickness(ResizeBorderThickness(scale)),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
    }

    public static int CaptionHeight(double scale) => Math.Max(1, (int)Math.Round(BaseCaptionHeight * scale));
    public static double ResizeBorderThickness(double scale) => Math.Max(1, BaseResizeBorder * scale);
    public static int SystemButtonWidth(double scale) => (int)Math.Round(BaseSystemButtonWidth * scale);
    public static int SystemButtonHeight(double scale) => (int)Math.Round(BaseSystemButtonHeight * scale);
}
