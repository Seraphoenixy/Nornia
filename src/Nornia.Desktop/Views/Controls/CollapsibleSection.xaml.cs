using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nornia.Desktop.Views.Controls;

/// <summary>
/// VS Code-style collapsible side-bar section: a chevron header whose click toggles the body. The
/// right-side <see cref="HeaderContent"/> action area never collapses. When <see cref="ToggleCommand"/>
/// is set the click routes through the view model (so session state can be tracked); otherwise the
/// control flips <see cref="IsExpanded"/> itself (the TwoWay binding keeps a view-model property in sync).
/// </summary>
public partial class CollapsibleSection : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(CollapsibleSection), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(CollapsibleSection), new PropertyMetadata(null));

    public static readonly DependencyProperty BadgeCountProperty = DependencyProperty.Register(
        nameof(BadgeCount), typeof(int), typeof(CollapsibleSection), new PropertyMetadata(0));

    public static readonly DependencyProperty AlwaysShowBadgeProperty = DependencyProperty.Register(
        nameof(AlwaysShowBadge), typeof(bool), typeof(CollapsibleSection), new PropertyMetadata(false));

    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent), typeof(object), typeof(CollapsibleSection), new PropertyMetadata(null));

    /// <summary>外部内容专用属性:绝不能挂在继承来的 <c>UserControl.Content</c> 上——
    /// 直接子元素会整体顶掉控件自身 XAML 的 chrome(分区头/折叠机构),折叠随之失效。</summary>
    public static readonly DependencyProperty SectionContentProperty = DependencyProperty.Register(
        nameof(SectionContent), typeof(object), typeof(CollapsibleSection), new PropertyMetadata(null));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(CollapsibleSection), new PropertyMetadata(true));

    public static readonly DependencyProperty ToggleCommandProperty = DependencyProperty.Register(
        nameof(ToggleCommand), typeof(ICommand), typeof(CollapsibleSection), new PropertyMetadata(null));

    public static readonly DependencyProperty ToggleCommandParameterProperty = DependencyProperty.Register(
        nameof(ToggleCommandParameter), typeof(object), typeof(CollapsibleSection), new PropertyMetadata(null));

    public CollapsibleSection()
    {
        InitializeComponent();
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Bold label rendered beside the chevron (VS Code section title, e.g. "暂存的更改").
    /// Takes the place of <see cref="Header"/>; the legacy content API still works when Title is null.</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Count shown in the blue pill badge at the right edge of the header
    /// (VS Code "暂存的更改 48"); hidden when zero or negative unless
    /// <see cref="AlwaysShowBadge"/> is enabled.</summary>
    public int BadgeCount
    {
        get => (int)GetValue(BadgeCountProperty);
        set => SetValue(BadgeCountProperty, value);
    }

    /// <summary>When true, keep the count badge visible at zero so the header reserves a stable
    /// right-edge slot. The default remains false for sections that only show meaningful counts.</summary>
    public bool AlwaysShowBadge
    {
        get => (bool)GetValue(AlwaysShowBadgeProperty);
        set => SetValue(AlwaysShowBadgeProperty, value);
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    /// <summary>分区主体内容(在分区头之下、随折叠显示/隐藏)。使用方必须通过
    /// <c>CollapsibleSection.SectionContent</c> 属性元素提供,而非直接子元素。</summary>
    public object? SectionContent
    {
        get => GetValue(SectionContentProperty);
        set => SetValue(SectionContentProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public ICommand? ToggleCommand
    {
        get => (ICommand?)GetValue(ToggleCommandProperty);
        set => SetValue(ToggleCommandProperty, value);
    }

    public object? ToggleCommandParameter
    {
        get => GetValue(ToggleCommandParameterProperty);
        set => SetValue(ToggleCommandParameterProperty, value);
    }

    private void HeaderToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ToggleCommand is { } command)
        {
            if (command.CanExecute(ToggleCommandParameter))
            {
                command.Execute(ToggleCommandParameter);
            }

            return;
        }

        IsExpanded = !IsExpanded;
    }
}
