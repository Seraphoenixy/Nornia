namespace Nornia.Runtime.Services;

/// <summary>Process-spawn-free executable resolution used by both runtime detection (as the fallback
/// after <c>where.exe</c>) and the environment fingerprint provider. Shared so both paths resolve
/// candidates identically, which keeps the fingerprint stable across scans.</summary>
internal static class WindowsPathLocator
{
    /// <summary>Scans the PATH environment variable for an executable. Invalid entries are skipped.</summary>
    public static string? ResolveFromPath(string executable)
    {
        var executableName = Path.HasExtension(executable) ? executable : $"{executable}.exe";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore invalid PATH entries and continue discovery.
            }
        }

        return null;
    }

    /// <summary>Falls back to the App Paths registry keys (HKCU first, then HKLM).</summary>
    public static string? ResolveWindowsAppPath(string executable)
    {
        var executableName = Path.HasExtension(executable) ? executable : $"{executable}.exe";
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($"{appPaths}\\{executableName}");
            if (key?.GetValue(null) is string path && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
