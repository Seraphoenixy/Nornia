using System.Windows;
using System.Windows.Controls;

namespace Nornia.Desktop.Views.Controls;

/// <summary>Common chrome for the three primary workbench sidebars.</summary>
public partial class SidebarShell : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SidebarShell), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HeaderActionsProperty = DependencyProperty.Register(
        nameof(HeaderActions), typeof(object), typeof(SidebarShell), new PropertyMetadata(null));

    public static readonly DependencyProperty TopContentProperty = DependencyProperty.Register(
        nameof(TopContent), typeof(object), typeof(SidebarShell), new PropertyMetadata(null));

    public static readonly DependencyProperty BodyContentProperty = DependencyProperty.Register(
        nameof(BodyContent), typeof(object), typeof(SidebarShell), new PropertyMetadata(null));

    public SidebarShell() => InitializeComponent();

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }

    public object? TopContent
    {
        get => GetValue(TopContentProperty);
        set => SetValue(TopContentProperty, value);
    }

    public object? BodyContent
    {
        get => GetValue(BodyContentProperty);
        set => SetValue(BodyContentProperty, value);
    }
}
