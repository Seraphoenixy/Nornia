using Nornia.Core.Interfaces;

namespace Nornia.Desktop.ViewModels;

/// <summary>Formats the persisted-scan timestamp from <c>scan_state</c> as the page-header freshness
/// hint ("上次扫描：X 前"), so users can tell how current a served-from-snapshot list is and when a
/// manual 重新扫描 is warranted. Purely cosmetic: any failure degrades to an empty hint.</summary>
public static class InventoryScanAgeText
{
    public const string RuntimeScanKind = "runtimes";
    public const string PackageScanKind = "packages";

    /// <summary>Loads and formats the last scan time for one inventory kind; empty when unavailable.</summary>
    public static async Task<string> LoadAsync(
        IInventoryScanStateRepository? repository,
        string kind,
        CancellationToken cancellationToken = default)
    {
        if (repository is null) return string.Empty;
        try
        {
            var state = await repository.GetAsync(kind, cancellationToken);
            return Format(state?.ScannedAt);
        }
        catch
        {
            // The freshness hint must never fail the page that hosts it.
            return string.Empty;
        }
    }

    public static string Format(long? scannedAt)
    {
        if (scannedAt is null or 0) return "上次扫描：从未扫描";
        var elapsed = DateTimeOffset.Now - DateTimeOffset.FromUnixTimeSeconds(scannedAt.Value);
        var text = elapsed.TotalSeconds < 60 ? "刚刚"
            : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes} 分钟前"
            : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours} 小时前"
            : $"{(int)elapsed.TotalDays} 天前";
        return $"上次扫描：{text}";
    }
}
