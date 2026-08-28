using System.Windows;
using System.Windows.Media;
using Nornia.Desktop.Services;

namespace Nornia.Desktop.Views.Controls;

/// <summary>Reusable tree indent-guide layer shared by Explorer and search results.
/// Guide positions are supplied by the flattened row model (cached <c>double[]</c>) and the
/// existing TreeGuideBrush token is used.
///
/// 渲染实现:单次 OnRender 画 N 条 1px 竖线(对照 VS Code 的 CSS 背景 guide —— 每行零额外
/// DOM)。旧实现是每深度一个 1px Border 子元素 + 跨容器 ActualHeight/DataTrigger 绑定:
/// 深 15 行的行 = 15 个额外 UI 元素 + 15 条跨容器绑定,虚拟化回收时反复触发。
/// 引导线是纯装饰(不参与命中测试),OnRender 重绘成本为 N 次 DrawRectangle,无布局参与。</summary>
public partial class SidebarTreeGuideLayer : FrameworkElement
{
    public static readonly DependencyProperty GuideLeftsProperty = DependencyProperty.Register(
        nameof(GuideLefts), typeof(IEnumerable<double>), typeof(SidebarTreeGuideLayer),
        new PropertyMetadata(null, OnInputsChanged));

    public static readonly DependencyProperty ShowGuidesProperty = DependencyProperty.Register(
        nameof(ShowGuides), typeof(bool), typeof(SidebarTreeGuideLayer),
        new PropertyMetadata(false, OnInputsChanged));

    private Brush? _brush;

    public SidebarTreeGuideLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => ThemeEvents.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeEvents.ThemeChanged -= OnThemeChanged;
    }

    public IEnumerable<double>? GuideLefts
    {
        get => (IEnumerable<double>?)GetValue(GuideLeftsProperty);
        set => SetValue(GuideLeftsProperty, value);
    }

    public bool ShowGuides
    {
        get => (bool)GetValue(ShowGuidesProperty);
        set => SetValue(ShowGuidesProperty, value);
    }

    private static void OnInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SidebarTreeGuideLayer)d).InvalidateVisual();

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        _brush = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!ShowGuides || ActualHeight <= 0 || GuideLefts is not { } lefts)
        {
            return;
        }

        var brush = _brush ??= Application.Current?.TryFindResource("TreeGuideBrush") as Brush ?? Brushes.Transparent;
        foreach (var left in lefts)
        {
            drawingContext.DrawRectangle(brush, null, new Rect(left, 0, 1, ActualHeight));
        }
    }
}
