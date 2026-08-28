using System.Globalization;
using System.Resources;

namespace Nornia.CLI.Localization;

/// <summary>Resource accessor for CLI user-facing messages. The neutral resource is English; the
/// <c>zh-Hans</c> satellite provides Simplified Chinese. Selection follows <see cref="CultureInfo.CurrentUICulture"/>
/// which <see cref="Program"/> configures from the system locale or the <c>--lang</c> option.</summary>
public static class CliText
{
    private static readonly ResourceManager Manager = new("Nornia.CLI.Localization.CliMessages", typeof(CliText).Assembly);

    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}