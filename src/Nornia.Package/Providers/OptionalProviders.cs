using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Localization;

namespace Nornia.Package.Providers;

/// <summary>Scoop provider. Its line-oriented output is deliberately parsed conservatively.</summary>
public sealed class ScoopProvider(IProcessRunner processRunner) : IPackageProvider, IPackageProviderAvailability
{
    public string Name => "scoop";
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => (await processRunner.RunAsync("where.exe", ["scoop"], cancellationToken: cancellationToken)).IsSuccess;
    public async Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Parse(await RunAsync(["search", query], "search", progress, cancellationToken), false);
    public async Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Parse(await RunAsync(["list"], "list installed packages", progress, cancellationToken), true);
    public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => RunAsync(version is null ? ["install", packageId] : ["install", $"{packageId}@{version}"], "install", progress, cancellationToken);
    public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => RunAsync(["uninstall", packageId], "uninstall", progress, cancellationToken);
    public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => RunAsync(["update", packageId], "upgrade", progress, cancellationToken);
    private async Task<string> RunAsync(IReadOnlyList<string> args, string operation, IProgress<ProcessOutput>? progress, CancellationToken token) { var r = await processRunner.RunAsync("scoop", args, progress, token); if (!r.IsSuccess) throw new InvalidOperationException(WingetText.Format("Provider_ScoopFailed", operation, r.ExitCode, (string.IsNullOrWhiteSpace(r.StandardError) ? r.StandardOutput : r.StandardError).Trim())); return r.StandardOutput; }
    private static IReadOnlyList<PackageInfo> Parse(string text, bool installed) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Where(parts => parts.Length >= 2 && !parts[0].Equals("Name", StringComparison.OrdinalIgnoreCase)).Select(parts => new PackageInfo(parts[0], parts[0], parts[1], null, "scoop", installed, "Unknown")).ToArray();
}

/// <summary>Chocolatey provider using its stable machine-readable --limit-output format.</summary>
public sealed class ChocolateyProvider(IProcessRunner processRunner) : IPackageProvider, IPackageProviderAvailability
{
    public string Name => "choco";
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => (await processRunner.RunAsync("where.exe", ["choco"], cancellationToken: cancellationToken)).IsSuccess;
    public async Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Parse(await RunAsync(["search", query, "--limit-output", "--no-progress"], "search", progress, cancellationToken), false);
    public async Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Parse(await RunAsync(["list", "--local-only", "--limit-output", "--no-progress"], "list installed packages", progress, cancellationToken), true);
    public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => RunAsync(version is null ? ["install", packageId, "-y", "--no-progress"] : ["install", packageId, "--version", version, "-y", "--no-progress"], "install", progress, cancellationToken);
    public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => RunAsync(["uninstall", packageId, "-y", "--no-progress"], "uninstall", progress, cancellationToken);
    public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => RunAsync(["upgrade", packageId, "-y", "--no-progress"], "upgrade", progress, cancellationToken);
    private async Task<string> RunAsync(IReadOnlyList<string> args, string operation, IProgress<ProcessOutput>? progress, CancellationToken token) { var r = await processRunner.RunAsync("choco", args, progress, token); if (!r.IsSuccess) throw new InvalidOperationException(WingetText.Format("Provider_ChocolateyFailed", operation, r.ExitCode, (string.IsNullOrWhiteSpace(r.StandardError) ? r.StandardOutput : r.StandardError).Trim())); return r.StandardOutput; }
    private static IReadOnlyList<PackageInfo> Parse(string text, bool installed) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('|', 2)).Where(parts => parts.Length == 2).Select(parts => new PackageInfo(parts[0], parts[0], parts[1], null, "choco", installed, "Unknown")).ToArray();
}
