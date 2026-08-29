using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Nornia.Desktop.Services;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Nornia.Desktop.Code;

/// <summary>Renders the same TextMate presentation snapshot used by the resource-manager code
/// preview.  The brush lookup is supplied by the host so both code and diff views resolve the
/// active theme from their own resource scope.
/// 主题切换经 <see cref="ThemeEvents.ThemeChanged"/> 广播(弱引用订阅,不持有宿主视图)触发
/// 一次全量重解析:之后每 token 的刷子/Typeface 都是 O(1) 字典命中,不再逐 token
/// TryFindResource / 新建 Typeface。</summary>
public sealed class CodeTokenColorizer : DocumentColorizingTransformer
{
    /// <summary>The colorizer can use every one of these palette names; they are re-resolved
    /// once per theme change into <see cref="_brushCache"/> (the "frozen" palette).</summary>
    private static readonly string[] CachedBrushNames =
    [
        "CodeTokenCommentBrush",
        "CodeTokenStringBrush",
        "CodeTokenNumberBrush",
        "CodeTokenKeywordBrush",
        "CodeTokenTypeBrush",
        "CodeTokenFunctionBrush",
        "CodeTokenTagBrush",
        "CodeTokenAttributeBrush",
        "CodeTokenLinkBrush",
        "TextBrush",
    ];

    private readonly Func<string, Brush?> _brush;
    private readonly Func<FontFamily> _fontFamily;
    // 构造线程的 Dispatcher(宿主视图线程):主题广播归组用,见 RefreshThemeForBroadcast。
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, Brush?> _brushCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Family, bool Italic, bool Bold), Typeface> _typefaceCache = new();
    private bool _paletteCurrent;
    private IReadOnlyDictionary<int, IReadOnlyList<CodeTokenSpan>> _byLine =
        new Dictionary<int, IReadOnlyList<CodeTokenSpan>>();

    public CodeTokenColorizer(Func<string, Brush?> brush, Func<FontFamily> fontFamily)
    {
        _brush = brush;
        _fontFamily = fontFamily;
        // 宿主视图(代码阅读器/Diff 阅读器)在各自主题路径调用 RefreshTheme();
        // 此处的弱引用订阅兜底覆盖没有显式接线的宿主,且不阻止已关闭视图回收。
        ColorizerThemeWatcher.Register(this);
    }

    public bool HasTokens => _byLine.Count > 0;

    /// <summary>主题变化时重解析冻结调色板(宿主视图主题路径或 ThemeEvents 广播触发)。
    /// 只解析 <see cref="CachedBrushNames"/> 中的名称;未登记的名称在首次取用时惰性补齐。</summary>
    public void RefreshTheme() => RebuildPalette();

    /// <summary>ThemeEvents 广播入口(ColorizerThemeWatcher 调用):生产环境颜色化器恒建于
    /// UI 线程,行为不变;测试进程里并行测试类可能从工作线程 Raise——非宿主线程只归组回
    /// 宿主 Dispatcher,避免跨线程执行 TryFindResource 等线程亲和调用。</summary>
    internal void RefreshThemeForBroadcast()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(RefreshTheme);
            return;
        }

        RefreshTheme();
    }

    private void RebuildPalette()
    {
        _brushCache.Clear();
        foreach (var name in CachedBrushNames)
        {
            _brushCache[name] = _brush(name);
        }

        _typefaceCache.Clear();
        _paletteCurrent = true;
    }

    private Brush? ResolveBrush(string name)
    {
        if (!_paletteCurrent)
        {
            RebuildPalette();
        }

        if (!_brushCache.TryGetValue(name, out var brush))
        {
            brush = _brush(name);
            _brushCache[name] = brush;
        }

        return brush;
    }

    private Typeface GetOrCreateTypeface(FontFamily family, bool italic, bool bold)
    {
        var key = (family.Source, italic, bold);
        if (!_typefaceCache.TryGetValue(key, out var typeface))
        {
            typeface = new Typeface(
                family,
                italic ? FontStyles.Italic : FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);
            _typefaceCache[key] = typeface;
        }

        return typeface;
    }

    public void SetSnapshot(CodePresentationSnapshot? snapshot)
    {
        // Worker-generated snapshots already carry this index. Keep the fallback for tests and
        // callers that construct a snapshot directly.
        _byLine = snapshot?.TokensByLine
            ?? snapshot?.Tokens
                .GroupBy(token => token.Line)
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyList<CodeTokenSpan>)group.ToArray())
            ?? new Dictionary<int, IReadOnlyList<CodeTokenSpan>>();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!_byLine.TryGetValue(line.LineNumber, out var tokens))
        {
            return;
        }

        foreach (var token in tokens)
        {
            var start = Math.Clamp(line.Offset + token.Start, line.Offset, line.EndOffset);
            var end = Math.Clamp(start + token.Length, start, line.EndOffset);
            if (end <= start)
            {
                continue;
            }

            // Scope rules take precedence (including font styles); a token without a matching
            // scope falls back to the coarse kind palette, exactly as the resource-manager view.
            var style = TokenTheme.Resolve(token.Scopes);
            Brush? brush = null;
            if (style?.Role is { } role && TokenTheme.CodeReaderBrushName(role) is { } tokenName)
            {
                brush = ResolveBrush(tokenName);
            }

            brush ??= ResolveBrush(TokenBrushName(token.Kind));
            if (brush is null)
            {
                continue;
            }

            var italic = style?.Italic == true;
            var bold = style?.Bold == true;
            // 缓存 Typeface 在 ChangeLinePart 前解析(每行同一 (family, italic, bold) 组合
            // 复用同一实例,避免逐 token 分配)。
            var typeface = italic || bold ? GetOrCreateTypeface(_fontFamily(), italic, bold) : null;
            ChangeLinePart(start, end, element =>
            {
                var properties = element.TextRunProperties;
                properties.SetForegroundBrush(brush);
                if (typeface is not null)
                {
                    properties.SetTypeface(typeface);
                }

                if (style?.Underline == true)
                {
                    properties.SetTextDecorations(TextDecorations.Underline);
                }
            });
        }
    }

    private static string TokenBrushName(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Comment => "CodeTokenCommentBrush",
        CodeTokenKind.String => "CodeTokenStringBrush",
        CodeTokenKind.Number => "CodeTokenNumberBrush",
        CodeTokenKind.Keyword => "CodeTokenKeywordBrush",
        CodeTokenKind.Type => "CodeTokenTypeBrush",
        CodeTokenKind.Function => "CodeTokenFunctionBrush",
        CodeTokenKind.Tag => "CodeTokenTagBrush",
        CodeTokenKind.Attribute => "CodeTokenAttributeBrush",
        CodeTokenKind.Link => "CodeTokenLinkBrush",
        _ => "TextBrush",
    };
}

/// <summary>ThemeEvents → colorizer 调色板重建的弱引用分发器:静态事件只强引用本分发器,
/// 已关闭视图的 colorizer 可被 GC 回收(订阅表定期清扫),避免"静态事件持有宿主视图"的泄漏。
/// 主题切换是低频事件,每次广播清扫一次即可。</summary>
internal static class ColorizerThemeWatcher
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<CodeTokenColorizer>> Subscribers = [];
    private static bool _subscribed;

    public static void Register(CodeTokenColorizer colorizer)
    {
        lock (Gate)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                ThemeEvents.ThemeChanged += OnThemeChanged;
            }

            Subscribers.Add(new WeakReference<CodeTokenColorizer>(colorizer));
        }
    }

    private static void OnThemeChanged(object? sender, AppTheme theme)
    {
        List<CodeTokenColorizer>? live = null;
        lock (Gate)
        {
            for (var i = Subscribers.Count - 1; i >= 0; i--)
            {
                if (!Subscribers[i].TryGetTarget(out var target))
                {
                    Subscribers.RemoveAt(i);
                    continue;
                }

                (live ??= []).Add(target);
            }
        }

        if (live is null)
        {
            return;
        }

        foreach (var colorizer in live)
        {
            colorizer.RefreshThemeForBroadcast();
        }
    }
}
