namespace Nornia.Core;

/// <summary>Centralized application-wide constants. Hard-coded values that previously lived in
/// features (UI log cap, scan cooldowns) are declared here once.</summary>
public static class NorniaSettings
{
    /// <summary>Serilog rolling file retention in days (Desktop).</summary>
    public const int SerilogFileRetentionDays = 14;

    /// <summary>Maximum entries kept in the Desktop UI output panel.</summary>
    public const int UiLogMaximumEntries = 1000;

    /// <summary>Environment scans are coalesced for this many seconds so repeated refreshes
    /// from different callers reuse the latest result instead of re-scanning every provider.</summary>
    public const int InventoryScanCacheSeconds = 30;

    /// <summary>File-system cache scans are cached for this many seconds before a forced rescan.</summary>
    public const int CacheScanCacheSeconds = 60;
}