using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nornia.Desktop.Code;

/// <summary>One selectable symbol in a file outline (right-hand outline panel).</summary>
public sealed record CodeOutlineEntry(
    string Kind,
    string Name,
    string DisplayName,
    string Signature,
    int Line,
    int Depth,
    string Id = "",
    int EndLine = 0,
    int StartColumn = 1,
    int EndColumn = 1,
    string? ParentId = null);

/// <summary>Parses a structural file outline (C# / XML / JSON); unsupported kinds return no entries
/// so the outline panel hides itself instead of showing an empty sidebar.</summary>
public interface ICodeOutlineParser
{
    IReadOnlyList<CodeOutlineEntry> Parse(string text, CodeOutlineKind kind);
}

public sealed class CodeOutlineParser : ICodeOutlineParser
{
    public static readonly CodeOutlineParser Instance = new();

    public IReadOnlyList<CodeOutlineEntry> Parse(string text, CodeOutlineKind kind) =>
        CodeSymbolAnalyzer.Instance.Analyze(text, kind).ToOutlineEntries();

    // ===== C# via Roslyn =====

    private static IReadOnlyList<CodeOutlineEntry> ParseCSharp(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tree = CSharpSyntaxTree.ParseText(text);
        var entries = new List<CodeOutlineEntry>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case NamespaceDeclarationSyntax ns:
                    AddEntry(entries, ns, "namespace", ns.Name.ToString(), ns.Name.ToString(), NamespaceDepth(ns));
                    break;
                case FileScopedNamespaceDeclarationSyntax fileNs:
                    AddEntry(entries, fileNs, "namespace", fileNs.Name.ToString(), fileNs.Name.ToString(), NamespaceDepth(fileNs));
                    break;
                case BaseTypeDeclarationSyntax type:
                    AddEntry(entries, type, "type", TypeName(type), TypeKeyword(type) + " " + TypeName(type), TypeDepth(type));
                    break;
                case MethodDeclarationSyntax method:
                    AddEntry(entries, method, "member", method.Identifier.Text, method.ReturnType + " " + method.Identifier.Text + MethodParams(method.ParameterList), MemberDepth(method));
                    break;
                case ConstructorDeclarationSyntax ctor:
                    AddEntry(entries, ctor, "member", ctor.Identifier.Text, ctor.Identifier.Text + MethodParams(ctor.ParameterList), MemberDepth(ctor));
                    break;
                case PropertyDeclarationSyntax property:
                    AddEntry(entries, property, "member", property.Identifier.Text, property.Type + " " + property.Identifier.Text, MemberDepth(property));
                    break;
                case EventDeclarationSyntax @event:
                    AddEntry(entries, @event, "member", @event.Identifier.Text, @event.Type + " event " + @event.Identifier.Text, MemberDepth(@event));
                    break;
                case IndexerDeclarationSyntax indexer:
                    AddEntry(entries, indexer, "member", "this[]", "this" + MethodParams(indexer.ParameterList), MemberDepth(indexer));
                    break;
            }
        }

        return entries;
    }

    private static string TypeName(BaseTypeDeclarationSyntax type) =>
        type.Identifier.Text + (type is TypeDeclarationSyntax { TypeParameterList: { } typeParams } ? typeParams.ToString() : string.Empty);

    private static string TypeKeyword(BaseTypeDeclarationSyntax type) => type switch
    {
        EnumDeclarationSyntax => "enum",
        TypeDeclarationSyntax t => t.Keyword.Text,
        _ => "type",
    };

    private static string MethodParams(BaseParameterListSyntax parameters)
    {
        var parts = parameters.Parameters.Select(p => p.Type?.ToString().Split('.').LastOrDefault() ?? "?");
        return "(" + string.Join(", ", parts) + ")";
    }

    private static int TypeDepth(SyntaxNode node) => node.Ancestors().Count(a => a is BaseTypeDeclarationSyntax or NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax);

    private static int MemberDepth(SyntaxNode node) => node.Ancestors().Count(a => a is BaseTypeDeclarationSyntax) + 1;

    private static int NamespaceDepth(SyntaxNode node) => node.Ancestors().OfType<NamespaceDeclarationSyntax>().Count();

    private static void AddEntry(List<CodeOutlineEntry> entries, SyntaxNode node, string kind, string name, string signature, int depth)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        entries.Add(new CodeOutlineEntry(kind, name, name, Truncate(signature), line, depth));
    }

    // ===== XML / XAML element tree =====

    private static IReadOnlyList<CodeOutlineEntry> ParseXml(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            using var reader = new StringReader(text);
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var entries = new List<CodeOutlineEntry>();
            foreach (var element in document.Descendants())
            {
                if (element is not XElement xml)
                {
                    continue;
                }

                if (xml is not IXmlLineInfo lineInfo || !lineInfo.HasLineInfo())
                {
                    continue;
                }

                var name = xml.Name.LocalName;
                var depth = xml.Ancestors().OfType<XElement>().Count();
                entries.Add(new CodeOutlineEntry("element", name, "<" + name + ">", "<" + name + ">", lineInfo.LineNumber, depth));
            }

            return entries;
        }
        catch (XmlException)
        {
            return [];
        }
    }

    // ===== JSON property tree (via Utf8JsonReader + byte-level line index) =====

    private static IReadOnlyList<CodeOutlineEntry> ParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
        {
            return [];
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var lineStarts = BuildLineStarts(bytes);
            var entries = new List<CodeOutlineEntry>();
            var depth = 0;

            var reader = new Utf8JsonReader(bytes);
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString() ?? string.Empty;
                        var line = LineAt(lineStarts, (int)reader.TokenStartIndex);
                        entries.Add(new CodeOutlineEntry("property", name, name, name, line, Math.Max(0, depth - 1)));
                        break;
                }
            }

            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<CodeOutlineEntry> ParseMarkdown(string text)
    {
        var entries = new List<CodeOutlineEntry>();
        foreach (var (line, index) in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((line, index) => (line, index)))
        {
            var level = line.TakeWhile(ch => ch == '#').Count();
            if (level is < 1 or > 6 || line.Length <= level || !char.IsWhiteSpace(line[level])) continue;
            var name = line[(level + 1)..].Trim();
            if (name.Length > 0) entries.Add(new CodeOutlineEntry("heading", name, name, name, index + 1, level - 1));
        }

        return entries;
    }

    private static IReadOnlyList<CodeOutlineEntry> ParseIndentation(string text)
    {
        var entries = new List<CodeOutlineEntry>();
        foreach (var (line, index) in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((line, index) => (line, index)))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var nameEnd = trimmed.IndexOfAny([':', '(']);
            if (nameEnd <= 0) continue;
            var name = trimmed[..nameEnd].Trim();
            if (name.Length == 0) continue;
            var depth = line.TakeWhile(ch => ch is ' ' or '\t').Sum(ch => ch == '\t' ? 4 : 1) / 2;
            entries.Add(new CodeOutlineEntry("section", name, name, trimmed, index + 1, depth));
        }

        return entries;
    }

    private static IReadOnlyList<CodeOutlineEntry> ParseBraceDeclarations(string text)
    {
        var entries = new List<CodeOutlineEntry>();
        var depth = 0;
        foreach (var (line, index) in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((line, index) => (line, index)))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains('{') && !trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                var name = trimmed.Split(['(', '{', ' ', ':'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
                if (name.Length > 0 && name.Length < 100) entries.Add(new CodeOutlineEntry("block", name, name, trimmed, index + 1, Math.Max(0, depth)));
                depth++;
            }

            depth = Math.Max(0, depth - trimmed.Count(ch => ch == '}'));
        }

        return entries;
    }

    private static int[] BuildLineStarts(byte[] utf8)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'\n')
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

    private static string Truncate(string text, int maxLength = 140)
    {
        var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= maxLength ? single : single[..maxLength] + "…";
    }
}
