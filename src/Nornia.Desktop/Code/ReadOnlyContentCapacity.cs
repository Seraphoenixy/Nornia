namespace Nornia.Desktop.Code;

/// <summary>Shared boundaries for read-only source and diff surfaces.  The policy has no WPF or
/// Git dependency, making capacity decisions testable before a view allocates a TextDocument.</summary>
public enum ReadOnlyContentTier
{
    Full,
    Windowed,
    Summary,
}

public static class ReadOnlyContentCapacity
{
    public const long FullSourceBytes = 8L * 1024 * 1024;
    public const long WindowedSourceBytes = 32L * 1024 * 1024;
    public const int FullDiffLines = 100_000;
    public const int WindowedDiffLines = 500_000;

    public static ReadOnlyContentTier ForSource(long bytes) => Default.ForSource(bytes);

    public static ReadOnlyContentTier ForDiff(int lines) => Default.ForDiff(lines);

    internal static ReadOnlyContentLimits Default => ReadOnlyContentLimits.Default;
}

/// <summary>可实例化的分层阈值(设置 <c>nornia.reading.*</c> 可覆盖默认常量)。
/// 分层单调性由构造保证:窗口化阈值不允许低于全量阈值(低于时抬到全量,窗口化层自然消失,
/// 不会出现"窗口化反而比全量更小"的倒挂)。</summary>
public sealed record ReadOnlyContentLimits(
    long FullSourceBytes,
    long WindowedSourceBytes,
    int FullDiffLines,
    int WindowedDiffLines)
{
    public static ReadOnlyContentLimits Default { get; } = new(
        ReadOnlyContentCapacity.FullSourceBytes,
        ReadOnlyContentCapacity.WindowedSourceBytes,
        ReadOnlyContentCapacity.FullDiffLines,
        ReadOnlyContentCapacity.WindowedDiffLines);

    public long EffectiveWindowedSourceBytes => Math.Max(WindowedSourceBytes, FullSourceBytes);

    public int EffectiveWindowedDiffLines => Math.Max(WindowedDiffLines, FullDiffLines);

    // 关系模式要求常量,而位置参数是属性:用比较链而不是 switch 模式。
    public ReadOnlyContentTier ForSource(long bytes)
    {
        if (bytes <= FullSourceBytes)
        {
            return ReadOnlyContentTier.Full;
        }

        return bytes <= EffectiveWindowedSourceBytes ? ReadOnlyContentTier.Windowed : ReadOnlyContentTier.Summary;
    }

    public ReadOnlyContentTier ForDiff(int lines)
    {
        if (lines <= FullDiffLines)
        {
            return ReadOnlyContentTier.Full;
        }

        return lines <= EffectiveWindowedDiffLines ? ReadOnlyContentTier.Windowed : ReadOnlyContentTier.Summary;
    }
}
