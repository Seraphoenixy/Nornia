using System.Globalization;

namespace Nornia.Desktop.Localization;

/// <summary>
/// Central access point for localized strings. Raises <see cref="CultureChanged"/> whenever the
/// effective UI culture changes so view models can re-raise localized display properties.
/// </summary>
public static class Loc
{
    /// <summary>
    /// Raised on the UI thread when the effective UI culture changes. View models bound to
    /// localized display strings should re-raise those properties in response.
    /// </summary>
    public static event EventHandler<CultureInfo>? CultureChanged;

    /// <summary>Look up a localized string by key, falling back to <c>[key]</c> when missing.</summary>
    public static string GetString(string key) =>
        GetString(key, CultureInfo.CurrentUICulture);

    public static string GetString(string key, CultureInfo culture) =>
        ResourceHelper.GetRaw(key, culture);

    /// <summary>Format a localized string with <see cref="string.Format"/>-style arguments.</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(GetString(key), args);

    /// <summary>Re-evaluate the current culture and notify listeners if it changed.</summary>
    private static CultureInfo _lastCulture = CultureInfo.CurrentUICulture;

    public static void Refresh()
    {
        var current = CultureInfo.CurrentUICulture;
        if (Equals(current, _lastCulture)) return;
        _lastCulture = current;
        CultureChanged?.Invoke(null, current);
    }
}
