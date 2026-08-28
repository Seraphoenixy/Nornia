using System.Windows;
using System.Windows.Media;

namespace Nornia.Desktop.Behaviors;

/// <summary>
/// Bridges the current <see cref="DataContext"/> into <see cref="ContextMenu"/> content. Context
/// menus are hosted in a separate popup and are not part of the placement target's visual tree, so
/// <c>RelativeSource FindAncestor</c> bindings cannot reach the owning view model from menu items.
/// Freezable resources participate in data-context inheritance, which is exactly what this bridge
/// uses: <c>{Binding Value, Source={StaticResource Proxy}}</c> inside a menu resolves to the host's
/// view model.
/// </summary>
public sealed class DataContextBridge : Freezable
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(object), typeof(DataContextBridge), new PropertyMetadata(null));

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new DataContextBridge();
}