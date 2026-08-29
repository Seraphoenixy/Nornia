using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Interfaces;
using Nornia.Core.Collections;
using Nornia.Core.Models;
using Nornia.Desktop.Services;
using Nornia.Package.Services;
using System.ComponentModel;
using System.Windows.Data;

namespace Nornia.Desktop.ViewModels;

/// <summary>Which package list the package workspace is showing. Mirrors UniGetUI's split between
/// installed / updatable / discoverable views so the toolbar and grid semantics are explicit.</summary>
public enum PackageListMode
{
    Installed,
    Updates,
    Search
}

public partial class PackagesViewModel(
    IPackageProvider packageProvider,
    IPackageInventoryService inventoryService,
    IConfirmationService confirmationService,
    IUiLogService logService,
    CacheViewModel cache,
    IClipboardService? clipboard = null,
    IUiPerformanceMetrics? performanceMetrics = null,
    IInventoryScanStateRepository? scanStateRepository = null)
    : PageViewModel("软件包", logService), INavigationTarget
{
    private readonly IClipboardService _clipboard = clipboard ?? NullClipboardService.Instance;
    /// <summary>Installed packages loaded from the local inventory (also the base for the Updates view).</summary>
    public BulkObservableCollection<PackageInfo> Packages { get; } = [];

    /// <summary>Packages returned by the most recent winget search.</summary>
    public BulkObservableCollection<PackageInfo> SearchResults { get; } = [];

    public ObservableCollection<PackageInfo> SelectedPackages { get; } = [];
    public ICollectionView FilteredPackages => CollectionViewSource.GetDefaultView(Packages);
    public ICollectionView SearchView => CollectionViewSource.GetDefaultView(SearchResults);

    /// <summary>Cache workspace exposed to the packages page's "缓存" tab. Cache management is part of
    /// the package management page rather than a standalone module, sharing the page lifecycle so a
    /// single navigation entry drives both the package list and its cache review.</summary>
    public CacheViewModel Cache { get; } = cache;

    [ObservableProperty]
    private int selectedWorkspaceTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    private PackageInfo? selectedPackage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string searchQuery = string.Empty;

    [ObservableProperty] private string filterText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeAllCommand))]
    private PackageListMode listMode;

    public string SelectedPackageSummary => SelectedPackage is null
        ? "选择软件包以查看可用操作。"
        : $"{SelectedPackage.Name} · {SelectedPackage.Id} · {SelectedPackage.Version} · {SelectedPackage.Provider}";

    [ObservableProperty]
    private string packageListTitle = "已安装的软件";

    [ObservableProperty]
    private string packageListSummary = "正在读取已安装软件…";

    [ObservableProperty]
    private string emptyStateMessage = "没有已安装的软件。";

    [ObservableProperty]
    private bool isInstalledListEmpty;

    [ObservableProperty]
    private bool isSearchListEmpty;

    [ObservableProperty]
    private bool isProviderColumnVisible;

    /// <summary>页头新鲜度提示:包清单快照何时扫描,由 scan_state 提供(空表示不可用/未注册仓库)。</summary>
    [ObservableProperty] private string lastScanDisplay = string.Empty;

    protected override async Task OnFirstActivatedAsync()
    {
        SelectedPackages.CollectionChanged += (_, _) =>
        {
            UninstallCommand.NotifyCanExecuteChanged();
            UpgradeCommand.NotifyCanExecuteChanged();
            NotifyCopyCommands();
        };
        FilteredPackages.Filter = MatchesFilter;
        SearchView.Filter = MatchesSearch;
        // 不再预激活缓存页:首次激活曾触发全盘缓存扫描,启动/进页即静默扫盘;
        // 缓存数据改由“扫描缓存”按钮显式加载(缓存标签页自带空状态引导)。
        // 快照优先:先用持久化包清单立即渲染首屏(winget list 可能长达数十秒),再走
        // TTL 门控刷新;快照足够新时门控刷新直接返回库内数据,不运行 winget。
        await SeedPersistedPackagesAsync();
        await ListInstalledGatedAsync();
    }

    /// <summary>从持久化包清单立即填充列表,失败静默(门控刷新会落回全扫兜底)。</summary>
    private async Task SeedPersistedPackagesAsync()
    {
        try
        {
            ShowInstalledList(await inventoryService.GetPersistedAsync());
            LastScanDisplay = await InventoryScanAgeText.LoadAsync(
                scanStateRepository, InventoryScanAgeText.PackageScanKind);
        }
        catch
        {
            // 持久化暂不可用(如表未建好):交给下面的门控刷新处理。
        }
    }

    /// <summary>首激活的门控加载:快照在 TTL 内时零 winget 进程;过期时全量扫描一次。</summary>
    private Task ListInstalledGatedAsync() => RunAsync("读取已安装软件包", async cancellationToken =>
    {
        ShowInstalledList(await inventoryService.RefreshAsync(OperationProgress, cancellationToken));
        LastScanDisplay = await InventoryScanAgeText.LoadAsync(
            scanStateRepository, InventoryScanAgeText.PackageScanKind, cancellationToken);
    }, "确认包管理器可用后重试。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => RunAsync("搜索软件包", async cancellationToken =>
    {
        var packages = await packageProvider.SearchAsync(SearchQuery, OperationProgress, cancellationToken);
        ShowSearchResults(packages, $"“{SearchQuery}” 的搜索结果");
    }, "检查包管理器是否可用，或更换关键词后重试。", canCancel: true);

    /// <summary>「重新扫描」按钮:永远强制重跑 winget 并更新持久化快照与 scan_state。</summary>
    [RelayCommand]
    private Task ListInstalledAsync() => RunAsync("读取已安装软件包", async cancellationToken =>
    {
        ShowInstalledList(await inventoryService.RefreshForcedAsync(OperationProgress, cancellationToken));
        LastScanDisplay = await InventoryScanAgeText.LoadAsync(
            scanStateRepository, InventoryScanAgeText.PackageScanKind, cancellationToken);
    }, "确认包管理器可用后重试。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private Task InstallSelectedAsync() => InstallPackageAsync(SelectedPackage!.Id, null);

    private Task InstallPackageAsync(string id, string? version) => RunAsync("安装软件包", async cancellationToken =>
    {
        await packageProvider.InstallAsync(id, version, OperationProgress, cancellationToken);
        await LoadInstalledPackagesAsync(cancellationToken);
    }, "查看 Output 中的包管理器诊断后重试。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private Task UninstallAsync() => RunAsync("卸载软件包", async cancellationToken =>
    {
        var selected = SelectedPackages.Where(package => package.IsInstalled).ToArray();
        if (selected.Length == 0 && SelectedPackage is { IsInstalled: true } current) selected = [current];
        if (selected.Length == 0) throw new InvalidOperationException("请先选择一个或多个已安装软件包。");
        var summary = string.Join(Environment.NewLine, selected.Take(12).Select(package => $"• {package.Name} ({package.Id})"));
        if (selected.Length > 12) summary += $"{Environment.NewLine}…以及 {selected.Length - 12} 项";
        if (!confirmationService.Confirm(
                "确认卸载软件包",
                $"将卸载 {selected.Length} 个软件包：{Environment.NewLine}{Environment.NewLine}{summary}{Environment.NewLine}{Environment.NewLine}此操作可能影响依赖项目，且 Nornia 不保证可自动回滚。选择“否”可安全取消。"))
        {
            LogService.Write("INFO", "用户取消了软件包卸载。");
            return;
        }

            foreach (var package in selected) await packageProvider.UninstallAsync(package.Id, package.Name, OperationProgress, cancellationToken);
        await LoadInstalledPackagesAsync(cancellationToken);
    }, "如卸载失败，请确认软件未在运行并查看 Problems。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private Task UpgradeAsync() => RunAsync("升级软件包", async cancellationToken =>
    {
        var selected = SelectedPackages.Where(package => package.IsInstalled && !string.IsNullOrWhiteSpace(package.AvailableVersion)).ToArray();
        if (selected.Length == 0 && SelectedPackage is { IsInstalled: true, AvailableVersion: not null } current) selected = [current];
        if (selected.Length == 0) throw new InvalidOperationException("请选择一个或多个存在更新的软件包。");
        foreach (var package in selected) await packageProvider.UpgradeAsync(package.Id, OperationProgress, cancellationToken);
        await LoadInstalledPackagesAsync(cancellationToken);
    }, "查看 Output 中的包管理器诊断后重试。", canCancel: true);

    /// <summary>Bulk-upgrade every installed package that has an available update (the Updates view's
    /// primary action). Runs directly like a single upgrade; the full log remains in Output.</summary>
    [RelayCommand(CanExecute = nameof(CanUpgradeAll))]
    private Task UpgradeAllAsync() => RunAsync("全部升级软件包", async cancellationToken =>
    {
        var updatable = Packages.Where(package => package.IsInstalled && !string.IsNullOrWhiteSpace(package.AvailableVersion)).ToArray();
        if (updatable.Length == 0) throw new InvalidOperationException("没有可升级的软件包。");
        LogService.Write("INFO", $"开始批量升级 {updatable.Length} 个软件包。");
        foreach (var package in updatable)
        {
            await packageProvider.UpgradeAsync(package.Id, OperationProgress, cancellationToken);
        }

        await LoadInstalledPackagesAsync(cancellationToken);
    }, "查看 Output 中的包管理器诊断后重试。", canCancel: true);

    [RelayCommand]
    private void SetInstalledMode() => ListMode = PackageListMode.Installed;

    [RelayCommand]
    private void SetUpdatesMode() => ListMode = PackageListMode.Updates;

    [RelayCommand]
    private void SetSearchMode() => ListMode = PackageListMode.Search;

    private bool CanSearch() => !string.IsNullOrWhiteSpace(SearchQuery);
    private bool CanInstallSelected() => SelectedPackage is { IsInstalled: false };

    private bool CanUninstall() => SelectedPackage is { IsInstalled: true } || SelectedPackages.Any(package => package.IsInstalled);

    private bool CanUpgrade() =>
        (SelectedPackage is { IsInstalled: true } package && !string.IsNullOrWhiteSpace(package.AvailableVersion))
        || SelectedPackages.Any(package => package.IsInstalled && !string.IsNullOrWhiteSpace(package.AvailableVersion));

    private bool CanUpgradeAll() => Packages.Any(package => package.IsInstalled && !string.IsNullOrWhiteSpace(package.AvailableVersion));

    // ===== Copy commands (grid 复制选中 / 复制全部) =====

    private void NotifyCopyCommands()
    {
        CopySelectedPackagesCommand.NotifyCanExecuteChanged();
        CopyAllPackagesCommand.NotifyCanExecuteChanged();
        CopyAllSearchResultsCommand.NotifyCanExecuteChanged();
    }

    private static string FormatPackage(PackageInfo package) =>
        $"{package.Name}\t{package.Id}\t{package.Version}\t{package.AvailableVersion}\t{package.Provider}";

    [RelayCommand(CanExecute = nameof(CanCopySelectedPackages))]
    private void CopySelectedPackages() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedPackages.Select(FormatPackage)));

    private bool CanCopySelectedPackages() => SelectedPackages.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllPackages))]
    private void CopyAllPackages() =>
        _clipboard.SetText(string.Join(Environment.NewLine, Packages.Select(FormatPackage)));

    private bool CanCopyAllPackages() => Packages.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllSearchResults))]
    private void CopyAllSearchResults() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SearchResults.Select(FormatPackage)));

    private bool CanCopyAllSearchResults() => SearchResults.Count > 0;

    /// <summary>安装/卸载/升级之后的重载:必须强制刷新,绕过合并缓存与持久化快照 TTL,
    /// 否则列表会展示变更前的旧数据。</summary>
    private async Task LoadInstalledPackagesAsync(CancellationToken cancellationToken = default)
    {
        ShowInstalledList(await inventoryService.RefreshForcedAsync(OperationProgress, cancellationToken));
        LastScanDisplay = await InventoryScanAgeText.LoadAsync(
            scanStateRepository, InventoryScanAgeText.PackageScanKind, cancellationToken);
    }

    private void ShowInstalledList(IEnumerable<PackageInfo> packages)
    {
        var snapshot = packages as IReadOnlyList<PackageInfo> ?? packages.ToArray();
        using var performance = performanceMetrics?.Begin("packages.installed.publish", snapshot.Count, "ui-batch");
        Packages.ReplaceRange(snapshot);

        ListMode = PackageListMode.Installed;
        IsProviderColumnVisible = Packages.Select(package => package.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
        NotifyCopyCommands();
        UpdateInstalledListState();
    }

    private void ShowSearchResults(IEnumerable<PackageInfo> packages, string summary)
    {
        var snapshot = packages as IReadOnlyList<PackageInfo> ?? packages.ToArray();
        using var performance = performanceMetrics?.Begin("packages.search.publish", snapshot.Count, "ui-batch");
        SearchResults.ReplaceRange(snapshot);

        ListMode = PackageListMode.Search;
        PackageListTitle = summary;
        NotifyCopyCommands();
        IsSearchListEmpty = SearchResults.Count == 0;
        PackageListSummary = IsSearchListEmpty
            ? "未找到匹配的软件。"
            : $"{summary} · 共 {SearchResults.Count} 项；选择一项即可安装。";
    }

    /// <summary>Refreshes the installed view (re-applying the current mode filter) and recomputes the
    /// empty flag from the filtered view so an "all up to date" Updates view shows its empty state.</summary>
    private void UpdateInstalledListState()
    {
        FilteredPackages.Refresh();
        IsInstalledListEmpty = FilteredPackages.IsEmpty;
        PackageListSummary = ListMode == PackageListMode.Updates
            ? $"共 {Packages.Count(package => !string.IsNullOrWhiteSpace(package.AvailableVersion))} 项可升级"
            : $"共 {Packages.Count} 项";
        UpgradeAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnListModeChanged(PackageListMode value)
    {
        SelectedPackage = null;
        SelectedPackages.Clear();
        FilteredPackages.Filter = MatchesFilter;
        UpdateInstalledListState();
        switch (value)
        {
            case PackageListMode.Updates:
                PackageListTitle = "可升级的软件包";
                EmptyStateMessage = "所有软件包都是最新。";
                break;
            case PackageListMode.Search:
                PackageListTitle = "搜索结果";
                EmptyStateMessage = "未找到匹配的软件。";
                break;
            default:
                PackageListTitle = "已安装的软件";
                EmptyStateMessage = "没有已安装的软件。";
                break;
        }
    }

    partial void OnFilterTextChanged(string value) => UpdateInstalledListState();

    partial void OnSelectedPackageChanged(PackageInfo? value)
    {
        OnPropertyChanged(nameof(SelectedPackageSummary));
        UninstallCommand.NotifyCanExecuteChanged();
        UpgradeCommand.NotifyCanExecuteChanged();
    }

    public void ApplyNavigationContext(NavigationContext? context)
    {
        switch (context)
        {
            case NavigationContext.CacheHighConfidence:
                SelectedWorkspaceTab = 1;
                Cache.ApplyNavigationContext(new NavigationContext.CacheHighConfidence());
                break;
            case NavigationContext.CacheByPackage(var id, var name, var provider):
                SelectedWorkspaceTab = 1;
                Cache.ApplyNavigationContext(new NavigationContext.CacheByPackage(id, name, provider));
                break;
            case NavigationContext.Updates:
                SelectedWorkspaceTab = 0;
                ListMode = PackageListMode.Updates;
                break;
            default:
                SelectedWorkspaceTab = 0;
                ListMode = PackageListMode.Installed;
                break;
        }
    }

    private bool MatchesFilter(object item) => item is PackageInfo package
        && (ListMode != PackageListMode.Updates || !string.IsNullOrWhiteSpace(package.AvailableVersion))
        && (string.IsNullOrWhiteSpace(FilterText)
            || package.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || package.Id.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    private bool MatchesSearch(object item) => item is PackageInfo package
        && (string.IsNullOrWhiteSpace(FilterText)
            || package.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || package.Id.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
}
