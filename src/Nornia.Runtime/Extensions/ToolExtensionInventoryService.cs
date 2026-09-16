using Nornia.Core;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;

namespace Nornia.Runtime.Extensions;

/// <summary>开发工具扩展依赖清单服务:组件 → 生态映射,已装 + 可用更新合并,按生态做
/// 内存 30 秒冷却缓存(与 <see cref="NorniaSettings.InventoryScanCacheSeconds"/> 一致)与
/// single-flight 合并并发扫描。变更操作(pip/npm/dotnet tool 的安装/卸载/升级)后调用方
/// 必须以 force=true 刷新,避免冷却缓存掩盖变更结果。</summary>
public sealed class ToolExtensionInventoryService(IEnumerable<IToolExtensionProvider> providers) : IToolExtensionInventoryService
{
    private readonly IReadOnlyDictionary<ToolExtensionEcosystem, IToolExtensionProvider> _providers =
        providers.ToDictionary(provider => provider.Ecosystem);

    private readonly object _gate = new();
    private readonly Dictionary<ToolExtensionEcosystem, EcosystemCache> _cache = new();

    public ToolExtensionEcosystem? GetEcosystemForComponent(string componentId) =>
        ToolEcosystemMap.TryGet(componentId, out var ecosystem) ? ecosystem : null;

    public IToolExtensionProvider GetProvider(ToolExtensionEcosystem ecosystem) =>
        _providers.TryGetValue(ecosystem, out var provider)
            ? provider
            : throw new InvalidOperationException($"不支持的扩展依赖生态 '{ecosystem}'。");

    public Task<IReadOnlyList<ToolExtension>> RefreshAsync(
        ToolExtensionEcosystem ecosystem,
        bool force,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(ecosystem);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (!_cache.TryGetValue(ecosystem, out var entry))
            {
                entry = new EcosystemCache();
                _cache[ecosystem] = entry;
            }

            if (entry.InFlight is not null)
            {
                // 变更后的强制刷新不得复用变更前开始的非强制扫描。
                if (force && !entry.InFlightIsForced)
                {
                    var previous = entry.InFlight;
                    var chained = ScanForcedAfterAsync(provider, previous, progress, cancellationToken);
                    Track(ecosystem, entry, chained, isForced: true);
                    return chained.WaitAsync(cancellationToken);
                }

                return entry.InFlight.WaitAsync(cancellationToken);
            }

            if (!force && entry.LastScan is not null && now - entry.ScannedAt < NorniaSettings.InventoryScanCacheSeconds)
            {
                return Task.FromResult(entry.LastScan);
            }

            var scan = ScanAsync(provider, progress, cancellationToken);
            Track(ecosystem, entry, scan, force);
            return scan.WaitAsync(cancellationToken);
        }
    }

    private void Track(ToolExtensionEcosystem ecosystem, EcosystemCache entry, Task<IReadOnlyList<ToolExtension>> scan, bool isForced)
    {
        entry.InFlight = scan;
        entry.InFlightIsForced = isForced;
        _ = scan.ContinueWith(completed =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(entry.InFlight, completed))
                {
                    entry.InFlight = null;
                    entry.InFlightIsForced = false;
                    if (completed.IsCompletedSuccessfully)
                    {
                        entry.LastScan = completed.Result;
                        entry.ScannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    }
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<IReadOnlyList<ToolExtension>> ScanForcedAfterAsync(
        IToolExtensionProvider provider,
        Task<IReadOnlyList<ToolExtension>> previousScan,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await previousScan.ConfigureAwait(false);
        }
        catch
        {
            // 强制刷新是变更后的权威尝试:先前共享扫描失败时,由本次决定成败。
        }

        return await ScanAsync(provider, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ToolExtension>> ScanAsync(
        IToolExtensionProvider provider,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        var installed = await provider.ListInstalledAsync(progress, cancellationToken);
        var outdated = await provider.ListOutdatedAsync(progress, cancellationToken);
        return Merge(provider.Ecosystem, installed, outdated);
    }

    internal static IReadOnlyList<ToolExtension> Merge(
        ToolExtensionEcosystem ecosystem,
        IReadOnlyList<ToolExtension> installed,
        IReadOnlyList<ToolExtension> outdated)
    {
        var latestBy = outdated
            .Where(item => !string.IsNullOrWhiteSpace(item.AvailableVersion))
            .ToDictionary(item => item.Name, item => item.AvailableVersion!, StringComparer.OrdinalIgnoreCase);
        return installed
            .Select(item => latestBy.TryGetValue(item.Name, out var latest) && !string.Equals(latest, item.Version, StringComparison.OrdinalIgnoreCase)
                ? item with { AvailableVersion = latest }
                : item with { AvailableVersion = null })
            .ToArray();
    }

    private sealed class EcosystemCache
    {
        public IReadOnlyList<ToolExtension>? LastScan;
        public long ScannedAt;
        public Task<IReadOnlyList<ToolExtension>>? InFlight;
        public bool InFlightIsForced;
    }
}
