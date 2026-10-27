namespace Nornia.Core.Collections;

/// <summary>前后缀差分(对照 VS Code listView 的 splice 思路):给定当前序列与目标序列,
/// 找出"公共前缀不动、公共后缀不动、中间一个区间先删后插"的最小中间操作。
/// 行对象引用稳定(复用池/持久树节点)时,展开/折叠/流式追加只产生受影响区间的
/// CollectionChanged,滚动锚点与容器回收由视图侧保持。</summary>
public static class CollectionDiffer
{
    /// <summary>中间区间超过这一项数时，逐条 Remove/Insert 的 WPF 通知成本近似二次方
    /// （每条通知都要过一遍 ItemWindow/CollectionView），此时调用方应改为单次快照发布
    /// （<see cref="BulkObservableCollection{T}.ReplaceRange"/>）。小区间保留差分通知，
    /// 让未受影响行的容器、滚动锚点与展开态存活。展开/折叠这类小改动走差分，
    /// 大批量增删走快照。</summary>
    public const int SinglePassReplaceThreshold = 128;

    /// <summary>中间操作:从 <see cref="RemoveStart"/> 删除 <see cref="RemoveCount"/> 项,
    /// 再在 <see cref="InsertAt"/> 插入 <see cref="InsertCount"/> 项（插入区间连续，
    /// 故不再物化下标表）。无变化时两个计数均为 0。</summary>
    public readonly record struct MiddleOperation(int RemoveStart, int RemoveCount, int InsertAt, int InsertCount)
    {
        public bool IsNoOp => RemoveCount == 0 && InsertCount == 0;
    }

    public static MiddleOperation Compute<T>(IReadOnlyList<T> current, IReadOnlyList<T> desired,
        IEqualityComparer<T>? comparer = null)
    {
        var equals = new Func<T, T, bool>((x, y) => (comparer ?? EqualityComparer<T>.Default).Equals(x, y));

        var prefix = 0;
        var limit = Math.Min(current.Count, desired.Count);
        while (prefix < limit && equals(current[prefix], desired[prefix])) prefix++;

        var suffix = 0;
        while (suffix < limit - prefix && equals(current[current.Count - 1 - suffix], desired[desired.Count - 1 - suffix])) suffix++;

        return new MiddleOperation(
            prefix,
            current.Count - prefix - suffix,
            prefix,
            Math.Max(0, desired.Count - prefix - suffix));
    }

    /// <summary>对 <paramref name="collection"/> 应用中间操作(先删后插,下标不漂移)。
    /// 返回被移除的元素(调用方负责资源释放,如解除事件订阅)。</summary>
    public static IReadOnlyList<T> Apply<T>(
        System.Collections.ObjectModel.ObservableCollection<T> collection,
        IReadOnlyList<T> desired,
        IEqualityComparer<T>? comparer = null) =>
        Apply(collection, desired, Compute(collection, desired, comparer));

    /// <summary>应用调用方已经算好的中间操作：省掉第二次全表前后缀比较——大列表里那一次
    /// 逐项等值扫描与差分本身同量级。</summary>
    public static IReadOnlyList<T> Apply<T>(
        System.Collections.ObjectModel.ObservableCollection<T> collection,
        IReadOnlyList<T> desired,
        MiddleOperation operation)
    {
        var removed = new List<T>(operation.RemoveCount);
        for (var i = operation.RemoveStart + operation.RemoveCount - 1; i >= operation.RemoveStart; i--)
        {
            removed.Add(collection[i]);
            collection.RemoveAt(i);
        }

        for (var i = 0; i < operation.InsertCount; i++)
        {
            collection.Insert(operation.InsertAt + i, desired[operation.InsertAt + i]);
        }

        return removed;
    }
}
