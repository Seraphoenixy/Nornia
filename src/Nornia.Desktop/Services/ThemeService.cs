using System;
using System.Linq;
using System.Windows;

namespace Nornia.Desktop.Services;

/// <summary>Selectable application themes (A2).</summary>
public enum AppTheme
{
    Dark,
    Light,
    HighContrast
}

/// <summary>
/// Swaps the active theme resource dictionary at runtime. All color references in the app use
/// <c>DynamicResource</c>, so replacing the merged dictionary re-themes the shell and any open
/// windows immediately. Page content resolves its theme at parse time, so a full re-theme of already
/// opened pages is guaranteed after a restart (the choice is persisted, so it applies on next launch).
/// </summary>
public static class ThemeService
{
    private const string DarkUri = "pack://application:,,,/Nornia.Desktop;component/Themes/Dark.xaml";
    private const string LightUri = "pack://application:,,,/Nornia.Desktop;component/Themes/Light.xaml";
    private const string HighContrastUri = "pack://application:,,,/Nornia.Desktop;component/Themes/HighContrast.xaml";

    public static void Apply(AppTheme theme)
    {
        if (Application.Current is null) return;
        var resources = Application.Current.Resources;
        var themeDict = resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString?.Contains("Themes/", StringComparison.OrdinalIgnoreCase) == true);
        var uri = theme switch
        {
            AppTheme.Light => new Uri(LightUri, UriKind.Absolute),
            AppTheme.HighContrast => new Uri(HighContrastUri, UriKind.Absolute),
            _ => new Uri(DarkUri, UriKind.Absolute),
        };

        if (themeDict is not null)
        {
            themeDict.Source = uri;
        }
        else
        {
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        }

        // Broadcast so code-facing views (editor chrome, highlighting palette, git graph lanes)
        // re-resolve their brushes immediately instead of waiting for a restart.
        ThemeEvents.Raise(theme);
    }
}
