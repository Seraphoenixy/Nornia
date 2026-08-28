namespace Nornia.Package.Parsing;

/// <summary>
/// Bilingual Winget table headers. Winget localizes its tabular output to the OS display language
/// (English neutral or Simplified Chinese observed so far), so parsing recognizes every word per
/// canonical column and derives column boundaries from display-width positions rather than
/// whitespace splitting.
/// </summary>
internal static class WingetHeaderDictionary
{
    /// <summary>Canonical column key → word per language. New languages only need a new entry here.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Columns =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["name"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "Name",
                ["zh-Hans"] = "名称",
            },
            ["id"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "Id",
                ["zh-Hans"] = "ID",
            },
            ["version"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "Version",
                ["zh-Hans"] = "版本",
            },
            ["available"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "Available",
                ["zh-Hans"] = "可用",
            },
            ["source"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "Source",
                ["zh-Hans"] = "源",
            },
        };

    /// <summary>Header inspection outcome: canonical columns with display-width start offsets.</summary>
    public sealed record HeaderLayout(string? Language, IReadOnlyList<KeyValuePair<string, int>> Columns, bool Recognized);

    /// <summary>Matches the header words in <paramref name="headerLine"/> against the dictionary and
    /// returns a layout ordered by display position. Returns <see cref="HeaderLayout.Recognized"/>
    /// <c>false</c> when no header word matches, so callers can fall back to column splitting.</summary>
    public static HeaderLayout Detect(string headerLine)
    {
        var matched = new List<KeyValuePair<string, int>>();
        var languageVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (word, start) in Tokenize(headerLine))
        {
            var canonical = FindCanonical(word, out var language);
            if (canonical is null)
            {
                continue;
            }

            matched.Add(new KeyValuePair<string, int>(canonical, start));
            languageVotes[language!] = languageVotes.GetValueOrDefault(language!) + 1;
        }

        if (matched.Count == 0)
        {
            return new HeaderLayout(null, [], false);
        }

        var detectedLanguage = languageVotes.OrderByDescending(pair => pair.Value).First().Key;
        return new HeaderLayout(detectedLanguage, matched.OrderBy(pair => pair.Value).ToList(), true);
    }

    private static string? FindCanonical(string word, out string? language)
    {
        foreach (var (canonical, words) in Columns)
        {
            foreach (var (lang, candidate) in words)
            {
                if (string.Equals(word, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    language = lang;
                    return canonical;
                }
            }
        }

        language = null;
        return null;
    }

    /// <summary>Splits the header line into words with the display-width offset where each word starts.</summary>
    private static IReadOnlyList<(string Word, int Start)> Tokenize(string headerLine)
    {
        var tokens = new List<(string Word, int Start)>();
        var position = 0;
        var tokenStart = -1;
        for (var index = 0; index < headerLine.Length; index++)
        {
            var character = headerLine[index];
            if (character == ' ')
            {
                if (tokenStart >= 0)
                {
                    tokens.Add((headerLine[tokenStart..index], position - WingetDisplay.Width(headerLine[tokenStart..index])));
                    tokenStart = -1;
                }

                position += 1;
            }
            else
            {
                if (tokenStart < 0)
                {
                    tokenStart = index;
                }

                position += WingetDisplay.Width(character);
            }
        }

        if (tokenStart >= 0)
        {
            tokens.Add((headerLine[tokenStart..], position - WingetDisplay.Width(headerLine[tokenStart..])));
        }

        return tokens;
    }
}