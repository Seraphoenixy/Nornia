using System.Globalization;
using System.Resources;

namespace Nornia.Package.Localization;

/// <summary>Resource accessor for package-manager user-facing messages. The neutral resource is
/// English and the <c>zh-Hans</c> satellite provides Simplified Chinese. Selection follows
/// <see cref="CultureInfo.CurrentUICulture"/>, which the CLI configures from the system locale or
/// <c>--lang</c> and Desktop inherits from the OS. Missing keys fall back to the key itself.</summary>
public static class WingetText
{
    private static readonly ResourceManager Manager = new("Nornia.Package.Localization.WingetMessages", typeof(WingetText).Assembly);

    public static string Get(string key) => Get(key, CultureInfo.CurrentUICulture);

    public static string Get(string key, CultureInfo culture) => Manager.GetString(key, culture) ?? key;

    public static string Format(string key, params object?[] args) =>
        Format(key, CultureInfo.CurrentUICulture, args);

    public static string Format(string key, CultureInfo culture, params object?[] args) =>
        string.Format(culture, Get(key, culture), args);
}