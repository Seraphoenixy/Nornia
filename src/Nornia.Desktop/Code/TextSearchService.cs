using System.Text.RegularExpressions;

namespace Nornia.Desktop.Code;

/// <summary>Plain-text search options shared by the find bar and the find state on preview tabs.</summary>
public sealed record TextSearchOptions(bool CaseSensitive, bool WholeWord, bool UseRegex);

/// <summary>One search hit: byte-agnostic char offsets into the document text plus its 1-based line.</summary>
public sealed record TextSearchMatch(int Length, int Offset, int Line);

/// <summary>Finds text matches for the read-only workbench find bar. Pure string logic so the
/// case / whole-word / regex modes are unit-testable without any editor instance.</summary>
public interface ITextSearchService
{
    IReadOnlyList<TextSearchMatch> FindAll(string text, string term, TextSearchOptions options);
}

public sealed class TextSearchService : ITextSearchService
{
    public static readonly TextSearchService Instance = new();

    public IReadOnlyList<TextSearchMatch> FindAll(string text, string term, TextSearchOptions options)
    {
        if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (options.UseRegex)
        {
            return FindRegex(text, term, options);
        }

        return FindPlain(text, term, options);
    }

    private static IReadOnlyList<TextSearchMatch> FindPlain(string text, string term, TextSearchOptions options)
    {
        var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<TextSearchMatch>();
        var index = 0;
        var line = 1;
        var lineStart = 0;
        while ((index = text.IndexOf(term, index, comparison)) >= 0)
        {
            if (!options.WholeWord || IsWholeWord(text, index, term.Length))
            {
                AdvanceLine(text, ref line, ref lineStart, index);
                matches.Add(new TextSearchMatch(term.Length, index, line));
            }

            index += Math.Max(term.Length, 1);
        }

        return matches;
    }

    /// <summary>Whole-word rule: the characters directly before/after the match are not word
    /// characters (letters, digits or underscore) — the classic <c>\b</c> definition. Shared with
    /// the disk-based <see cref="FileReadOnlyDocumentSource"/> so line-streamed search keeps the
    /// exact same boundary semantics.</summary>
    internal static bool IsWholeWord(string text, int offset, int length)
    {
        if (offset > 0 && IsWordChar(text[offset - 1]))
        {
            return false;
        }

        var end = offset + length;
        return end >= text.Length || !IsWordChar(text[end]);
    }

    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Validates a regular expression for the find bar. Returns null when the pattern
    /// compiles (or the term is empty); otherwise the compiler error message the view shows.</summary>
    public static string? GetRegexError(string? term, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(term))
        {
            return null;
        }

        try
        {
            var regexOptions = RegexOptions.CultureInvariant;
            if (!caseSensitive)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            _ = new Regex(term, regexOptions);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static IReadOnlyList<TextSearchMatch> FindRegex(string text, string term, TextSearchOptions options)
    {
        if (GetRegexError(term, options.CaseSensitive) is not null)
        {
            // Malformed expression: report no matches instead of throwing into the UI.
            return [];
        }

        var regex = new Regex(term, BuildRegexOptions(options.CaseSensitive));
        var matches = new List<TextSearchMatch>();
        var line = 1;
        var lineStart = 0;
        foreach (Match match in regex.Matches(text))
        {
            // Whole-word is enforced on top of the regular expression so the toggle stays
            // meaningful in regex mode too.
            if (options.WholeWord && !IsWholeWord(text, match.Index, match.Length))
            {
                continue;
            }

            AdvanceLine(text, ref line, ref lineStart, match.Index);
            matches.Add(new TextSearchMatch(match.Length, match.Index, line));
        }

        return matches;
    }

    internal static RegexOptions BuildRegexOptions(bool caseSensitive) =>
        RegexOptions.CultureInvariant | (caseSensitive ? 0 : RegexOptions.IgnoreCase);

    /// <summary>单通行号推进:行光标 (line, lineStart) 随匹配偏移单调前移,全文每个字符至多
    /// 扫描一次(总成本 O(n))。旧实现对每个匹配都从文件头重数 '\n'(匹配多时近 O(n²),
    /// 8MB + 万级匹配可达秒级)。</summary>
    private static void AdvanceLine(string text, ref int line, ref int lineStart, int offset)
    {
        for (var i = lineStart; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
    }
}