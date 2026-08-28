using Nornia.Package.Localization;

namespace Nornia.Package.Providers;

/// <summary>
/// Winget exit codes with observable semantics. The "no match" code has drifted across winget
/// versions — legacy 0x8A150011 versus 0x8A150014 observed on v1.10 — so both are accepted and the
/// integration probe records the live value in the compatibility matrix.
/// </summary>
public static class WingetExitCodes
{
    public const int LegacyNoMatch = unchecked((int)0x8A150011);
    public const int NoMatch = unchecked((int)0x8A150014);
    // APPINSTALLER_CLI_ERROR_MULTIPLE_INSTALLED_PACKAGES_MATCHED. winget list shows several
    // installed instances for one Id (e.g. .NET SDK feature bands or multiple MSIX versions);
    // uninstall must then pick a version with --version or use --all-versions.
    public const int MultipleInstalledPackagesMatched = unchecked((int)0x8A150016);
    // APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE. This can happen when a cached
    // package snapshot is ahead of the package that Winget can actually upgrade.
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    // APPINSTALLER_CLI_ERROR_INSTALLER_FAILED. The installer-specific exit code remains in
    // winget output; Windows Installer 1602 means the elevation/installation was cancelled.
    public const int InstallerFailed = unchecked((int)0x8A15010C);

    /// <summary>True when the exit code means "the query produced no matching packages" rather than failure.</summary>
    public static bool IsNoMatch(int exitCode) => exitCode == LegacyNoMatch || exitCode == NoMatch;

    /// <summary>True when the uninstall query matched several installed instances of one package.</summary>
    public static bool IsMultipleInstalled(int exitCode) => exitCode == MultipleInstalledPackagesMatched;

    /// <summary>True when Winget found the package but cannot apply an update to it.</summary>
    public static bool IsUpdateNotApplicable(int exitCode) => exitCode == UpdateNotApplicable;

    /// <summary>True only for the installer cancellation case, not every installer failure.
    /// This must not be retried: it would immediately present a second UAC prompt after the user
    /// has just declined the first one.</summary>
    public static bool IsInstallerCancelled(int exitCode, string output) =>
        exitCode == InstallerFailed &&
        (output.Contains("1602", StringComparison.Ordinal) ||
         output.Contains("取消安装", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("cancelled installation", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("canceled installation", StringComparison.OrdinalIgnoreCase));

    /// <summary>Formats a (possibly negative) Win32 exit code as an unsigned hex value, e.g. 0x8A150014.</summary>
    public static string Format(int exitCode) => $"0x{exitCode & 0xFFFFFFFFL:X8}";
}

/// <summary>
/// Structured failure from a winget process. Derives from <see cref="InvalidOperationException"/> so
/// the existing CLI/Desktop catch chains keep working with zero changes while callers can read the
/// exit code and the command that failed.
/// </summary>
public sealed class WingetException : InvalidOperationException
{
    public WingetException(string commandName, int exitCode, string outputDetail)
        : base(WingetText.Format("Package_WingetFailed", commandName, WingetExitCodes.Format(exitCode), outputDetail))
    {
        CommandName = commandName;
        ExitCode = exitCode;
        OutputDetail = outputDetail;
    }

    public string CommandName { get; }

    public int ExitCode { get; }

    public string OutputDetail { get; }

    /// <summary>Whether the package installer was cancelled while requesting elevation.</summary>
    public bool IsInstallerCancelled => WingetExitCodes.IsInstallerCancelled(ExitCode, OutputDetail);
}
