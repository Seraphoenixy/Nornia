namespace Nornia.Core.Collections;

/// <summary>前后缀差分(对照 VS Code listView 的 splice 思路):给定当前序列与目标序列,
/// 找出"公共前缀不动、公共后缀不动、中间一个区间先删后插"的最小中间操作。
/// 行对象引用稳定(复用池/持久树节点)时,展开/折叠/流式追加只产生受影响区间的
/// CollectionChanged,滚动锚点与容器回收由视图侧保持。</summary>
public static class CollectionDiffer
{
    /// <summary>中间操作:从 <see cref="RemoveStart"/> 删除 <see cref="RemoveCount"/> 项,
    /// 再在 <see cref="InsertAt"/> 插入 <see cref="Items"/>。无变化时两者均为空。</summary>
    public readonly record struct MiddleOperation(int RemoveStart, int RemoveCount, int InsertAt, IReadOnlyList<int> Items)
    {
        public bool IsNoOp => RemoveCount == 0 && Items.Count == 0;
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

        var removeStart = prefix;
        var removeCount = current.Count - prefix - suffix;
        var items = new List<int>(desired.Count - prefix - suffix);
        for (var i = prefix; i < desired.Count - suffix; i++)
        {
            items.Add(i);
        }

        return new MiddleOperation(removeStart, removeCount, prefix, items);
    }

    /// <summary>对 <paramref name="collection"/> 应用中间操作(先删后插,下标不漂移)。
    /// 返回被移除的元素(调用方负责资源释放,如解除事件订阅)。</summary>
    public static IReadOnlyList<T> Apply<T>(
        System.Collections.ObjectModel.ObservableCollection<T> collection,
        IReadOnlyList<T> desired,
        IEqualityComparer<T>? comparer = null)
    {
        var op = Compute(collection, desired, comparer);
        var removed = new List<T>(op.RemoveCount);
        for (var i = op.RemoveStart + op.RemoveCount - 1; i >= op.RemoveStart; i--)
        {
            removed.Add(collection[i]);
            collection.RemoveAt(i);
        }

        for (var i = 0; i < op.Items.Count; i++)
        {
            collection.Insert(op.InsertAt + i, desired[op.Items[i]]);
        }

        return removed;
    }
}
