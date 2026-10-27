using Nornia.Core.Models;

namespace Nornia.Desktop.Code;

/// <summary>边距上的一条 Blame 注解(只在提交切换段的首行显示;续行留白)。
/// Uncommitted 表示工作树未提交行(git 的 0000 哈希块)。Summary 供悬浮详情。</summary>
public readonly record struct BlameAnnotation(
    int Line,
    string DisplayAuthor,
    DateTimeOffset When,
    string ShortHash,
    bool IsBoundary,
    bool IsUncommitted,
    string Summary = "");

/// <summary>GitBlame → 边距注解的纯布局(无 WPF 依赖,可单测):
/// 相邻同提交的行折叠为一段,只保留段首注解;相对日期按"现在"参数计算。</summary>
public static class BlameAnnotationLayout
{
    public static IReadOnlyList<BlameAnnotation> Build(GitBlame blame)
    {
        var result = new List<BlameAnnotation>();
        var previous = -1;
        for (var line = 0; line < blame.LineCommits.Count; line++)
        {
            var index = blame.LineCommits[line];
            if (index == previous || index < 0 || index >= blame.Commits.Count)
            {
                continue;
            }

            previous = index;
            var commit = blame.Commits[index];
            var uncommitted = IsUncommittedHash(commit.Hash);
            result.Add(new BlameAnnotation(
                line + 1,
                uncommitted ? "未提交" : commit.AuthorName,
                commit.AuthorDate,
                ShortHash(commit.Hash),
                commit.IsBoundary,
                uncommitted,
                commit.Summary));
        }

        return result;
    }

    /// <summary>悬浮详情:作者 · 完整时间 · 短哈希 + 换行提交主题(未提交行只有一句提示)。</summary>
    public static string FormatToolTip(in BlameAnnotation annotation)
    {
        if (annotation.IsUncommitted)
        {
            return "工作树未提交的更改";
        }

        return $"{annotation.DisplayAuthor} · {annotation.When.ToLocalTime():yyyy-MM-dd HH:mm} · {annotation.ShortHash}\n{annotation.Summary}";
    }

    /// <summary>注解文本:"作者 · 相对时间 · a1b2c3d"(未提交行只有 "未提交")。</summary>
    public static string Format(BlameAnnotation annotation, DateTimeOffset now)
    {
        if (annotation.IsUncommitted)
        {
            return "未提交";
        }

        return $"{annotation.DisplayAuthor} · {FormatRelativeDate(annotation.When, now)} · {annotation.ShortHash}";
    }

    /// <summary>VS Code/GitLens 式相对日期:今天/昨天/N 天前(≤30)/N 个月前(≤11)/年份。</summary>
    public static string FormatRelativeDate(DateTimeOffset when, DateTimeOffset now)
    {
        var local = when.ToLocalTime();
        var nowLocal = now.ToLocalTime();
        var date = local.Date;
        var today = nowLocal.Date;
        var days = (today - date).Days;
        if (days <= 0)
        {
            return "今天";
        }

        if (days == 1)
        {
            return "昨天";
        }

        if (days <= 30)
        {
            return $"{days} 天前";
        }

        var months = (today.Year - date.Year) * 12 + today.Month - date.Month;
        if (today.Day < date.Day)
        {
            months--;
        }

        if (months is >= 1 and <= 11)
        {
            return $"{months} 个月前";
        }

        return date.Year == today.Year - 1 ? "去年" : $"{date.Year} 年";
    }

    private static bool IsUncommittedHash(string hash) =>
        hash.Length > 0 && hash.All(ch => ch == '0');

    private static string ShortHash(string hash) => hash.Length > 7 ? hash[..7] : hash;
}
