using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Nornia.Desktop.Services;

/// <summary>Selectable accent presets. <see cref="AccentPreset.Default"/> keeps the theme's built-in
/// accent; Custom reads an <c>#RRGGBB</c>/<c>#AARRGGBB</c> value from settings.</summary>
public enum AccentPreset { Default, Teal, Iris, Custom }

/// <summary>
/// Applies a user-chosen accent color by overlaying a small resource dictionary on top of the
/// active theme dictionary. Only accent-family brushes are redefined (buttons, focus, tab-top
/// border, activity badge, selection) — never text or surface colors — keeping contrast risk
/// bounded. Hover/pressed shades are darkened from the base accent. Removing the overlay restores
/// the theme defaults, so DynamicResource consumers re-resolve automatically.
/// </summary>
public static class ThemeFactory
{
    /// <summary>Accent-family resource keys overridden while an accent is active.</summary>
    public static readonly string[] AccentFamilyKeys =
    [
        "AccentBrush",
        "ButtonBrush",
        "ButtonHoverBrush",
        "ButtonPressedBrush",
        "FocusBorderBrush",
        "TabActiveBorderTopBrush",
        "ActivityBarBadgeBackgroundBrush",
        "SelectionActiveBrush",
    ];

    private static ResourceDictionary? _accentOverride;

    /// <summary>Resolves the effective accent color, or null when the theme default should be kept
    /// (Default preset, or a malformed Custom value).</summary>
    public static Color? ResolveAccent(string? accent)
    {
        return accent?.Trim() switch
        {
            "Teal" => Color.FromRgb(0x00, 0xB2, 0x94),
            "Iris" => Color.FromRgb(0x7C, 0x5C, 0xFF),
            { } custom when TryParseHex(custom, out var color) => color,
            _ => null,
        };
    }

    /// <summary>Darkens a color by the given factor (1.0 = unchanged, 0.8 = 20% darker).</summary>
    public static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)(color.R * factor),
        (byte)(color.G * factor),
        (byte)(color.B * factor));

    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c> (leading '#' optional).</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.Transparent;
        var hex = text?.Trim().TrimStart('#');
        if (hex is null || (hex.Length != 6 && hex.Length != 8)
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (hex.Length == 6)
        {
            color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }
        else
        {
            color = Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }

        return true;
    }

    /// <summary>Applies the settings' accent as a merged-dictionary overlay; null-safe without an
    /// active WPF <see cref="Application"/> (tests), idempotent across theme switches.</summary>
    public static void ApplyAccent(string? accentValue)
    {
        if (Application.Current is null)
        {
            return;
        }

        var resources = Application.Current.Resources;
        if (_accentOverride is not null)
        {
            resources.MergedDictionaries.Remove(_accentOverride);
            _accentOverride = null;
        }

        var accent = ResolveAccent(accentValue);
        if (accent is null)
        {
            return;
        }

        var overlay = new ResourceDictionary();
        var baseColor = accent.Value;
        SetBrush(overlay, "AccentBrush", baseColor);
        SetBrush(overlay, "ButtonBrush", baseColor);
        SetBrush(overlay, "ButtonHoverBrush", Darken(baseColor, 0.88));
        SetBrush(overlay, "ButtonPressedBrush", Darken(baseColor, 0.72));
        SetBrush(overlay, "FocusBorderBrush", baseColor);
        SetBrush(overlay, "TabActiveBorderTopBrush", baseColor);
        SetBrush(overlay, "ActivityBarBadgeBackgroundBrush", baseColor);
        SetBrush(overlay, "SelectionActiveBrush", Color.FromArgb(0x66, baseColor.R, baseColor.G, baseColor.B));
        resources.MergedDictionaries.Add(overlay);
        _accentOverride = overlay;
    }

    private static void SetBrush(ResourceDictionary dictionary, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        dictionary[key] = brush;
    }
}
