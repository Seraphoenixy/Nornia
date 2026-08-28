using System.Windows;
using System.Windows.Controls;

namespace Nornia.Desktop.Views;

public partial class SettingsView : UserControl
{
    public const double CompactLayoutThreshold = 1040d;

    public static readonly DependencyProperty IsCompactLayoutProperty =
        DependencyProperty.Register(nameof(IsCompactLayout), typeof(bool), typeof(SettingsView),
            new PropertyMetadata(true));

    public bool IsCompactLayout
    {
        get => (bool)GetValue(IsCompactLayoutProperty);
        private set => SetValue(IsCompactLayoutProperty, value);
    }

    internal static bool ShouldUseCompactLayout(double width) =>
        width <= 0 || width < CompactLayoutThreshold;

    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => UpdateCompactLayout(args.NewSize.Width);
        Loaded += (_, _) => UpdateCompactLayout(ActualWidth);
    }

    private void UpdateCompactLayout(double width) =>
        IsCompactLayout = ShouldUseCompactLayout(width);
}
