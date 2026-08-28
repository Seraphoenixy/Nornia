using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nornia.Desktop.Views.Controls;

/// <summary>Shared five-slot row layout used by sidebar tree and list projections.</summary>
public partial class SidebarRowChrome : UserControl
{
    public static readonly DependencyProperty IndentProperty = DependencyProperty.Register(
        nameof(Indent), typeof(Thickness), typeof(SidebarRowChrome), new PropertyMetadata(default(Thickness)));

    public static readonly DependencyProperty ShowExpanderProperty = DependencyProperty.Register(
        nameof(ShowExpander), typeof(bool), typeof(SidebarRowChrome), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowGlyphProperty = DependencyProperty.Register(
        nameof(ShowGlyph), typeof(bool), typeof(SidebarRowChrome),
        new PropertyMetadata(true, OnShowGlyphChanged));

    public static readonly DependencyProperty GlyphInExpanderCellProperty = DependencyProperty.Register(
        nameof(GlyphInExpanderCell), typeof(bool), typeof(SidebarRowChrome),
        new PropertyMetadata(true, OnGlyphLayoutChanged));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(SidebarRowChrome), new PropertyMetadata(true));

    public static readonly DependencyProperty ToggleCommandProperty = DependencyProperty.Register(
        nameof(ToggleCommand), typeof(ICommand), typeof(SidebarRowChrome), new PropertyMetadata(null));

    public static readonly DependencyProperty ToggleCommandParameterProperty = DependencyProperty.Register(
        nameof(ToggleCommandParameter), typeof(object), typeof(SidebarRowChrome), new PropertyMetadata(null));

    public static readonly DependencyProperty RowCommandProperty = DependencyProperty.Register(
        nameof(RowCommand), typeof(ICommand), typeof(SidebarRowChrome), new PropertyMetadata(null));

    public static readonly DependencyProperty RowCommandParameterProperty = DependencyProperty.Register(
        nameof(RowCommandParameter), typeof(object), typeof(SidebarRowChrome), new PropertyMetadata(null));

    public static readonly DependencyProperty GlyphContentProperty = DependencyProperty.Register(
        nameof(GlyphContent), typeof(object), typeof(SidebarRowChrome), new PropertyMetadata(null));

    public static readonly DependencyProperty MainContentProperty = DependencyProperty.Register(
        nameof(MainContent), typeof(object), typeof(SidebarRowChrome), new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryContentProperty = DependencyProperty.Register(
        nameof(SecondaryContent), typeof(object), typeof(SidebarRowChrome), new PropertyMetadata(null));

    public static readonly DependencyProperty TrailingContentProperty = DependencyProperty.Register(
        nameof(TrailingContent), typeof(object), typeof(SidebarRowChrome), new PropertyMetadata(null));

    public SidebarRowChrome() => InitializeComponent();

    public Thickness Indent { get => (Thickness)GetValue(IndentProperty); set => SetValue(IndentProperty, value); }
    public bool ShowExpander { get => (bool)GetValue(ShowExpanderProperty); set => SetValue(ShowExpanderProperty, value); }
    public bool ShowGlyph { get => (bool)GetValue(ShowGlyphProperty); set => SetValue(ShowGlyphProperty, value); }
    public bool GlyphInExpanderCell { get => (bool)GetValue(GlyphInExpanderCellProperty); set => SetValue(GlyphInExpanderCellProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public ICommand? ToggleCommand { get => (ICommand?)GetValue(ToggleCommandProperty); set => SetValue(ToggleCommandProperty, value); }
    public object? ToggleCommandParameter { get => GetValue(ToggleCommandParameterProperty); set => SetValue(ToggleCommandParameterProperty, value); }
    public ICommand? RowCommand { get => (ICommand?)GetValue(RowCommandProperty); set => SetValue(RowCommandProperty, value); }
    public object? RowCommandParameter { get => GetValue(RowCommandParameterProperty); set => SetValue(RowCommandParameterProperty, value); }
    public object? GlyphContent { get => GetValue(GlyphContentProperty); set => SetValue(GlyphContentProperty, value); }
    public object? MainContent { get => GetValue(MainContentProperty); set => SetValue(MainContentProperty, value); }
    public object? SecondaryContent { get => GetValue(SecondaryContentProperty); set => SetValue(SecondaryContentProperty, value); }
    public object? TrailingContent { get => GetValue(TrailingContentProperty); set => SetValue(TrailingContentProperty, value); }

    private static void OnShowGlyphChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is SidebarRowChrome row)
        {
            row.UpdateGlyphColumn();
        }
    }

    private static void OnGlyphLayoutChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is SidebarRowChrome row)
        {
            row.UpdateGlyphColumn();
        }
    }

    private void UpdateGlyphColumn() => GlyphColumn.Width = GlyphInExpanderCell ? new GridLength(0) : new GridLength(20);

    private void Row_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButtonAncestor(source) is not null)
        {
            return;
        }

        if (RowCommand is { } command && command.CanExecute(RowCommandParameter))
        {
            command.Execute(RowCommandParameter);
            e.Handled = true;
        }
    }

    private static Button? FindButtonAncestor(DependencyObject source)
    {
        for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is Button button) return button;
            if (current is SidebarRowChrome) break;
        }

        return null;
    }
}
