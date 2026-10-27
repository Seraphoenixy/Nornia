namespace Nornia.Desktop.Code;

/// <summary>查找匹配所在的 diff 面板:内联模式落在统一行模型上,并排模式落在旧/新侧行模型上。</summary>
public enum DiffSearchPane
{
    Inline,
    Old,
    New,
}

/// <summary>一处 diff 查找命中:所在面板 + 行内列偏移 + 长度。刻意只描述"文本坐标",
/// 不携带 AvalonEdit 的文档偏移——那一步需要编辑器实例,留在视图层完成。</summary>
public readonly record struct DiffSearchMatch(DiffSearchPane Pane, int LineIndex, int Column, int Length);

/// <summary>diff 查找引擎。<b>纯字符串逻辑、零 WPF 依赖</b>,因此可以脱离 STA 宿主单测。
///
/// <para>此前这段逻辑嵌在 <c>DiffDocumentView.xaml.cs</c> 里(防抖计时器、逐行扫描、并排双栏
/// 合并排序、1 万条上限、索引回绕),属于"业务逻辑写入 code-behind"——AGENTS.md 明令禁止,
/// 且那部分零测试覆盖。同时它与 <c>FilePreviewTab</c> 的查找是两套实现(大小写/整词/正则支持
/// 不一致)。现在两侧共用 <see cref="ITextSearchService"/> 的匹配语义,行为自然收敛。</para>
///
/// <para>查找选项通过 <see cref="TextSearchOptions"/> 传入而不是各写一份:<c>ITextSearchService</c>
/// 已支持大小写/整词/正则,本引擎只是把它按"行模型"驱动一遍,并负责并排双栏的合并顺序。</para></summary>
public static class DiffTextSearch
{
    /// <summary>单次查找收集的匹配上限。放在这里唯一定义——此前视图里有 5 处硬编码的
    /// <c>10_000</c>,而 VM 里另有一份 <c>MaxSearchMatches</c>。</summary>
    public const int MaximumMatches = SearchLimits.MaximumFindMatches;

    /// <summary>diff 查找面板目前不暴露选项开关,行为等同"忽略大小写、非整词、非正则"——
    /// 与改造前的实现保持一致。一旦面板加上开关,把这个值换成绑定即可,引擎无需改动。</summary>
    public static readonly TextSearchOptions DefaultOptions = new(CaseSensitive: false, WholeWord: false, UseRegex: false);

    /// <summary>在一段行模型上查找(内联统一行模型,或并排的某一侧)。匹配按行号、列号升序返回,
    /// 单行内多处命中按出现顺序。</summary>
    public static IReadOnlyList<DiffSearchMatch> SearchLines(
        IReadOnlyList<DiffRenderLine> lines,
        DiffSearchPane pane,
        string query,
        ITextSearchService search,
        TextSearchOptions? options = null,
        int maximumMatches = MaximumMatches)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(search);

        if (string.IsNullOrEmpty(query) || maximumMatches <= 0)
        {
            return [];
        }

        var effective = options ?? DefaultOptions;
        var matches = new List<DiffSearchMatch>();
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            CollectLine(lines[lineIndex].Text, lineIndex, pane, query, effective, search, maximumMatches, matches);
            if (matches.Count >= maximumMatches)
            {
                break;
            }
        }

        return matches;
    }

    /// <summary>并排模式的行对齐合并:两侧匹配按(行号, 旧在前)交错,使 F3 自上而下推进时
    /// 不会先走完整条旧侧再跳回顶部走新侧。</summary>
    public static IReadOnlyList<DiffSearchMatch> MergeSideBySide(
        IReadOnlyList<DiffSearchMatch> oldMatches,
        IReadOnlyList<DiffSearchMatch> newMatches,
        int maximumMatches = MaximumMatches)
    {
        ArgumentNullException.ThrowIfNull(oldMatches);
        ArgumentNullException.ThrowIfNull(newMatches);

        if (maximumMatches <= 0)
        {
            return [];
        }

        var merged = new List<DiffSearchMatch>(Math.Min(oldMatches.Count + newMatches.Count, maximumMatches));
        var (oldIndex, newIndex) = (0, 0);
        while ((oldIndex < oldMatches.Count || newIndex < newMatches.Count) && merged.Count < maximumMatches)
        {
            var takeOld = newIndex >= newMatches.Count
                || (oldIndex < oldMatches.Count
                    && oldMatches[oldIndex].LineIndex <= newMatches[newIndex].LineIndex);
            merged.Add(takeOld ? oldMatches[oldIndex++] : newMatches[newIndex++]);
        }

        return merged;
    }

    /// <summary>环形步进:取下一处/上一处命中。当前无命中(<paramref name="currentIndex"/> 为负)
    /// 时,向后从头开始、向前从末尾开始,与查找框的 F3 / Shift+F3 语义一致。</summary>
    public static int Step(int currentIndex, int matchCount, int step)
    {
        if (matchCount <= 0)
        {
            return -1;
        }

        if (currentIndex < 0)
        {
            return step < 0 ? matchCount - 1 : 0;
        }

        return (currentIndex + step + matchCount) % matchCount;
    }

    private static void CollectLine(
        string text,
        int lineIndex,
        DiffSearchPane pane,
        string query,
        TextSearchOptions options,
        ITextSearchService search,
        int maximumMatches,
        List<DiffSearchMatch> matches)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var hit in search.FindAll(text, query, options))
        {
            matches.Add(new DiffSearchMatch(pane, lineIndex, hit.Offset, hit.Length));
            if (matches.Count >= maximumMatches)
            {
                return;
            }
        }
    }
}
