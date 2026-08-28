using System.Windows.Controls;

namespace Nornia.Desktop.Services;

/// <summary>虚拟化 ListBox 的滚动锚点守卫(对照 VS Code 树/列表刷新保持视口位置):
/// 集合增量更新时,批内首次变更捕获"可视首行",下一渲染帧把列表滚回该锚点。
/// 旧行为(整表 Clear + 重填)在 WPF 中表现为滚动归零 + 内容闪烁;
/// 增量通知 + 锚点恢复后,流式搜索/展开/刷新期间视口保持稳定。</summary>
public sealed class VirtualizedScrollAnchorGuard : IDisposable
{
    private FrameCoalescer? _frame;
    private object? _anchor;
    private bool _captured;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frame?.Dispose();
        _frame = null;
        _anchor = null;
        _captured = false;
    }

    /// <summary>在 ListBox.CollectionChanged 中调用。</summary>
    public void OnCollectionChanged(ListBox list)
    {
        if (_disposed || _captured)
        {
            return; // 同一同步批次只捕获一次
        }

        _captured = true;
        _anchor = GetTopVisibleItem(list);

        _frame ??= new FrameCoalescer();
        _frame.Schedule(() => Restore(list));
    }

    private void Restore(ListBox list)
    {
        _captured = false;
        var anchor = _anchor;
        _anchor = null;
        if (anchor is not null && list.Items.Contains(anchor))
        {
            list.ScrollIntoView(anchor);
        }
    }

    /// <summary>虚拟化列表只实例化可视区附近的容器:第一个已生成容器即顶部可见行
    /// (行高固定,无需像素级补偿)。</summary>
    private static object? GetTopVisibleItem(ListBox list)
    {
        if (list.Items.Count == 0)
        {
            return null;
        }

        var generator = list.ItemContainerGenerator;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is not null)
            {
                return list.Items[i];
            }
        }

        return null;
    }
}
