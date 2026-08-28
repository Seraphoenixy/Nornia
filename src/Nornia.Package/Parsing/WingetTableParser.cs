using Nornia.Core.Models;
using System.Text.RegularExpressions;

namespace Nornia.Package.Parsing;

/// <summary>
/// Parses winget's fixed-width tabular output (search/list). Headers are matched through the
/// bilingual dictionary and column boundaries come from display-width positions, so names that
/// contain two or more spaces stay intact and column order drift is tolerated. Every row that can
/// not be mapped is counted so layout drift remains observable.
/// </summary>
public sealed partial class WingetTableParser : IWingetOutputParser
{
    public string Kind => "table";

    /// <summary>Parses winget tabular output into packages.</summary>
    public WingetParseResult Parse(string output, bool isInstalled)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var separatorIndex = Array.FindIndex(lines, static line => IsSeparatorLine(line));
        if (separatorIndex <= 0)
        {
            return new WingetParseResult([], 0, 0);
        }

        var layout = WingetHeaderDictionary.Detect(lines[separatorIndex - 1]);
        var useLayout = layout.Recognized && HasRequiredColumns(layout);
        var packages = new List<PackageInfo>();
        var parsed = 0;
        var skipped = 0;

        foreach (var line in lines.Skip(separatorIndex + 1))
        {
            var (name, id, version, available, source) = ReadRow(line, layout, useLayout, isInstalled);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    skipped++;
                }

                continue;
            }

            parsed++;
            packages.Add(new PackageInfo(
                id,
                name,
                version,
                string.IsNullOrWhiteSpace(available) ? null : available,
                string.IsNullOrWhiteSpace(source) ? WingetSources.Local : source,
                isInstalled,
                WingetArchitecture.Detect(name, id)));
        }

        return new WingetParseResult(packages, parsed, skipped, useLayout ? layout.Language : null);
    }

    /// <summary>The layout is only usable for column slicing when the three required columns
    /// (name, id, version) were recognized; partially recognized headers fall back to column order.</summary>
    private static bool HasRequiredColumns(WingetHeaderDictionary.HeaderLayout layout)
    {
        var keys = layout.Columns.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        return keys.Contains("name") && keys.Contains("id") && keys.Contains("version");
    }

    private static bool IsSeparatorLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed.All(static character => character is '-' or ' ');
    }

    private static (string Name, string Id, string Version, string? Available, string Source) ReadRow(
        string line,
        WingetHeaderDictionary.HeaderLayout layout,
        bool useLayout,
        bool isInstalled)
    {
        if (useLayout)
        {
            var columns = layout.Columns;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < columns.Count; index++)
            {
                var end = index + 1 < columns.Count ? columns[index + 1].Value : int.MaxValue;
                values[columns[index].Key] = WingetDisplay.Slice(line, columns[index].Value, end).Trim();
            }

            return (
                values.GetValueOrDefault("name") ?? string.Empty,
                values.GetValueOrDefault("id") ?? string.Empty,
                values.GetValueOrDefault("version") ?? string.Empty,
                values.GetValueOrDefault("available"),
                values.GetValueOrDefault("source") ?? string.Empty);
        }

        // Unknown header: fall back to the documented column order with whitespace splitting.
        // A 4-column list row is name/id/version/source; the Available column only exists in
        // 5-column rows (and never in search output), so it must not pick up the Source column.
        var parts = ColumnSeparatorRegex().Split(line.Trim());
        var available = isInstalled && parts.Length >= 5 ? parts[3].Trim() : null;
        var source = parts.Length >= 4 ? parts[^1].Trim() : string.Empty;
        return (
            parts.Length > 0 ? parts[0].Trim() : string.Empty,
            parts.Length > 1 ? parts[1].Trim() : string.Empty,
            parts.Length > 2 ? parts[2].Trim() : string.Empty,
            available,
            source);
    }

    [GeneratedRegex("\\s{2,}")]
    private static partial Regex ColumnSeparatorRegex();
}