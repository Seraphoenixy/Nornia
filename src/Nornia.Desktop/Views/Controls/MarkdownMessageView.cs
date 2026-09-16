using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        // 提交悬浮窗随正文自然增高；正文内部不再形成第二个滚动区域。
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Loaded += (_, _) =>
        {
            ThemeEvents.ThemeChanged += OnThemeChanged;
            Rebuild();
        };
        Unloaded += (_, _) => ThemeEvents.ThemeChanged -= OnThemeChanged;
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownMessageView)d).Rebuild();

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        // 非宿主线程的广播归组回宿主 Dispatcher(见 CodeDocumentView.OnThemeChanged)。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(Rebuild);
            return;
        }

        Rebuild();
    }

    private void Rebuild()
    {
        var document = CommitMessageRenderer.Render(Markdown);
        Document = document;

        // FlowDocumentScrollViewer 即使 HorizontalAlignment=Left，也会因分页布局把 DesiredSize
        // 撑到可用上限。显式使用文本的自然宽度，MaxWidth 再负责长内容的 550px 上限。
        Width = MeasureNaturalContentWidth(Markdown, document);
    }

    private static double MeasureNaturalContentWidth(string? markdown, FlowDocument document)
    {
        var typeface = new Typeface(document.FontFamily, document.FontStyle,
            document.FontWeight, document.FontStretch);
        var pixelsPerDip = Application.Current?.MainWindow is { } window
            ? VisualTreeHelper.GetDpi(window).PixelsPerDip
            : 1d;
        var widest = 0d;
        foreach (var line in (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var formatted = new FormattedText(line, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, document.FontSize,
                document.Foreground, pixelsPerDip);
            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
        }

        // 列表缩进、项目符号和代码块内边距的统一余量；ToolTip 外层 Padding 不计入此处。
        return Math.Max(1d, Math.Ceiling(widest + 20d));
    }
}
