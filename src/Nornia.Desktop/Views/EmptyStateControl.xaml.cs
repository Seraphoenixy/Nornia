using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nornia.Desktop.Views;

/// <summary>
/// Reusable empty-state surface (U5): a centered icon + title + optional description, plus an
/// optional context action button (U8) so the blank area offers a next step instead of only text.
/// Replace ad-hoc <see cref="TextBlock"/> empty states with this control for a consistent look.
/// </summary>
public partial class EmptyStateControl : UserControl
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(EmptyStateControl),
            new PropertyMetadata(Codicons.File));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyStateControl),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(EmptyStateControl),
            new PropertyMetadata(string.Empty));

    /// <summary>Optional primary action label; the whole <see cref="ActionCommand"/> row collapses when empty.</summary>
    public static readonly DependencyProperty ActionTextProperty =
        DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyStateControl),
            new PropertyMetadata(string.Empty, (d, _) => ((EmptyStateControl)d).UpdateActionVisibility()));

    /// <summary>Optional command executed by the <see cref="ActionText"/> button.</summary>
    public static readonly DependencyProperty ActionCommandProperty =
        DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(EmptyStateControl),
            new PropertyMetadata(null, (d, _) => ((EmptyStateControl)d).UpdateActionVisibility()));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public EmptyStateControl()
    {
        InitializeComponent();
        UpdateActionVisibility();
    }

    private void UpdateActionVisibility()
    {
        // Keep the button collapsed unless both the label and a command are provided.
        if (ActionButton is null) return;
        ActionButton.Visibility = !string.IsNullOrWhiteSpace(ActionText) && ActionCommand is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
