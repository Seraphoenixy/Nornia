using ICSharpCode.AvalonEdit.Highlighting;
using System.Windows;
using System.Windows.Media;

namespace Nornia.Desktop.Code;

/// <summary>
/// Recolors an AvalonEdit fallback definition from the active theme's <c>CodeToken*</c> palette,
/// using the exact same rule table as the resource-manager code reader's <see cref="TokenTheme"/>
/// (scope/name fragments, last-match-wins) plus font styles. Because the same definition instance
/// is re-themed, updates to already open editors are live. Unmapped names keep their built-in color.
/// </summary>
public static class ThemeHighlightingColorizer
{
    /// <summary>Applies the current theme palette + font styles to every named color of the
    /// definition. Callers must redraw/refresh the editor view after this (e.g. <c>TextView.Redraw()</c>).</summary>
    public static void ApplyDefinitionTheme(IHighlightingDefinition definition)
    {
        foreach (var color in definition.NamedHighlightingColors)
        {
            var style = TokenTheme.ResolveFragment(color.Name);
            if (style is null)
            {
                continue;
            }

            if (style.Role is { } role && TokenTheme.CodeReaderBrushName(role) is { } token &&
                ResolveTokenColor(token) is { } themeColor)
            {
                // A fresh highlight brush per apply avoids reusing a frozen dictionary brush (a frozen
                // brush cannot change color on the next theme switch).
                color.Foreground = new SimpleHighlightingBrush(themeColor);
            }

            // Font styles ride the same rule table as the code reader (comment italic, markup bold…).
            color.FontWeight = style.Bold ? FontWeights.Bold : null;
            color.FontStyle = style.Italic ? FontStyles.Italic : null;
        }
    }

    /// <summary>Resolves a palette token to its theme color, or null when the token is missing.</summary>
    internal static Color? ResolveTokenColor(string token) =>
        Application.Current?.TryFindResource(token) is SolidColorBrush brush ? brush.Color : null;

    /// <summary>Maps an AvalonEdit color name to its <c>CodeToken*</c> palette token via the shared
    /// rule table (unmapped names return null, keeping the built-in color).</summary>
    internal static string? MatchToken(string colorName)
    {
        var style = TokenTheme.ResolveFragment(colorName);
        return style?.Role is { } role ? TokenTheme.CodeReaderBrushName(role) : null;
    }
}
