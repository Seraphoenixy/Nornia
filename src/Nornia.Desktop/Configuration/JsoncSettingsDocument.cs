using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nornia.Desktop.Configuration;

/// <summary>Small JSONC root-object editor. Existing property value spans are patched in place so
/// comments, unknown settings, ordering and surrounding formatting remain untouched.</summary>
internal sealed class JsoncSettingsDocument
{
    private readonly Dictionary<string, PropertySpan> _properties;

    private JsoncSettingsDocument(string path, string text, JsonObject root,
        Dictionary<string, PropertySpan> properties, SettingsDiagnostic? diagnostic,
        Encoding encoding, bool hasByteOrderMark)
    {
        Path = path;
        Text = text;
        Root = root;
        _properties = properties;
        Diagnostic = diagnostic;
        TextEncoding = encoding;
        HasByteOrderMark = hasByteOrderMark;
        NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{encoding.WebName}|{hasByteOrderMark}|{text}")));
    }

    public string Path { get; }
    public string Text { get; }
    public JsonObject Root { get; }
    public string Revision { get; }
    public SettingsDiagnostic? Diagnostic { get; }
    public bool IsValid => Diagnostic is null;
    public Encoding TextEncoding { get; }
    public bool HasByteOrderMark { get; }
    public string NewLine { get; }

    public static async Task<JsoncSettingsDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        if (File.Exists(path))
        {
            bytes = await ReadAllBytesWithRetryAsync(path, cancellationToken);
        }
        else
        {
            bytes = [];
        }
        var (encoding, preambleLength) = DetectEncoding(bytes);
        var text = bytes.Length == 0 ? "{\n}\n" : encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        try
        {
            var node = JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject root) throw new JsonException("设置文件根节点必须是 JSON 对象。");
            return new(path, text, root, ScanRootProperties(text), null, encoding, preambleLength > 0);
        }
        catch (JsonException exception)
        {
            var line = checked((int)(exception.LineNumber ?? 0) + 1);
            var column = checked((int)(exception.BytePositionInLine ?? 0) + 1);
            var snippet = text.Split(["\r\n", "\n"], StringSplitOptions.None).ElementAtOrDefault(line - 1)?.Trim();
            var message = snippet is null ? exception.Message : $"{exception.Message} 附近：{snippet}";
            return new(path, text, new JsonObject(), [], new(path, line, column, message), encoding, preambleLength > 0);
        }
    }

    private static async Task<byte[]> ReadAllBytesWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        var maxRetries = 3;
        var retryDelay = 100;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await File.ReadAllBytesAsync(path, cancellationToken);
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay *= 2;
            }
        }
        return await File.ReadAllBytesAsync(path, cancellationToken);
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

    public string Patch(IReadOnlyList<SettingOperation> operations)
    {
        if (!IsValid) throw new InvalidOperationException($"设置文件包含错误：{Diagnostic!.Message}");
        var replacements = new List<TextReplacement>();
        var additions = new List<KeyValuePair<string, JsonNode?>>();
        var rootUpdates = new List<(string Key, JsonNode? Value, bool Reset)>();
        foreach (var operation in operations.Where(item => string.IsNullOrWhiteSpace(item.LanguageId))
                     .GroupBy(item => item.Key, StringComparer.Ordinal).Select(group => group.Last()))
            rootUpdates.Add((operation.Key, operation.Value?.DeepClone(), operation.Reset));
        foreach (var languageGroup in operations.Where(item => !string.IsNullOrWhiteSpace(item.LanguageId))
                     .GroupBy(item => item.LanguageId!, StringComparer.OrdinalIgnoreCase))
        {
            var rootKey = $"[{languageGroup.Key}]";
            var languageObject = Root[rootKey] as JsonObject is { } existing
                ? (JsonObject)existing.DeepClone()
                : new JsonObject();
            foreach (var operation in languageGroup)
                if (operation.Reset) languageObject.Remove(operation.Key);
                else languageObject[operation.Key] = operation.Value?.DeepClone();
            if (_properties.TryGetValue(rootKey, out var languageSpan))
            {
                if (languageObject.Count == 0)
                {
                    replacements.Add(RemovalFor(languageSpan));
                }
                else
                {
                    var raw = Text[languageSpan.ValueStart..languageSpan.ValueEnd];
                    var nested = new JsoncSettingsDocument(Path, raw, (JsonObject)(Root[rootKey]!.DeepClone()),
                        ScanRootProperties(raw), null, TextEncoding, false);
                    var nestedOperations = languageGroup.Select(operation => operation with { LanguageId = null }).ToArray();
                    replacements.Add(new(languageSpan.ValueStart, languageSpan.ValueEnd - languageSpan.ValueStart,
                        nested.Patch(nestedOperations)));
                }
            }
            else if (languageObject.Count > 0)
            {
                additions.Add(new(rootKey, languageObject));
            }
        }

        foreach (var update in rootUpdates)
        {
            if (_properties.TryGetValue(update.Key, out var span))
            {
                if (update.Reset) replacements.Add(RemovalFor(span));
                else if (update.Value is JsonObject newObject && Root[update.Key] is JsonObject oldObject)
                {
                    var raw = Text[span.ValueStart..span.ValueEnd];
                    var nested = new JsoncSettingsDocument(Path, raw, (JsonObject)oldObject.DeepClone(),
                        ScanRootProperties(raw), null, TextEncoding, false);
                    var nestedOperations = oldObject.Select(item => item.Key).Union(newObject.Select(item => item.Key),
                            StringComparer.Ordinal)
                        .Where(key => !JsonNode.DeepEquals(oldObject[key], newObject[key]))
                        .Select(key => new SettingOperation(key, newObject[key]?.DeepClone(), !newObject.ContainsKey(key)))
                        .ToArray();
                    replacements.Add(new(span.ValueStart, span.ValueEnd - span.ValueStart,
                        nested.Patch(nestedOperations)));
                }
                else replacements.Add(new(span.ValueStart, span.ValueEnd - span.ValueStart, Serialize(update.Value)));
            }
            else if (!update.Reset)
            {
                additions.Add(new(update.Key, update.Value));
            }
        }

        var result = Text;
        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            result = result.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Text);
        if (additions.Count > 0) result = InsertProperties(result, additions);
        return result;
    }

    private TextReplacement RemovalFor(PropertySpan span)
    {
        var end = span.PropertyEnd;
        var cursor = end;
        SkipTrivia(Text, ref cursor);
        if (cursor < Text.Length && Text[cursor] == ',')
        {
            cursor++;
            if (cursor < Text.Length && Text[cursor] == '\r') cursor++;
            if (cursor < Text.Length && Text[cursor] == '\n') cursor++;
            return new(span.PropertyStart, cursor - span.PropertyStart, string.Empty);
        }

        var start = span.PropertyStart;
        var previous = start - 1;
        while (previous >= 0 && char.IsWhiteSpace(Text[previous])) previous--;
        if (previous >= 0 && Text[previous] == ',') start = previous;
        return new(start, end - start, string.Empty);
    }

    private static string InsertProperties(string text, IReadOnlyList<KeyValuePair<string, JsonNode?>> additions)
    {
        var closingBrace = FindRootClosingBrace(text);
        var before = text[..closingBrace];
        var hasProperties = ScanRootProperties(text).Count > 0;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = DetectIndent(text);
        var builder = new StringBuilder();
        if (hasProperties)
        {
            var trim = before.TrimEnd();
            before = trim + (trim.EndsWith(",", StringComparison.Ordinal) ? string.Empty : ",") + newline;
        }
        else
        {
            before = before.TrimEnd() + newline;
        }
        builder.Append(before);
        for (var index = 0; index < additions.Count; index++)
        {
            var item = additions[index];
            builder.Append(indent).Append(JsonSerializer.Serialize(item.Key)).Append(": ").Append(Serialize(item.Value));
            if (index < additions.Count - 1) builder.Append(',');
            builder.Append(newline);
        }
        builder.Append(text[closingBrace..]);
        return builder.ToString();
    }

    private static string DetectIndent(string text)
    {
        foreach (var span in ScanRootProperties(text).Values.OrderBy(item => item.PropertyStart))
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, span.PropertyStart - 1));
            var start = lineStart < 0 ? 0 : lineStart + 1;
            var indent = text[start..span.PropertyStart];
            if (indent.All(character => character is ' ' or '\t')) return indent;
        }
        return "  ";
    }

    private static string Serialize(JsonNode? node) => node?.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }) ?? "null";

    private static int FindRootClosingBrace(string text)
    {
        var scanner = new Scanner(text);
        scanner.SkipTrivia();
        if (!scanner.Consume('{')) throw new JsonException();
        var depth = 1;
        while (!scanner.End)
        {
            if (scanner.PeekString()) { scanner.SkipString(); continue; }
            if (scanner.PeekComment()) { scanner.SkipComment(); continue; }
            var character = scanner.Current;
            if (character is '{' or '[') depth++;
            else if (character is '}' or ']')
            {
                depth--;
                if (depth == 0) return scanner.Position;
            }
            scanner.Advance();
        }
        throw new JsonException();
    }

    private static Dictionary<string, PropertySpan> ScanRootProperties(string text)
    {
        var result = new Dictionary<string, PropertySpan>(StringComparer.Ordinal);
        var scanner = new Scanner(text);
        scanner.SkipTrivia();
        if (!scanner.Consume('{')) return result;
        while (!scanner.End)
        {
            scanner.SkipTrivia();
            if (scanner.Consume('}')) break;
            var start = scanner.Position;
            var name = scanner.ReadString();
            scanner.SkipTrivia();
            if (!scanner.Consume(':')) throw new JsonException("设置属性缺少冒号。");
            scanner.SkipTrivia();
            var valueStart = scanner.Position;
            scanner.SkipValue();
            var valueEnd = scanner.Position;
            result[name] = new(start, valueStart, valueEnd, valueEnd);
            scanner.SkipTrivia();
            if (scanner.Consume(',')) continue;
            if (scanner.Current == '}') continue;
            throw new JsonException("设置属性之间缺少逗号。");
        }
        return result;
    }

    private static void SkipTrivia(string text, ref int cursor)
    {
        var scanner = new Scanner(text, cursor);
        scanner.SkipTrivia();
        cursor = scanner.Position;
    }

    private readonly record struct PropertySpan(int PropertyStart, int ValueStart, int ValueEnd, int PropertyEnd);
    private readonly record struct TextReplacement(int Start, int Length, string Text);

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
            if (!PeekComment()) return;
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
            if (!Consume('"')) throw new JsonException("需要字符串。");
            while (!End)
            {
                var character = Current;
                Position++;
                if (character == '\\') { if (!End) Position++; continue; }
                if (character == '"') return;
            }
            throw new JsonException("字符串未结束。");
        }

        public void SkipValue()
        {
            if (Current == '"') { SkipString(); return; }
            if (Current is '{' or '[')
            {
                var open = Current;
                var close = open == '{' ? '}' : ']';
                var depth = 0;
                while (!End)
                {
                    if (PeekString()) { SkipString(); continue; }
                    if (PeekComment()) { SkipComment(); continue; }
                    if (Current == open) depth++;
                    else if (Current == close && --depth == 0) { Position++; return; }
                    Position++;
                }
                throw new JsonException("复合值未结束。");
            }
            while (!End && Current is not ',' and not '}' && Current is not '\r' and not '\n') Position++;
            while (Position > 0 && char.IsWhiteSpace(text[Position - 1])) Position--;
        }
    }
}
