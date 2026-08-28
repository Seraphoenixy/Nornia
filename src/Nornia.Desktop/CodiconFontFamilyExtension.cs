using System.Windows.Markup;
using System.Windows.Media;

namespace Nornia.Desktop;

/// <summary>
/// Creates the embedded Codicon font with the base URI required by WPF's resource-font loader.
/// </summary>
[MarkupExtensionReturnType(typeof(FontFamily))]
public sealed class CodiconFontFamilyExtension : MarkupExtension
{
    private static readonly Uri AssemblyBaseUri = new("pack://application:,,,/Nornia.Desktop;component/");

    public override object ProvideValue(IServiceProvider? serviceProvider) =>
        new FontFamily(AssemblyBaseUri, "./Assets/#codicon");
}
