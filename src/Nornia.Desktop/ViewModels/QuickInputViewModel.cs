using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Collections;

namespace Nornia.Desktop.ViewModels;

/// <summary>One selectable row in the quick input overlay (command palette / quick open). The action
/// is a delegate so the palette can reuse existing view-model commands without an ICommand layer.</summary>
public sealed record QuickPickItem(string Title, string? Detail, string Glyph, Action Execute);

/// <summary>
/// VS Code-style QuickInput: a top-centered text box plus a scored, filterable list, used by the
/// command palette (Ctrl+Shift+P) and quick open (Ctrl+P).
///
/// 性能设计(对照 VS Code quickinput + fuzzyScorer):
/// - 过滤结果驻留在 <c>_filtered</c> 列表,击键才重算(旧实现每次 VisibleItems() 调用都
///   O(N) 重新过滤 —— 每次方向键 = 两次全量过滤);
/// - 焦点是 <c>_focusIndex</c> 整数,<see cref="MoveSelection"/> O(1)(旧实现 ToList + FindIndex);
/// - 匹配带打分排序:标题子串 &gt; 详情子串 &gt; 词首模糊 &gt; 散点模糊(VS Code fuzzyScorer
///   的简化版:位置奖励 + 间隙惩罚 + 词首奖励);
/// - 可见列表用 <see cref="CollectionDiffer"/> 增量同步,列表视图只收到变化区间的通知。
/// Pure logic (no WPF UI), so the overlay view stays thin and this class is unit-testable.
/// </summary>
public partial class QuickInputViewModel : ObservableObject
{
    private readonly ObservableCollection<QuickPickItem> _visible = [];
    private List<QuickPickItem> _filtered = [];
    private int _focusIndex;

    [ObservableProperty]
    private bool isOpen;

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string filterText = string.Empty;

    [ObservableProperty]
    private QuickPickItem? selectedItem;

    public BulkObservableCollection<QuickPickItem> Items { get; } = [];

    /// <summary>Filtered view consumed by the overlay list:打分排序后的可见行集合(过滤结果
    /// 驻留,不再按谓词逐次重过滤)。XAML 直接绑定该集合。</summary>
    public ObservableCollection<QuickPickItem> View => _visible;

    public QuickInputViewModel()
    {
    }

    /// <summary>Opens the overlay with the given title and items; the filter starts empty and the
    /// first item is pre-selected so Enter confirms immediately.</summary>
    public void Open(string title, IEnumerable<QuickPickItem> items)
    {
        Items.ReplaceRange(items);

        Title = title;
        // FilterText 可能恰好已是空串(不触发 setter):显式重算过滤。
        FilterText = string.Empty;
        ApplyFilter();
        _focusIndex = 0;
        SelectedItem = _filtered.FirstOrDefault();
        IsOpen = true;
    }

    /// <summary>Replaces the source rows without closing the overlay or resetting its query. Used
    /// by quick open after its background workspace index completes.</summary>
    public void ReplaceItems(IEnumerable<QuickPickItem> items) =>
        Items.ReplaceRange(items);

    public void Close() => IsOpen = false;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSelectedItemChanged(QuickPickItem? value)
    {
        // 外部设置选中(如列表点击):同步焦点下标,方向键从该项继续。
        if (value is null)
        {
            _focusIndex = 0;
            return;
        }

        for (var i = 0; i < _filtered.Count; i++)
        {
            if (ReferenceEquals(_filtered[i], value))
            {
                _focusIndex = i;
                return;
            }
        }

        _focusIndex = 0;
    }

    /// <summary>Moves the selection to the next/previous visible row (overlay Up/Down keys)。
    /// O(1):焦点是整数下标,不再重建/遍历可见列表。</summary>
    public void MoveSelection(int offset)
    {
        var count = _filtered.Count;
        if (count == 0)
        {
            SelectedItem = null;
            return;
        }

        _focusIndex = (_focusIndex + offset + count) % count;
        SelectedItem = _filtered[_focusIndex];
    }

    /// <summary>Executes the selected item and closes the overlay (Enter).</summary>
    [RelayCommand]
    private void Confirm()
    {
        var target = SelectedItem ?? _filtered.FirstOrDefault();
        if (target is null)
        {
            Close();
            return;
        }

        var execute = target.Execute;
        Close();
        execute?.Invoke();
    }

    /// <summary>重算过滤 + 打分排序,并增量同步可见列表与焦点。
    /// 排序稳定:同分保持原始顺序(按原始下标)。</summary>
    private void ApplyFilter()
    {
        var query = FilterText?.Trim() ?? string.Empty;
        var scored = new List<(QuickPickItem Item, int Score, int Index)>(Items.Count);

        if (query.Length == 0)
        {
            for (var i = 0; i < Items.Count; i++)
            {
                scored.Add((Items[i], 0, i));
            }
        }
        else
        {
            for (var i = 0; i < Items.Count; i++)
            {
                var score = ScoreItem(Items[i], query);
                if (score >= 0)
                {
                    scored.Add((Items[i], score, i));
                }
            }
        }

        scored.Sort((a, b) =>
            a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));

        var desired = new List<QuickPickItem>(scored.Count);
        foreach (var (item, _, _) in scored)
        {
            desired.Add(item);
        }

        // 增量同步:列表视图只收到变化区间的 CollectionChanged。
        CollectionDiffer.Apply(_visible, desired, ReferenceEqualityComparer.Instance);
        _filtered = desired;

        // 焦点:当前选中项仍在结果中 → 保持其(新)下标;否则回到顶部。
        if (SelectedItem is { } selected)
        {
            var newIndex = -1;
            for (var i = 0; i < desired.Count; i++)
            {
                if (ReferenceEquals(desired[i], selected))
                {
                    newIndex = i;
                    break;
                }
            }

            _focusIndex = Math.Max(0, newIndex);
        }
        else
        {
            _focusIndex = 0;
        }

        if (desired.Count == 0)
        {
            SelectedItem = null;
        }
        else if (SelectedItem is null || !ReferenceEquals(SelectedItem, desired[_focusIndex]))
        {
            SelectedItem = desired[_focusIndex];
        }
    }

    /// <summary>匹配打分(≥0 表示命中)。分档保证语义优先级:
    /// 标题子串(100k 档) &gt; 详情子串(50k 档) &gt; 模糊子序列(10k 档 + 词首/间隙调整)。
    /// 模糊打分:每个命中字符按间隙惩罚(-2/格)、词首奖励(+8)、标题前缀奖励(+10)
    /// —— 连续匹配与词首匹配排在散点匹配之前(VS Code fuzzyScorer 的简化)。</summary>
    private static int ScoreItem(QuickPickItem item, string query)
    {
        var title = item.Title ?? string.Empty;

        var titleHit = title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (titleHit >= 0)
        {
            return 100_000 - titleHit;
        }

        if (item.Detail is { Length: > 0 } detail)
        {
            var detailHit = detail.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (detailHit >= 0)
            {
                return 50_000 - detailHit;
            }
        }

        var score = 0;
        var ti = 0;
        var lastMatch = -1;
        for (var qi = 0; qi < query.Length; qi++)
        {
            var q = query[qi];
            var found = -1;
            while (ti < title.Length)
            {
                if (char.ToUpperInvariant(title[ti]) == char.ToUpperInvariant(q))
                {
                    found = ti;
                    break;
                }

                ti++;
            }

            if (found < 0)
            {
                return -1;
            }

            score -= (found - lastMatch - 1) * 2; // 间隙惩罚
            if (IsWordStart(title, found))
            {
                score += 8; // 词首奖励
            }

            lastMatch = found;
            ti = found + 1;
        }

        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 10; // 前缀奖励
        }

        return 10_000 + score;
    }

    /// <summary>词首:标题首字符,或前一字符是分隔符(空格/-/._/(/: 等)。</summary>
    private static bool IsWordStart(string title, int index)
    {
        if (index == 0)
        {
            return true;
        }

        return " ./-_:(".IndexOf(title[index - 1]) >= 0;
    }
}
