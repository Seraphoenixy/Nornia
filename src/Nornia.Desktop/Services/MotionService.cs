using System.Windows;
using Microsoft.Win32;

namespace Nornia.Desktop.Services;

/// <summary>
/// 系统"动画"偏好适配(VS Code monaco-reduce-motion 对应物):
/// 读取 Windows "在桌面上显示动画"(个人化 → 个性化,EnableAnimations),关闭时把
/// App.xaml 的 Duration* 时长资源整体置零 —— 所有共享动画样式(悬停/选中/按下/焦点
/// 的 120–300ms 透明度过渡)立即变成瞬时切换,无需逐个动画判断。
/// 读取失败按"动画开启"处理(best-effort,不影响启动)。
/// </summary>
public static class MotionService
{
    /// <summary>系统"在桌面上显示动画"是否开启(失败默认开启)。</summary>
    public static bool SystemAnimationsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableAnimations") is not int value || value != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>系统动画关闭 ⇒ DurationFast/Normal/Slow 全部置零(须在首个窗口显示前调用,
    /// 样式经 StaticResource 在加载时解析时长)。</summary>
    public static void ApplySystemMotionPreference()
    {
        if (SystemAnimationsEnabled())
        {
            return;
        }

        var zero = new Duration(TimeSpan.Zero);
        Application.Current.Resources["DurationFast"] = zero;
        Application.Current.Resources["DurationNormal"] = zero;
        Application.Current.Resources["DurationSlow"] = zero;
    }
}
