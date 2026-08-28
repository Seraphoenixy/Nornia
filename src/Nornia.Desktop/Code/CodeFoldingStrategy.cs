using System.IO;
using System.Xml;
using Nornia.Core.Models;

namespace Nornia.Desktop.Code;

/// <summary>One foldable region (1-based inclusive document lines).</summary>
public sealed record CodeFoldSection(int StartLine, int EndLine, bool PreserveEndLine = false, string? SymbolId = null)
{
    public bool IsMultiLine => EndLine > StartLine;
}

/// <summary>Derives foldable regions from source text for the structured languages the workbench
/// renders. Pure text logic (no WPF), so folding sections are unit-testable.</summary>
public interface ICodeFoldingStrategy
{
    IReadOnlyList<CodeFoldSection> FindSections(string text, CodeFileType fileType);
}

public sealed class CodeFoldingStrategy : ICodeFoldingStrategy
{
    private const int MaxFoldingRegions = 5000;

    public static readonly CodeFoldingStrategy Instance = new();

    public IReadOnlyList<CodeFoldSection> FindSections(string text, CodeFileType fileType) =>
        CodeSymbolAnalyzer.Instance.Analyze(text, fileType.OutlineKind)
            .ToFoldSections()
            .Take(MaxFoldingRegions)
            .ToArray();

    private sealed class CodeFoldSectionComparer : IEqualityComparer<CodeFoldSection>
    {
        public static readonly CodeFoldSectionComparer Instance = new();
        public bool Equals(CodeFoldSection? x, CodeFoldSection? y) =>
            x is not null && y is not null && x.StartLine == y.StartLine && x.EndLine == y.EndLine
            && x.PreserveEndLine == y.PreserveEndLine;
        public int GetHashCode(CodeFoldSection obj) => HashCode.Combine(obj.StartLine, obj.EndLine, obj.PreserveEndLine);
    }

    /// <summary>显式区域标记(vscode 默认 FoldingMarkers):行首 <c>#region</c> / <c>#endregion</c>
    /// 围出固定区域,不必依赖缩进/花括号。未闭合的 region 不产出折叠段。</summary>
    private static IReadOnlyList<CodeFoldSection> FindMarkerSections(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var stack = new Stack<int>();
        var sections = new List<CodeFoldSection>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("#endregion", StringComparison.Ordinal))
            {
                if (stack.Count > 0)
                {
                    var start = stack.Pop();
                    if (i + 1 > start)
                    {
                        sections.Add(new CodeFoldSection(start, i + 1, PreserveEndLine: true));
                    }
                }
            }
            else if (line.StartsWith("#region", StringComparison.Ordinal))
            {
                stack.Push(i + 1);
            }
        }

        return sections;
    }

    /// <summary>Braces folding with string + line/block comment awareness (C# / C-family); JSON also
    /// folds arrays so whole-value sections collapse.</summary>
    private static IReadOnlyList<CodeFoldSection> FindBraceSections(string text, bool foldArrays)
    {
        var sections = new List<CodeFoldSection>();
        var stack = new Stack<int>();
        var lineStarts = BuildLineStarts(text);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"':
                    SkipString(text, ref i, '\"');
                    break;
                case '\'':
                    SkipString(text, ref i, '\'');
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                    SkipLineComment(text, ref i);
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '*':
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                    {
                        i++;
                    }

                    i++;
                    break;
                case '{':
                case '[' when foldArrays:
                    stack.Push(i);
                    break;
                case '}' when stack.Count > 0:
                case ']' when foldArrays && stack.Count > 0:
                {
                    var startOffset = stack.Pop();
                    if (startOffset < i)
                    {
                        sections.Add(new CodeFoldSection(
                            LineAt(lineStarts, startOffset),
                            LineAt(lineStarts, i),
                            PreserveEndLine: true));
                    }

                    break;
                }
            }
        }

        return sections;
    }

    private static void SkipString(string text, ref int i, char quote)
    {
        i++;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                return;
            }

            i++;
        }
    }

    private static void SkipLineComment(string text, ref int i)
    {
        while (i < text.Length && text[i] != '\n')
        {
            i++;
        }
    }

    /// <summary>XML / XAML: elements that contain children start and end on different lines.</summary>
    private static IReadOnlyList<CodeFoldSection> FindXmlSections(string text)
    {
        var sections = new List<CodeFoldSection>();
        var starts = new Stack<int>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = false,
                IgnoreWhitespace = false,
            });
            while (reader.Read())
            {
                if (reader is not IXmlLineInfo lineInfo || !lineInfo.HasLineInfo())
                {
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (!reader.IsEmptyElement)
                    {
                        starts.Push(lineInfo.LineNumber);
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && starts.Count > 0)
                {
                    var startLine = starts.Pop();
                    var endLine = lineInfo.LineNumber;
                    if (endLine > startLine)
                    {
                        // The end tag is a same-level boundary and must remain visible.
                        sections.Add(new CodeFoldSection(startLine, endLine, PreserveEndLine: true));
                    }
                }
            }
        }
        catch (XmlException)
        {
            return [];
        }

        return sections;
    }

    private static IReadOnlyList<CodeFoldSection> FindIndentSections(string text)
    {
        var result = new List<CodeFoldSection>();
        var stack = new Stack<(int Indent, int Line)>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var indent = line.TakeWhile(ch => ch is ' ' or '\t').Sum(ch => ch == '\t' ? 4 : 1);
            while (stack.Count > 0 && indent <= stack.Peek().Indent)
            {
                var section = stack.Pop();
                if (index > section.Line)
                {
                    // The first line at the lower indentation is a sibling/boundary line,
                    // so it must remain visible when the preceding block is folded.
                    result.Add(new CodeFoldSection(section.Line, index + 1, PreserveEndLine: true));
                }
            }

            stack.Push((indent, index + 1));
        }

        while (stack.Count > 0)
        {
            var section = stack.Pop();
            if (lines.Length > section.Line) result.Add(new CodeFoldSection(section.Line, lines.Length));
        }

        return result;
    }

    private static IReadOnlyList<CodeFoldSection> FindMarkdownSections(string text)
    {
        var headings = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select((line, index) => (Level: line.TakeWhile(ch => ch == '#').Count(), Line: index + 1))
            .Where(item => item.Level > 0 && item.Level <= 6).ToArray();
        return headings.Select((heading, index) => new CodeFoldSection(heading.Line, index + 1 < headings.Length ? headings[index + 1].Line - 1 : text.Count(ch => ch == '\n') + 1))
            .Where(section => section.IsMultiLine).ToArray();
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    private static int LineAt(int[] lineStarts, int offset)
    {
        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo + 1;
    }
}
