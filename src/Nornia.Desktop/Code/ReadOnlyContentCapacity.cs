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

    public static ReadOnlyContentTier ForSource(long bytes) => bytes switch
    {
        <= FullSourceBytes => ReadOnlyContentTier.Full,
        <= WindowedSourceBytes => ReadOnlyContentTier.Windowed,
        _ => ReadOnlyContentTier.Summary,
    };

    public static ReadOnlyContentTier ForDiff(int lines) => lines switch
    {
        <= FullDiffLines => ReadOnlyContentTier.Full,
        <= WindowedDiffLines => ReadOnlyContentTier.Windowed,
        _ => ReadOnlyContentTier.Summary,
    };
}
