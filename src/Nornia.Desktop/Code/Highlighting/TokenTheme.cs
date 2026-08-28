using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Nornia.Desktop.Code;

/// <summary>语义角色:主题规则解析的结果,由代码阅读器和 Diff 阅读器共同映射到
/// CodeToken* 色板。共享同一张规则表和色板保证两条阅读路径语义与配色一致。</summary>
public enum CodeTokenRole
{
    Comment,
    String,
    Number,
    Keyword,
    Type,
    Function,
    Tag,
    Attribute,
    Link,
    Preprocessor,
    Punctuation,
    Default,
}

/// <summary>Theme style resolved for a group of TextMate scopes (or an AvalonEdit color name):
/// the semantic role to look up in the consumer's palette plus font-style flags. Mirrors vscode's
/// <c>TokenStyle</c> (foreground + bold/italic/underline). Brushes are resolved lazily at draw
/// time, so a live theme switch only needs a redraw.</summary>
public sealed record TokenThemeStyle(CodeTokenRole? Role = null, bool Bold = false, bool Italic = false, bool Underline = false);

/// <summary>
/// Resolves scope stacks to theme styles with vscode-main's last-match-wins rule semantics
/// (see <c>tokenClassificationRegistry.ts</c> / TMTheme scope selectors): the rule list is ordered
/// from generic to specific and the <em>last</em> matching rule wins. The resolved content
/// (role + flags) is theme-independent, so the cache never needs invalidation; only the final
/// brush lookup depends on the active theme.
/// </summary>
public static class TokenTheme
{
    /// <summary>规则表:Fragment 命中任意 scope/颜色名即匹配;数组按“通用 → 具体”排序,后命中者
    /// 胜出(vscode TMTheme 语义)。Role 为 null 表示不改前景色(仅套用字体样式,如 markdown 加粗)。
    /// 顺序要点:"default" 先于 "keyword"(代码阅读器中 keyword 赢得冲突,如 keyword.control.default);
    /// "keyword" 最后(与 "operator"/"type" 冲突时关键字获胜);"function"/"method" 在 "type" 之后。</summary>
    private static readonly (string Fragment, CodeTokenRole? Role, bool Bold, bool Italic, bool Underline)[] Rules =
    [
        ("default", CodeTokenRole.Default, false, false, false),
        ("number", CodeTokenRole.Number, false, false, false),
        ("numeric", CodeTokenRole.Number, false, false, false),
        ("string", CodeTokenRole.String, false, false, false),
        ("preprocessor", CodeTokenRole.Preprocessor, false, false, false),
        ("attribute", CodeTokenRole.Attribute, false, false, false),
        ("class", CodeTokenRole.Type, false, false, false),
        ("type", CodeTokenRole.Type, false, false, false),
        ("tag", CodeTokenRole.Tag, false, false, false),
        // Markdown structure: headings and fenced/inline code are emitted as markup scopes by
        // TextMate. Keep these before the generic markup font rules so the shared reader gives
        // them a visible foreground even when the semantic pipeline disables AvalonEdit fallback.
        ("markup.heading", CodeTokenRole.Type, true, false, false),
        ("markup.fenced_code", CodeTokenRole.String, false, false, false),
        ("markup.raw", CodeTokenRole.String, false, false, false),
        ("meta.embedded.block", CodeTokenRole.String, false, false, false),
        ("fenced_code.block.language", CodeTokenRole.Keyword, false, false, false),
        ("comment", CodeTokenRole.Comment, false, true, false),
        ("method", CodeTokenRole.Function, false, false, false),
        ("function", CodeTokenRole.Function, false, false, false),
        ("punctuation", CodeTokenRole.Punctuation, false, false, false),
        ("operator", CodeTokenRole.Punctuation, false, false, false),
        ("url", CodeTokenRole.Link, false, false, false),
        ("link", CodeTokenRole.Link, false, false, false),
        ("markup.bold", null, true, false, false),
        ("markup.italic", null, false, true, false),
        ("markup.underline.link", CodeTokenRole.Link, false, false, true),
        ("keyword", CodeTokenRole.Keyword, false, false, false),
    ];

    /// <summary>无锁并发缓存:热路径(每 token 一次 Resolve)原先要两次 <c>lock</c>;
    /// ConcurrentDictionary 的 TryGetValue 读路径无锁,写入竞争由内部分段吸收。
    /// 值可为 null(未命中规则的否定缓存,与旧实现语义一致)。</summary>
    private static readonly ConcurrentDictionary<string, TokenThemeStyle?> Cache = new(StringComparer.Ordinal);
    private static int _insertCount;

    private static string CacheKey(IReadOnlyList<string> scopes) =>
        scopes.Count == 1 ? scopes[0] : string.Join('\n', scopes);

    /// <summary>Resolves the style for a scope stack; returns null when no rule matches. The result
    /// is cached by the joined scope stack; the cache is append-only and never invalidated because
    /// the resolved content (role + flags) is theme-independent.</summary>
    public static TokenThemeStyle? Resolve(IReadOnlyList<string>? scopes)
    {
        if (scopes is null || scopes.Count == 0)
        {
            return null;
        }

        var key = CacheKey(scopes);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        TokenThemeStyle? style = null;
        foreach (var scope in scopes)
        {
            for (var i = 0; i < Rules.Length; i++)
            {
                var rule = Rules[i];
                if (scope.Contains(rule.Fragment, StringComparison.OrdinalIgnoreCase))
                {
                    style = new TokenThemeStyle(rule.Role, rule.Bold, rule.Italic, rule.Underline);
                }
            }
        }

        if (Cache.Count > 4096 && Interlocked.Increment(ref _insertCount) % 512 == 0)
        {
            Cache.Clear();
        }

        Cache[key] = style;

        return style;
    }

    /// <summary>Matches a single text fragment (the color-name path used by the Diff reader's
    /// AvalonEdit built-in definitions): shares the exact rule table with <see cref="Resolve"/>.</summary>
    public static TokenThemeStyle? ResolveFragment(string? fragment) =>
        string.IsNullOrWhiteSpace(fragment) ? null : Resolve([fragment]);

    /// <summary>代码阅读器色板:角色 → CodeToken* 调色板 token。Preprocessor/Punctuation/Default
    /// 在代码阅读器没有对应类别,返回 null(沿用默认正文色)。</summary>
    public static string? CodeReaderBrushName(CodeTokenRole role) => role switch
    {
        CodeTokenRole.Comment => "CodeTokenCommentBrush",
        CodeTokenRole.String => "CodeTokenStringBrush",
        CodeTokenRole.Number => "CodeTokenNumberBrush",
        CodeTokenRole.Keyword => "CodeTokenKeywordBrush",
        CodeTokenRole.Type => "CodeTokenTypeBrush",
        CodeTokenRole.Function => "CodeTokenFunctionBrush",
        CodeTokenRole.Tag => "CodeTokenTagBrush",
        CodeTokenRole.Attribute => "CodeTokenAttributeBrush",
        CodeTokenRole.Link => "CodeTokenLinkBrush",
        _ => null,
    };

    /// <summary>Compatibility name for callers that recolor AvalonEdit fallback definitions.
    /// Diff no longer owns a second palette: it resolves exactly the same CodeToken* resources as
    /// the resource-manager reader.</summary>
    public static string? DiffBrushName(CodeTokenRole role) => CodeReaderBrushName(role);
}
