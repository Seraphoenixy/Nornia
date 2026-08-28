using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.Services;
using Nornia.Package.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;

namespace Nornia.Desktop.ViewModels;

public partial class CacheViewModel : PageViewModel, INavigationTarget
{
    private readonly ICacheInventoryService _inventoryService;
    private readonly ICacheCleanupService _cleanupService;
    private readonly IConfirmationService _confirmationService;
    private readonly IPackageRepository _packageRepository;
    private readonly CachePackageAssociationService _associationService;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClipboardService _clipboard;

    public ObservableCollection<CacheCandidateItem> Candidates { get; } = [];
    public ObservableCollection<CachePackageSummaryItem> PackageSummaries { get; } = [];

    /// <summary>Multi-selection for the package-summary grid and the candidate grid (Ctrl+C / 复制选中).</summary>
    public ObservableCollection<CachePackageSummaryItem> SelectedPackageSummaries { get; } = [];
    public ObservableCollection<CacheCandidateItem> SelectedCandidates { get; } = [];
    public ObservableCollection<string> SourceFilters { get; } = ["全部"];
    public ObservableCollection<string> TypeFilters { get; } = ["全部"];
    public ICollectionView FilteredCandidates { get; }
    public IReadOnlyList<string> ConfidenceFilters { get; } = ["全部", "高置信", "需审查"];

    [ObservableProperty]
    private string confidenceFilter = "全部";

    [ObservableProperty]
    private string sourceFilter = "全部";

    [ObservableProperty]
    private string typeFilter = "全部";

    [ObservableProperty]
    private CachePackageSummaryItem? selectedPackageSummary;

    [ObservableProperty]
    private CacheCandidateItem? selectedCandidate;

    public int SelectedCount => Candidates.Count(candidate => candidate.IsSelected);
    public long SelectedSizeBytes => Candidates.Where(candidate => candidate.IsSelected).Sum(candidate => candidate.SizeBytes);
    public string SelectedSummary => $"已选择 {SelectedCount} 项，预计释放 {FormatSize(SelectedSizeBytes)}";
    public long TotalSizeBytes => Candidates.Sum(candidate => candidate.SizeBytes);
    public string TotalSizeSummary => $"共 {Candidates.Count} 项，可审查空间 {FormatSize(TotalSizeBytes)}";

    /// <summary>Whether the candidate list has no rows. Kept as an explicit flag (notified after every
    /// load) so the grid and the empty-state overlay toggle in lockstep instead of relying on a dynamic
    /// <c>Candidates.Count</c> binding whose timing can leave the overlay covering the grid.</summary>
    public bool IsCandidateListEmpty => Candidates.Count == 0;

    [ObservableProperty] private string cleanupResultSummary = string.Empty;

    public CacheViewModel(
        ICacheInventoryService inventoryService,
        ICacheCleanupService cleanupService,
        IPackageRepository packageRepository,
        CachePackageAssociationService associationService,
        IConfirmationService confirmationService,
        IUiLogService logService,
        IUiDispatcher? dispatcher = null,
        IClipboardService? clipboard = null) : base("缓存管理", logService)
    {
        _inventoryService = inventoryService;
        _cleanupService = cleanupService;
        _packageRepository = packageRepository;
        _associationService = associationService;
        _confirmationService = confirmationService;
        _dispatcher = dispatcher ?? new WpfUiDispatcher();
        _clipboard = clipboard ?? NullClipboardService.Instance;
        SelectedPackageSummaries.CollectionChanged += (_, _) => NotifyCopyCommands();
        SelectedCandidates.CollectionChanged += (_, _) => NotifyCopyCommands();
        FilteredCandidates = CollectionViewSource.GetDefaultView(Candidates);
        FilteredCandidates.Filter = MatchesFilter;
        // Keep the scan command's can-execute in sync with the busy state so the button shows a
        // disabled state while a scan is already running instead of silently ignoring clicks.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) ScanCommand.NotifyCanExecuteChanged();
        };
    }

    // 首次激活不自动扫描缓存:扫描是全盘目录遍历,只在用户点击“扫描缓存”时执行
    // (OnFirstActivatedAsync 保持基类空实现;空状态指引见 CacheView 的 EmptyStateControl)。

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => RunAsync("扫描应用缓存", async cancellationToken =>
        await LoadCandidatesAsync(cancellationToken), "检查筛选条件或查看 Problems 后重试。", canCancel: true);

    private bool CanScan() => !IsBusy;

    private async Task LoadCandidatesAsync(CancellationToken cancellationToken)
    {
        // 过程信息降噪:不再打印“开始加载缓存清单(服务=…)”这类纯诊断行,保留扫描进度与终态汇总。
        var progress = new Progress<string>(message => LogService.Write("INFO", message));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rawCandidates = await _inventoryService.ScanForcedAsync(progress, cancellationToken);
        stopwatch.Stop();
        LogService.Write("INFO", $"缓存扫描完成：发现 {rawCandidates.Count} 个候选，耗时 {stopwatch.Elapsed.TotalSeconds:F1}s");
        var packages = await _packageRepository.GetAllAsync(cancellationToken);
        var associatedCandidates = _associationService.Associate(rawCandidates, packages);
        var summaries = _associationService.Summarize(associatedCandidates);
        LogService.Write("INFO", $"缓存关联汇总：{associatedCandidates.Count} 个候选归属 {summaries.Count} 个软件包分组");

        await RunOnDispatcherAsync(() =>
        {
            foreach (var existing in Candidates) existing.PropertyChanged -= OnCandidatePropertyChanged;
            Candidates.Clear();
            foreach (var candidate in associatedCandidates)
            {
                var item = new CacheCandidateItem(candidate);
                item.PropertyChanged += OnCandidatePropertyChanged;
                Candidates.Add(item);
            }

            PackageSummaries.Clear();
            foreach (var summary in summaries)
            {
                PackageSummaries.Add(new CachePackageSummaryItem(summary));
            }

            SelectedPackageSummary = null;
            RebuildFilter(SourceFilters, SourceFilter, candidate => candidate.Source, value => SourceFilter = value);
            RebuildFilter(TypeFilters, TypeFilter, candidate => candidate.CacheType, value => TypeFilter = value);
            FilteredCandidates.Refresh();
            RefreshSelectionSummary();
            NotifyCopyCommands();
            OnPropertyChanged(nameof(TotalSizeBytes));
            OnPropertyChanged(nameof(TotalSizeSummary));
            OnPropertyChanged(nameof(IsCandidateListEmpty));
        });

        LogService.Write("INFO", $"缓存清单已刷新：{Candidates.Count} 个候选，{PackageSummaries.Count} 个软件包分组，可审查空间 {FormatSize(TotalSizeBytes)}");
        StatusMessage = Candidates.Count == 0 ? "未发现可管理的缓存目录" : $"发现 {Candidates.Count} 个缓存候选，已按 {PackageSummaries.Count} 个软件包归类";
    }

    /// <summary>Runs <paramref name="action"/> on the dispatcher (UI) thread, or inline when already
    /// there (or when no WPF application is running, e.g. under unit tests).</summary>
    private Task RunOnDispatcherAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action);
    }

    private void RebuildFilter(
        ObservableCollection<string> filters,
        string current,
        Func<CacheCandidateItem, string> selector,
        Action<string> setter)
    {
        filters.Clear();
        filters.Add("全部");
        foreach (var value in Candidates.Select(selector).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            filters.Add(value);
        }

        if (!filters.Contains(current)) setter("全部");
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private Task CleanAsync() => RunAsync("清理缓存", async cancellationToken =>
    {
        var selected = Candidates.Where(candidate => candidate.IsSelected).ToArray();
        var summary = string.Join(Environment.NewLine, selected.Take(12).Select(candidate => $"• {candidate.Path} ({candidate.SizeDisplay})"));
        if (selected.Length > 12) summary += $"{Environment.NewLine}…以及 {selected.Length - 12} 项";
        if (!_confirmationService.Confirm("确认清理缓存", $"将清理所选缓存目录的内容，并保留目录本身：{Environment.NewLine}{Environment.NewLine}{summary}{Environment.NewLine}{Environment.NewLine}{SelectedSummary}{Environment.NewLine}{Environment.NewLine}已删除内容无法由 Nornia 撤销。选择“否”可安全取消。"))
        {
            LogService.Write("INFO", "用户取消了缓存清理。");
            return;
        }

        var before = selected.Sum(candidate => candidate.SizeBytes);
        var results = await _cleanupService.CleanAsync(selected.Select(candidate => candidate.Id).ToArray(), new Progress<string>(message => LogService.Write("INFO", message)), cancellationToken);
        foreach (var result in results)
        {
            LogService.Write(result.Status == CacheCleanupStatus.Cleaned ? "INFO" : "WARNING", $"{result.Status}: {result.Path} {result.Message}");
        }
        var reclaimed = results.Where(result => result.Status == CacheCleanupStatus.Cleaned).Sum(result => result.ReclaimedBytes);
        CleanupResultSummary = $"清理完成：预计 {FormatSize(before)}，实际释放 {FormatSize(reclaimed)}。";
        await LoadCandidatesAsync(cancellationToken);
    }, "仍有失败项时，可打开目录或查看 Problems。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanCleanPackage))]
    private Task CleanPackageAsync(CachePackageSummaryItem? summary) => RunAsync("清理软件包缓存", async cancellationToken =>
    {
        if (summary is null) return;
        var selected = Candidates.Where(candidate => MatchesPackage(candidate, summary)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("该软件包没有可清理的缓存。");
        var reviewCount = selected.Count(candidate => candidate.Confidence == CacheConfidence.Review);
        var lines = string.Join(Environment.NewLine, selected.Take(12).Select(candidate =>
            $"• {(candidate.Confidence == CacheConfidence.Review ? "[需审查] " : string.Empty)}{candidate.Path} ({candidate.SizeDisplay})"));
        if (selected.Length > 12) lines += $"{Environment.NewLine}…以及 {selected.Length - 12} 项";
        var reviewWarning = reviewCount > 0
            ? $"{Environment.NewLine}{Environment.NewLine}注意：其中 {reviewCount} 项为「需审查」置信度，清理后无法恢复，请确认路径后再继续。"
            : string.Empty;
        if (!_confirmationService.Confirm(
                "确认清理软件包缓存",
                $"将清理软件包“{summary.PackageName}”的 {selected.Length} 个缓存目录内容，并保留目录本身：{Environment.NewLine}{Environment.NewLine}{lines}{Environment.NewLine}{Environment.NewLine}预计释放 {FormatSize(selected.Sum(candidate => candidate.SizeBytes))}。已删除内容无法由 Nornia 撤销。{reviewWarning}"))
        {
            LogService.Write("INFO", $"用户取消了软件包“{summary.PackageName}”的缓存清理。");
            return;
        }

        var before = selected.Sum(candidate => candidate.SizeBytes);
        var results = await _cleanupService.CleanAsync(selected.Select(candidate => candidate.Id).ToArray(), new Progress<string>(message => LogService.Write("INFO", message)), cancellationToken);
        foreach (var result in results)
        {
            LogService.Write(result.Status == CacheCleanupStatus.Cleaned ? "INFO" : "WARNING", $"{result.Status}: {result.Path} {result.Message}");
        }
        var reclaimed = results.Where(result => result.Status == CacheCleanupStatus.Cleaned).Sum(result => result.ReclaimedBytes);
        CleanupResultSummary = $"软件包“{summary.PackageName}”清理完成：预计 {FormatSize(before)}，实际释放 {FormatSize(reclaimed)}。";
        await LoadCandidatesAsync(cancellationToken);
    }, "仍有失败项时，可打开目录或查看 Problems。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private void OpenSelected()
    {
        Process.Start(new ProcessStartInfo(SelectedCandidate!.Path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void SelectHighConfidence()
    {
        foreach (var candidate in Candidates) candidate.IsSelected = candidate.Confidence == CacheConfidence.High;
        ConfidenceFilter = "高置信";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var candidate in Candidates) candidate.IsSelected = false;
    }

    public void ApplyNavigationContext(NavigationContext? context)
    {
        switch (context)
        {
            case NavigationContext.CacheHighConfidence:
                ConfidenceFilter = "高置信";
                SelectHighConfidence();
                break;
            case NavigationContext.CacheByPackage(var id, var name, var provider):
                ConfidenceFilter = "全部";
                break;
        }
    }

    private bool CanClean() => Candidates.Any(candidate => candidate.IsSelected);
    private bool CanOpenSelected() => SelectedCandidate is not null && Directory.Exists(SelectedCandidate.Path);
    private bool CanCleanPackage(CachePackageSummaryItem? summary) => summary is not null;

    // ===== Copy commands (网格 复制选中 / 复制全部) =====

    private void NotifyCopyCommands()
    {
        CopySelectedPackageSummariesCommand.NotifyCanExecuteChanged();
        CopyAllPackageSummariesCommand.NotifyCanExecuteChanged();
        CopySelectedCandidatesCommand.NotifyCanExecuteChanged();
        CopyAllCandidatesCommand.NotifyCanExecuteChanged();
    }

    private static string FormatPackageSummary(CachePackageSummaryItem summary) =>
        $"{summary.PackageName}\t{summary.PackageId}\t{summary.Provider}\t{summary.CandidateCount}\t{summary.SizeDisplay}";

    private static string FormatCandidate(CacheCandidateItem candidate) =>
        $"{candidate.Confidence}\t{candidate.Source}\t{candidate.PackageName}\t{candidate.SizeDisplay}\t{candidate.Path}";

    [RelayCommand(CanExecute = nameof(CanCopySelectedPackageSummaries))]
    private void CopySelectedPackageSummaries() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedPackageSummaries.Select(FormatPackageSummary)));

    private bool CanCopySelectedPackageSummaries() => SelectedPackageSummaries.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllPackageSummaries))]
    private void CopyAllPackageSummaries() =>
        _clipboard.SetText(string.Join(Environment.NewLine, PackageSummaries.Select(FormatPackageSummary)));

    private bool CanCopyAllPackageSummaries() => PackageSummaries.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopySelectedCandidates))]
    private void CopySelectedCandidates() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedCandidates.Select(FormatCandidate)));

    private bool CanCopySelectedCandidates() => SelectedCandidates.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllCandidates))]
    private void CopyAllCandidates() =>
        _clipboard.SetText(string.Join(Environment.NewLine, Candidates.Select(FormatCandidate)));

    private bool CanCopyAllCandidates() => Candidates.Count > 0;

    partial void OnConfidenceFilterChanged(string value) => FilteredCandidates.Refresh();
    partial void OnSourceFilterChanged(string value) => FilteredCandidates.Refresh();
    partial void OnTypeFilterChanged(string value) => FilteredCandidates.Refresh();
    partial void OnSelectedPackageSummaryChanged(CachePackageSummaryItem? value) => FilteredCandidates.Refresh();

    partial void OnSelectedCandidateChanged(CacheCandidateItem? value) => OpenSelectedCommand.NotifyCanExecuteChanged();

    private bool MatchesFilter(object value) => value is CacheCandidateItem candidate
        && (SelectedPackageSummary is null || MatchesPackage(candidate, SelectedPackageSummary))
        && (TypeFilter == "全部" || string.Equals(candidate.CacheType, TypeFilter, StringComparison.OrdinalIgnoreCase))
        && (SourceFilter == "全部" || string.Equals(candidate.Source, SourceFilter, StringComparison.OrdinalIgnoreCase))
        && ConfidenceFilter switch
        {
            "高置信" => candidate.Confidence == CacheConfidence.High,
            "需审查" => candidate.Confidence == CacheConfidence.Review,
            _ => true
        };

    private static bool MatchesPackage(CacheCandidateItem candidate, CachePackageSummaryItem summary) =>
        string.Equals(candidate.PackageId, summary.PackageId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.PackageName, summary.PackageName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.Provider, summary.Provider, StringComparison.OrdinalIgnoreCase);

    private void OnCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CacheCandidateItem.IsSelected)) RefreshSelectionSummary();
    }

    private void RefreshSelectionSummary()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSizeBytes));
        OnPropertyChanged(nameof(SelectedSummary));
        CleanCommand.NotifyCanExecuteChanged();
    }

    private static string FormatSize(long size) => new CacheCandidateItem(new("", "", "", size, CacheConfidence.High, "")).SizeDisplay;
}

public partial class CacheCandidateItem(CacheCandidate candidate) : ObservableObject
{
    public string Id { get; } = candidate.Id;
    public string Source { get; } = candidate.Source;
    public string Path { get; } = candidate.Path;
    public long SizeBytes { get; } = candidate.SizeBytes;
    public CacheConfidence Confidence { get; } = candidate.Confidence;
    public string Reason { get; } = candidate.Reason;
    public string PackageName { get; } = candidate.PackageDisplayName;
    public string PackageId { get; } = candidate.PackageId ?? string.Empty;
    public string CacheType { get; } = candidate.CacheTypeDisplay;
    public string Provider { get; } = candidate.PackageProvider ?? string.Empty;
    public string UserDirectory { get; } = candidate.UserDirectory ?? string.Empty;
    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024d:F1} KB",
        < 1024 * 1024 * 1024 => $"{SizeBytes / 1024d / 1024d:F1} MB",
        _ => $"{SizeBytes / 1024d / 1024d / 1024d:F2} GB"
    };

    /// <summary>Friendly label for the user directory the cache lives in (LocalAppData / AppData /
    /// user profile), falling back to the full root path.</summary>
    public string UserDirectoryLabel => UserDirectoryLabelFor(UserDirectory);

    public static string UserDirectoryLabelFor(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return string.Empty;
        if (PathEquals(directory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))) return "AppData\\Local";
        if (PathEquals(directory, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))) return "AppData\\Roaming";
        if (PathEquals(directory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) return "用户目录";
        return directory;
    }

    private static bool PathEquals(string a, string b) =>
        !string.IsNullOrEmpty(b) && string.Equals(
            System.IO.Path.GetFullPath(a).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            System.IO.Path.GetFullPath(b).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool isSelected = candidate.IsRecommended;
}

public sealed class CachePackageSummaryItem(CachePackageSummary summary)
{
    public string PackageName { get; } = summary.PackageName;
    public string PackageId { get; } = summary.PackageId ?? string.Empty;
    public string Provider { get; } = summary.Provider ?? string.Empty;
    public long SizeBytes { get; } = summary.SizeBytes;
    public int CandidateCount { get; } = summary.CandidateCount;
    public IReadOnlyList<CacheTypeCount> CacheTypes { get; } = summary.CacheTypes;
    public string TypeDisplay => CacheTypes.Count == 0
        ? "其他"
        : string.Join(" · ", CacheTypes.Select(type => $"{type.Type}({type.Count})"));
    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024d:F1} KB",
        < 1024 * 1024 * 1024 => $"{SizeBytes / 1024d / 1024d:F1} MB",
        _ => $"{SizeBytes / 1024d / 1024d / 1024d:F2} GB"
    };
}
