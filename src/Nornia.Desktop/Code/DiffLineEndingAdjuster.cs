using Nornia.Core.Models;

namespace Nornia.Desktop.Code;

/// <summary>
/// 渲染前对已解析的 git diff 做行尾归一:git 会把“仅行尾换行符状态变化”的行(文件末尾
/// 新增/去掉换行符、或 CRLF↔LF)如实标记为 删+增 对,但两行可见文字完全相同。这类行若按
/// 增/删着色,用户会看到“末尾多出一块实际没有更改的内容块”。
/// 这里把文字(忽略结尾 CR 后)完全相同的 删/增 配对折叠为一条上下文行(保留两端行号与
/// 归一化文本);"\ No newline at end of file" 提示行原样保留,行尾差异的原因依然可见。
/// 纯函数,不触碰 git 模型之外的任何状态。
/// </summary>
public static class DiffLineEndingAdjuster
{
    /// <summary>折叠整个文件 diff 中的行尾-only 变更对;无变化时返回原实例。</summary>
    public static GitFileDiff Adjust(GitFileDiff diff)
    {
        if (diff.Hunks.Count == 0)
        {
            return diff;
        }

        var hunks = new List<GitDiffHunk>(diff.Hunks.Count);
        var changed = false;
        foreach (var hunk in diff.Hunks)
        {
            var lines = AdjustLines(hunk.Lines);
            if (!ReferenceEquals(lines, hunk.Lines))
            {
                changed = true;
                hunks.Add(hunk with { Lines = lines });
            }
            else
            {
                hunks.Add(hunk);
            }
        }

        return changed ? diff with { Hunks = hunks } : diff;
    }

    /// <summary>折叠单个 hunk 行序列(可含 HunkHeader 首行)中的行尾-only 变更对;
    /// 无折叠时返回原实例。</summary>
    public static IReadOnlyList<GitDiffLine> AdjustLines(IReadOnlyList<GitDiffLine> lines)
    {
        var contextFor = new Dictionary<int, GitDiffLine>();
        var skip = new HashSet<int>();

        var i = 0;
        while (i < lines.Count)
        {
            if (!IsChangeRegionKind(lines[i].Kind))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < lines.Count && IsChangeRegionKind(lines[i].Kind))
            {
                i++;
            }

            PairLineEndingOnly(lines, start, i, contextFor, skip);
        }

        if (contextFor.Count == 0)
        {
            return lines;
        }

        var result = new List<GitDiffLine>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            if (skip.Contains(index))
            {
                continue;
            }

            if (contextFor.TryGetValue(index, out var context))
            {
                result.Add(context);
                continue;
            }

            result.Add(lines[index]);
        }

        return result;
    }

    /// <summary>git 在同一变更区域内先输出连续的删除块再输出连续的新增块(EOF 换行符提示
    /// 行可能夹在两块之间,属于变更区域的一部分)。按序配对后,文字仅差结尾 CR 的对即
    /// “行尾-only”变更:折叠为一条上下文行。</summary>
    private static void PairLineEndingOnly(
        IReadOnlyList<GitDiffLine> lines,
        int start,
        int end,
        Dictionary<int, GitDiffLine> contextFor,
        HashSet<int> skip)
    {
        var removed = new List<int>();
        var added = new List<int>();
        for (var index = start; index < end; index++)
        {
            switch (lines[index].Kind)
            {
                case GitDiffLineKind.Removed:
                    removed.Add(index);
                    break;
                case GitDiffLineKind.Added:
                    added.Add(index);
                    break;
            }
        }

        var pairs = Math.Min(removed.Count, added.Count);
        for (var p = 0; p < pairs; p++)
        {
            var removedLine = lines[removed[p]];
            var addedLine = lines[added[p]];
            var normalized = removedLine.Text.TrimEnd('\r');
            if (normalized != addedLine.Text.TrimEnd('\r'))
            {
                continue;
            }

            contextFor[removed[p]] = new GitDiffLine(
                GitDiffLineKind.Context,
                removedLine.OldLineNumber,
                addedLine.NewLineNumber,
                normalized);
            skip.Add(added[p]);
        }
    }

    private static bool IsChangeRegionKind(GitDiffLineKind kind) =>
        kind is GitDiffLineKind.Added or GitDiffLineKind.Removed or GitDiffLineKind.Notice;
}
