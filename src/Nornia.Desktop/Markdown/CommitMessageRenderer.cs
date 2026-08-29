using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Nornia.Desktop.Markdown;

/// <summary>
/// 最小 markdown → <see cref="FlowDocument"/> 渲染器,专用于提交消息悬浮窗。规则对齐
/// VS Code git 扩展的 <c>getHistoryItemHover</c>:
/// <list type="bullet">
/// <item>每条消息行视为独立段落(与该扩展 <c>message.replace(/\r\n|\r|\n/g, '\n\n')</c> 的
///     预处理一致 —— 提交正文的每一个换行都成为段落分界,而不是 markdown 的软换行);</item>
/// <item>行首块语法:标题 / 分隔线 / 引用 / 无序与有序列表 / 围栏代码块;</item>
/// <item>行内语法:加粗、斜体、行内代码、链接、反斜杠转义(删除线因 WPF 文本模型限制按字面量保留)。</item>
/// </list>
/// 所有颜色与字体都从当前主题/应用资源解析(与应用其它自定义渲染控件一致);
/// <see cref="Application.Current"/> 为空(纯测试环境)时回退中性灰。
/// </summary>
public static class CommitMessageRenderer
{
    /// <summary>渲染提交消息全文(主题 + 正文)。</summary>
    public static FlowDocument Render(string? text)
    {
        var fontFamily = (Application.Current?.TryFindResource("UiFontFamily") as FontFamily)
            ?? new FontFamily("Segoe UI");
        var fontSize = ResolveDouble("TypeBody", 13);
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = fontFamily,
            FontSize = fontSize,
            Foreground = Resolve("TextBrush"),
            // Mixed CJK / long identifiers must keep VS Code hover's flush-left layout. WPF's
            // paragraph formatter otherwise stretches the short fragments preceding an
            // unbreakable Latin word, producing visibly sparse lines.
            TextAlignment = TextAlignment.Left,
            // VS Code workbench hover: 13px 字体 / 19px 行高。
            LineHeight = fontSize * 19d / 13d,
        };

        var lines = Normalize(text ?? string.Empty);
        var paragraphIndex = 0;
        var totalBlocks = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // 围栏代码块:``` / ~~~ 起止,整块一个字面段落(不解析行内语法)。
            var fence = FenceMarker(line);
            if (fence is not null)
            {
                var body = new List<string>();
                var closed = false;
                while (i + 1 < lines.Count)
                {
                    i++;
                    if (FenceMarker(lines[i]) == fence)
                    {
                        closed = true;
                        break;
                    }
                    body.Add(lines[i]);
                }
                document.Blocks.Add(BuildCodeBlock(body));
                if (closed) continue;
                break;
            }

            var block = BuildBlock(line, totalBlocks, paragraphIndex);
            if (block is not null)
            {
                document.Blocks.Add(block);
                paragraphIndex++;
            }
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(EmptyParagraph());
        }

        return document;
    }

    // ===== 块构建 =====

    private static Paragraph? BuildBlock(string line, int totalBlocks, int paragraphIndex)
    {
        // 标题:1-6 个 # 后跟空格。
        var heading = 0;
        while (heading < 6 && heading < line.Length && line[heading] == '#')
        {
            heading++;
        }
        if (heading > 0 && heading < line.Length && line[heading] == ' ')
        {
            var headingParagraph = NewParagraph();
            headingParagraph.FontWeight = FontWeights.Bold;
            headingParagraph.Foreground = Resolve("BrightTextBrush");
            headingParagraph.FontSize = ResolveDouble("TypeBody", 13) + (6 - heading);
            headingParagraph.LineHeight = headingParagraph.FontSize * 19d / 13d;
            AppendInline(headingParagraph, line[(heading + 1)..].Trim());
            return headingParagraph;
        }

        // 分隔线:三个及以上 - / * / _。
        if (IsRule(line))
        {
            var rule = NewParagraph();
            rule.BorderBrush = Resolve("ToolTipBorderBrush");
            rule.BorderThickness = new Thickness(0, 0, 0, 1);
            rule.Margin = new Thickness(0, 5, 0, 5);
            return rule;
        }

        // 引用:至少一个 '>'(多个 '>' 视为嵌套,本次按一层渲染)。
        if (line.StartsWith('>'))
        {
            var quote = NewParagraph();
            quote.Padding = new Thickness(10, 0, 0, 0);
            quote.BorderBrush = Resolve("MutedTextBrush");
            quote.BorderThickness = new Thickness(2, 0, 0, 0);
            quote.Foreground = Resolve("MutedTextBrush");
            var content = line.TrimStart('>').TrimStart();
            AppendInline(quote, content);
            return quote;
        }

        // 无序列表:- / * / + 后接空格。
        if (line.Length >= 2 && (line[0] == '-' || line[0] == '*' || line[0] == '+') && line[1] == ' ')
        {
            var itemParagraph = NewParagraph();
            itemParagraph.Padding = new Thickness(6, 0, 0, 0);
            var bullet = new Run("• ") { Foreground = Resolve("MutedTextBrush") };
            itemParagraph.Inlines.Add(bullet);
            AppendInline(itemParagraph, line[2..]);
            return itemParagraph;
        }

        // 有序列表:数字 + . 或 ) 后接空格。
        var ordered = OrderedMarker(line, out var numberLength);
        if (ordered)
        {
            var itemParagraph = NewParagraph();
            itemParagraph.Padding = new Thickness(6, 0, 0, 0);
            var bullet = new Run($"{line[..numberLength].TrimEnd('.', ')')}. ") { Foreground = Resolve("MutedTextBrush") };
            itemParagraph.Inlines.Add(bullet);
            AppendInline(itemParagraph, line[numberLength..].TrimStart());
            return itemParagraph;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var bodyParagraph = NewParagraph();
        AppendInline(bodyParagraph, line);
        return bodyParagraph;
    }

    private static Paragraph BuildCodeBlock(IReadOnlyList<string> body)
    {
        var paragraph = NewParagraph();
        paragraph.Background = Resolve("InputBrush");
        paragraph.Padding = new Thickness(8, 4, 8, 4);
        paragraph.Margin = new Thickness(0, 4, 0, 4);
        paragraph.FontFamily = (Application.Current?.TryFindResource("MonoFontFamily") as FontFamily)
            ?? new FontFamily("Consolas");
        paragraph.FontSize = ResolveDouble("TypeBody", 13) - 1;
        paragraph.LineHeight = paragraph.FontSize * 19d / 13d;
        for (var i = 0; i < body.Count; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }
            paragraph.Inlines.Add(new Run(body[i]));
        }
        return paragraph;
    }

    // ===== 行内解析 =====

    /// <summary>行内语法扫描,输出 Run / Span / Hyperlink 序列;过深或无法闭合时按字面量处理。</summary>
    private static void AppendInline(Paragraph paragraph, string text)
    {
        foreach (var inline in ParseInline(text, 0))
        {
            paragraph.Inlines.Add(inline);
        }
    }

    private static IEnumerable<Inline> ParseInline(string text, int depth)
    {
        var result = new List<Inline>();
        var plain = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (plain.Length == 0) return;
            result.Add(new Run(AddLongWordBreakOpportunities(plain.ToString())));
            plain.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];

            // 反斜杠转义:转义任一标点,输出字面字符。
            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // 行内代码:`...`(不成对时按字面量)。
            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    Flush();
                    result.Add(CodeSpan(text[(i + 1)..close]));
                    i = close + 1;
                    continue;
                }
                plain.Append(c);
                i++;
                continue;
            }

            // 链接:[label](url)(不成对时按字面量)。
            if (c == '[' && depth < 8)
            {
                var labelEnd = text.IndexOf(']', i + 1);
                if (labelEnd > i && labelEnd + 1 < text.Length && text[labelEnd + 1] == '(')
                {
                    var urlEnd = text.IndexOf(')', labelEnd + 2);
                    if (urlEnd > labelEnd + 1)
                    {
                        Flush();
                        var link = new Hyperlink { Foreground = Resolve("LinkBrush") };
                        link.TextDecorations = TextDecorations.Underline;
                        foreach (var inner in ParseInline(text[(i + 1)..labelEnd], depth + 1))
                        {
                            link.Inlines.Add(inner);
                        }
                        result.Add(link);
                        i = urlEnd + 1;
                        continue;
                    }
                }
                plain.Append(c);
                i++;
                continue;
            }

            // 删除线(~~...~~):WPF 文本模型未提供 Span 级删除线(TextEffect 的 Target/Xaml 在当前
            // 版本不可用),按字面量输出,不丢用户字符。
            if (c == '~')
            {
                plain.Append(c);
                i++;
                continue;
            }

            // 加粗 / 斜体(** 优先;**...**、*...*、__...__、_..._)。
            if (c == '*' || c == '_')
            {
                var strong = i + 1 < text.Length && text[i + 1] == c;
                var marker = new string(c, strong ? 2 : 1);
                var close = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
                if (close > i)
                {
                    Flush();
                    var emphasis = new Span();
                    if (strong)
                    {
                        emphasis.FontWeight = FontWeights.Bold;
                    }
                    else
                    {
                        emphasis.FontStyle = FontStyles.Italic;
                    }
                    foreach (var inner in ParseInline(text[(i + marker.Length)..close], depth + 1))
                    {
                        emphasis.Inlines.Add(inner);
                    }
                    result.Add(emphasis);
                    i = close + marker.Length;
                    continue;
                }
                plain.Append(c);
                i++;
                continue;
            }

            plain.Append(c);
            i++;
        }

        Flush();
        return result;
    }

    private static Span CodeSpan(string code)
    {
        var span = new Span { Background = Resolve("InputBrush"), FontFamily = (Application.Current?.TryFindResource("MonoFontFamily") as FontFamily) ?? new FontFamily("Consolas") };
        span.Inlines.Add(new Run(code));
        return span;
    }

    // ===== 工具 =====

    private static List<string> Normalize(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Split('\n').ToList();
    }

    private static string? FenceMarker(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return "```";
        }
        if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            return "~~~";
        }
        return null;
    }

    private static bool IsRule(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3) return false;
        var first = trimmed[0];
        if (first is not ('-' or '*' or '_')) return false;
        foreach (var c in trimmed)
        {
            if (c != first) return false;
        }
        return true;
    }

    private static bool OrderedMarker(string line, out int markerLength)
    {
        markerLength = 0;
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i == 0 || i >= line.Length) return false;
        if (line[i] is not ('.' or ')')) return false;
        if (i + 1 >= line.Length || line[i + 1] != ' ') return false;
        markerLength = i + 1;
        return true;
    }

    private static bool IsEscapable(char c) =>
        c is '\\' or '`' or '*' or '_' or '[' or ']' or '(' or ')' or '~' or '#' or '>' or '-' or '+' or '.' or '!' or '|';

    /// <summary>
    /// WPF 不会像 VS Code 的 <c>overflow-wrap: break-word</c> 一样拆分超长英文标识符。
    /// 对超过一行安全片段长度的 ASCII 单词加入不可见软换行点：优先使用下划线和
    /// camelCase/PascalCase 边界，并以固定上限兜底。字符本身及视觉内容保持不变。
    /// </summary>
    private static string AddLongWordBreakOpportunities(string text)
    {
        const int maxSegmentLength = 24;
        const char zeroWidthSpace = '\u200B';
        var result = new StringBuilder(text.Length + 8);
        var insertedBreak = false;
        var cursor = 0;
        while (cursor < text.Length)
        {
            if (!IsAsciiWordCharacter(text[cursor]))
            {
                result.Append(text[cursor]);
                cursor++;
                continue;
            }

            var wordStart = cursor;
            var wordEnd = wordStart + 1;
            while (wordEnd < text.Length && IsAsciiWordCharacter(text[wordEnd])) wordEnd++;
            if (wordEnd - wordStart <= maxSegmentLength)
            {
                result.Append(text, wordStart, wordEnd - wordStart);
                cursor = wordEnd;
                continue;
            }

            var lastBreak = wordStart;
            for (var i = wordStart; i < wordEnd; i++)
            {
                var naturalBoundary = i > wordStart
                    && (text[i - 1] == '_'
                        || (char.IsLower(text[i - 1]) && char.IsUpper(text[i])));
                if (i > wordStart && (naturalBoundary || i - lastBreak >= maxSegmentLength))
                {
                    result.Append(zeroWidthSpace);
                    insertedBreak = true;
                    lastBreak = i;
                }
                result.Append(text[i]);
            }

            cursor = wordEnd;
        }

        return insertedBreak ? result.ToString() : text;
    }

    private static bool IsAsciiWordCharacter(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

    private static Paragraph NewParagraph() => new()
    {
        // VS Code 历史项悬浮窗:段落间距 4px(首段上缘 4px、末段下缘 2px 由整体 Margin 承担)。
        Margin = new Thickness(0, 2, 0, 2),
        TextAlignment = TextAlignment.Left,
    };

    private static Paragraph EmptyParagraph() => NewParagraph();

    private static Brush Resolve(string key) =>
        (Application.Current?.TryFindResource(key) as Brush) ?? Brushes.Gray;

    private static double ResolveDouble(string key, double fallback) =>
        Application.Current?.TryFindResource(key) is double value ? value : fallback;
}
