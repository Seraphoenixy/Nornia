using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Localization;
using Nornia.Package.Parsing;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Nornia.Package.Providers;

/// <summary>
/// Winget integration with protocol-aware parsing. Tabular text is parsed through the bilingual
/// display-width table parser; structured JSON is selected automatically when the live CLI supports
/// it. "No match" exit codes yield empty results instead of failures, read-only queries use short
/// timeouts without retry (unlike mutations), and truncated display names are backfilled with
/// bounded concurrency and per-Id deduplication.
/// </summary>
public sealed partial class WingetProvider(
    IProcessRunner processRunner,
    WingetProbe? probe = null,
    IInteractiveProcessRunner? interactiveProcessRunner = null) : IPackageProvider, IPackageProviderAvailability
{
    private const int BackfillMaxConcurrency = 4;
    private const int BackfillCacheMaxEntries = 512;
    private static readonly TimeSpan ReadOnlyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackfillTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(10);

    private readonly WingetProbe _probe = probe ?? new WingetProbe(processRunner);

    /// <summary>Resolved full names per Id (successful backfills only), so repeated list refreshes
    /// in the same process skip <c>winget show</c>. Bounded; evicted wholesale when oversized.</summary>
    private readonly ConcurrentDictionary<string, string> _backfillCache = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "winget";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        // `where.exe` does not resolve Windows App Execution Aliases. A functional winget
        // installation can therefore be reported as missing, while a stale alias can be
        // reported as present. Starting the harmless version command is the capability check
        // that matches the operation path used by every package command.
        try
        {
            return (await processRunner.RunAsync("winget.exe", ["--version"], cancellationToken: cancellationToken)).IsSuccess;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        // Start the capability probe concurrently with the winget call so the probe's process
        // startup cost is hidden behind the command itself.
        var probing = _probe.GetCapabilitiesAsync(cancellationToken);
        var result = await RunReadOnlyAsync(
            ["search", "--query", query, "--source", "winget", "--accept-source-agreements", "--disable-interactivity"],
            "search", progress, cancellationToken);
        return await ParseReadOnlyResultAsync(result, "search", isInstalled: false, cancellationToken, probing);
    }

    public async Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var probing = _probe.GetCapabilitiesAsync(cancellationToken);
        var result = await RunReadOnlyAsync(
            ["list", "--accept-source-agreements", "--disable-interactivity"],
            "list installed packages", progress, cancellationToken);
        var packages = await ParseReadOnlyResultAsync(result, "list", isInstalled: true, cancellationToken, probing);
        return await EnrichTruncatedNamesAsync(packages, progress, cancellationToken);
    }

    public Task InstallAsync(
        string packageId,
        string? version = null,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var arguments = new List<string>
        {
            "install", "--id", packageId, "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        };
        if (!string.IsNullOrWhiteSpace(version))
        {
            arguments.Add("--version");
            arguments.Add(version);
        }

        return ExecuteMutationAsync(arguments, "install", progress, cancellationToken);
    }

    public async Task UninstallAsync(
        string packageId,
        string? packageName = null,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        try
        {
            await ExecuteMutationAsync(
                ["uninstall", "--id", packageId, "--exact", "--accept-source-agreements", "--disable-interactivity"],
                "uninstall", progress, cancellationToken);
        }
        catch (WingetException exception) when (WingetExitCodes.IsMultipleInstalled(exception.ExitCode))
        {
            // 0x8A150016: 同一包在系统上安装了多个版本实例(如 .NET SDK 的多个 feature band)。
            // 不带 --version 的卸载无法定位目标;查询实际已安装的版本,逐个精确卸载,
            // 而不是用 --all-versions 一刀切(那会卸载该包的所有版本)。
            progress?.Report(new ProcessOutput(WingetText.Get("Winget_UninstallMultipleFallback"), false));
            if (!await TryUninstallByInstalledVersionsAsync(packageId, progress, cancellationToken))
            {
                throw;
            }
        }
        catch (WingetException exception) when (packageName is not null && WingetExitCodes.IsNoMatch(exception.ExitCode))
        {
            // 非 winget 安装的本机(ARP)条目在 winget 目录里没有对应 Id,按 Id 精确匹配得到
            // 0x8A150014;winget list 里这类条目的 Id 就是显示名,改用 --name 精确匹配本机
            // 安装项即可卸载,仍由 winget 执行(调用系统的卸载程序)。回退是预期内的正常路径,
            // 进度行走 stdout(IsError=false):按降噪策略不入日志,终态由 RunAsync 汇总行承载。
            progress?.Report(new ProcessOutput(WingetText.Get("Winget_UninstallByNameFallback"), false));
            await ExecuteMutationAsync(
                ["uninstall", "--name", packageName, "--exact", "--accept-source-agreements", "--disable-interactivity"],
                "uninstall", progress, cancellationToken);
        }
    }

    public Task UpgradeAsync(
        string packageId,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return ExecuteMutationAsync(
            ["upgrade", "--id", packageId, "--exact", "--source", "winget",
             "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            "upgrade", progress, cancellationToken);
    }

    private async Task ExecuteMutationAsync(
        IReadOnlyList<string> arguments,
        string operation,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        var result = await RunWithTimeoutAndRetryAsync(arguments, progress, cancellationToken);
        ThrowIfFailed(result, operation);
    }

    /// <summary>Uninstalls every installed version instance of a package by querying the live
    /// installed list and running <c>winget uninstall --id &lt;id&gt; --version &lt;version&gt; --exact</c>
    /// per instance. Returns false when the version query itself fails, so the caller can rethrow
    /// the original 0x8A150016 error instead of masking it.</summary>
    private async Task<bool> TryUninstallByInstalledVersionsAsync(
        string packageId,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> versions;
        try
        {
            versions = await ListInstalledVersionsAsync(packageId, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or WingetException)
        {
            return false;
        }

        if (versions.Count == 0)
        {
            return false;
        }

        foreach (var version in versions)
        {
            progress?.Report(new ProcessOutput(WingetText.Format("Winget_UninstallByVersion", packageId, version), false));
            try
            {
                await ExecuteMutationAsync(
                    ["uninstall", "--id", packageId, "--exact", "--version", version,
                     "--accept-source-agreements", "--disable-interactivity"],
                    "uninstall", progress, cancellationToken);
            }
            catch (WingetException versionException) when (WingetExitCodes.IsNoMatch(versionException.ExitCode))
            {
                // 该版本在逐个卸载时已不存在(被并发移除或先导命令已处理),继续卸载其余版本。
            }
        }

        return true;
    }

    /// <summary>Queries every installed version of one package via <c>winget list --id &lt;id&gt; --exact</c>.
    /// Uses the same parser as list refresh so bilingual table/JSON layouts are handled identically;
    /// rows are NOT deduplicated here because multiple version instances are the whole point.</summary>
    private async Task<IReadOnlyList<string>> ListInstalledVersionsAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        var result = await RunReadOnlyAsync(
            ["list", "--id", packageId, "--exact", "--accept-source-agreements", "--disable-interactivity"],
            "list", null, cancellationToken);
        if (WingetExitCodes.IsNoMatch(result.ExitCode))
        {
            return [];
        }

        ThrowIfFailed(result, "list");

        var capabilities = await _probe.GetCapabilitiesAsync(cancellationToken);
        var parsed = WingetOutputParserFactory.Create(capabilities).Parse(result.StandardOutput, isInstalled: true);
        return parsed.Packages
            .Where(package => string.Equals(package.Id, packageId, StringComparison.OrdinalIgnoreCase))
            .Select(package => package.Version)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Read-only queries use a short timeout and no retry; the caller decides what a
    /// non-zero exit means (no-match is handled by <see cref="ParseReadOnlyResultAsync"/>).</summary>
    private async Task<ProcessResult> RunReadOnlyAsync(
        IReadOnlyList<string> arguments,
        string commandName,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadOnlyTimeout);
        try
        {
            return await processRunner.RunAsync("winget.exe", arguments, progress, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WingetException(commandName, 0, WingetText.Get("Winget_ReadOnlyTimeout"));
        }
    }

    private async Task<ProcessResult> RunWithTimeoutAndRetryAsync(
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MutationTimeout);
            try
            {
                // WinGet suppresses its progress stream when stdout/stderr are redirected. The
                // desktop host supplies a ConPTY-backed runner so WinGet believes it is attached
                // to a terminal and emits the OSC 9;4 frames consumed by the UI. CLI/test hosts
                // may not provide that capability and safely fall back to the regular runner.
                var result = interactiveProcessRunner is null
                    ? await processRunner.RunAsync("winget.exe", arguments, progress, timeout.Token)
                    : await interactiveProcessRunner.RunInteractiveAsync("winget.exe", arguments, progress, timeout.Token);
                // A declined UAC request ultimately appears as installer exit 1602. Retrying it
                // automatically just opens another elevation prompt and makes the operation look
                // stuck; return immediately so ThrowIfFailed can give an actionable explanation.
                if (result.IsSuccess || result.ExitCode == -1 || WingetExitCodes.IsNoMatch(result.ExitCode) ||
                    WingetExitCodes.IsInstallerCancelled(result.ExitCode, result.StandardOutput + Environment.NewLine + result.StandardError) ||
                    attempt == 2)
                {
                    return result;
                }

                progress?.Report(new ProcessOutput(WingetText.Get("Winget_RetryAfterFailure"), true));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == 1)
            {
                progress?.Report(new ProcessOutput(WingetText.Get("Winget_RetryAfterTimeout"), true));
            }
        }
    }

    /// <summary>Validates a read-only result and converts it into packages, treating no-match
    /// exit codes as an empty result (recording them on the probe for the compatibility matrix).</summary>
    private async Task<IReadOnlyList<PackageInfo>> ParseReadOnlyResultAsync(
        ProcessResult result,
        string commandName,
        bool isInstalled,
        CancellationToken cancellationToken,
        Task<WingetCapabilities>? probeTask = null)
    {
        if (WingetExitCodes.IsNoMatch(result.ExitCode))
        {
            _probe.ObserveNoMatchExitCode(result.ExitCode);
            return [];
        }
        ThrowIfFailed(result, commandName);

        var capabilities = probeTask is null
            ? await _probe.GetCapabilitiesAsync(cancellationToken)
            : await probeTask;
        var parser = WingetOutputParserFactory.Create(capabilities);
        var parsed = parser.Parse(result.StandardOutput, isInstalled);
        if (parser.Kind == "json" && parsed.Packages.Count == 0 && LooksLikeTableOutput(result.StandardOutput))
        {
            // The structured schema drifted from the contract or the probe over-reported support:
            // fall back to the tabular parser once instead of silently returning an empty list.
            parsed = new WingetTableParser().Parse(result.StandardOutput, isInstalled);
        }

        _probe.ObserveHeaderLanguage(parsed.HeaderLanguage);
        return Deduplicate(parsed.Packages);
    }

    private static void ThrowIfFailed(ProcessResult result, string commandName)
    {
        if (result.IsSuccess)
        {
            return;
        }

        // winget 把交互式进度(转轴帧/空格填充)也写进 stdout,清洗后再进异常消息;
        // stderr 通常干净,同样过一遍保持一致。
        var raw = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        var detail = WingetOutputText.SanitizeDetail(raw);
        if (result.ExitCode == -1)
        {
            if (detail.Length > 0)
            {
                detail += Environment.NewLine;
            }

            detail += WingetText.Get("Winget_ProcessStartHint");
        }
        if (WingetExitCodes.IsInstallerCancelled(result.ExitCode, detail))
        {
            if (detail.Length > 0)
            {
                detail += Environment.NewLine;
            }

            detail += WingetText.Get("Winget_InstallerCancelledHint");
        }
        if (WingetExitCodes.IsNoMatch(result.ExitCode) && commandName == "uninstall")
        {
            if (detail.Length > 0)
            {
                detail += Environment.NewLine;
            }

            detail += WingetText.Get("Winget_UninstallNoMatchHint");
        }

        throw new WingetException(commandName, result.ExitCode, detail);
    }

    /// <summary>Resolves truncated display names ("…") by querying the full name once per unique Id,
    /// with bounded concurrency. Failures keep the truncated name so listing never blocks on them.</summary>
    private async Task<IReadOnlyList<PackageInfo>> EnrichTruncatedNamesAsync(
        IReadOnlyList<PackageInfo> packages,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        if (packages.All(package => !package.Name.Contains('…') || IsLocalIdentity(package.Id)))
        {
            return packages;
        }

        using var gate = new SemaphoreSlim(BackfillMaxConcurrency);
        var cache = new ConcurrentDictionary<string, Lazy<Task<string?>>>(StringComparer.OrdinalIgnoreCase);
        var enriched = await Task.WhenAll(packages.Select(package =>
            package.Name.Contains('…') && !IsLocalIdentity(package.Id)
                ? BackfillNameAsync(package, cache, gate, progress, cancellationToken)
                : Task.FromResult(package)));
        return enriched;
    }

    private async Task<PackageInfo> BackfillNameAsync(
        PackageInfo package,
        ConcurrentDictionary<string, Lazy<Task<string?>>> cache,
        SemaphoreSlim gate,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var lazy = cache.GetOrAdd(package.Id,
                _ => new Lazy<Task<string?>>(() => TryGetPackageNameAsync(package.Id, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                var fullName = await lazy.Value;
                if (fullName is null)
                {
                    progress?.Report(new ProcessOutput(WingetText.Format("Winget_BackfillFailed", package.Id), true));
                    return package;
                }

                return package with { Name = fullName };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                progress?.Report(new ProcessOutput(WingetText.Format("Winget_BackfillFailed", package.Id), true));
                return package;
            }
            catch (Exception)
            {
                progress?.Report(new ProcessOutput(WingetText.Format("Winget_BackfillFailed", package.Id), true));
                return package;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> TryGetPackageNameAsync(string packageId, CancellationToken cancellationToken)
    {
        if (_backfillCache.TryGetValue(packageId, out var cached))
        {
            return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BackfillTimeout);
        var result = await processRunner.RunAsync(
            "winget.exe",
            ["show", "--id", packageId, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"],
            cancellationToken: timeout.Token);
        if (!result.IsSuccess)
        {
            return null;
        }

        var match = PackageFoundRegex().Match(result.StandardOutput);
        if (!match.Success)
        {
            return null;
        }

        var fullName = match.Groups["name"].Value.Trim();
        _backfillCache[packageId] = fullName;
        if (_backfillCache.Count > BackfillCacheMaxEntries)
        {
            _backfillCache.Clear();
        }

        return fullName;
    }

    private static IReadOnlyList<PackageInfo> Deduplicate(IEnumerable<PackageInfo> packages) => packages
        .GroupBy(package => $"{package.Id}\u001F{package.Provider}\u001F{package.Architecture}", StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(package => package.AvailableVersion is not null).ThenByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase).First())
        .ToArray();

    private static bool IsLocalIdentity(string packageId) =>
        packageId.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase)
        || packageId.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeTableOutput(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Any(static line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length >= 3 && trimmed.All(static character => character is '-' or ' ');
        });

    [GeneratedRegex("(?:Found|已找到)\\s+(?<name>.+?)\\s+\\[[^\\]]+\\]", RegexOptions.Multiline)]
    private static partial Regex PackageFoundRegex();
}
