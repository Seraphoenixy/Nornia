using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nornia.Core.Models;

namespace Nornia.Desktop.Code;

/// <summary>1-based line/column range. The end position is exclusive, matching VS Code ranges.</summary>
public readonly record struct CodeSymbolRange(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public bool IsMultiLine => EndLine > StartLine;

    public bool Contains(int line, int column = 1)
    {
        if (line < StartLine || line > EndLine)
        {
            return false;
        }

        if (line == StartLine && column < StartColumn)
        {
            return false;
        }

        return line != EndLine || column < EndColumn;
    }

    public bool Contains(CodeSymbolRange other) =>
        Contains(other.StartLine, other.StartColumn)
        && (other.EndLine < EndLine || other.EndLine == EndLine && other.EndColumn <= EndColumn);

    public int SpanLength => Math.Max(0, (EndLine - StartLine) * 10_000 + EndColumn - StartColumn);
}

/// <summary>One symbol node shared by the outline, breadcrumbs and folding projection.</summary>
public sealed class CodeSymbolNode : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isActive;
    private IReadOnlyList<CodeSymbolNode> _visibleChildren = [];

    internal CodeSymbolNode(
        string id,
        string kind,
        string name,
        string displayName,
        string signature,
        CodeSymbolRange range,
        CodeSymbolRange selectionRange,
        CodeSymbolRange? foldingRange,
        bool showInOutline,
        bool preserveEndLine)
    {
        Id = id;
        Kind = kind;
        Name = name;
        DisplayName = displayName;
        Signature = signature;
        Range = range;
        SelectionRange = selectionRange;
        FoldingRange = foldingRange ?? range;
        ShowInOutline = showInOutline;
        PreserveEndLine = preserveEndLine;
    }

    public string Id { get; internal set; }
    public string Kind { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string Signature { get; }
    public CodeSymbolRange Range { get; }
    public CodeSymbolRange SelectionRange { get; }
    public CodeSymbolRange FoldingRange { get; }
    public CodeSymbolNode? Parent { get; internal set; }
    public IReadOnlyList<CodeSymbolNode> Children { get; internal set; } = [];
    public IReadOnlyList<CodeSymbolNode> VisibleChildren
    {
        get => _visibleChildren;
        internal set => SetField(ref _visibleChildren, value);
    }
    public bool ShowInOutline { get; }
    public bool PreserveEndLine { get; }
    public bool IsFoldable => Range.IsMultiLine;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>Immutable symbol document with VS Code-style containment queries.</summary>
public sealed class CodeSymbolDocument
{
    internal CodeSymbolDocument(IReadOnlyList<CodeSymbolNode> roots)
    {
        Roots = roots;
        All = Flatten(roots).ToArray();
    }

    public static CodeSymbolDocument Empty { get; } = new([]);
    public IReadOnlyList<CodeSymbolNode> Roots { get; }
    public IReadOnlyList<CodeSymbolNode> All { get; }

    public CodeSymbolNode? FindById(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(node => node.Id == id);

    public CodeSymbolNode? FindDeepestContaining(int line, int column = 1)
    {
        CodeSymbolNode? result = null;
        foreach (var node in All)
        {
            if (node.ShowInOutline && node.Range.Contains(line, column)
                && (result is null || node.Range.SpanLength < result.Range.SpanLength))
            {
                result = node;
            }
        }

        return result;
    }

    public IReadOnlyList<CodeSymbolNode> GetAncestors(CodeSymbolNode? node, int maxCount = int.MaxValue)
    {
        var chain = new List<CodeSymbolNode>();
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current.ShowInOutline)
            {
                chain.Add(current);
            }
        }

        chain.Reverse();
        return chain.Count <= maxCount ? chain : chain[^maxCount..];
    }

    public IReadOnlyList<CodeOutlineEntry> ToOutlineEntries()
    {
        var entries = new List<CodeOutlineEntry>();
        AddVisibleEntries(Roots, 0, null, entries);
        return entries;
    }

    public IReadOnlyList<CodeSymbolNode> Filter(string query)
    {
        var normalized = query.Trim();
        var roots = new List<CodeSymbolNode>();
        foreach (var root in Roots)
        {
            if (ApplyFilter(root, normalized))
            {
                roots.Add(root);
            }
        }

        return roots;
    }

    private static bool ApplyFilter(CodeSymbolNode node, string query)
    {
        var matching = query.Length == 0
            || node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || node.Signature.Contains(query, StringComparison.OrdinalIgnoreCase);
        var children = node.Children.Where(child => ApplyFilter(child, query)).ToArray();
        node.VisibleChildren = query.Length == 0
            ? VisibleChildren(node.Children)
            : children.SelectMany(child => child.ShowInOutline ? [child] : child.VisibleChildren).ToArray();
        return matching || children.Length > 0;
    }

    internal static IReadOnlyList<CodeSymbolNode> VisibleChildren(IEnumerable<CodeSymbolNode> nodes) =>
        nodes.SelectMany(child => child.ShowInOutline ? [child] : VisibleChildren(child.Children)).ToArray();

    public IReadOnlyList<CodeFoldSection> ToFoldSections()
    {
        return All
            .Where(node => node.IsFoldable)
            .GroupBy(node => (node.FoldingRange.StartLine, node.FoldingRange.EndLine, node.PreserveEndLine))
            .Select(group =>
            {
                var node = group.FirstOrDefault(candidate => candidate.ShowInOutline) ?? group.First();
                return new CodeFoldSection(
                    group.Key.StartLine,
                    group.Key.EndLine,
                    group.Key.PreserveEndLine,
                    node.Id);
            })
            .OrderBy(section => section.StartLine)
            .ThenByDescending(section => section.EndLine)
            .ToArray();
    }

    private static void AddVisibleEntries(
        IEnumerable<CodeSymbolNode> nodes,
        int depth,
        string? visibleParentId,
        ICollection<CodeOutlineEntry> entries)
    {
        foreach (var node in nodes)
        {
            var nextDepth = depth;
            var nextParentId = visibleParentId;
            if (node.ShowInOutline)
            {
                entries.Add(new CodeOutlineEntry(
                    node.Kind,
                    node.Name,
                    node.DisplayName,
                    node.Signature,
                    node.Range.StartLine,
                    depth,
                    node.Id,
                    node.Range.EndLine,
                    node.Range.StartColumn,
                    node.Range.EndColumn,
                    visibleParentId));
                nextDepth++;
                nextParentId = node.Id;
            }

            AddVisibleEntries(node.Children, nextDepth, nextParentId, entries);
        }
    }

    private static IEnumerable<CodeSymbolNode> Flatten(IEnumerable<CodeSymbolNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }
}

public interface ICodeSymbolAnalyzer
{
    CodeSymbolDocument Analyze(string text, CodeOutlineKind kind);
}

/// <summary>Builds the shared symbol tree consumed by outline, breadcrumbs and folding.</summary>
public sealed class CodeSymbolAnalyzer : ICodeSymbolAnalyzer
{
    private sealed record Seed(
        string Kind,
        string Name,
        string DisplayName,
        string Signature,
        CodeSymbolRange Range,
        CodeSymbolRange SelectionRange,
        CodeSymbolRange? FoldingRange = null,
        bool ShowInOutline = true,
        bool PreserveEndLine = true,
        int? HierarchyLevel = null);

    private sealed class XmlDraft
    {
        public required string Name { get; init; }
        public required int StartLine { get; init; }
        public required int StartColumn { get; init; }
        public int EndLine { get; set; }
        public int EndColumn { get; set; }
        public bool Completed { get; set; }
    }

    private sealed class JsonFrame
    {
        public required Seed Container { get; set; }
        public Seed? OwnerProperty { get; init; }
        public Seed? PendingProperty { get; set; }
    }

    public static readonly CodeSymbolAnalyzer Instance = new();

    public CodeSymbolDocument Analyze(string text, CodeOutlineKind kind)
    {
        if (string.IsNullOrWhiteSpace(text) || kind == CodeOutlineKind.None)
        {
            return CodeSymbolDocument.Empty;
        }

        try
        {
            var seeds = kind switch
            {
                CodeOutlineKind.CSharp => AnalyzeCSharp(text),
                CodeOutlineKind.Xml => AnalyzeXml(text),
                CodeOutlineKind.Json => AnalyzeJson(text),
                CodeOutlineKind.Brace => AnalyzeBraces(text),
                CodeOutlineKind.Indentation => AnalyzeIndentation(text),
                CodeOutlineKind.Markdown => AnalyzeMarkdown(text),
                _ => [],
            };
            return BuildDocument(seeds);
        }
        catch (Exception) when (kind is CodeOutlineKind.CSharp or CodeOutlineKind.Xml or CodeOutlineKind.Json)
        {
            // Keep the preview usable. Individual language analyzers already retain completed
            // nodes; this final guard only protects the UI from an unexpected parser failure.
            return CodeSymbolDocument.Empty;
        }
    }

    private static IReadOnlyList<Seed> AnalyzeCSharp(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var seeds = new List<Seed>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case NamespaceDeclarationSyntax ns:
                    AddSyntaxSeed(seeds, ns, "namespace", ns.Name.ToString(), ns.Name.ToString());
                    break;
                case FileScopedNamespaceDeclarationSyntax fileNs:
                    AddSyntaxSeed(seeds, fileNs, "namespace", fileNs.Name.ToString(), fileNs.Name.ToString());
                    break;
                case BaseTypeDeclarationSyntax type:
                    AddSyntaxSeed(seeds, type, "type", TypeName(type), TypeKeyword(type) + " " + TypeName(type));
                    break;
                case MethodDeclarationSyntax method:
                    AddSyntaxSeed(seeds, method, "member", method.Identifier.Text,
                        method.ReturnType + " " + method.Identifier.Text + MethodParams(method.ParameterList));
                    break;
                case ConstructorDeclarationSyntax ctor:
                    AddSyntaxSeed(seeds, ctor, "member", ctor.Identifier.Text,
                        ctor.Identifier.Text + MethodParams(ctor.ParameterList));
                    break;
                case PropertyDeclarationSyntax property:
                    AddSyntaxSeed(seeds, property, "member", property.Identifier.Text,
                        property.Type + " " + property.Identifier.Text);
                    break;
                case EventDeclarationSyntax @event:
                    AddSyntaxSeed(seeds, @event, "member", @event.Identifier.Text,
                        @event.Type + " event " + @event.Identifier.Text);
                    break;
                case IndexerDeclarationSyntax indexer:
                    AddSyntaxSeed(seeds, indexer, "member", "this[]",
                        "this" + MethodParams(indexer.ParameterList));
                    break;
            }
        }

        // Roslyn's declaration range starts on the declaration line, while the editor's fold
        // marker belongs on the line containing the opening brace. Keep both identities.
        var hasDeclaration = seeds.Count > 0;
        var braceSeeds = AnalyzeBraces(text, showInOutline: false, includeMarkers: false);
        if (!hasDeclaration || braceSeeds.Count > 0)
        {
            seeds.AddRange(braceSeeds);
        }

        AddMarkerSeeds(text, seeds);
        return seeds;
    }

    private static void AddSyntaxSeed(List<Seed> seeds, SyntaxNode node, string kind, string name, string signature)
    {
        var span = node.GetLocation().GetLineSpan();
        var range = new CodeSymbolRange(
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
        var selection = new CodeSymbolRange(range.StartLine, range.StartColumn, range.StartLine, range.StartColumn + Math.Max(1, name.Length));
        var openBrace = node.DescendantTokens().FirstOrDefault(token => token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OpenBraceToken));
        CodeSymbolRange? foldingRange = null;
        if (openBrace.RawKind != 0)
        {
            var open = openBrace.GetLocation().GetLineSpan().StartLinePosition;
            foldingRange = new CodeSymbolRange(open.Line + 1, open.Character + 1, range.EndLine, range.EndColumn);
        }

        seeds.Add(new Seed(kind, name, name, Truncate(signature), range, selection, foldingRange));
    }

    private static IReadOnlyList<Seed> AnalyzeXml(string text)
    {
        var drafts = new List<XmlDraft>();
        var stack = new Stack<XmlDraft>();
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
                if (reader is not IXmlLineInfo info || !info.HasLineInfo())
                {
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    var draft = new XmlDraft
                    {
                        Name = reader.LocalName,
                        StartLine = info.LineNumber,
                        StartColumn = info.LinePosition + 1,
                    };
                    drafts.Add(draft);
                    if (reader.IsEmptyElement)
                    {
                        draft.EndLine = info.LineNumber;
                        draft.EndColumn = info.LinePosition + Math.Max(2, reader.Name.Length + 3);
                        draft.Completed = true;
                    }
                    else
                    {
                        stack.Push(draft);
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && stack.Count > 0)
                {
                    var draft = stack.Pop();
                    draft.EndLine = info.LineNumber;
                    draft.EndColumn = info.LinePosition + Math.Max(2, reader.Name.Length + 3);
                    draft.Completed = true;
                }
            }
        }
        catch (XmlException)
        {
            // Completed elements remain usable even when a later element is malformed.
        }

        return drafts
            .Where(draft => draft.Completed)
            .Select(draft =>
            {
                var range = new CodeSymbolRange(draft.StartLine, draft.StartColumn, draft.EndLine, draft.EndColumn);
                return new Seed("element", draft.Name, "<" + draft.Name + ">", "<" + draft.Name + ">", range,
                    new CodeSymbolRange(draft.StartLine, draft.StartColumn, draft.StartLine, draft.StartColumn + draft.Name.Length + 2));
            })
            .ToArray();
    }

    private static IReadOnlyList<Seed> AnalyzeJson(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var seeds = new List<Seed>();
        var frames = new Stack<JsonFrame>();
        try
        {
            var reader = new Utf8JsonReader(bytes);
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                    {
                        var start = RangeAt(bytes, (int)reader.TokenStartIndex, (int)reader.TokenStartIndex + 1);
                        var container = new Seed(
                            reader.TokenType == JsonTokenType.StartObject ? "object" : "array",
                            reader.TokenType == JsonTokenType.StartObject ? "{}" : "[]",
                            reader.TokenType == JsonTokenType.StartObject ? "{}" : "[]",
                            reader.TokenType == JsonTokenType.StartObject ? "object" : "array",
                            start, start, ShowInOutline: false);
                        var ownerProperty = frames.Count > 0 ? frames.Peek().PendingProperty : null;
                        if (frames.Count > 0) frames.Peek().PendingProperty = null;

                        seeds.Add(container);
                        frames.Push(new JsonFrame { Container = container, OwnerProperty = ownerProperty });
                        break;
                    }
                    case JsonTokenType.PropertyName:
                        if (frames.Count > 0)
                        {
                            var name = reader.GetString() ?? string.Empty;
                            var start = RangeAt(bytes, (int)reader.TokenStartIndex, (int)reader.BytesConsumed);
                            frames.Peek().PendingProperty = new Seed("property", name, name, name, start, start);
                        }

                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (frames.Count > 0)
                        {
                            var frame = frames.Pop();
                            var end = RangeAt(bytes, (int)frame.Container.Range.StartByte(bytes), (int)reader.BytesConsumed);
                            frame.Container = frame.Container with
                            {
                                Range = new CodeSymbolRange(frame.Container.Range.StartLine, frame.Container.Range.StartColumn, end.EndLine, end.EndColumn),
                            };
                            ReplaceSeed(seeds, frame.Container);
                            if (frame.OwnerProperty is { } ownerProperty)
                            {
                                seeds.Add(ownerProperty with
                                {
                                    Range = new CodeSymbolRange(ownerProperty.Range.StartLine, ownerProperty.Range.StartColumn, end.EndLine, end.EndColumn),
                                });
                            }
                        }

                        break;
                    default:
                        if (frames.Count > 0 && frames.Peek().PendingProperty is { } scalarProperty
                            && reader.TokenType is not JsonTokenType.Comment)
                        {
                            var end = RangeAt(bytes, (int)reader.TokenStartIndex, (int)reader.BytesConsumed);
                            seeds.Add(scalarProperty with
                            {
                                Range = new CodeSymbolRange(scalarProperty.Range.StartLine, scalarProperty.Range.StartColumn, end.EndLine, end.EndColumn),
                            });
                            frames.Peek().PendingProperty = null;
                        }

                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Keep completed scalar/property and closed-container seeds collected so far.
        }

        return seeds.ToArray();
    }

    private static IReadOnlyList<Seed> AnalyzeBraces(string text, bool showInOutline = true, bool includeMarkers = true)
    {
        var seeds = new List<Seed>();
        var stack = new Stack<(int Offset, char Open)>();
        var lineStarts = BuildLineStarts(text);
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"': SkipString(text, ref i, '"'); break;
                case '\'': SkipString(text, ref i, '\''); break;
                case '/' when i + 1 < text.Length && text[i + 1] == '/': SkipLineComment(text, ref i); break;
                case '{': stack.Push((i, '{')); break;
                case '}' when stack.Count > 0:
                {
                    var start = stack.Pop().Offset;
                    if (start < i)
                    {
                        var startPos = PositionAt(lineStarts, start, text);
                        var endPos = PositionAt(lineStarts, i, text);
                        var name = HeaderName(text, start);
                        seeds.Add(new Seed("block", name, name, name,
                            new CodeSymbolRange(startPos.Line, startPos.Column, endPos.Line, endPos.Column + 1),
                            new CodeSymbolRange(startPos.Line, startPos.Column, startPos.Line, startPos.Column + Math.Max(1, name.Length)),
                            ShowInOutline: showInOutline));
                    }

                    break;
                }
            }
        }

        if (includeMarkers)
        {
            AddMarkerSeeds(text, seeds);
        }
        return seeds;
    }

    private static IReadOnlyList<Seed> AnalyzeIndentation(string text)
    {
        var seeds = new List<Seed>();
        var lines = NormalizeLines(text);
        var stack = new Stack<(int Indent, int Line)>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var indent = IndentOf(line);
            while (stack.Count > 0 && indent <= stack.Peek().Indent)
            {
                var section = stack.Pop();
                if (index + 1 > section.Line)
                {
                    AddLineSeed(seeds, lines, section.Line, index + 1, "section", SectionName(lines[section.Line - 1]), true);
                }
            }

            stack.Push((indent, index + 1));
        }

        while (stack.Count > 0)
        {
            var section = stack.Pop();
            if (lines.Length > section.Line)
            {
                AddLineSeed(seeds, lines, section.Line, lines.Length, "section", SectionName(lines[section.Line - 1]), false);
            }
        }

        return seeds;
    }

    private static IReadOnlyList<Seed> AnalyzeMarkdown(string text)
    {
        var lines = NormalizeLines(text);
        // 与预览渲染共用同一套 Markdig 标题模型(ATX + Setext,排除代码围栏),
        // 保证大纲树与渲染出的标题一一对应。
        var headings = Nornia.Desktop.Markdown.MarkdownHeadingModel.ExtractHeadings(text).ToArray();
        var seeds = new List<Seed>(headings.Length);
        for (var i = 0; i < headings.Length; i++)
        {
            var heading = headings[i];
            // Keep a heading's section open through deeper headings, and close it
            // immediately before the next heading at the same or higher level.
            var endLine = lines.Length;
            for (var next = i + 1; next < headings.Length; next++)
            {
                if (headings[next].Level <= heading.Level)
                {
                    endLine = Math.Max(heading.Line, headings[next].Line - 1);
                    break;
                }
            }

            AddLineSeed(seeds, lines, heading.Line, endLine, "heading", heading.Text, false, heading.Level);
        }

        return seeds;
    }

    private static void AddLineSeed(List<Seed> seeds, string[] lines, int startLine, int endLine,
        string kind, string name, bool preserveEndLine, int? hierarchyLevel = null)
    {
        var startColumn = Math.Max(1, lines[startLine - 1].Length - lines[startLine - 1].TrimStart().Length + 1);
        var endColumn = endLine <= lines.Length ? lines[endLine - 1].Length + 1 : 1;
        var range = new CodeSymbolRange(startLine, startColumn, endLine, endColumn);
        seeds.Add(new Seed(kind, name, name, name, range,
            new CodeSymbolRange(startLine, startColumn, startLine, startColumn + Math.Max(1, name.Length)),
            PreserveEndLine: preserveEndLine, HierarchyLevel: hierarchyLevel));
    }

    private static void AddMarkerSeeds(string text, List<Seed> seeds)
    {
        var lines = NormalizeLines(text);
        var stack = new Stack<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("#region", StringComparison.Ordinal))
            {
                stack.Push(i + 1);
            }
            else if (trimmed.StartsWith("#endregion", StringComparison.Ordinal) && stack.Count > 0)
            {
                var start = stack.Pop();
                if (i + 1 > start)
                {
                    AddLineSeed(seeds, lines, start, i + 1, "region", lines[start - 1].Trim(), true);
                }
            }
        }
    }

    private static CodeSymbolDocument BuildDocument(IReadOnlyList<Seed> seeds)
    {
        var ordered = seeds
            .Where(seed => seed.Range.EndLine > seed.Range.StartLine || seed.ShowInOutline)
            .OrderBy(seed => seed.Range.StartLine)
            .ThenBy(seed => seed.Range.StartColumn)
            .ThenByDescending(seed => seed.Range.EndLine)
            .ThenByDescending(seed => seed.Range.EndColumn)
            .ToArray();
        var nodes = ordered.Select(seed => new CodeSymbolNode(
            string.Empty, seed.Kind, seed.Name, seed.DisplayName, seed.Signature, seed.Range,
            seed.SelectionRange, seed.FoldingRange, seed.ShowInOutline, seed.PreserveEndLine)).ToArray();
        var roots = new List<CodeSymbolNode>();
        var children = nodes.ToDictionary(node => node, _ => new List<CodeSymbolNode>());
        for (var i = 0; i < nodes.Length; i++)
        {
            var parent = ordered[i].HierarchyLevel is int childLevel
                ? nodes
                    .Take(i)
                    .Where((candidate, index) => ordered[index].HierarchyLevel is int parentLevel
                        && parentLevel < childLevel)
                    .LastOrDefault()
                : nodes
                    .Take(i)
                    .Where(candidate => candidate.Range.Contains(ordered[i].Range))
                    .OrderBy(candidate => candidate.Range.SpanLength)
                    .FirstOrDefault();
            if (parent is null)
            {
                roots.Add(nodes[i]);
            }
            else
            {
                nodes[i].Parent = parent;
                children[parent].Add(nodes[i]);
            }
        }

        foreach (var pair in children)
        {
            pair.Key.Children = pair.Value;
        }

        AssignIds(roots, "symbol");
        foreach (var node in nodes)
        {
            node.VisibleChildren = CodeSymbolDocument.VisibleChildren(node.Children);
        }
        return new CodeSymbolDocument(roots);
    }

    private static void AssignIds(IEnumerable<CodeSymbolNode> nodes, string parentId)
    {
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var key = node.Kind + ":" + node.Name;
            counters.TryGetValue(key, out var count);
            counters[key] = count + 1;
            node.Id = $"{parentId}/{key}@{node.Range.StartLine}:{node.Range.StartColumn}:{count}";
            AssignIds(node.Children, node.Id);
        }
    }

    private static string[] NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }

    private static (int Line, int Column) PositionAt(int[] starts, int offset, string text)
    {
        var line = Array.BinarySearch(starts, offset);
        if (line < 0) line = ~line - 1;
        return (line + 1, offset - starts[line] + 1);
    }

    private static CodeSymbolRange RangeAt(byte[] bytes, int start, int end)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < start && i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n') { line++; lineStart = i + 1; }
        }

        var endLine = line;
        var endLineStart = lineStart;
        for (var i = start; i < end && i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n') { endLine++; endLineStart = i + 1; }
        }

        return new CodeSymbolRange(line, start - lineStart + 1, endLine, Math.Max(1, end - endLineStart + 1));
    }

    private static int IndentOf(string line) => line.TakeWhile(ch => ch is ' ' or '\t').Sum(ch => ch == '\t' ? 4 : 1);

    private static string SectionName(string line)
    {
        var trimmed = line.Trim();
        var end = trimmed.IndexOfAny([':', '(']);
        return end > 0 ? trimmed[..end].Trim() : trimmed;
    }

    private static string HeaderName(string text, int braceOffset)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, braceOffset - 1));
        var header = text[(lineStart + 1)..braceOffset].Trim();
        return header.Length == 0 ? "block" : header;
    }

    private static string TypeName(BaseTypeDeclarationSyntax type) =>
        type.Identifier.Text + (type is TypeDeclarationSyntax { TypeParameterList: { } parameters } ? parameters.ToString() : string.Empty);

    private static string TypeKeyword(BaseTypeDeclarationSyntax type) => type switch
    {
        EnumDeclarationSyntax => "enum",
        TypeDeclarationSyntax declaration => declaration.Keyword.Text,
        _ => "type",
    };

    private static string MethodParams(BaseParameterListSyntax parameters) =>
        "(" + string.Join(", ", parameters.Parameters.Select(parameter => parameter.Type?.ToString().Split('.').LastOrDefault() ?? "?")) + ")";

    private static string Truncate(string text, int maxLength = 140)
    {
        var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= maxLength ? single : single[..maxLength] + "…";
    }

    private static void SkipString(string text, ref int index, char quote)
    {
        index++;
        while (index < text.Length)
        {
            if (text[index] == '\\') { index += 2; continue; }
            if (text[index] == quote) return;
            index++;
        }
    }

    private static void SkipLineComment(string text, ref int index)
    {
        while (index < text.Length && text[index] != '\n') index++;
    }

    private static void ReplaceSeed(List<Seed> seeds, Seed replacement)
    {
        var index = seeds.FindIndex(seed => seed.Kind == replacement.Kind && seed.Range.StartLine == replacement.Range.StartLine
            && seed.Range.StartColumn == replacement.Range.StartColumn && seed.Name == replacement.Name);
        if (index >= 0) seeds[index] = replacement;
    }
}

internal static class CodeSymbolRangeExtensions
{
    // JSON token offsets are only used while parsing the current document; deriving the byte
    // position again keeps the public range type independent of UTF-8 implementation details.
    public static int StartByte(this CodeSymbolRange range, byte[] bytes)
    {
        var line = 1;
        var offset = 0;
        while (line < range.StartLine && offset < bytes.Length)
        {
            if (bytes[offset++] == (byte)'\n') line++;
        }

        return Math.Min(bytes.Length, offset + Math.Max(0, range.StartColumn - 1));
    }
}
