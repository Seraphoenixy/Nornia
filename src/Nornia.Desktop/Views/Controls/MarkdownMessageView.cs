using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.Services;

namespace Nornia.Desktop.Views.Controls;

/// <summary>
/// 只读 markdown 消息视图(提交悬浮窗正文):把 <see cref="Markdown"/> 文本经
/// <see cref="CommitMessageRenderer"/> 渲染为 <see cref="FlowDocument"/>。
/// 渲染时从当前主题解析颜色/字体;主题切换(ThemeEvents)与文本变化都会重建文档,
/// 保证悬浮窗在任何主题下即时换肤。透明背景,由宿主(悬浮窗表面)提供底色。
/// </summary>
public sealed class MarkdownMessageView : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownMessageView),
        new FrameworkPropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownMessageView()
    {
        Background = Brushes.Transparent;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Loaded += (_, _) =>
        {
            ThemeEvents.ThemeChanged += OnThemeChanged;
            Rebuild();
        };
        Unloaded += (_, _) => ThemeEvents.ThemeChanged -= OnThemeChanged;
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownMessageView)d).Rebuild();

    private void OnThemeChanged(object? sender, AppTheme theme) => Rebuild();

    private void Rebuild() => Document = CommitMessageRenderer.Render(Markdown);
}