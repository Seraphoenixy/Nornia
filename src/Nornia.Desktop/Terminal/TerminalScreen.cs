namespace Nornia.Desktop.Terminal;

/// <summary>A character cell with resolved ARGB colors (parser resolves ANSI indexes through the
/// host palette; truecolor passes through directly).</summary>
public struct TerminalCell
{
    public char Char;
    public int Foreground;
    public int Background;
    public bool Bold;
    public bool Underline;
    public bool Inverse;
    /// <summary>绘制宽度:1 = 单列;2 = 宽字符(其列号已按两列推进);0 = 零宽/组合字符
    /// (不占列)。宽字符支持(T6)与列运算共用。</summary>
    public byte Width;
}

/// <summary>
/// Character-cell terminal screen: a fixed-size primary grid plus a capped scrollback ring, a
/// switchable alternate buffer (vim/less), a cursor with save/restore, erase/scroll primitives and
/// an optional scroll region. Pure model — the ANSI parser feeds it, the WPF surface renders it; no
/// UI types involved so the whole thing is unit-testable. Mutations are locked; <see cref="Changed"/>
/// is always raised outside the lock so UI handlers can safely read the model.
/// </summary>
public sealed class TerminalScreen
{
    private readonly object _gate = new();
    // T1: 滚动缓冲为环形数组(head + count):超限进新行不再 List.RemoveAt(0) 整表移位,
    // 而是覆盖最旧槽位并前进 head —— O(1)。被覆盖的最旧行数组就地复用为新底行(零分配)。
    private readonly TerminalCell[][]? _scrollbackRing;
    private readonly int[]? _scrollbackVersionRing;
    private int _scrollbackHead;
    private int _scrollbackCount;
    private List<TerminalCell[]> _primary;
    private List<TerminalCell[]>? _alternate;
    private List<TerminalCell[]> _rows;
    // 行内容版本号:每行一个递增计数,行内容变化时自增;滚动/换行时随内容移动。
    // 渲染端按 (行索引 → 版本) 缓存每行的 DrawingVisual,版本未变直接 DrawDrawing 回放,
    // 只重排真正变化的行(xterm.js dirty-row 思路)。
    private int[] _primaryVersions;
    private int[]? _alternateVersions;
    private int[] _rowVersions;
    // T5: 行文本惰性字符串缓存(渲染/复制共用)。键为"自底向上行号"(与 GetLine 相同),
    // 值为 (内容版本, 文本);版本未变直接复用,避免每次渲染/复制重建行字符串。
    private readonly Dictionary<int, (int Version, string Text)> _renderedLineCache = new();
    // T5: Changed 之外附带受影响行号区间(屏幕行 0 起);滚动/整屏变化发全区间。
    private int _dirtyStart = int.MaxValue;
    private int _dirtyEnd = -1;
    private int _columns;
    private int _cursorRow;
    private int _cursorColumn;
    // VT terminals defer wrapping until the next printable character.  Moving immediately after
    // writing the last column makes the model one cell ahead of ConPTY and causes cursor drift.
    private bool _wrapPending;
    private bool _cursorVisible = true;
    private bool _inAlternate;
    private int _savedRow;
    private int _savedColumn;
    private int _regionTop;
    private int _regionBottom;
    private bool _dirty;
    private bool _suspendChanged;
    private readonly AnsiParser _parser;

    public TerminalScreen(int columns = 120, int rows = 30, int scrollbackLimit = 2000, Func<int, int>? palette = null)
    {
        _columns = Math.Max(20, columns);
        var initialRows = Math.Max(10, rows);
        _primary = NewRows(initialRows);
        _alternate = null!; // T7: 备用屏惰性分配,进入备屏时才创建
        _primaryVersions = NewVersions(initialRows);
        _alternateVersions = null!;
        _rows = _primary;
        _rowVersions = _primaryVersions;
        _regionBottom = initialRows - 1;
        ScrollbackLimit = scrollbackLimit;
        if (scrollbackLimit > 0)
        {
            _scrollbackRing = new TerminalCell[scrollbackLimit][];
            _scrollbackVersionRing = new int[scrollbackLimit];
        }
        _parser = new AnsiParser(this, palette);
    }

    public int Columns => _columns;

    public int Rows => _rows.Count;

    public int ScrollbackLimit { get; }

    /// <summary>Total lines available when scrolled up: scrollback + screen rows.</summary>
    public int TotalLines => _scrollbackCount + _rows.Count;

    public event Action? Changed;

    /// <summary>受本次变更影响的屏幕行号区间(行 0 起,闭区间)。与 <see cref="Changed"/> 同时
    /// 触发;滚动/整屏操作发全区间。供渲染端对脏行做最小重排(T5)。</summary>
    public event Action<int, int>? RowsChanged;

    public TerminalCell GetCell(int row, int column)
    {
        lock (_gate)
        {
            return row >= 0 && row < _rows.Count && column >= 0 && column < _columns
                ? _rows[row][column]
                : default;
        }
    }

    /// <summary>Reads one line, newest-first: 0 = bottom of the screen, larger = older lines
    /// (scrollback). Returns null when out of range.</summary>
    public TerminalCell[]? GetLine(int lineFromBottom)
    {
        lock (_gate)
        {
            return GetLineCore(lineFromBottom);
        }
    }

    private TerminalCell[]? GetLineCore(int lineFromBottom)
    {
        if (lineFromBottom < 0 || lineFromBottom >= TotalLines)
        {
            return null;
        }

        if (lineFromBottom < _rows.Count)
        {
            return (TerminalCell[])_rows[_rows.Count - 1 - lineFromBottom].Clone();
        }

        var newestIndex = lineFromBottom - _rows.Count;
        return (TerminalCell[])_scrollbackRing![(_scrollbackHead + _scrollbackCount - 1 - newestIndex) % _scrollbackRing.Length].Clone();
    }

    /// <summary>内容版本 of the same line (same newest-first indexing as <see cref="GetLine"/>).
    /// 同一行索引的版本相等 ⇒ 内容不变,渲染端可回放缓存;不等(或越界返回 0)则需重排。
    /// 空白行恒为版本 0。</summary>
    public int GetLineVersion(int lineFromBottom)
    {
        lock (_gate)
        {
            return GetLineVersionCore(lineFromBottom);
        }
    }

    private int GetLineVersionCore(int lineFromBottom)
    {
        if (lineFromBottom < 0 || lineFromBottom >= TotalLines)
        {
            return 0;
        }

        if (lineFromBottom < _rows.Count)
        {
            return _rowVersions[_rows.Count - 1 - lineFromBottom];
        }

        var newestIndex = lineFromBottom - _rows.Count;
        return _scrollbackVersionRing![(_scrollbackHead + _scrollbackCount - 1 - newestIndex) % _scrollbackVersionRing.Length];
    }

    /// <summary>行文本惰性缓存读取(T5/T8):同版本复用上次生成的字符串,版本变化才重建。
    /// 与 <see cref="GetLine"/> 相同的自底向上行号;空白行返回 ""。渲染与复制共用。</summary>
    public string? GetRenderedLine(int lineFromBottom)
    {
        lock (_gate)
        {
            if (lineFromBottom < 0 || lineFromBottom >= TotalLines)
            {
                return null;
            }

            var version = GetLineVersionCore(lineFromBottom);
            if (_renderedLineCache.TryGetValue(lineFromBottom, out var entry) && entry.Version == version)
            {
                return entry.Text;
            }

            var cells = GetLineCore(lineFromBottom);
            if (cells is null)
            {
                return null;
            }

            var length = cells.Length;
            while (length > 0 && cells[length - 1].Char == '\0')
            {
                length--;
            }

            var text = LineToText(cells.AsSpan(0, length));
            if (_renderedLineCache.Count >= 4096)
            {
                _renderedLineCache.Clear();
            }

            _renderedLineCache[lineFromBottom] = (version, text);
            return text;
        }
    }

    public (int Row, int Column) Cursor
    {
        get { lock (_gate) return (_cursorRow, _cursorColumn); }
    }

    public bool CursorVisible
    {
        get { lock (_gate) return _cursorVisible; }
    }

    public bool InAlternateBuffer
    {
        get { lock (_gate) return _inAlternate; }
    }

    /// <summary>Feeds plain text through the persistent ANSI parser (fallback mode, tests). The
    /// same parser instance stays alive so SGR state carries across chunks.</summary>
    public void FeedText(string text) => _parser.Feed(text);

    /// <summary>Batch feed: feeds text and raises <see cref="Changed"/> only once, avoiding
    /// per-character render thrash for redirected shells.</summary>
    public void FeedTextBatch(string text)
    {
        lock (_gate) _suspendChanged = true;
        try
        {
            _parser.Feed(text);
        }
        finally
        {
            lock (_gate) _suspendChanged = false;
            RaiseChanged();
        }
    }

    internal void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            var newColumns = Math.Max(20, columns);
            var newRows = Math.Max(10, rows);
            if (newColumns == _columns && newRows == _rows.Count)
            {
                return;
            }

            if (newColumns == _columns)
            {
                // T7: 列数不变 → 只改行数:保留行内容与版本,不做整块重建/全量 bump。
                ResizeRowCountOnly(newRows);
                _cursorRow = Math.Min(_cursorRow, newRows - 1);
                _cursorColumn = Math.Min(_cursorColumn, newColumns - 1);
                _wrapPending = false;
                _regionTop = 0;
                _regionBottom = newRows - 1;
                MarkRowsDirty();
                MarkDirty();
            }
            else
            {
                var resized = NewRows(newRows);
                var copyRows = Math.Min(newRows, _rows.Count);
                for (var row = 0; row < copyRows; row++)
                {
                    var source = _rows[_rows.Count - copyRows + row];
                    var dest = resized[row];
                    var copyLength = Math.Min(newColumns, _columns);
                    copyLength = Math.Min(copyLength, Math.Min(source.Length, dest.Length));
                    if (copyLength > 0)
                    {
                        Array.Copy(source, 0, dest, 0, copyLength);
                    }
                }

                // 列宽变化 ⇒ 所有行内容(截断/宽度)需重排:版本号整体 +1。
                var newVersions = NewVersions(newRows);
                var copyVersionRows = Math.Min(newRows, _rowVersions.Length);
                for (var row = 0; row < copyVersionRows; row++)
                {
                    newVersions[row] = _rowVersions[_rowVersions.Length - copyVersionRows + row] + 1;
                }

                _columns = newColumns;
                _primary = resized;
                _alternateVersions = null;
                _alternate = null; // T7: 备用屏惰性分配;列变化不预建
                _primaryVersions = newVersions;
                _rows = _primary;
                _rowVersions = _primaryVersions;
                _inAlternate = false;
                _cursorRow = Math.Min(_cursorRow, newRows - 1);
                _cursorColumn = Math.Min(_cursorColumn, newColumns - 1);
                _wrapPending = false;
                _regionTop = 0;
                _regionBottom = newRows - 1;
                MarkRowsDirty();
                MarkDirty();
            }

            _renderedLineCache.Clear();
        }

        RaiseChanged();
    }

    /// <summary>列数不变的 resize:只增删行,不动已有行内容与版本(T7)。
    /// 备用屏已分配时同步行数,避免进入备屏后行数不匹配。</summary>
    private void ResizeRowCountOnly(int newRows)
    {
        var delta = newRows - _rows.Count;
        if (delta > 0)
        {
            for (var i = 0; i < delta; i++)
            {
                _primary.Add(BlankRow());
                _primaryVersions = GrowVersions(_primaryVersions, 1);
                if (_alternate is not null)
                {
                    _alternate.Add(BlankRow());
                    _alternateVersions = GrowVersions(_alternateVersions!, 1);
                }
            }
        }
        else if (delta < 0)
        {
            // 与原 Resize 一致:保留"底部 newRows 行"(旧实现 copyRows 取自底对齐)。
            _primary.RemoveRange(0, -delta);
            _primaryVersions = ShrinkVersions(_primaryVersions, newRows);
            if (_alternate is not null)
            {
                _alternate.RemoveRange(0, -delta);
                _alternateVersions = ShrinkVersions(_alternateVersions!, newRows);
            }
        }

        // 版本数组被替换为新实例后,_rowVersions(活动缓冲别名)必须跟随。
        _rowVersions = ReferenceEquals(_rows, _alternate) && _alternateVersions is not null
            ? _alternateVersions
            : _primaryVersions;
    }

    private static int[] GrowVersions(int[] versions, int count)
    {
        var grown = new int[versions.Length + count];
        Array.Copy(versions, grown, versions.Length);
        return grown;
    }

    private static int[] ShrinkVersions(int[] versions, int count)
    {
        var shrunk = new int[count];
        Array.Copy(versions, versions.Length - count, shrunk, 0, count);
        return shrunk;
    }

    // ===== mutation primitives (locked; the parser runs on the pump thread) =====

    internal void WriteChar(char character, int foreground, int background, bool bold, bool underline)
    {
        lock (_gate)
        {
            WriteCharCore(character, foreground, background, bold, underline);
        }

        RaiseChanged();
    }

    /// <summary>批量文本路径(T3):一次进锁、循环写格、一次收尾 RaiseChanged。字符集/控制字符
    /// 语义与 <see cref="WriteChar"/> 完全一致(ANSI 解析器只对纯文本段调用)。RaiseChanged
    /// 自带 _suspendChanged 守卫,嵌套在 FeedTextBatch 下时不会破坏其单次通知契约。</summary>
    internal void WriteChars(ReadOnlySpan<char> characters, int foreground, int background, bool bold, bool underline)
    {
        lock (_gate)
        {
            foreach (var character in characters)
            {
                WriteCharCore(character, foreground, background, bold, underline);
            }
        }

        RaiseChanged();
    }

    private void WriteCharCore(char character, int foreground, int background, bool bold, bool underline)
    {
        EnsureCursorWithinScreen();
        // \r / \n / \t / \b 只移动光标(换行引发的滚屏由 MoveDownWrapping 的版本位移
        // 覆盖),行像素内容不变 ⇒ 不 bump,避免每次击键都重排可见行。
        switch (character)
        {
            case '\r':
                _cursorColumn = 0;
                _wrapPending = false;
                MarkDirty();
                return;
            case '\n':
                MoveDownWrapping();
                _wrapPending = false;
                MarkDirty();
                return;
            case '\t':
                _cursorColumn = Math.Min(_columns - 1, (_cursorColumn / 8 + 1) * 8);
                MarkDirty();
                return;
            case '\b':
                _wrapPending = false;
                if (_cursorColumn > 0)
                {
                    _cursorColumn--;
                }

                MarkDirty();
                return;
        }

        if (_wrapPending)
        {
            MoveDownWrapping();
            _wrapPending = false;
        }

        var width = CellWidth(character);
        if (width == 0)
        {
            // 组合字符:零宽 —— 覆盖当前格(近似合并),不推进列,列运算保持稳定。
            var cell = _rows[_cursorRow][_cursorColumn];
            cell.Char = character;
            cell.Width = 0;
            _rows[_cursorRow][_cursorColumn] = cell;
            BumpRow(_cursorRow);
            MarkDirty();
            return;
        }

        if (width == 2 && _cursorColumn + 1 >= _columns)
        {
            // 宽字符写在全行最后一格:按 1 列占位 + 延迟换行(xterm 近似;避免越界写)。
            _rows[_cursorRow][_cursorColumn] = new TerminalCell
            {
                Char = character, Foreground = foreground, Background = background,
                Bold = bold, Underline = underline, Width = 1,
            };
            _wrapPending = true;
        }
        else
        {
            _rows[_cursorRow][_cursorColumn] = new TerminalCell
            {
                Char = character, Foreground = foreground, Background = background,
                Bold = bold, Underline = underline, Width = (byte)width,
            };
            if (width == 2)
            {
                // 第二列占位(不显示字符),光标推进两列。
                if (_cursorColumn + 1 < _columns)
                {
                    _rows[_cursorRow][_cursorColumn + 1] = new TerminalCell { Width = 0 };
                }

                _cursorColumn = Math.Min(_columns - 1, _cursorColumn + 2);
                if (_cursorColumn + 1 >= _columns)
                {
                    _wrapPending = true;
                }
            }
            else
            {
                if (_cursorColumn + 1 >= _columns)
                {
                    _wrapPending = true;
                }
                else
                {
                    _cursorColumn++;
                }
            }
        }

        BumpRow(_cursorRow);
        MarkDirty();
    }

    /// <summary>T6: 字符绘制宽度。0 = 组合/零宽,1 = 单列,2 = 宽(CJK/全角)。</summary>
    public static byte CellWidth(char character)
    {
        var code = (int)character;
        if (code >= 0x0300 && code <= 0x036F)
        {
            return 0; // combining diacritical marks
        }

        return IsWideCodePoint(code) ? (byte)2 : (byte)1;
    }

    private static bool IsWideCodePoint(int code) =>
        // East Asian Width W/F (BMP approximation; 代理对超出 char 范围,交由表层按码点处理)。
        (code >= 0x1100 && code <= 0x115F) ||   // Hangul Jamo
        (code >= 0x2E80 && code <= 0xA4CF) ||   // CJK Radicals … Yi
        (code >= 0xAC00 && code <= 0xD7A3) ||   // Hangul Syllables
        (code >= 0xF900 && code <= 0xFAFF) ||   // CJK Compatibility Ideographs
        (code >= 0xFE30 && code <= 0xFE4F) ||   // CJK Compatibility Forms
        (code >= 0xFF00 && code <= 0xFF60) ||   // Fullwidth Forms
        (code >= 0xFFE0 && code <= 0xFFE6);     // fullwidth signs

    internal void MoveCursorTo(int row, int column)
    {
        lock (_gate)
        {
            _cursorRow = Math.Clamp(row, 0, _rows.Count - 1);
            _cursorColumn = Math.Clamp(column, 0, _columns - 1);
            _wrapPending = false;
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void MoveCursorRelative(int rowDelta, int columnDelta)
    {
        lock (_gate)
        {
            _cursorRow = Math.Clamp(_cursorRow + rowDelta, 0, _rows.Count - 1);
            _cursorColumn = Math.Clamp(_cursorColumn + columnDelta, 0, _columns - 1);
            _wrapPending = false;
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void SetCursorColumn(int column)
    {
        lock (_gate)
        {
            _cursorColumn = Math.Clamp(column, 0, _columns - 1);
            _wrapPending = false;
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void EraseInLine(int mode)
    {
        lock (_gate)
        {
            var row = _rows[_cursorRow];
            switch (mode)
            {
                case 0:
                    ClearRange(row, _cursorColumn, _columns - 1);
                    break;
                case 1:
                    ClearRange(row, 0, _cursorColumn);
                    break;
                case 2:
                    ClearRange(row, 0, _columns - 1);
                    break;
            }

            BumpRow(_cursorRow);
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void EraseInDisplay(int mode)
    {
        lock (_gate)
        {
            switch (mode)
            {
                case 0:
                    EraseInLineCore(0);
                    BumpRow(_cursorRow);
                    for (var row = _cursorRow + 1; row < _rows.Count; row++)
                    {
                        ClearRange(_rows[row], 0, _columns - 1);
                        BumpRow(row);
                    }

                    break;
                case 1:
                    for (var row = 0; row < _cursorRow; row++)
                    {
                        ClearRange(_rows[row], 0, _columns - 1);
                        BumpRow(row);
                    }

                    EraseInLineCore(1);
                    BumpRow(_cursorRow);
                    break;
                case 2:
                    BumpAllRows();
                    foreach (var row in _rows)
                    {
                        ClearRange(row, 0, _columns - 1);
                    }

                    break;
                case 3:
                    BumpAllRows();
                    foreach (var row in _rows)
                    {
                        ClearRange(row, 0, _columns - 1);
                    }

                    _scrollbackHead = 0;
                    _scrollbackCount = 0;
                    _renderedLineCache.Clear();
                    break;
            }

            MarkDirty();
        }

        RaiseChanged();
    }

    internal void ScrollUp(int lines)
    {
        lock (_gate)
        {
            var count = Math.Clamp(lines, 0, _regionBottom - _regionTop + 1);
            for (var i = 0; i < count; i++)
            {
                for (var row = _regionTop; row < _regionBottom; row++)
                {
                    _rows[row] = _rows[row + 1];
                    _rowVersions[row] = _rowVersions[row + 1];
                }

                _rows[_regionBottom] = BlankRow();
                _rowVersions[_regionBottom] = 0;
            }

            MarkRowsDirty();
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void ScrollDown(int lines)
    {
        lock (_gate)
        {
            var count = Math.Clamp(lines, 0, _regionBottom - _regionTop + 1);
            for (var i = 0; i < count; i++)
            {
                for (var row = _regionBottom; row > _regionTop; row--)
                {
                    _rows[row] = _rows[row - 1];
                    _rowVersions[row] = _rowVersions[row - 1];
                }

                _rows[_regionTop] = BlankRow();
                _rowVersions[_regionTop] = 0;
            }

            MarkRowsDirty();
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void SetScrollRegion(int top, int bottom)
    {
        lock (_gate)
        {
            _regionTop = Math.Clamp(top, 0, _rows.Count - 1);
            _regionBottom = Math.Clamp(bottom, _regionTop, _rows.Count - 1);
            _cursorRow = 0;
            _cursorColumn = 0;
            _wrapPending = false;
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void SaveCursor()
    {
        lock (_gate)
        {
            _savedRow = _cursorRow;
            _savedColumn = _cursorColumn;
        }
    }

    internal void RestoreCursor()
    {
        lock (_gate)
        {
            _cursorRow = Math.Clamp(_savedRow, 0, _rows.Count - 1);
            _cursorColumn = Math.Clamp(_savedColumn, 0, _columns - 1);
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void SetCursorVisible(bool visible)
    {
        lock (_gate)
        {
            _cursorVisible = visible;
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void EnterAlternateBuffer()
    {
        lock (_gate)
        {
            // T7: 备用屏惰性分配(进入时才创建)。
            if (_alternate is null || _alternateVersions is null)
            {
                _alternate = NewRows(_primary.Count);
                _alternateVersions = NewVersions(_primary.Count);
            }

            foreach (var row in _alternate)
            {
                ClearRange(row, 0, _columns - 1);
            }

            _rows = _alternate;
            _rowVersions = _alternateVersions;
            BumpAllRows();
            _cursorRow = 0;
            _cursorColumn = 0;
            _wrapPending = false;
            _regionTop = 0;
            _regionBottom = _rows.Count - 1;
            _inAlternate = true;
            MarkDirty();
        }

        RaiseChanged();
    }

    internal void ExitAlternateBuffer()
    {
        lock (_gate)
        {
            _rows = _primary;
            _rowVersions = _primaryVersions;
            BumpAllRows();
            _cursorRow = 0;
            _cursorColumn = 0;
            _wrapPending = false;
            _regionTop = 0;
            _regionBottom = _rows.Count - 1;
            _inAlternate = false;
            MarkDirty();
        }

        RaiseChanged();
    }

    /// <summary>Full plain-text dump (scrollback + screen) for clipboard copy.</summary>
    public string ToPlainText()
    {
        lock (_gate)
        {
            var lines = new List<string>(TotalLines);
            // 环形滚动缓冲:按时间序(最旧 → 最新)遍历。
            if (_scrollbackRing is not null)
            {
                for (var newestIndex = _scrollbackCount - 1; newestIndex >= 0; newestIndex--)
                {
                    lines.Add(LineToText(_scrollbackRing[(_scrollbackHead + _scrollbackCount - 1 - newestIndex) % _scrollbackRing.Length]).TrimEnd('\0').TrimEnd());
                }
            }

            foreach (var row in _rows)
            {
                lines.Add(LineToText(row).TrimEnd('\0').TrimEnd());
            }

            return string.Join('\n', lines).TrimEnd('\n').TrimEnd('\0');
        }
    }

    private static string LineToText(TerminalCell[] row) => LineToText(row.AsSpan());

    private static string LineToText(ReadOnlySpan<TerminalCell> cells)
    {
        var text = new char[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            text[i] = cells[i].Char;
        }

        return new string(text);
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _scrollbackHead = 0;
            _scrollbackCount = 0;
            _renderedLineCache.Clear();
            BumpAllRows();
            foreach (var row in _rows)
            {
                ClearRange(row, 0, _columns - 1);
            }

            _cursorRow = 0;
            _cursorColumn = 0;
            _wrapPending = false;
            MarkDirty();
        }

        RaiseChanged();
    }

    private void EnsureCursorWithinScreen()
    {
        _cursorRow = Math.Clamp(_cursorRow, 0, _rows.Count - 1);
        _cursorColumn = Math.Clamp(_cursorColumn, 0, _columns - 1);
    }

    private void MoveDownWrapping()
    {
        if (_cursorRow >= _regionBottom)
        {
            if (!_inAlternate)
            {
                // T1: 环形滚动缓冲 —— 覆盖最旧槽位 + 前进 head,O(1) 无整表搬移;
                // 被覆盖的最旧行数组就地复用为新的底行(零分配)。
                TerminalCell[]? recycled = null;
                if (_scrollbackRing is not null)
                {
                    var index = (_scrollbackHead + _scrollbackCount) % _scrollbackRing.Length;
                    recycled = _scrollbackRing[index];
                    _scrollbackRing[index] = _primary[0];
                    _scrollbackVersionRing![index] = _rowVersions[0];
                    if (_scrollbackCount < _scrollbackRing.Length)
                    {
                        _scrollbackCount++;
                    }
                    else
                    {
                        _scrollbackHead = (_scrollbackHead + 1) % _scrollbackRing.Length;
                    }
                }

                for (var row = 0; row < _regionBottom; row++)
                {
                    _primary[row] = _primary[row + 1];
                    _primaryVersions[row] = _primaryVersions[row + 1];
                }

                if (recycled is not null)
                {
                    Array.Clear(recycled, 0, recycled.Length);
                    _primary[_regionBottom] = recycled;
                }
                else
                {
                    _primary[_regionBottom] = BlankRow();
                }

                _primaryVersions[_regionBottom] = 0;
                MarkRowsDirty();
            }
            else
            {
                for (var row = 0; row < _regionBottom; row++)
                {
                    _alternate![row] = _alternate[row + 1];
                    _alternateVersions![row] = _alternateVersions![row + 1];
                }

                _alternate![_regionBottom] = BlankRow();
                _alternateVersions![_regionBottom] = 0;
                MarkRowsDirty();
            }
        }
        else
        {
            _cursorRow++;
        }

        _cursorColumn = 0;
        _wrapPending = false;
    }

    private static void ClearRange(TerminalCell[] row, int start, int end)
    {
        for (var column = Math.Max(0, start); column <= Math.Min(end, row.Length - 1); column++)
        {
            row[column] = default;
        }
    }

    private TerminalCell[] BlankRow()
    {
        var row = new TerminalCell[_columns];
        return row;
    }

    private List<TerminalCell[]> NewRows(int count)
    {
        var rows = new List<TerminalCell[]>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(BlankRow());
        }

        return rows;
    }

    /// <summary>Erase-without-raise variant used inside <see cref="EraseInDisplay"/> batches.</summary>
    private void EraseInLineCore(int mode)
    {
        var row = _rows[_cursorRow];
        switch (mode)
        {
            case 0:
                ClearRange(row, _cursorColumn, _columns - 1);
                break;
            case 1:
                ClearRange(row, 0, _cursorColumn);
                break;
            case 2:
                ClearRange(row, 0, _columns - 1);
                break;
        }
    }

    /// <summary>单行内容版本号 +1(行数组被就地改写后调用);同时记入脏行区间(T5)。</summary>
    private void BumpRow(int screenRow)
    {
        if (screenRow >= 0 && screenRow < _rowVersions.Length)
        {
            _rowVersions[screenRow]++;
            if (screenRow < _dirtyStart)
            {
                _dirtyStart = screenRow;
            }

            if (screenRow > _dirtyEnd)
            {
                _dirtyEnd = screenRow;
            }
        }
    }

    /// <summary>当前缓冲全部行版本号 +1(整屏擦除/缓冲切换后调用)。</summary>
    private void BumpAllRows()
    {
        for (var row = 0; row < _rowVersions.Length; row++)
        {
            _rowVersions[row]++;
        }

        MarkRowsDirty();
    }

    /// <summary>整屏行位置/内容都可能变化(滚动位移、resize、缓冲切换):脏行区间取全幅。</summary>
    private void MarkRowsDirty()
    {
        _dirtyStart = 0;
        _dirtyEnd = _rowVersions.Length - 1;
    }

    private static int[] NewVersions(int count) => new int[count];

    private void MarkDirty() => _dirty = true;

    private void RaiseChanged()
    {
        int start;
        int end;
        lock (_gate)
        {
            if (_suspendChanged) return;
            if (!_dirty) return;
            _dirty = false;
            start = _dirtyStart == int.MaxValue ? 0 : _dirtyStart;
            end = _dirtyEnd < start ? start : _dirtyEnd;
            _dirtyStart = int.MaxValue;
            _dirtyEnd = -1;
        }

        // 事件在锁外触发,处理端可安全回读模型。
        RowsChanged?.Invoke(start, end);
        Changed?.Invoke();
    }
}
