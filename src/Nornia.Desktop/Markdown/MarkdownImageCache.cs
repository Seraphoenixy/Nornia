using System.IO;
using System.Windows.Media.Imaging;

namespace Nornia.Desktop.Markdown;

/// <summary>进程级 Markdown 图片缓存:最近最少使用 + 按嵌入引用计数,双上限(条目数/字节数)。
/// 解码出的位图统一 Freeze(跨线程安全、可被多个 FlowDocument 共享)。渲染文档对每处嵌入
/// 调用 <see cref="Acquire"/>,文档释放时按同一列表调用 <see cref="Release"/>;引用计数归零的
/// 条目在缓存超限时按 LRU 逐出——缓存不再持有其引用,位图随 GC(终结器释放非托管内存)回收,
/// 因此已关闭标签不会让位图被缓存固定到进程退出。仍被可见文档引用的条目永不被逐出。</summary>
public sealed class MarkdownImageCache
{
    public const int DefaultMaxEntries = 128;
    public const long DefaultMaxBytes = 96L * 1024 * 1024;

    public static readonly MarkdownImageCache Instance = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private long _bytes;
    /// <summary>单调 LRU 序列(同毫秒内多次访问也能确定先后)。</summary>
    private long _lruSequence;

    public MarkdownImageCache(int maxEntries = DefaultMaxEntries, long maxBytes = DefaultMaxBytes)
    {
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    private sealed class Entry
    {
        public required BitmapSource Image { get; init; }
        public long Bytes { get; init; }
        public int RefCount { get; set; }
        public long LastUsed { get; set; }
    }

    /// <summary>取缓存位图;缺失时解码并登记(引用计数 0)。返回的图像尚未引用——
    /// 每处嵌入文档需各调用一次 <see cref="Acquire"/>。</summary>
    public BitmapSource? GetOrLoad(string path)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var hit))
            {
                hit.LastUsed = ++_lruSequence;
                return hit.Image;
            }
        }

        var image = Decode(path);
        if (image is null) return null;

        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var concurrent))
            {
                concurrent.LastUsed = ++_lruSequence;
                // BitmapSource 无显式 Dispose:丢弃引用,由 GC(终结器释放非托管位图)回收。
                return concurrent.Image;
            }

            var now = ++_lruSequence;
            var bytes = EstimateBytes(image);
            _entries[path] = new Entry { Image = image, Bytes = bytes, LastUsed = now };
            _bytes += bytes;
            // 只允许逐出严格更旧的零引用条目——刚加入的条目本身永不逐出。
            EvictLocked(newerThan: now);
            return image;
        }
    }

    /// <summary>声明当前文档嵌入该图片(防止其被 LRU 逐出)。条目不存在时为空操作。</summary>
    public void Acquire(string path)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var entry))
            {
                entry.RefCount++;
                entry.LastUsed = ++_lruSequence;
            }
        }
    }

    /// <summary>释放一处嵌入引用。图片从未成功加载(或引用已归零)时为空操作——
    /// 调用方可安全地按解析期预计算的完整路径列表释放。</summary>
    public void Release(string path)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var entry) && entry.RefCount > 0)
            {
                entry.RefCount--;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            // BitmapSource 无显式 Dispose:丢弃引用,由 GC(终结器释放非托管位图)回收。
            _entries.Clear();
            _bytes = 0;
        }
    }

    /// <summary>缓存快照(测试与诊断):条目数 / 引用计数总和 / 估计字节数。</summary>
    public (int Entries, int Referenced, long Bytes) Snapshot()
    {
        lock (_gate)
        {
            var totalReferences = _entries.Values.Sum(item => item.RefCount);
            return (_entries.Count, totalReferences, _bytes);
        }
    }

    /// <summary>指定路径当前的嵌入引用数(测试;条目缺失返回 0)。</summary>
    internal int ReferenceCount(string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(path, out var entry) ? entry.RefCount : 0;
        }
    }

    /// <summary>条目是否存在(测试;与并行测试的全局快照差分解耦)。</summary>
    internal bool ContainsKey(string path)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(path);
        }
    }

    /// <summary>超限时按 LRU 逐出零引用条目;只逐出严格早于 <paramref name="newerThan"/> 的条目,
    /// 所有候选都被引用时允许溢出(可见文档不可失图)。</summary>
    private void EvictLocked(long newerThan)
    {
        while (_entries.Count > _maxEntries || _bytes > _maxBytes)
        {
            var victim = _entries
                .Where(pair => pair.Value.RefCount == 0 && pair.Value.LastUsed < newerThan)
                .OrderBy(pair => pair.Value.LastUsed)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (victim is null) break;

            var entry = _entries[victim];
            _entries.Remove(victim);
            _bytes -= entry.Bytes;
            // 位图随引用丢弃,由 GC(终结器)回收非托管内存。
        }
    }

    private static long EstimateBytes(BitmapSource image) =>
        (long)image.PixelWidth * image.PixelHeight * 4;

    private static BitmapSource? Decode(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1200;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
