using System.Buffers;

namespace Nornia.Desktop.Code;

/// <summary>
/// 迷你地图的 CPU 侧帧缓冲(E1 残余:WriteableBitmap 双缓冲 + 脏行位图)。
/// 像素以 BGRA 字节序的 int 保存(与 <c>PixelFormats.Bgra32</c> 的 WritePixels 直通);
/// 行写入按"与当前内容比较,变化才标记脏"——滚动/重复重绘时零脏行 → 零 WritePixels,
/// 只有真正变化的行被提交(替代固定 110ms 定时器每次整条重画)。
/// 纯逻辑、无 WPF 依赖,可单测。
/// </summary>
public sealed class MinimapFramebuffer
{
    private readonly int _width;
    private readonly int _height;
    private readonly int[] _pixels;
    private readonly bool[] _dirty;

    public MinimapFramebuffer(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "帧缓冲尺寸必须为正");
        }

        _width = width;
        _height = height;
        _pixels = new int[width * height];
        _dirty = new bool[height];
        // 初始全脏:首次提交必须整体上屏。
        Array.Fill(_dirty, true);
    }

    public int Width => _width;
    public int Height => _height;
    public int[] Pixels => _pixels;

    /// <summary>把一行像素写入缓冲;内容与当前行相同时不动(不标记脏),
    /// 不同则覆盖并标记该行为脏。</summary>
    public void SetRow(int y, ReadOnlySpan<int> rowPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        if (y >= _height)
        {
            return;
        }

        var start = y * _width;
        var take = Math.Min(rowPixels.Length, _width);
        var same = true;
        for (var i = 0; i < take; i++)
        {
            if (_pixels[start + i] != rowPixels[i])
            {
                same = false;
                break;
            }
        }

        // 超出宽度的输入忽略;内容整体一致时跳过(滚动重绘零脏行)。
        if (same && take == _width)
        {
            return;
        }

        rowPixels[..take].CopyTo(_pixels.AsSpan(start));
        if (take < _width)
        {
            Array.Clear(_pixels, start + take, _width - take);
        }

        _dirty[y] = true;
    }

    /// <summary>整缓冲作废:全部行标记脏(尺寸/主题/文档整体变化的完整重绘)。</summary>
    public void MarkAllDirty() => Array.Fill(_dirty, true);

    /// <summary>取连续的脏行区间(起点, 行数)并清除脏标记——一次提交一批连续行,
    /// 返回后调用方用 WritePixels 逐区间上屏(双缓冲的"提交"半段)。</summary>
    public List<(int Start, int Count)> TakeDirtyRowRuns()
    {
        var runs = new List<(int, int)>(4);
        var y = 0;
        while (y < _height)
        {
            if (!_dirty[y])
            {
                y++;
                continue;
            }

            var start = y;
            while (y < _height && _dirty[y])
            {
                y++;
            }

            runs.Add((start, y - start));
        }

        Array.Clear(_dirty, 0, _dirty.Length);
        return runs;
    }

    /// <summary>是否有任何脏行(测试/诊断)。</summary>
    public bool HasDirtyRows
    {
        get
        {
            foreach (var flag in _dirty)
            {
                if (flag) return true;
            }

            return false;
        }
    }
}