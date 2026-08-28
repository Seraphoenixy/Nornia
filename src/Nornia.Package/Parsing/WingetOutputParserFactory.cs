using Nornia.Package.Providers;

namespace Nornia.Package.Parsing;

/// <summary>Selects the parser matching the live winget CLI capabilities. The structured JSON
/// protocol is preferred when available; the tabular format is the current default.</summary>
public static class WingetOutputParserFactory
{
    public static IWingetOutputParser Create(WingetCapabilities capabilities) =>
        capabilities.HasStructuredOutput ? new WingetJsonParser() : new WingetTableParser();
}