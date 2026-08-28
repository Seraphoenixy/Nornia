using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace Nornia.Desktop.Localization;

/// <summary>
/// Loads <see cref="AppResources.resx"/> (and optional per-culture <c>AppResources.{culture}.resx</c>)
/// embedded resources and exposes localized strings. Uses XML parsing so the accessor is fully
/// deterministic and does not depend on IDE-generated designer files. Neutral culture values are
/// the fallback; a culture-specific resource overrides individual keys it defines.
/// </summary>
internal static class ResourceHelper
{
    private const string NeutralManifestName = "Nornia.Desktop.Localization.AppResources.xml";

    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> _perCulture = new();

    public static string GetRaw(string key, CultureInfo culture)
    {
        var effective = EffectiveCulture(culture);
        var overrides = LoadCultureSpecific(effective);
        var neutral = LoadNeutral();

        if (overrides.TryGetValue(key, out var value) && value is not null) return value;
        if (neutral.TryGetValue(key, out value) && value is not null) return value;
        return $"[{key}]";
    }

    private static CultureInfo EffectiveCulture(CultureInfo culture) =>
        culture == CultureInfo.InvariantCulture || string.IsNullOrEmpty(culture.Name)
            ? CultureInfo.InvariantCulture
            : culture;

    private static Dictionary<string, string> LoadNeutral() =>
        LoadXml(NeutralManifestName, "neutral");

    private static Dictionary<string, string> LoadCultureSpecific(CultureInfo culture)
    {
        if (culture == CultureInfo.InvariantCulture)
        {
            return Empty;
        }

        var name = $"Nornia.Desktop.Localization.AppResources.{culture.Name}.xml";
        return LoadXml(name, culture.Name);
    }

    private static Dictionary<string, string> LoadXml(string manifestName, string cultureTag)
    {
        var key = $"{cultureTag}:{manifestName}";
        return _perCulture.GetOrAdd(key, _ =>
        {
            using var stream = GetManifestResourceStreamOrDefault(manifestName);
            if (stream is null)
            {
                return Empty;
            }

            try
            {
                var doc = XDocument.Load(stream);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                return doc.Root?
                    .Elements(ns + "data")
                    .GroupBy(e => e.Attribute("name")?.Value, StringComparer.Ordinal)
                    .ToDictionary(
                        g => g.Key ?? string.Empty,
                        g => g.First().Element(ns + "value")?.Value ?? string.Empty,
                        StringComparer.Ordinal)
                    ?? Empty;
            }
            catch
            {
                return Empty;
            }
        });
    }

    private static Stream? GetManifestResourceStreamOrDefault(string manifestName)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceStream(manifestName);
    }

    private static readonly Dictionary<string, string> Empty = new(0);
}
