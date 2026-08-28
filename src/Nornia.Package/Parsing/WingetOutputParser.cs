using Nornia.Core.Models;
using System.Text.RegularExpressions;

namespace Nornia.Package.Parsing;

/// <summary>Parses one Winget command's standard output into package records. A parser is chosen per
/// live winget capabilities (tabular text vs structured JSON) through <see cref="WingetOutputParserFactory"/>.</summary>
public interface IWingetOutputParser
{
    /// <summary>"table" for the fixed-width tabular format, "json" for the structured output protocol.</summary>
    string Kind { get; }

    WingetParseResult Parse(string output, bool isInstalled);
}

/// <summary>winget 来源标签约定。<see cref="Local"/> 标注 winget list 中来源列为空的 ARP 条目
/// ——本机自行安装(非 winget/msstore 目录)的应用:它们不是 winget 安装的,winget 目录里也没有,
/// 不得默认显示为 "winget";卸载这类条目需按显示名(<c>--name</c>)匹配而非目录 Id。</summary>
public static class WingetSources
{
    public const string Local = "本机";
}

/// <summary>Parse outcome with diagnostics. Skipped rows are counted so table-layout drift stays
/// observable instead of silently dropping packages.</summary>
public sealed record WingetParseResult(
    IReadOnlyList<PackageInfo> Packages,
    int ParsedRowCount,
    int SkippedRowCount,
    string? HeaderLanguage = null);

/// <summary>Display-width helpers shared by the table parser. CJK characters occupy two columns, so
/// column slicing must count display width instead of string length.</summary>
internal static class WingetDisplay
{
    public static int Width(char character) => character is
        (>= '\u1100' and <= '\u115F') or
        (>= '\u2E80' and <= '\uA4CF') or
        (>= '\uAC00' and <= '\uD7A3') or
        (>= '\uF900' and <= '\uFAFF') or
        (>= '\uFE10' and <= '\uFE6F') or
        (>= '\uFF00' and <= '\uFF60') or
        (>= '\uFFE0' and <= '\uFFE6') ? 2 : 1;

    public static int Width(string value) => value.Sum(Width);

    /// <summary>Returns the substring occupying display columns <c>[start, end)</c>.</summary>
    public static string Slice(string value, int start, int end)
    {
        var result = new System.Text.StringBuilder();
        var position = 0;
        foreach (var character in value)
        {
            if (position >= end)
            {
                break;
            }

            if (position + Width(character) > start)
            {
                result.Append(character);
            }

            position += Width(character);
        }

        return result.ToString();
    }
}

/// <summary>Heuristic architecture detection from a package's display name and id. Winget does not
/// emit the architecture as a column, so the value is a best-effort guess aligned with the package
/// record's display purpose.</summary>
internal static partial class WingetArchitecture
{
    public static string Detect(string name, string id)
    {
        var text = $"{name} {id}";
        return ArchitectureRegex().Match(text) is { Success: true } match
            ? match.Value.ToUpperInvariant()
            : "Unknown";
    }

    [GeneratedRegex("(?<![A-Za-z0-9])(x64|x86|arm64)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureRegex();
}