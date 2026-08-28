using System.Collections.Generic;
using System.Linq;

namespace Nornia.Desktop.Code;

/// <summary>折叠区间的只读几何模型(扁平数组 + 父索引 + 层级),对齐 vscode-main 的
/// <c>FoldingRegions</c>(foldingRanges.ts)。折叠/展开状态由 AvalonEdit 的
/// <see cref="ICSharpCode.AvalonEdit.Folding.FoldingSection"/> 持有,此模型只负责几何、
/// 嵌套层级与命中查询,供折叠栏(chevron)与折叠段背景渲染使用。</summary>
public sealed record FoldRegion(int StartLine, int EndLine, bool IsCollapsed, int ParentIndex, int Level)
{
    public bool IsMultiLine => EndLine > StartLine;
    public bool ContainsLine(int line) => line >= StartLine && line <= EndLine;
}

public static class FoldingRegions
{
    /// <summary>由按起止行给出的折叠段构建嵌套模型。输入无需排序——Build 内部按
    /// (StartLine 升序, EndLine 降序)排序后做栈式嵌套(等起点的外层先于内层)。</summary>
    public static IReadOnlyList<FoldRegion> Build(IReadOnlyList<(int StartLine, int EndLine, bool IsCollapsed)> foldings)
    {
        if (foldings.Count == 0)
        {
            return [];
        }

        var ordered = foldings
            .Where(item => item.StartLine >= 1 && item.EndLine > item.StartLine)
            .OrderBy(item => item.StartLine)
            .ThenByDescending(item => item.EndLine)
            .ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }
        var regions = new FoldRegion[ordered.Length];
        var stack = new Stack<int>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var (start, end, collapsed) = ordered[i];
            var parent = -1;
            while (stack.Count > 0)
            {
                var candidate = stack.Peek();
                if (regions[candidate].EndLine >= end)
                {
                    parent = candidate;
                    break;
                }

                stack.Pop();
            }

            regions[i] = new FoldRegion(start, end, collapsed, parent, parent < 0 ? 1 : regions[parent].Level + 1);
            stack.Push(i);
        }

        return regions;
    }

    /// <summary>包含该行的最内层区域(vscode <c>findRange</c> 语义:命中最深嵌套者;同层取
    /// 数组序首个,与原线性实现逐位一致)。E2:只扫描 StartLine &le; line 的前缀段
    /// (二分定位下界),不扫描已排序数组的整个尾部。</summary>
    public static FoldRegion? FindAtLine(IReadOnlyList<FoldRegion> regions, int line)
    {
        FoldRegion? deepest = null;
        var limit = LastIndexWithStartLessOrEqual(regions, line);
        for (var i = 0; i <= limit; i++)
        {
            var region = regions[i];
            if (region.ContainsLine(line) && (deepest is null || region.Level > deepest.Level))
            {
                deepest = region;
            }
        }

        return deepest;
    }

    /// <summary>起始于该行且未被折叠区间遮挡的最外层区域(折叠栏 chevron 与点击目标):
    /// 区域整体不能位于任何折叠区间内部(被遮挡会从视图中消失)。只扫描 StartLine == line
    /// 的连续段(二分定位起点;同 Start 内 EndLine 降序)。</summary>
    public static FoldRegion? FindStartVisibleAtLine(IReadOnlyList<FoldRegion> regions, int line)
    {
        FoldRegion? best = null;
        var first = LowerBoundByStart(regions, line);
        for (var i = first; i < regions.Count && regions[i].StartLine == line; i++)
        {
            var region = regions[i];
            if (region.EndLine <= line || IsRegionHidden(regions, region))
            {
                continue;
            }

            if (best is null || region.Level < best.Level)
            {
                best = region;
            }
        }

        return best;
    }

    /// <summary>区域是否位于某个已折叠区间内部(自身将被折叠内容隐藏,不渲染 chevron)。
    /// 与原实现一致:严格包含(Start 更小且 End 不小于)的已折叠段;等起点不视为遮挡。</summary>
    public static bool IsRegionHidden(IReadOnlyList<FoldRegion> regions, FoldRegion region)
    {
        foreach (var other in regions)
        {
            if (other.IsCollapsed && other.StartLine < region.StartLine && region.EndLine <= other.EndLine)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>该行是否位于某个折叠区间内部(非首行)——用于隐藏被折叠内容的 chevron,
    /// 以及点击折叠内容行时展开最近外层。</summary>
    public static bool IsLineHiddenInCollapsed(IReadOnlyList<FoldRegion> regions, int line)
    {
        var limit = LastIndexWithStartLessOrEqual(regions, line);
        for (var i = 0; i <= limit; i++)
        {
            var region = regions[i];
            if (region.IsCollapsed && region.StartLine < line && line <= region.EndLine)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>包含该行的最内层已折叠区间(点击被折叠内容行时展开它)。同原语义:
    /// 最深层;同层取数组序首个。</summary>
    public static FoldRegion? FindCollapsedContaining(IReadOnlyList<FoldRegion> regions, int line)
    {
        FoldRegion? deepest = null;
        var limit = LastIndexWithStartLessOrEqual(regions, line);
        for (var i = 0; i <= limit; i++)
        {
            var region = regions[i];
            if (region.IsCollapsed && region.StartLine < line && line <= region.EndLine &&
                (deepest is null || region.Level > deepest.Level))
            {
                deepest = region;
            }
        }

        return deepest;
    }

    /// <summary>最后一个 StartLine &le; line 的下标(二分);无则 -1。要求 StartLine 非降序。</summary>
    internal static int LastIndexWithStartLessOrEqual(IReadOnlyList<FoldRegion> regions, int line)
    {
        var lo = 0;
        var hi = regions.Count - 1;
        var answer = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (regions[mid].StartLine <= line)
            {
                answer = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return answer;
    }

    /// <summary>第一个 StartLine &ge; line 的下标(二分);要求 StartLine 非降序。</summary>
    private static int LowerBoundByStart(IReadOnlyList<FoldRegion> regions, int line)
    {
        var lo = 0;
        var hi = regions.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (regions[mid].StartLine < line)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}