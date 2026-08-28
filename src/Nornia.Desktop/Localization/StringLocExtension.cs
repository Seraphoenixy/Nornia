using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Nornia.Desktop.Localization;

/// <summary>
/// XAML markup extension that resolves a localized string at parse time, e.g.
/// <c>Text="{loc:StringLoc Key=Nav_Settings}"</c>. Values are resolved once against the build-time
/// culture; runtime culture changes are handled by view-model property re-evaluation (see
/// <see cref="Loc.CultureChanged"/>).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StringLocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public StringLocExtension() { }
    public StringLocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        string.IsNullOrEmpty(Key) ? string.Empty : Loc.GetString(Key, CultureInfo.CurrentUICulture);
}
