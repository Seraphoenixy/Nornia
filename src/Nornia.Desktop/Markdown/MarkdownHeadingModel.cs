using Markdig;
using Markdig.Syntax;

namespace Nornia.Desktop.Markdown;

/// <summary>统一的 Markdown 标题模型:预览渲染、大纲和导航共用同一套 Markdig 提取逻辑。
/// ATX 与 Setext 标题都由解析器识别,代码围栏中的伪标题随 AST 自然排除;
/// 重复标题保持 "-N" 锚点后缀规则。</summary>
public static class MarkdownHeadingModel
{
    /// <summary>解析 Markdown 源码并提取标题列表(文档顺序)。与预览渲染共用同一 Pipeline,
    /// 保证大纲与渲染结果永远一致。</summary>
    public static (MarkdownDocument Document, IReadOnlyList<MarkdownHeading> Headings) Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = MarkdownDelimiterNormalizer.Normalize(source);
        var document = Markdig.Markdown.Parse(normalized, MarkdownPreviewService.Pipeline);
        return (document, CollectHeadings(document, normalized.Split('\n')));
    }

    public static IReadOnlyList<MarkdownHeading> ExtractHeadings(string source) => Parse(source).Headings;

    /// <summary>标题的纯文本(与 <see cref="CollectHeadings"/> 同一提取规则,供渲染器做标题块
    /// → 标题模型的对应,保证引文/列表内标题同样参与命名)。</summary>
    public static string HeadingText(HeadingBlock block) => PlainText(block.Inline);

    /// <summary>从 Markdig AST 提取标题:文本、层级、1-based 源码行号、可读锚点与 WPF 安全名称。
    /// Setext 标题的 <c>block.Line</c> 指向下划线行(===/---),标题行是其上一行;
    /// 通过下划线行原文判定,避免依赖 Markdig 未暴露的标题块内部形态。</summary>
    public static IReadOnlyList<MarkdownHeading> CollectHeadings(MarkdownDocument document, string[]? sourceLines = null)
    {
        var result = new List<MarkdownHeading>();
        foreach (var block in document.Descendants<HeadingBlock>())
        {
            var text = PlainText(block.Inline).Trim();
            if (text.Length == 0) continue;
            result.Add(new MarkdownHeading(text, Slug(text, result), block.Level,
                HeadingLine(block, sourceLines), $"md_heading_{result.Count}"));
        }

        return result;
    }

    /// <summary>标题的 1-based 源码行号。Setext 时 block.Line(0-based)指向下划线行,标题行即上一行。</summary>
    private static int HeadingLine(HeadingBlock block, string[]? sourceLines)
    {
        if (sourceLines is not null
            && block.Line >= 0 && block.Line < sourceLines.Length
            && IsSetextUnderline(sourceLines[block.Line]))
        {
            return block.Line; // 下划线的 0-based 行号 = 标题行的 1-based 行号
        }

        return block.Line + 1;
    }

    private static bool IsSetextUnderline(string rawLine)
    {
        var raw = rawLine.Trim();
        return raw.Length > 0 && raw.All(ch => ch == '=' || ch == '-');
    }

    private static string PlainText(Markdig.Syntax.Inlines.ContainerInline? inline)
    {
        if (inline is null) return string.Empty;
        var builder = new System.Text.StringBuilder();
        for (var child = inline.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case Markdig.Syntax.Inlines.LiteralInline literal: builder.Append(literal.Content.ToString()); break;
                case Markdig.Syntax.Inlines.CodeInline code: builder.Append(code.Content); break;
                case Markdig.Extensions.Mathematics.MathInline math: builder.Append(math.Content.ToString()); break;
                case Markdig.Syntax.Inlines.ContainerInline nested: builder.Append(PlainText(nested)); break;
                case Markdig.Syntax.Inlines.LineBreakInline: builder.Append(' '); break;
            }
        }

        return builder.ToString();
    }

    private static string Slug(string text, IReadOnlyList<MarkdownHeading> existing)
    {
        var slug = new string(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray())
            .Trim().Replace(' ', '-');
        if (slug.Length == 0) slug = "heading";
        var baseSlug = slug;
        var count = 1;
        while (existing.Any(item => item.Anchor.Equals(slug, StringComparison.OrdinalIgnoreCase)))
        {
            slug = $"{baseSlug}-{count++}";
        }

        return slug;
    }
}
