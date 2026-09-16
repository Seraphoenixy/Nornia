using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Nornia.Desktop.Services;

/// <summary>
/// 程序化动效的统一入口(XAML 样式动画之外的补充,Fluent Motion 原则):
/// - 时长与缓动一律读取 App.xaml 的 <c>Duration*</c>/<c>Ease*</c> 令牌,不逐处硬编码;
/// - 系统"在桌面上显示动画"关闭(与 <see cref="MotionService"/> 同一开关)或处于高对比度
///   主题时,所有动画直接跳过——动效是增强,不是功能依赖。
/// </summary>
public static class MotionHelper
{
    /// <summary>当前是否允许执行代码动效。</summary>
    public static bool IsEnabled
    {
        get
        {
            if (!MotionService.SystemAnimationsEnabled()) return false;
            var app = Application.Current;
            if (app is null) return false;
            var theme = app.Resources.MergedDictionaries.FirstOrDefault(d =>
                d.Source?.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase) == true);
            return theme?.Source.OriginalString.Contains("HighContrast", StringComparison.OrdinalIgnoreCase) != true;
        }
    }

    private static TimeSpan DurationOf(string key) =>
        Application.Current?.TryFindResource(key) is Duration duration ? duration.TimeSpan : TimeSpan.FromMilliseconds(200);

    private static EasingFunctionBase? EaseOf(string key) =>
        Application.Current?.TryFindResource(key) as EasingFunctionBase;

    /// <summary>
    /// 进入动效:淡入 + 轻微位移(Fluent 惯用 6~8px)。侧栏展开/页面切换/命令面板下落共用。
    /// 幂等——重复调用会以新的起点重新播放,不会累积变换。
    /// </summary>
    /// <param name="element">目标元素(容器级,如侧栏宿主/页面宿主,勿对逐项列表使用)。</param>
    /// <param name="fromY">起始纵向偏移;负值表示自上方落入(命令面板)。</param>
    /// <param name="fromX">起始横向偏移;侧栏等左侧滑入可用负值。</param>
    public static void FadeSlideIn(UIElement element, double fromY = 8, double fromX = 0)
    {
        if (element is null || !IsEnabled) return;

        var translate = EnsureTranslateTransform(element);
        translate.X = fromX;
        translate.Y = fromY;

        var opacityAnimation = new DoubleAnimation(0, 1, DurationOf("DurationNormal"))
        {
            EasingFunction = EaseOf("EaseOutSoft"),
        };
        var translateXAnimation = new DoubleAnimation(fromX, 0, DurationOf("DurationNormal"))
        {
            EasingFunction = EaseOf("EaseOutStandard"),
        };
        var translateYAnimation = new DoubleAnimation(fromY, 0, DurationOf("DurationNormal"))
        {
            EasingFunction = EaseOf("EaseOutStandard"),
        };
        element.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        translate.BeginAnimation(TranslateTransform.XProperty, translateXAnimation);
        translate.BeginAnimation(TranslateTransform.YProperty, translateYAnimation);
    }

    /// <summary>确保元素携带可动画的 <see cref="TranslateTransform"/>(已有则复用,含 TransformGroup 场景)。</summary>
    public static TranslateTransform EnsureTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;
        if (element.RenderTransform is TransformGroup group &&
            group.Children.OfType<TranslateTransform>().LastOrDefault() is { } inner)
        {
            return inner;
        }

        var translate = new TranslateTransform();
        element.RenderTransform = translate;
        return translate;
    }
}
