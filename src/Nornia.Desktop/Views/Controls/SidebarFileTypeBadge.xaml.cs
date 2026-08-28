using System.Windows;
using System.Windows.Controls;

namespace Nornia.Desktop.Views.Controls;

/// <summary>Shared language/file identity slot for sidebar rows.</summary>
public partial class SidebarFileTypeBadge : UserControl
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(SidebarFileTypeBadge), new PropertyMetadata(string.Empty));

    public SidebarFileTypeBadge() => InitializeComponent();

    public string? Path
    {
        get => (string?)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }
}
