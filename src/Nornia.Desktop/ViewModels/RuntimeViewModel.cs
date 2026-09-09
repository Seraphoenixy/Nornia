using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoreRuntime = Nornia.Core.Models.Runtime;
using Nornia.Core.Interfaces;
using Nornia.Core.Collections;
using Nornia.Core.Models;
using Nornia.Core.Services;
using Nornia.Desktop.Services;
using Nornia.Package.Providers;
using Nornia.Package.Services;
using Nornia.Runtime.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace Nornia.Desktop.ViewModels;

public partial class RuntimeViewModel(
    IRuntimeInventoryService inventoryService,
    IPackageProvider packageProvider,
    IPackageInventoryService packageInventoryService,
    IRuntimePackageResolver packageResolver,
    IConfirmationService confirmationService,
    IUiLogService logService,
    IClipboardService? clipboard = null,
    IUiPerformanceMetrics? performanceMetrics = null,
    IInventoryScanStateRepository? scanStateRepository = null) : PageViewModel("运行库", logService), INavigationTarget
{
    private readonly IClipboardService _clipboard = clipboard ?? NullClipboardService.Instance;

    public BulkObservableCollection<ManagedComponentItem> Runtimes { get; } = [];
    public ObservableCollection<ManagedComponentItem> SelectedRuntimes { get; } = [];
    public ICollectionView FilteredRuntimes => CollectionViewSource.GetDefaultView(Runtimes);

    /// <summary>页头新鲜度提示:快照何时扫描,由 scan_state 提供(空表示不可用/未注册仓库)。</summary>
    [ObservableProperty] private string lastScanDisplay = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    private ManagedComponentItem? selectedRuntime;

    [ObservableProperty] private string filterText = string.Empty;
    public string SelectedRuntimeSummary => SelectedRuntime is null ? "选择 Runtime 查看版本、路径和可用操作。" : $"{SelectedRuntime.Name} {SelectedRuntime.Version} · {SelectedRuntime.Architecture} · {SelectedRuntime.InstallPath}";

    protected override async Task OnFirstActivatedAsync()
    {
        SelectedRuntimes.CollectionChanged += (_, _) =>
        {
            RemoveCommand.NotifyCanExecuteChanged();
            UpgradeCommand.NotifyCanExecuteChanged();
            NotifyCopyCommands();
        };
        FilteredRuntimes.Filter = MatchesFilter;
        // 快照优先:先用持久化清单立即渲染首屏(扫描可能长达数秒),再走 TTL 门控刷新;
        // 快照足够新且环境指纹未变时,门控刷新直接返回库内数据,不派生任何进程。
        await SeedPersistedAsync();
        await LoadFromInventoryAsync();
    }

    /// <summary>从持久化快照立即填充列表,失败静默(门控刷新会落回全扫兜底)。</summary>
    private async Task SeedPersistedAsync()
    {
        try
        {
            var runtimes = await inventoryService.GetPersistedAsync();
            var packages = await packageInventoryService.GetPersistedAsync();
            PublishRuntimes(runtimes, packages);
            LastScanDisplay = await InventoryScanAgeText.LoadAsync(
                scanStateRepository, InventoryScanAgeText.RuntimeScanKind);
        }
        catch
        {
            // 持久化暂不可用(如表未建好):交给下面的门控刷新处理。
        }
    }

    // ===== Copy commands (grid 复制选中 / 复制全部) =====

    private void NotifyCopyCommands()
    {
        CopySelectedRuntimesCommand.NotifyCanExecuteChanged();
        CopyAllRuntimesCommand.NotifyCanExecuteChanged();
    }

    private static string FormatRuntime(ManagedComponentItem runtime) =>
        $"{runtime.Name}\t{runtime.Version}\t{runtime.AvailableVersion}\t{runtime.Architecture}\t{runtime.InstallPath}";

    [RelayCommand(CanExecute = nameof(CanCopySelectedRuntimes))]
    private void CopySelectedRuntimes() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedRuntimes.Select(FormatRuntime)));

    private bool CanCopySelectedRuntimes() => SelectedRuntimes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllRuntimes))]
    private void CopyAllRuntimes() =>
        _clipboard.SetText(string.Join(Environment.NewLine, Runtimes.Select(FormatRuntime)));

    private bool CanCopyAllRuntimes() => Runtimes.Count > 0;

    /// <summary>首激活的门控加载:快照新鲜时直接返回库内数据(零进程派生),否则全量扫描。</summary>
    private Task LoadFromInventoryAsync() => RunAsync("读取运行库", async cancellationToken =>
    {
        SetPageLoading();
        try
        {
            await ReloadRuntimesAsync(forceRescan: false, cancellationToken);
            PublishListState();
        }
        catch (Exception ex)
        {
            SetPageError($"读取失败：{ex.Message}");
            throw;
        }
    }, "确认相关命令已加入 PATH，或查看 Problems。", canCancel: true);

    /// <summary>用户显式发起的扫描:永远强制重扫并更新持久化快照与 scan_state。</summary>
    [RelayCommand]
    private Task ScanAsync() => RunAsync("扫描 Runtime", async cancellationToken =>
    {
        SetPageLoading();
        try
        {
            await ReloadRuntimesAsync(forceRescan: true, cancellationToken);
            PublishListState();
        }
        catch (Exception ex)
        {
            SetPageError($"扫描失败：{ex.Message}");
            throw;
        }
    }, "确认相关命令已加入 PATH，或查看 Problems。", canCancel: true);

    private void PublishListState()
    {
        if (Runtimes.Count == 0)
        {
            SetPageEmpty("没有已扫描到的 Runtime。点击「重新扫描」开始。");
        }
        else
        {
            SetPageReady();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private Task RemoveAsync() => RunAsync("移除 Runtime", async cancellationToken =>
    {
        var runtimes = SelectedRuntimes.Count > 0 ? SelectedRuntimes.ToArray() : SelectedRuntime is not null ? [SelectedRuntime] : [];
        if (runtimes.Length == 0)
        {
            LogService.Write("INFO", "请先选择一个或多个 Runtime。");
            return;
        }

        var selectedRuntimes = runtimes.Where(r => EnvironmentComponentCatalog.Get(r.Name)?.CanManageWithWinget == true).ToArray();
        if (selectedRuntimes.Length == 0)
        {
            LogService.Write("WARNING", "所选 Runtime 中没有可以通过 Winget 管理的项目。");
            return;
        }

        var summary = string.Join(Environment.NewLine, selectedRuntimes.Take(12).Select(r => $"• {r.Name} {r.Version}"));
        if (selectedRuntimes.Length > 12) summary += $"{Environment.NewLine}…以及 {selectedRuntimes.Length - 12} 项";
        if (!confirmationService.Confirm(
                "确认移除 Runtime",
                $"将卸载以下 Runtime：{Environment.NewLine}{Environment.NewLine}{summary}{Environment.NewLine}{Environment.NewLine}此操作可能影响依赖项目，且不保证可自动回滚。选择&quot;否&quot;可安全取消。"))
        {
            LogService.Write("INFO", "用户取消了 Runtime 移除。");
            return;
        }

        var refreshedAfterMutation = false;
        foreach (var runtime in selectedRuntimes)
        {
            foreach (var package in packageResolver.ResolveMany(runtime.Name, runtime.Version))
            {
                try
                {
                    await packageProvider.UninstallAsync(package.PackageId, null, OperationProgress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Preserve the concrete package id in the operation diagnostic. Without
                    // this context a failed multi-package Runtime removal only reports the
                    // generic "移除 Runtime 失败" message.
                    throw new InvalidOperationException(
                        $"卸载 Runtime 包 {package.PackageId} 失败。", exception);
                }

                // Re-scan immediately after each completed winget mutation. This keeps the
                // runtime row and its detail form current during a multi-package removal.
                await ReloadRuntimesAsync(forceRescan: true, cancellationToken);
                refreshedAfterMutation = true;
            }
        }
        if (!refreshedAfterMutation)
        {
            // A resolver may legitimately return no package (for example, an unsupported
            // mapping). Still refresh the form once so it cannot retain a stale snapshot.
            await ReloadRuntimesAsync(forceRescan: true, cancellationToken);
        }
    }, "确认 Runtime 未被占用，再查看 Problems。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanUpgradeSelected))]
    private Task UpgradeAsync() => RunAsync("升级 Runtime", async cancellationToken =>
    {
        var runtimes = SelectedRuntimes.Count > 0 ? SelectedRuntimes.ToArray() : SelectedRuntime is not null ? [SelectedRuntime] : [];
        if (runtimes.Length == 0)
        {
            LogService.Write("INFO", "请先选择一个或多个 Runtime。");
            return;
        }

        var selectedRuntimes = runtimes.Where(r => r.HasUpdate && EnvironmentComponentCatalog.Get(r.Name)?.CanManageWithWinget == true).ToArray();
        if (selectedRuntimes.Length == 0)
        {
            LogService.Write("WARNING", "所选 Runtime 中没有可用更新或无法通过 Winget 管理。");
            return;
        }

        var refreshedAfterMutation = false;
        foreach (var runtime in selectedRuntimes)
        {
            var packages = SelectUpgradePackages(
                runtime.Name,
                runtime.Architecture,
                packageResolver.ResolveMany(runtime.Name, runtime.Version));
            foreach (var package in packages)
            {
                try
                {
                    await packageProvider.UpgradeAsync(package.PackageId, OperationProgress, cancellationToken);
                }
                catch (WingetException exception) when (WingetExitCodes.IsUpdateNotApplicable(exception.ExitCode))
                {
                    // The package snapshot can advertise an update that is no longer
                    // applicable to this architecture/install scope. Do not let (for
                    // example) an up-to-date x86 package block the selected x64 one.
                    LogService.Write("WARNING",
                        $"跳过 {package.PackageId}：WinGet 没有适用的更新（{WingetExitCodes.Format(exception.ExitCode)}）。");
                }

                // Whether winget upgraded the package or reported that the advertised update is
                // no longer applicable, refresh the affected form before the next item.
                await ReloadRuntimesAsync(forceRescan: true, cancellationToken);
                refreshedAfterMutation = true;
            }
        }
        if (!refreshedAfterMutation)
        {
            await ReloadRuntimesAsync(forceRescan: true, cancellationToken);
        }
    }, "查看 Output 中的安装器诊断后重试。", canCancel: true);

    /// <summary>重载运行库列表。forceRescan=true 用于显式扫描与移除/升级等变更后的重载
    /// (两个清单都绕过合并缓存与持久化快照 TTL);false 用于页面首激活的门控加载。</summary>
    private async Task ReloadRuntimesAsync(bool forceRescan, CancellationToken cancellationToken = default)
    {
        var runtimes = forceRescan
            ? await inventoryService.RefreshForcedAsync(cancellationToken)
            : await inventoryService.RefreshAsync(cancellationToken);
        var packages = forceRescan
            ? await packageInventoryService.RefreshForcedAsync(OperationProgress, cancellationToken)
            : await packageInventoryService.RefreshAsync(OperationProgress, cancellationToken);
        PublishRuntimes(runtimes, packages);
        LastScanDisplay = await InventoryScanAgeText.LoadAsync(
            scanStateRepository, InventoryScanAgeText.RuntimeScanKind, cancellationToken);
    }

    private void PublishRuntimes(IReadOnlyList<CoreRuntime> runtimes, IReadOnlyList<PackageInfo> packages)
    {
        var snapshot = runtimes
                     .Where(runtime => EnvironmentComponentCatalog.Get(runtime.Name)?.Category == EnvironmentComponentCategory.Runtime)
                     .OrderBy(runtime => runtime.Name).ThenByDescending(runtime => runtime.Version)
            .Select(runtime => new ManagedComponentItem(runtime, FindAvailableVersion(runtime, packages)))
            .ToArray();
        using var performance = performanceMetrics?.Begin("runtime.list.publish", snapshot.Length, "ui-batch");
        var selectedItems = SelectedRuntimes.ToArray();
        var selectedItem = SelectedRuntime;
        Runtimes.ReplaceRange(snapshot);
        ReselectPublishedItems(snapshot, selectedItems, selectedItem);
        FilteredRuntimes.Refresh();
        NotifyCopyCommands();
    }

    private void ReselectPublishedItems(
        IReadOnlyList<ManagedComponentItem> snapshot,
        IReadOnlyList<ManagedComponentItem> selectedItems,
        ManagedComponentItem? selectedItem)
    {
        var selected = selectedItems
            .Select(item => FindReplacement(snapshot, item))
            .Where(item => item is not null)
            .Cast<ManagedComponentItem>()
            .Distinct()
            .ToArray();
        SelectedRuntimes.Clear();
        foreach (var item in selected)
        {
            SelectedRuntimes.Add(item);
        }

        // Keep the detail form attached to the freshly scanned row when its stable location is
        // unchanged. If the row disappeared, clear it instead of displaying stale data.
        SelectedRuntime = selectedItem is null
            ? null
            : FindReplacement(snapshot, selectedItem);
    }

    private static string GetSelectionKey(ManagedComponentItem item) =>
        $"{item.Name}\u001F{item.Architecture}\u001F{item.InstallPath}";

    private static ManagedComponentItem? FindReplacement(
        IReadOnlyList<ManagedComponentItem> snapshot,
        ManagedComponentItem previous)
    {
        var exact = snapshot.FirstOrDefault(item => string.Equals(GetSelectionKey(item), GetSelectionKey(previous), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // Some detectors include the version in InstallPath (notably .NET). Prefer the row that
        // matches the advertised target version before falling back to a unique same-component
        // row; never keep the old object when the scan cannot identify a replacement.
        if (!string.IsNullOrWhiteSpace(previous.AvailableVersion))
        {
            var target = snapshot.FirstOrDefault(item =>
                string.Equals(item.Name, previous.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Architecture, previous.Architecture, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Version, previous.AvailableVersion, StringComparison.OrdinalIgnoreCase));
            if (target is not null) return target;
        }

        var candidates = snapshot.Where(item =>
                string.Equals(item.Name, previous.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Architecture, previous.Architecture, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
    partial void OnFilterTextChanged(string value) => FilteredRuntimes.Refresh();
    partial void OnSelectedRuntimeChanged(ManagedComponentItem? value)
    {
        OnPropertyChanged(nameof(SelectedRuntimeSummary));
        OnPropertyChanged(nameof(RemoveButtonTooltip));
        OnPropertyChanged(nameof(UpgradeButtonTooltip));
        RemoveCommand.NotifyCanExecuteChanged();
        UpgradeCommand.NotifyCanExecuteChanged();
    }

    public string RemoveButtonTooltip
    {
        get
        {
            if (SelectedRuntime is null && SelectedRuntimes.Count == 0) return "请先选择一个 Runtime";
            if (SelectedRuntime is not null && EnvironmentComponentCatalog.Get(SelectedRuntime.Name)?.CanManageWithWinget != true)
                return $"当前 Runtime ({SelectedRuntime.Name}) 不支持 Winget 管理，无法移除";
            return "选择可由 Winget 管理的 Runtime 后可移除；执行前会确认";
        }
    }

    public string UpgradeButtonTooltip
    {
        get
        {
            if (SelectedRuntime is null && SelectedRuntimes.Count == 0) return "请先选择一个 Runtime";
            if (SelectedRuntime?.HasUpdate != true && !SelectedRuntimes.Any(r => r.HasUpdate)) return "所选 Runtime 没有可用更新";
            if (SelectedRuntime is not null && EnvironmentComponentCatalog.Get(SelectedRuntime.Name)?.CanManageWithWinget != true)
                return $"当前 Runtime ({SelectedRuntime.Name}) 不支持 Winget 管理，无法升级";
            return "选择存在可用更新的 Runtime 后可升级";
        }
    }

    public void ApplyNavigationContext(NavigationContext? context)
    {
        if (context is NavigationContext.Scan) _ = ScanAsync();
    }

    private bool MatchesFilter(object item) => item is ManagedComponentItem runtime
        && (string.IsNullOrWhiteSpace(FilterText) || runtime.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) || runtime.Version.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    private bool CanRemoveSelected() =>
        (SelectedRuntime is not null || SelectedRuntimes.Count > 0)
        && (SelectedRuntime is not null ? EnvironmentComponentCatalog.Get(SelectedRuntime.Name)?.CanManageWithWinget == true : SelectedRuntimes.Any(r => EnvironmentComponentCatalog.Get(r.Name)?.CanManageWithWinget == true));

    private bool CanUpgradeSelected() =>
        (SelectedRuntime?.HasUpdate == true || SelectedRuntimes.Any(r => r.HasUpdate))
        && (SelectedRuntime is not null ? EnvironmentComponentCatalog.Get(SelectedRuntime.Name)?.CanManageWithWinget == true : SelectedRuntimes.Any(r => r.HasUpdate && EnvironmentComponentCatalog.Get(r.Name)?.CanManageWithWinget == true));

    private string? FindAvailableVersion(CoreRuntime component, IReadOnlyList<PackageInfo> packages)
    {
        try
        {
            var resolved = packageResolver.ResolveMany(component.Name, component.Version);
            return SelectAvailableVersion(component, packages, resolved);
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Limits Visual C++ upgrades to the architecture represented by the selected runtime.
    /// The mapping intentionally includes x86 for repair/install scenarios, but an upgrade of one
    /// runtime row must not attempt the other architecture first.</summary>
    internal static IReadOnlyList<RuntimePackage> SelectUpgradePackages(
        string runtimeName,
        string architecture,
        IReadOnlyList<RuntimePackage> packages)
    {
        if (!IsVisualCppRuntime(runtimeName)) return packages;

        var suffix = $".{architecture.Trim().ToLowerInvariant()}";
        var matching = packages
            .Where(package => package.PackageId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matching.Length > 0 ? matching : packages;
    }

    /// <summary>Matches the available version to the same architecture as a Visual C++ runtime
    /// row. Other runtimes retain the existing package-id matching behavior.</summary>
    internal static string? SelectAvailableVersion(
        CoreRuntime component,
        IReadOnlyList<PackageInfo> packages,
        IReadOnlyList<RuntimePackage> resolvedPackages)
    {
        var ids = resolvedPackages.Select(package => package.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = packages.Where(package => ids.Contains(package.Id));
        if (IsVisualCppRuntime(component.Name))
        {
            return matches.FirstOrDefault(package =>
                string.Equals(package.Architecture, component.Architecture, StringComparison.OrdinalIgnoreCase))?.AvailableVersion;
        }

        return matches.FirstOrDefault()?.AvailableVersion;
    }

    private static bool IsVisualCppRuntime(string runtimeName) =>
        string.Equals(EnvironmentComponentCatalog.Get(runtimeName)?.Id,
            "visual-cpp-redistributable", StringComparison.OrdinalIgnoreCase);
}

public sealed record ManagedComponentItem(CoreRuntime Component, string? AvailableVersion)
{
    public string Name => Component.Name;
    public string Version => Component.Version;
    public string InstallPath => Component.InstallPath;
    public string Architecture => Component.Architecture;
    public RuntimeStatus Status => Component.Status;
    public bool HasUpdate => !string.IsNullOrWhiteSpace(AvailableVersion);
}
