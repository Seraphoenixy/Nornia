using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace Nornia.Desktop.Commands;

/// <summary>
/// Loss-minimizing editor for VS Code's JSONC keybinding array. Existing object bodies are patched
/// by property span, so comments, property order, escaping and unknown fields survive edits.
/// </summary>
internal sealed class JsoncKeybindingDocument
{
    private readonly IReadOnlyList<ItemSpan> _items;
    private JsoncKeybindingDocument(string text, Encoding encoding, bool bom, IReadOnlyList<ItemSpan> items)
    {
        Text = text;
        Encoding = encoding;
        HasBom = bom;
        _items = items;
        NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{encoding.WebName}|{bom}|{text}")));
    }

    public string Text { get; }
    public Encoding Encoding { get; }
    public bool HasBom { get; }
    public string NewLine { get; }
    public string Revision { get; }

    public static async Task<JsoncKeybindingDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : [];
        var (encoding, preamble) = DetectEncoding(bytes);
        var bom = preamble > 0;
        var text = bytes.Length == 0 ? "[\n]\n" : encoding.GetString(bytes, preamble, bytes.Length - preamble);
        _ = JsonNode.Parse(text, new JsonNodeOptions(), new JsonDocumentOptions
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonArray
            ?? throw new JsonException("keybindings.json 根节点必须是数组。");
        return new(text, encoding, bom, ScanItems(text));
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Encoding.UTF8.GetPreamble())) return (new UTF8Encoding(false), 3);
        if (bytes.StartsWith(Encoding.Unicode.GetPreamble())) return (Encoding.Unicode, 2);
        if (bytes.StartsWith(Encoding.BigEndianUnicode.GetPreamble())) return (Encoding.BigEndianUnicode, 2);
        if (bytes.StartsWith(Encoding.UTF32.GetPreamble())) return (Encoding.UTF32, 4);
        var utf32Be = new UTF32Encoding(true, true);
        if (bytes.StartsWith(utf32Be.GetPreamble())) return (utf32Be, 4);
        return (new UTF8Encoding(false), 0);
    }

    public IReadOnlyList<KeybindingDefinition> ReadBindings()
    {
        var root = JsonNode.Parse(Text, new JsonNodeOptions(), new JsonDocumentOptions
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonArray ?? [];
        return root.OfType<JsonObject>().Select((item, index) => new KeybindingDefinition(
            item["key"]?.GetValue<string>() ?? string.Empty,
            item["command"]?.GetValue<string>() ?? string.Empty,
            item["when"]?.GetValue<string>(), item["args"]?.DeepClone(), false, index))
            .Where(item => item.Key.Length > 0 && item.Command.Length > 0).ToArray();
    }

    public string Patch(IReadOnlyList<KeybindingDefinition> requested)
    {
        var pending = requested.Where(item => !item.IsDefault)
            .GroupBy(item => item.Command, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<KeybindingDefinition>(group), StringComparer.Ordinal);
        var replacements = new List<Replacement>();
        foreach (var item in _items)
        {
            var command = item.Object["command"]?.GetValue<string>() ?? string.Empty;
            if (pending.TryGetValue(command, out var matches) && matches.Count > 0)
            {
                var next = matches.Dequeue();
                replacements.Add(new(item.ObjectStart, item.ObjectEnd - item.ObjectStart,
                    PatchObject(Text[item.ObjectStart..item.ObjectEnd], next)));
            }
            else
            {
                replacements.Add(RemoveItem(item));
            }
        }

        var additions = pending.Values.SelectMany(queue => queue).ToArray();
        var result = Text;
        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            result = result.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Text);
        if (additions.Length > 0) result = InsertItems(result, additions);
        return result;
    }

    private Replacement RemoveItem(ItemSpan item)
    {
        var end = item.ObjectEnd;
        var cursor = end;
        SkipTrivia(Text, ref cursor);
        if (cursor < Text.Length && Text[cursor] == ',')
        {
            cursor++;
            return new(item.ObjectStart, cursor - item.ObjectStart, string.Empty);
        }
        var start = item.ObjectStart;
        var previous = start - 1;
        while (previous >= 0 && char.IsWhiteSpace(Text[previous])) previous--;
        if (previous >= 0 && Text[previous] == ',') start = previous;
        return new(start, end - start, string.Empty);
    }

    private string InsertItems(string text, IReadOnlyList<KeybindingDefinition> additions)
    {
        var close = FindArrayClose(text);
        var before = text[..close];
        var hasItems = ScanItems(text).Count > 0;
        before = before.TrimEnd();
        if (hasItems && !before.EndsWith(",", StringComparison.Ordinal)) before += ",";
        var builder = new StringBuilder(before).Append(NewLine);
        var indent = DetectIndent(text);
        foreach (var binding in additions)
            builder.Append(indent).Append(SerializeBinding(binding)).Append(',').Append(NewLine);
        builder.Append(text[close..]);
        return builder.ToString();
    }

    private static string DetectIndent(string text)
    {
        var first = ScanItems(text).FirstOrDefault();
        if (first.ObjectEnd > first.ObjectStart)
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, first.ObjectStart - 1));
            var start = lineStart < 0 ? 0 : lineStart + 1;
            var indent = text[start..first.ObjectStart];
            if (indent.All(character => character is ' ' or '\t')) return indent;
        }
        return "  ";
    }

    private static string PatchObject(string raw, KeybindingDefinition binding)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["key"] = JsonValue.Create(KeyGestureNormalizer.Normalize(binding.Key)),
            ["command"] = JsonValue.Create(binding.Command),
            ["when"] = string.IsNullOrWhiteSpace(binding.When) ? null : JsonValue.Create(binding.When),
            ["args"] = binding.Args?.DeepClone(),
        };
        var spans = ScanProperties(raw);
        var replacements = new List<Replacement>();
        foreach (var (name, value) in values)
        {
            if (spans.TryGetValue(name, out var span))
            {
                if (value is null) replacements.Add(RemoveProperty(raw, span));
                else replacements.Add(new(span.ValueStart, span.ValueEnd - span.ValueStart, Serialize(value)));
            }
        }
        var result = raw;
        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            result = result.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Text);
        var missing = values.Where(item => item.Value is not null && !spans.ContainsKey(item.Key)).ToArray();
        if (missing.Length == 0) return result;
        var close = FindObjectClose(result);
        var prefix = result[..close].TrimEnd();
        if (ScanProperties(result).Count > 0 && !prefix.EndsWith(",", StringComparison.Ordinal)) prefix += ",";
        return prefix + " " + string.Join(", ", missing.Select(item =>
            $"{JsonSerializer.Serialize(item.Key)}: {Serialize(item.Value)}")) + result[close..];
    }

    private static Replacement RemoveProperty(string text, PropertySpan span)
    {
        var cursor = span.End;
        SkipTrivia(text, ref cursor);
        if (cursor < text.Length && text[cursor] == ',') return new(span.Start, cursor + 1 - span.Start, string.Empty);
        var start = span.Start;
        var previous = start - 1;
        while (previous >= 0 && char.IsWhiteSpace(text[previous])) previous--;
        if (previous >= 0 && text[previous] == ',') start = previous;
        return new(start, span.End - start, string.Empty);
    }

    private static string SerializeBinding(KeybindingDefinition binding)
    {
        var value = new JsonObject
        {
            ["key"] = KeyGestureNormalizer.Normalize(binding.Key),
            ["command"] = binding.Command,
        };
        if (!string.IsNullOrWhiteSpace(binding.When)) value["when"] = binding.When;
        if (binding.Args is not null) value["args"] = binding.Args.DeepClone();
        return Serialize(value);
    }

    private static string Serialize(JsonNode? node) => node?.ToJsonString(new JsonSerializerOptions
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";

    private static IReadOnlyList<ItemSpan> ScanItems(string text)
    {
        var result = new List<ItemSpan>();
        var scanner = new Scanner(text);
        scanner.SkipTrivia();
        if (!scanner.Consume('[')) throw new JsonException("快捷键根节点必须是数组。");
        while (true)
        {
            scanner.SkipTrivia();
            if (scanner.Consume(']')) break;
            var start = scanner.Position;
            scanner.SkipValue();
            var end = scanner.Position;
            var raw = text[start..end];
            var node = JsonNode.Parse(raw, new JsonNodeOptions(), new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new JsonException("快捷键数组项必须是对象。");
            result.Add(new(start, end, node));
            scanner.SkipTrivia();
            if (scanner.Consume(',')) continue;
            if (scanner.Current == ']') continue;
            throw new JsonException("快捷键数组项之间缺少逗号。");
        }
        return result;
    }

    private static Dictionary<string, PropertySpan> ScanProperties(string text)
    {
        var result = new Dictionary<string, PropertySpan>(StringComparer.Ordinal);
        var scanner = new Scanner(text);
        scanner.SkipTrivia();
        if (!scanner.Consume('{')) return result;
        while (true)
        {
            scanner.SkipTrivia();
            if (scanner.Consume('}')) break;
            var start = scanner.Position;
            var name = scanner.ReadString();
            scanner.SkipTrivia();
            if (!scanner.Consume(':')) throw new JsonException();
            scanner.SkipTrivia();
            var valueStart = scanner.Position;
            scanner.SkipValue();
            var valueEnd = scanner.Position;
            result[name] = new(start, valueStart, valueEnd, valueEnd);
            scanner.SkipTrivia();
            if (scanner.Consume(',')) continue;
            if (scanner.Current == '}') continue;
            throw new JsonException();
        }
        return result;
    }

    private static int FindArrayClose(string text) => FindClose(text, '[', ']');
    private static int FindObjectClose(string text) => FindClose(text, '{', '}');
    private static int FindClose(string text, char open, char close)
    {
        var scanner = new Scanner(text);
        scanner.SkipTrivia();
        if (!scanner.Consume(open)) throw new JsonException();
        var stack = new Stack<char>();
        stack.Push(open);
        while (!scanner.End)
        {
            if (scanner.PeekString()) { scanner.SkipString(); continue; }
            if (scanner.PeekComment()) { scanner.SkipComment(); continue; }
            if (scanner.Current is '{' or '[') stack.Push(scanner.Current);
            else if (scanner.Current is '}' or ']')
            {
                var expected = scanner.Current == '}' ? '{' : '[';
                if (stack.Pop() != expected) throw new JsonException();
                if (stack.Count == 0) return scanner.Position;
            }
            scanner.Advance();
        }
        throw new JsonException();
    }

    private static void SkipTrivia(string text, ref int cursor)
    {
        var scanner = new Scanner(text, cursor);
        scanner.SkipTrivia();
        cursor = scanner.Position;
    }

    private readonly record struct ItemSpan(int ObjectStart, int ObjectEnd, JsonObject Object);
    private readonly record struct PropertySpan(int Start, int ValueStart, int ValueEnd, int End);
    private readonly record struct Replacement(int Start, int Length, string Text);

    private sealed class Scanner(string text, int position = 0)
    {
        public int Position { get; private set; } = position;
        public bool End => Position >= text.Length;
        public char Current => End ? '\0' : text[Position];
        public void Advance() { if (!End) Position++; }
        public bool Consume(char value) { if (Current != value) return false; Position++; return true; }
        public bool PeekString() => Current == '"';
        public bool PeekComment() => Current == '/' && Position + 1 < text.Length && text[Position + 1] is '/' or '*';
        public void SkipTrivia()
        {
            while (!End)
            {
                if (char.IsWhiteSpace(Current)) { Position++; continue; }
                if (PeekComment()) { SkipComment(); continue; }
                break;
            }
        }
        public void SkipComment()
        {
            var block = text[Position + 1] == '*';
            Position += 2;
            if (!block) { while (!End && Current is not '\r' and not '\n') Position++; return; }
            while (Position + 1 < text.Length && !(text[Position] == '*' && text[Position + 1] == '/')) Position++;
            Position = Math.Min(text.Length, Position + 2);
        }
        public string ReadString()
        {
            var start = Position;
            SkipString();
            return JsonSerializer.Deserialize<string>(text[start..Position]) ?? string.Empty;
        }
        public void SkipString()
        {
            if (!Consume('"')) throw new JsonException();
            while (!End)
            {
                var character = Current;
                Position++;
                if (character == '\\') { if (!End) Position++; continue; }
                if (character == '"') return;
            }
            throw new JsonException();
        }
        public void SkipValue()
        {
            if (Current == '"') { SkipString(); return; }
            if (Current is '{' or '[')
            {
                var opens = new Stack<char>();
                do
                {
                    if (PeekString()) { SkipString(); continue; }
                    if (PeekComment()) { SkipComment(); continue; }
                    if (Current is '{' or '[') opens.Push(Current);
                    else if (Current is '}' or ']')
                    {
                        var expected = Current == '}' ? '{' : '[';
                        if (opens.Count == 0 || opens.Pop() != expected) throw new JsonException();
                        Position++;
                        if (opens.Count == 0) return;
                        continue;
                    }
                    Position++;
                } while (!End);
                throw new JsonException();
            }
            while (!End && Current is not ',' and not '}' and not ']') Position++;
            while (Position > 0 && char.IsWhiteSpace(text[Position - 1])) Position--;
        }
    }
}
