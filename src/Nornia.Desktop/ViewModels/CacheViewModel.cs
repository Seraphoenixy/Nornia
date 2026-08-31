using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Collections;
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
    private readonly CacheClassificationService _classificationService;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClipboardService _clipboard;
    private TimeSpan _lastScanElapsed;
    private bool _suspendSelectionRefresh;

    public BulkObservableCollection<CacheCandidateItem> Candidates { get; } = [];
    public ObservableCollection<CacheCategorySummaryItem> CategorySummaries { get; } = [];

    /// <summary>Multi-selection for the candidate grid (Ctrl+C / 复制选中).</summary>
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
    private CacheCategorySummaryItem? selectedCategorySummary;

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
        CacheClassificationService classificationService,
        IConfirmationService confirmationService,
        IUiLogService logService,
        IUiDispatcher? dispatcher = null,
        IClipboardService? clipboard = null) : base("缓存管理", logService)
    {
        _inventoryService = inventoryService;
        _cleanupService = cleanupService;
        _classificationService = classificationService;
        _confirmationService = confirmationService;
        _dispatcher = dispatcher ?? new WpfUiDispatcher();
        _clipboard = clipboard ?? NullClipboardService.Instance;
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
        await LoadCandidatesAsync(cancellationToken), "检查筛选条件或查看 Problems 后重试。", canCancel: true,
        successMessageFactory: BuildScanCompletionMessage);

    private bool CanScan() => !IsBusy;

    private async Task LoadCandidatesAsync(CancellationToken cancellationToken, bool forceRescan = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var rawCandidates = forceRescan
            ? await _inventoryService.ScanForcedAsync(progress: null, cancellationToken)
            : await _inventoryService.ScanAsync(progress: null, cancellationToken);
        stopwatch.Stop();
        _lastScanElapsed = stopwatch.Elapsed;
        var classifiedCandidates = _classificationService.Classify(rawCandidates);
        var summaries = _classificationService.Summarize(classifiedCandidates);

        await RunOnDispatcherAsync(() =>
        {
            SelectedCandidates.Clear();
            foreach (var existing in Candidates) existing.PropertyChanged -= OnCandidatePropertyChanged;
            var items = classifiedCandidates.Select(candidate =>
            {
                var item = new CacheCandidateItem(candidate);
                item.PropertyChanged += OnCandidatePropertyChanged;
                return item;
            }).ToArray();
            Candidates.ReplaceRange(items);

            CategorySummaries.Clear();
            foreach (var summary in summaries)
            {
                CategorySummaries.Add(new CacheCategorySummaryItem(summary));
            }

            RefreshSelectionState();
            SelectedCategorySummary = CategorySummaries.FirstOrDefault();
            RebuildFilter(SourceFilters, SourceFilter, candidate => candidate.Source, value => SourceFilter = value);
            RebuildFilter(TypeFilters, TypeFilter, candidate => candidate.CacheType, value => TypeFilter = value);
            FilteredCandidates.Refresh();
            NotifyCopyCommands();
            OnPropertyChanged(nameof(TotalSizeBytes));
            OnPropertyChanged(nameof(TotalSizeSummary));
            OnPropertyChanged(nameof(IsCandidateListEmpty));
        });

        StatusMessage = Candidates.Count == 0 ? "未发现可管理的缓存目录" : $"发现 {Candidates.Count} 个缓存候选，已归为 {CategorySummaries.Count} 个推测分类";
    }

    private string BuildScanCompletionMessage() =>
        $"缓存扫描完成：{Candidates.Count} 个候选，{CategorySummaries.Count} 个分类，可审查空间 {FormatSize(TotalSizeBytes)}，耗时 {_lastScanElapsed.TotalSeconds:F1}s";

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
            CleanupResultSummary = "已取消缓存清理。";
            return;
        }

        var before = selected.Sum(candidate => candidate.SizeBytes);
        var results = await _cleanupService.CleanAsync(selected.Select(candidate => candidate.Id).ToArray(), progress: null, cancellationToken);
        foreach (var result in results)
        {
            if (result.Status != CacheCleanupStatus.Cleaned)
            {
                LogService.Write("WARNING", $"{result.Status}: {result.Path} {result.Message}");
            }
        }
        var reclaimed = results.Where(result => result.Status == CacheCleanupStatus.Cleaned).Sum(result => result.ReclaimedBytes);
        CleanupResultSummary = $"清理完成：预计 {FormatSize(before)}，实际释放 {FormatSize(reclaimed)}。";
        await LoadCandidatesAsync(cancellationToken, forceRescan: false);
    }, "仍有失败项时，可打开目录或查看 Problems。", canCancel: true,
        successMessageFactory: () => CleanupResultSummary);

    [RelayCommand(CanExecute = nameof(CanCleanCategory))]
    private Task CleanCategoryAsync(CacheCategorySummaryItem? summary) => RunAsync("清理分类缓存", async cancellationToken =>
    {
        if (summary is null) return;
        var selected = Candidates.Where(candidate => candidate.IsSelected && MatchesCategory(candidate, summary)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("该分类没有已勾选的缓存项。");
        var reviewCount = selected.Count(candidate => candidate.Confidence == CacheConfidence.Review);
        var lines = string.Join(Environment.NewLine, selected.Take(12).Select(candidate =>
            $"• {(candidate.Confidence == CacheConfidence.Review ? "[需审查] " : string.Empty)}{candidate.Path} ({candidate.SizeDisplay})"));
        if (selected.Length > 12) lines += $"{Environment.NewLine}…以及 {selected.Length - 12} 项";
        var reviewWarning = reviewCount > 0
            ? $"{Environment.NewLine}{Environment.NewLine}注意：其中 {reviewCount} 项为「需审查」置信度，清理后无法恢复，请确认路径后再继续。"
            : string.Empty;
        if (!_confirmationService.Confirm(
                "确认清理分类缓存",
                $"“{summary.CategoryName}”是根据目录名称推测的分类。将清理该分类的 {selected.Length} 个缓存目录内容，并保留目录本身：{Environment.NewLine}{Environment.NewLine}{lines}{Environment.NewLine}{Environment.NewLine}预计释放 {FormatSize(selected.Sum(candidate => candidate.SizeBytes))}。已删除内容无法由 Nornia 撤销。{reviewWarning}"))
        {
            CleanupResultSummary = $"已取消分类“{summary.CategoryName}”的缓存清理。";
            return;
        }

        var before = selected.Sum(candidate => candidate.SizeBytes);
        var results = await _cleanupService.CleanAsync(selected.Select(candidate => candidate.Id).ToArray(), progress: null, cancellationToken);
        foreach (var result in results)
        {
            if (result.Status != CacheCleanupStatus.Cleaned)
            {
                LogService.Write("WARNING", $"{result.Status}: {result.Path} {result.Message}");
            }
        }
        var reclaimed = results.Where(result => result.Status == CacheCleanupStatus.Cleaned).Sum(result => result.ReclaimedBytes);
        CleanupResultSummary = $"分类“{summary.CategoryName}”清理完成：预计 {FormatSize(before)}，实际释放 {FormatSize(reclaimed)}。";
        await LoadCandidatesAsync(cancellationToken, forceRescan: false);
    }, "仍有失败项时，可打开目录或查看 Problems。", canCancel: true,
        successMessageFactory: () => CleanupResultSummary);

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private void OpenSelected()
    {
        Process.Start(new ProcessStartInfo(SelectedCandidate!.Path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void SelectHighConfidence()
    {
        SetCandidateSelection(candidate => candidate.Confidence == CacheConfidence.High);
        ConfidenceFilter = "高置信";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SetCandidateSelection(_ => false);
    }

    [RelayCommand(CanExecute = nameof(CanChangeCurrentCategorySelection))]
    private void SelectAllInCategory()
    {
        SetCurrentCategorySelection(true);
    }

    [RelayCommand(CanExecute = nameof(CanChangeCurrentCategorySelection))]
    private void ClearCategorySelection()
    {
        SetCurrentCategorySelection(false);
    }

    public void ApplyNavigationContext(NavigationContext? context)
    {
        switch (context)
        {
            case NavigationContext.CacheHighConfidence:
                ConfidenceFilter = "高置信";
                SelectHighConfidence();
                break;
        }
    }

    private bool CanClean() => Candidates.Any(candidate => candidate.IsSelected);
    private bool CanOpenSelected() => SelectedCandidate is not null && Directory.Exists(SelectedCandidate.Path);
    private bool CanCleanCategory(CacheCategorySummaryItem? summary) => summary is { SelectedCandidateCount: > 0 };
    private bool CanChangeCurrentCategorySelection() => SelectedCategorySummary is not null;

    // ===== Copy commands (网格 复制选中 / 复制全部) =====

    private void NotifyCopyCommands()
    {
        CopySelectedCategorySummariesCommand.NotifyCanExecuteChanged();
        CopyAllCategorySummariesCommand.NotifyCanExecuteChanged();
        CopySelectedCandidatesCommand.NotifyCanExecuteChanged();
        CopyAllCandidatesCommand.NotifyCanExecuteChanged();
    }

    private static string FormatCategorySummary(CacheCategorySummaryItem summary) =>
        $"{summary.CategoryName}\t{summary.TypeDisplay}\t{summary.SelectionCountDisplay}\t{summary.SelectionSizeDisplay}";

    private static string FormatCandidate(CacheCandidateItem candidate) =>
        $"{(candidate.IsSelected ? "已勾选" : "未勾选")}\t{candidate.CacheType}\t{candidate.Confidence}\t{candidate.Path}\t{candidate.SizeDisplay}";

    [RelayCommand(CanExecute = nameof(CanCopySelectedCategorySummaries))]
    private void CopySelectedCategorySummaries() =>
        _clipboard.SetText(FormatCategorySummary(SelectedCategorySummary!));

    private bool CanCopySelectedCategorySummaries() => SelectedCategorySummary is not null;

    [RelayCommand(CanExecute = nameof(CanCopyAllCategorySummaries))]
    private void CopyAllCategorySummaries() =>
        _clipboard.SetText(string.Join(Environment.NewLine, CategorySummaries.Select(FormatCategorySummary)));

    private bool CanCopyAllCategorySummaries() => CategorySummaries.Count > 0;

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
    partial void OnSelectedCategorySummaryChanged(CacheCategorySummaryItem? value)
    {
        if (value is null && CategorySummaries.Count > 0)
        {
            SelectedCategorySummary = CategorySummaries[0];
            return;
        }

        // Category navigation is the primary detail selector. Reset secondary filters so selecting
        // a valid category can never leave the detail grid blank because of a filter chosen for the
        // previous category (for example, "nuget" followed by "Google Chrome").
        if (value is not null)
        {
            TypeFilter = "全部";
            ConfidenceFilter = "全部";
            SourceFilter = "全部";
        }

        FilteredCandidates.Refresh();
        CopySelectedCategorySummariesCommand.NotifyCanExecuteChanged();
        SelectAllInCategoryCommand.NotifyCanExecuteChanged();
        ClearCategorySelectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCandidateChanged(CacheCandidateItem? value) => OpenSelectedCommand.NotifyCanExecuteChanged();

    private bool MatchesFilter(object value) => value is CacheCandidateItem candidate
        && (SelectedCategorySummary is null || MatchesCategory(candidate, SelectedCategorySummary))
        && (TypeFilter == "全部" || string.Equals(candidate.CacheType, TypeFilter, StringComparison.OrdinalIgnoreCase))
        && (SourceFilter == "全部" || string.Equals(candidate.Source, SourceFilter, StringComparison.OrdinalIgnoreCase))
        && ConfidenceFilter switch
        {
            "高置信" => candidate.Confidence == CacheConfidence.High,
            "需审查" => candidate.Confidence == CacheConfidence.Review,
            _ => true
        };

    private static bool MatchesCategory(CacheCandidateItem candidate, CacheCategorySummaryItem summary) =>
        string.Equals(candidate.CategoryKey, summary.CategoryKey, StringComparison.OrdinalIgnoreCase);

    private void OnCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CacheCandidateItem.IsSelected) && !_suspendSelectionRefresh)
        {
            RefreshSelectionState();
        }
    }

    private void SetCandidateSelection(Func<CacheCandidateItem, bool> selector)
    {
        _suspendSelectionRefresh = true;
        try
        {
            foreach (var candidate in Candidates)
            {
                candidate.IsSelected = selector(candidate);
            }
        }
        finally
        {
            _suspendSelectionRefresh = false;
        }

        RefreshSelectionState();
    }

    private void SetCurrentCategorySelection(bool isSelected)
    {
        var summary = SelectedCategorySummary;
        if (summary is null)
        {
            return;
        }

        _suspendSelectionRefresh = true;
        try
        {
            foreach (var candidate in Candidates.Where(candidate => MatchesCategory(candidate, summary)))
            {
                candidate.IsSelected = isSelected;
            }
        }
        finally
        {
            _suspendSelectionRefresh = false;
        }

        RefreshSelectionState();
    }

    private void RefreshSelectionState()
    {
        foreach (var summary in CategorySummaries)
        {
            var selected = Candidates.Where(candidate => candidate.IsSelected && MatchesCategory(candidate, summary)).ToArray();
            summary.UpdateSelection(selected.Length, selected.Sum(candidate => candidate.SizeBytes));
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSizeBytes));
        OnPropertyChanged(nameof(SelectedSummary));
        CleanCommand.NotifyCanExecuteChanged();
        CleanCategoryCommand.NotifyCanExecuteChanged();
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
    public string CategoryKey { get; } = candidate.CategoryKey ?? string.Empty;
    public string CategoryName { get; } = candidate.CategoryDisplayName;
    public string ClassificationReason { get; } = candidate.ClassificationReason ?? string.Empty;
    public string CacheType { get; } = candidate.CacheTypeDisplay;
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

public sealed class CacheCategorySummaryItem(CacheCategorySummary summary) : ObservableObject
{
    private int _selectedCandidateCount;
    private long _selectedSizeBytes;

    public string CategoryKey { get; } = summary.CategoryKey;
    public string CategoryName { get; } = summary.CategoryName;
    public long SizeBytes { get; } = summary.SizeBytes;
    public int CandidateCount { get; } = summary.CandidateCount;
    public IReadOnlyList<CacheTypeCount> CacheTypes { get; } = summary.CacheTypes;
    public string TypeDisplay => CacheTypes.Count == 0
        ? "其他"
        : string.Join(" · ", CacheTypes.Select(type => $"{type.Type}({type.Count})"));
    public int SelectedCandidateCount => _selectedCandidateCount;
    public long SelectedSizeBytes => _selectedSizeBytes;
    public string SelectionCountDisplay => $"{SelectedCandidateCount}/{CandidateCount}";
    public string SelectionSizeDisplay => $"{FormatSize(SelectedSizeBytes)} / {FormatSize(SizeBytes)}";

    public void UpdateSelection(int count, long sizeBytes)
    {
        if (_selectedCandidateCount != count)
        {
            _selectedCandidateCount = count;
            OnPropertyChanged(nameof(SelectedCandidateCount));
            OnPropertyChanged(nameof(SelectionCountDisplay));
        }

        if (_selectedSizeBytes != sizeBytes)
        {
            _selectedSizeBytes = sizeBytes;
            OnPropertyChanged(nameof(SelectedSizeBytes));
            OnPropertyChanged(nameof(SelectionSizeDisplay));
        }
    }

    private static string FormatSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024d:F1} KB",
        < 1024 * 1024 * 1024 => $"{size / 1024d / 1024d:F1} MB",
        _ => $"{size / 1024d / 1024d / 1024d:F2} GB"
    };
}
