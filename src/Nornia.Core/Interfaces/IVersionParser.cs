using System.Text.RegularExpressions;

namespace Nornia.Core.Interfaces;

/// <summary>Extracts a normalized version string from a tool's raw command output. Providers inject
/// a strategy (plain trimming, regex extraction) instead of each implementing their own parsing.</summary>
public interface IVersionParser
{
    string? Parse(string output);
}

/// <summary>Returns the trimmed output with a leading 'v'/'V' prefix removed (Node.js style).</summary>
public sealed class PlainVersionParser : IVersionParser
{
    public string? Parse(string output) =>
        output.Trim().TrimStart('v', 'V') is { Length: > 0 } version ? version : null;
}

/// <summary>Extracts a named regex group (default "version") from the output.</summary>
public sealed class RegexVersionParser(Regex pattern, string groupName = "version") : IVersionParser
{
    public string? Parse(string output) =>
        pattern.Match(output) is { Success: true } match ? match.Groups[groupName].Value : null;
}