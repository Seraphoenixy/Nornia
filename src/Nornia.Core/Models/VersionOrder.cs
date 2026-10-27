namespace Nornia.Core.Models;

/// <summary>
/// 版本号字符串的数值序比较。
/// </summary>
/// <remarks>
/// <para>切勿使用字典序（<see cref="StringComparer.OrdinalIgnoreCase"/>）比较版本号：字典序下
/// "9.0.1" 大于 "10.0.0"，会让「取最新已安装版本」选中错误的实例，
/// 进而把修复计划的目标版本算错。</para>
/// <para>本类型只负责「排序/取最新」这一件事，不参与版本约束（前缀/比较器范围）的求值——
/// 那是 <c>Nornia.Project.Models.VersionConstraint</c> 的职责。两者都要解析版本号，
/// 但语义不同：排序必须容忍占位值与古怪后缀并给出确定顺序，约束求值则宁可判定不通过。</para>
/// </remarks>
public static class VersionOrder
{
    /// <summary>可直接用于 <c>OrderBy</c>/<c>OrderByDescending</c> 的版本序比较器。</summary>
    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);

    /// <summary>
    /// 比较两个版本字符串。返回值为负表示 <paramref name="left"/> 较旧。
    /// 无法解析的一方被视为最旧（排在有完整版本号者之后），避免占位值篡夺「最新版本」。
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var leftParsed = TryGetParts(left, out var leftParts, out var leftPrerelease);
        var rightParsed = TryGetParts(right, out var rightParts, out var rightPrerelease);

        if (!leftParsed && !rightParsed)
        {
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        if (!leftParsed)
        {
            return -1;
        }

        if (!rightParsed)
        {
            return 1;
        }

        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            var comparison = (index < leftParts.Length ? leftParts[index] : 0)
                .CompareTo(index < rightParts.Length ? rightParts[index] : 0);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        // 稳定版高于同一版本的预发布，与 VersionValue 的语义保持一致。
        if (leftPrerelease is null && rightPrerelease is not null) return 1;
        if (leftPrerelease is not null && rightPrerelease is null) return -1;
        return string.Compare(leftPrerelease, rightPrerelease, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按数值序取最大者。序列为空时返回 <see langword="null"/>。
    /// </summary>
    public static TSource? MaxByVersion<TSource>(this IEnumerable<TSource> source, Func<TSource, string?> versionSelector)
    {
        TSource? best = default;
        string? bestVersion = null;
        var found = false;

        foreach (var item in source)
        {
            var version = versionSelector(item);
            if (!found)
            {
                best = item;
                bestVersion = version;
                found = true;
                continue;
            }

            if (Compare(version, bestVersion) > 0)
            {
                best = item;
                bestVersion = version;
            }
        }

        return found ? best : default;
    }

    private static bool TryGetParts(string? value, out int[] parts, out string? prerelease)
    {
        parts = [];
        prerelease = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().TrimStart('v', 'V');
        var separator = text.IndexOf('-');
        var head = separator < 0 ? text : text[..separator];
        prerelease = separator < 0 ? null : text[(separator + 1)..];

        var numbers = new List<int>(4);
        var assigned = false;
        foreach (var segment in head.Split('.'))
        {
            var digitCount = 0;
            while (digitCount < segment.Length && char.IsAsciiDigit(segment[digitCount]))
            {
                digitCount++;
            }

            // 遇到非数值段（如 "windows.1" 的 windows）或段内混入非数字后缀即停止，
            // 已收集到的数值部分仍然有效 —— 与 VersionValue.TryParseInstalled 一致。
            if (digitCount == 0)
            {
                break;
            }

            numbers.Add(int.Parse(segment[..digitCount]));
            assigned = true;

            if (digitCount < segment.Length)
            {
                break;
            }
        }

        parts = numbers.ToArray();
        return assigned;
    }
}
