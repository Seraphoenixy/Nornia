using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Nornia.Desktop.Services;

/// <summary>
/// Win11 系统材质与圆角适配(Fluent Material / 阶段4):
/// - <b>圆角</b>:DWMWA_WINDOW_CORNER_PREFERENCE = ROUND(Win11 22000+),对自绘无边框窗口兜底;
/// - <b>Mica</b>:DWMWA_SYSTEMBACKDROP_TYPE = MICA(仅 Win11 22621+、非高对比度主题)。启用后把
///   壳层表面(窗口/标题栏/活动栏/侧栏/面板)在运行时替换为半透明覆盖——Mica 只透在壳层,
///   编辑器与内容面保持不透明(Fluent 分层:内容层最实);覆盖经 <see cref="ThemeEvents"/>
///   在主题切换时按新主题色重新生成。
/// 回退路径:OS 版本不足、DWM 调用失败、高对比度主题 ⇒ 全部不生效,维持纯色;
/// 最大化时 DWM 不绘制 Mica ⇒ 暂时移除半透明覆盖(见 <see cref="ApplyWindowState"/>)。
/// </summary>
public static class WindowBackdropService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;
    private const int DwmsbtMica = 2;
    // 壳层半透明度(75% 不透明):过高则 Mica 几乎不可见,过低则壳层文本对比度下降。
    private const byte ShellSurfaceAlpha = 0xC0;

    private static readonly string[] ShellSurfaceKeys =
        ["WindowBrush", "TitleBarBrush", "ActivityBarBrush", "SideBarBrush", "PanelBrush"];

    private static bool _backdropActive;
    private static IntPtr _hwnd;

    /// <summary>WindowChrome 的玻璃框宽度:Mica 需要把 DWM 层延到客户区(-1 = 无限玻璃框);
    /// 未启用时保持 0(完全自绘,与原行为一致)。DpiHelper.ApplyWindowChrome 每次重建 chrome 时取值,
    /// 因此 DPI 变更后玻璃框自动恢复到与当前状态一致。</summary>
    public static Thickness ChromeGlassFrame => _backdropActive ? new Thickness(-1) : new Thickness(0);

    static WindowBackdropService()
    {
        // 主题运行时切换:高对比度立即回退纯色;Dark/Light 之间按新主题重设 Mica 暗色意图并重生成覆盖。
        ThemeEvents.ThemeChanged += (_, theme) =>
        {
            if (!_backdropActive) return;
            if (theme == AppTheme.HighContrast)
            {
                RemoveShellOverrides();
                _backdropActive = false;
                return;
            }

            SetImmersiveDarkMode(theme == AppTheme.Dark);
            ApplyShellOverrides();
        };
    }

    /// <summary>尝试启用系统圆角与 Mica。返回是否启用了 Mica(圆角成功与否不决定返回值)。</summary>
    public static bool TryEnable(Window window)
    {
        try
        {
            var version = Environment.OSVersion.Version;
            if (version.Build < 22000 || Application.Current is null) return false;

            _hwnd = new WindowInteropHelper(window).EnsureHandle();

            // 圆角自 Win11 首个版本可用;失败静默(部分桌面环境可禁用)。
            int round = DwmwcpRound;
            DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

            // Mica 需要 22621(Windows 11 22H2)起才支持的 SYSTEMBACKDROP 属性。
            if (version.Build < 22621 || IsTheme("HighContrast")) return false;

            int mica = DwmsbtMica;
            if (DwmSetWindowAttribute(_hwnd, DwmwaSystemBackdropType, ref mica, sizeof(int)) != 0) return false;
            SetImmersiveDarkMode(IsTheme("Dark"));

            _backdropActive = true;
            // 重建 chrome:GlassFrame 由 0 切到 -1,把 DWM 绘制的 Mica 层延到整个客户区。
            Behaviors.DpiHelper.ApplyWindowChrome(window);
            if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target)
            {
                // WPF 渲染目标转透明:未着色的区域露出 DWM 的 Mica,内容面仍由 WPF 不透明覆盖。
                target.BackgroundColor = Colors.Transparent;
            }

            ApplyShellOverrides();
            return true;
        }
        catch (Exception)
        {
            // 材质是纯增强:任何异常都回退到纯色窗口,不影响功能。
            RemoveShellOverrides();
            _backdropActive = false;
            return false;
        }
    }

    /// <summary>窗口状态变化时同步材质:最大化时 DWM 不绘制 Mica,半透明壳层会透出 DWM 底色,
    /// 因此暂时移除半透明覆盖;还原窗口后重新应用。</summary>
    public static void ApplyWindowState(Window window)
    {
        if (!_backdropActive) return;
        if (window.WindowState == WindowState.Maximized)
        {
            RemoveShellOverrides();
        }
        else
        {
            ApplyShellOverrides();
        }
    }

    private static void SetImmersiveDarkMode(bool dark)
    {
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    /// <summary>把壳层表面键替换为「当前主题同色、降低不透明度」的运行时覆盖。
    /// 覆盖写在 Application.Resources 根层,优先级高于主题字典,DynamicResource 即时生效;
    /// 颜色取自当前主题值,因此主题切换后由 ThemeEvents 回调重生成。</summary>
    private static void ApplyShellOverrides()
    {
        var resources = Application.Current?.Resources;
        if (resources is null || !_backdropActive) return;
        foreach (var key in ShellSurfaceKeys)
        {
            if (resources[key] is not SolidColorBrush { } brush) continue;
            if (brush.Color.A != 0xFF) continue; // 已是覆盖值(或主题本身半透明),不叠加
            var translucent = new SolidColorBrush(Color.FromArgb(ShellSurfaceAlpha, brush.Color.R, brush.Color.G, brush.Color.B));
            translucent.Freeze();
            resources[key] = translucent;
        }
    }

    /// <summary>移除根层覆盖,壳层回落到主题字典的纯色值。</summary>
    private static void RemoveShellOverrides()
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        foreach (var key in ShellSurfaceKeys)
        {
            resources.Remove(key);
        }
    }

    private static bool IsTheme(string name)
    {
        var dict = Application.Current?.Resources.MergedDictionaries;
        return dict is not null && ContainsTheme(dict, name);
    }

    private static bool ContainsTheme(System.Collections.ObjectModel.Collection<ResourceDictionary> dictionaries, string name)
    {
        foreach (var dict in dictionaries)
        {
            var source = dict.Source?.OriginalString;
            if (source is not null && source.Contains("Themes/", StringComparison.OrdinalIgnoreCase)
                && source.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int sizeOfValue);
}
