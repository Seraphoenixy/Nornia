using Nornia.Desktop.Services;
using Nornia.Desktop.Terminal;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nornia.Desktop.Views.Controls;

/// <summary>
/// Interactive ConPTY surface: renders a <see cref="TerminalSession"/>'s screen cells with themed
/// ANSI colors, forwards keyboard input to the pseudo console, supports drag selection + copy,
/// paste, wheel scrollback and a right-click menu. Lightweight OnRender (per visible line, with
/// color runs) mirrors the code workbench's rendering approach.
/// </summary>
public sealed class TerminalSurfaceControl : FrameworkElement
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(TerminalSession), typeof(TerminalSurfaceControl),
        new FrameworkPropertyMetadata(null, OnSessionChanged));

    public static readonly DependencyProperty TerminalFontSizeProperty = DependencyProperty.Register(
        nameof(TerminalFontSize), typeof(double), typeof(TerminalSurfaceControl),
        new FrameworkPropertyMetadata(13.0, OnTerminalFontSizeChanged));

    /// <summary>终端字体族(terminal.integrated.fontFamily)。为空时回退 App.xaml 的
    /// MonoFontFamily 令牌,测量与渲染在两个入口(<see cref="UpdateCellMetrics"/> / OnRender)都会
    /// 读取当前值,切换字体会立即重排会话的列宽。</summary>
    public static readonly DependencyProperty TerminalFontFamilyProperty = DependencyProperty.Register(
        nameof(TerminalFontFamily), typeof(string), typeof(TerminalSurfaceControl),
        new FrameworkPropertyMetadata(null, OnTerminalFontFamilyChanged));
    /// <summary>渲染合并:多次 RequestRender 只调度一次 InvalidateVisual(同帧内去重,渲染率
    /// 受 WPF 帧率天然限制 ≤60fps)。不再用固定节流定时器——那只会给每次回显/提示符重绘
    /// 白白增加 60ms 延迟(卡顿感),而 InvalidateVisual 本身就已经按帧合并批量输出。</summary>
    private volatile bool _renderPending;
    private readonly DispatcherTimer _resizeDebouncer;
    private int _scrollOffset;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private int _lastColumns = -1;
    private int _lastRows = -1;
    private int _pendingColumns;
    private int _pendingRows;
    private bool _hasFocus;
    private (int Row, int Column)? _selectionAnchor;
    private (int Row, int Column)? _selectionEnd;

    // ===== dirty-row 渲染缓存(xterm.js 思路:行版本未变 → 回放缓存 DrawingVisual,
    // 只重排真正变化的行;旧实现每帧全量重整形所有可见行) =====

    /// <summary>Visual 为 null 表示"已知空行"(不缓存空绘制,跳过回放)。</summary>
    private sealed record RowRenderCache(int Version, int TypefaceEpoch, int ThemeEpoch, int LayoutEpoch, DrawingVisual? Visual);

    /// <summary>滚动偏移/控件尺寸变化会使同一行索引的 y 坐标改变 ⇒ 纪元 +1 全量失效。</summary>
    private int _layoutEpoch;

    /// <summary>行索引(底为 0)→ 该行上次渲染的缓存。键空间受可视窗口约束,超限时整体清空。</summary>
    private readonly Dictionary<int, RowRenderCache> _rowRenderCache = new();
    private int _typefaceEpoch;
    private int _themeEpoch;
    private Typeface? _typeface;
    private Brush? _backgroundBrush;
    private Brush? _foregroundBrush;
    private Brush? _cursorBrush;
    private Brush? _selectionBrush;
    /// <summary>ANSI 颜色 → 冻结画刷缓存(旧实现每行每颜色 run 每帧 new SolidColorBrush)。</summary>
    private readonly Dictionary<int, SolidColorBrush> _colorBrushCache = new();
    private const int MaxColorBrushCache = 4096;
    private const int MaxRowRenderCache = 2048;

    public TerminalSurfaceControl()
    {
        Focusable = true;
        ClipToBounds = true;
        IsHitTestVisible = true;
        _resizeDebouncer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _resizeDebouncer.Tick += (_, _) =>
        {
            _resizeDebouncer.Stop();
            if (Session is not null && _pendingColumns > 0 && _pendingRows > 0)
            {
                Session.Resize(_pendingColumns, _pendingRows);
            }
        };
        SizeChanged += (_, _) =>
        {
            // 尺寸变化改变可视行数 ⇒ 同一行索引的 y 映射变化,行缓存布局纪元失效。
            _layoutEpoch++;
            UpdateCellMetrics();
            QueueResize();
            RequestRender();
        };
        MouseDown += OnSurfaceMouseDown;
        MouseMove += OnSurfaceMouseMove;
        MouseUp += OnSurfaceMouseUp;
        MouseWheel += OnSurfaceMouseWheel;
        GotKeyboardFocus += (_, _) => { _hasFocus = true; RequestRender(); };
        LostKeyboardFocus += (_, _) => { _hasFocus = false; RequestRender(); };
        Loaded += (_, _) => ThemeEvents.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeEvents.ThemeChanged -= OnThemeChanged;
    }

    public TerminalSession? Session
    {
        get => (TerminalSession?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public double TerminalFontSize
    {
        get => (double)GetValue(TerminalFontSizeProperty);
        set => SetValue(TerminalFontSizeProperty, value);
    }

    public string? TerminalFontFamily
    {
        get => (string?)GetValue(TerminalFontFamilyProperty);
        set => SetValue(TerminalFontFamilyProperty, value);
    }

    private static void OnTerminalFontSizeChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var control = (TerminalSurfaceControl)source;
        control.InvalidateTypeface();
        control.UpdateCellMetrics();
        control.QueueResize();
        control.RequestRender();
    }

    private static void OnTerminalFontFamilyChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var control = (TerminalSurfaceControl)source;
        control.InvalidateTypeface();
        control.UpdateCellMetrics();
        control.QueueResize();
        control.RequestRender();
    }

    /// <summary>字体身份变化:纪元 +1(行缓存按纪元失效),Typeface 重建延迟到下次测量/渲染。</summary>
    private void InvalidateTypeface()
    {
        _typefaceEpoch++;
        _typeface = null;
    }

    private static void OnSessionChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var control = (TerminalSurfaceControl)source;
        control._scrollOffset = 0;
        control._selectionAnchor = null;
        control._selectionEnd = null;
        control._lastColumns = -1;
        control._lastRows = -1;
        control._layoutEpoch++;
        control.ClearRowRenderCache();
        // TabControl recycles one surface instance across selected-tab switches, so the Session
        // dependency property rebinds with an OldValue. Redirected (fallback) sessions have a null
        // Screen — guard both sides to avoid subscribing/unsubscribing on null.
        if (args.OldValue is TerminalSession oldSession && oldSession.Screen is { } oldScreen)
        {
            oldScreen.Changed -= control.RequestRender;
        }

        if (args.NewValue is TerminalSession newSession && newSession.Screen is { } screen)
        {
            screen.Changed += control.RequestRender;
            control.QueueResize();
            control.RequestRender();
        }
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        // 非宿主线程的广播归组回宿主 Dispatcher(见 CodeDocumentView.OnThemeChanged):
        // 纪元与刷子缓存字段的写也必须落在宿主线程,RequestRender 依赖同线程状态。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnThemeChanged(sender, theme));
            return;
        }

        // 主题纪元 +1:行缓存按纪元失效,主题刷延迟重新解析。
        _themeEpoch++;
        _backgroundBrush = null;
        _foregroundBrush = null;
        _cursorBrush = null;
        _selectionBrush = null;
        RequestRender();
    }

    private void ClearRowRenderCache()
    {
        _rowRenderCache.Clear();
    }

    /// <summary>请求重绘:pump 线程与 UI 线程都会调用。调度一次 InvalidateVisual(同帧合并),
    /// 小增量(打字回显/提示符重绘)与批量输出同路径,不引入额外的固定节流延迟。</summary>
    private void RequestRender()
    {
        if (_renderPending)
        {
            return;
        }

        _renderPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _renderPending = false;
            InvalidateVisual();
        }));
    }

    // ===== metrics + resize propagation =====

    private void UpdateCellMetrics()
    {
        var typeface = new Typeface(MonoFont(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var sample = new FormattedText(
            "M", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, TerminalFontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _cellWidth = Math.Max(4, sample.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Max(8, sample.Height);
    }

    private void QueueResize()
    {
        if (Session?.Screen is not { } screen || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var columns = Math.Clamp((int)(ActualWidth / _cellWidth), 20, 500);
        var rows = Math.Clamp((int)(ActualHeight / _cellHeight), 5, 200);
        if (columns != _lastColumns || rows != _lastRows)
        {
            _lastColumns = columns;
            _lastRows = rows;
            _pendingColumns = columns;
            _pendingRows = rows;
            _resizeDebouncer.Stop();
            _resizeDebouncer.Start();
        }
    }

    private FontFamily MonoFont()
    {
        if (!string.IsNullOrWhiteSpace(TerminalFontFamily))
        {
            return new FontFamily(TerminalFontFamily);
        }

        return (Application.Current?.TryFindResource("MonoFontFamily") as FontFamily) ?? new FontFamily("Consolas");
    }

    // ===== rendering =====

    protected override void OnRender(DrawingContext drawingContext)
    {
        var screen = Session?.Screen;
        if (screen is null)
        {
            return;
        }

        drawingContext.DrawRectangle(EnsureBackground(), null, new Rect(0, 0, ActualWidth, ActualHeight));

        var visibleRows = Math.Max(0, (int)(ActualHeight / _cellHeight));
        var topIndex = _scrollOffset + visibleRows - 1;

        for (var visualRow = 0; visualRow < visibleRows; visualRow++)
        {
            var lineIndex = topIndex - visualRow;
            if (lineIndex < 0)
            {
                continue;
            }

            // T2: 行视觉内容与 y 坐标解耦 —— 缓存回放与重排都经平移变换定位,滚动只改
            // y 映射,不再整行缓存失效(旧实现滚轮每格 _layoutEpoch++ 全屏重排)。
            var y = visualRow * _cellHeight;
            var version = screen.GetLineVersion(lineIndex);
            if (_rowRenderCache.TryGetValue(lineIndex, out var cache) &&
                cache.Version == version &&
                cache.TypefaceEpoch == _typefaceEpoch &&
                cache.ThemeEpoch == _themeEpoch &&
                cache.LayoutEpoch == _layoutEpoch)
            {
                // 行内容未变:回放缓存绘制(无 GetLine 克隆、无文本整形、无画刷分配)。
                if (cache.Visual is not null)
                {
                    drawingContext.PushTransform(new TranslateTransform(0, y));
                    try
                    {
                        drawingContext.DrawDrawing(cache.Visual.Drawing);
                    }
                    finally
                    {
                        drawingContext.Pop();
                    }
                }

                continue;
            }

            var line = screen.GetLine(lineIndex);
            if (line is null)
            {
                continue;
            }

            var text = screen.GetRenderedLine(lineIndex) ?? string.Empty;
            var visual = RenderLine(line, text);
            if (_rowRenderCache.Count >= MaxRowRenderCache)
            {
                _rowRenderCache.Clear();
            }

            _rowRenderCache[lineIndex] = new RowRenderCache(version, _typefaceEpoch, _themeEpoch, _layoutEpoch, visual);
            if (visual is not null)
            {
                drawingContext.PushTransform(new TranslateTransform(0, y));
                try
                {
                    drawingContext.DrawDrawing(visual.Drawing);
                }
                finally
                {
                    drawingContext.Pop();
                }
            }
        }

        // Cursor block when focused + visible.
        if (_hasFocus && screen.CursorVisible && _scrollOffset == 0)
        {
            var (cursorRow, cursorColumn) = screen.Cursor;
            // The screen can have more rows than the viewport while layout is settling.  Render
            // the cursor in the same bottom-aligned coordinate system as the text rows.
            var visualCursorRow = cursorRow - Math.Max(0, screen.Rows - visibleRows);
            var y = visualCursorRow * _cellHeight;
            if (visualCursorRow >= 0 && y < ActualHeight)
            {
                drawingContext.DrawRectangle(EnsureCursor(), null, new Rect(cursorColumn * _cellWidth, y, _cellWidth, _cellHeight));
            }
        }

        DrawSelection(drawingContext, screen, visibleRows);
    }

    /// <summary>把一行渲染进独立 DrawingVisual 并缓存(内容画在 y=0,位置由调用方的
    /// 平移变换定位 → 滚动无需失效缓存,T2)。背景 run + 单段 FormattedText;前景色按
    /// 同色 run 合并为一次 <c>SetForegroundBrush(start,count)</c>(旧实现逐格一次)。</summary>
    private DrawingVisual? RenderLine(TerminalCell[] line, string text)
    {
        var typeface = EnsureTypeface();
        var foreground = EnsureForeground();
        var hasBackground = false;
        var hasText = false;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Background runs first (per-cell)。
            var runStart = 0;
            for (var column = 1; column <= line.Length; column++)
            {
                var bg = column < line.Length ? line[column].Background : 0;
                var prev = column > 0 ? line[column - 1].Background : 0;
                if (column == line.Length || bg != prev)
                {
                    if (prev != 0)
                    {
                        hasBackground = true;
                        context.DrawRectangle(ColorBrush(prev), null,
                            new Rect(runStart * _cellWidth, 0, (column - runStart) * _cellWidth, _cellHeight));
                    }

                    runStart = column;
                }
            }

            if (text.Length > 0)
            {
                hasText = true;
                var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, TerminalFontSize, foreground, dpi);
                // 前景色按 run 合并:同色连续段一次 SetForegroundBrush(start,count)。
                var colorRunStart = 0;
                var colorRun = 0;
                var inColorRun = false;
                for (var column = 0; column <= line.Length; column++)
                {
                    var color = column < line.Length ? line[column].Foreground : 0;
                    if (inColorRun && color != colorRun)
                    {
                        formatted.SetForegroundBrush(ColorBrush(colorRun), colorRunStart, column - colorRunStart);
                        inColorRun = false;
                    }

                    if (!inColorRun && color != 0)
                    {
                        colorRun = color;
                        colorRunStart = column;
                        inColorRun = true;
                    }
                }

                context.DrawText(formatted, new Point(0, 0));
            }
        }

        // 空行返回 null:缓存"已知空"状态,避免每帧 GetLine + 字符串构造。
        return hasBackground || hasText ? visual : null;
    }

    private Typeface EnsureTypeface()
    {
        _typeface ??= new Typeface(MonoFont(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return _typeface;
    }

    // 主题刷缓存在字段里(旧实现每帧 TryFindResource);主题变更时置空重新解析。
    // 资源刷本身已冻结,直接共享引用安全。

    private Brush EnsureBackground() =>
        _backgroundBrush ??= Brush("TerminalDefaultBackgroundBrush") ?? Brushes.Black;

    private Brush EnsureForeground() =>
        _foregroundBrush ??= Brush("TerminalDefaultForegroundBrush") ?? Brushes.Gray;

    private Brush EnsureCursor() =>
        _cursorBrush ??= Brush("TerminalCursorBrush") ?? Brushes.White;

    private Brush EnsureSelection() =>
        _selectionBrush ??= Brush("TextSelectionBrush") ?? Brushes.DodgerBlue;

    /// <summary>ANSI ARGB 颜色 → 冻结画刷(缓存命中时零分配)。</summary>
    private SolidColorBrush ColorBrush(int argb)
    {
        if (_colorBrushCache.TryGetValue(argb, out var cached))
        {
            return cached;
        }

        if (_colorBrushCache.Count >= MaxColorBrushCache)
        {
            _colorBrushCache.Clear();
        }

        var brush = new SolidColorBrush(ColorFromArgbInt(argb));
        brush.Freeze();
        _colorBrushCache[argb] = brush;
        return brush;
    }

    private void DrawSelection(DrawingContext drawingContext, TerminalScreen screen, int visibleRows)
    {
        if (_selectionAnchor is not { } anchor || _selectionEnd is not { } end)
        {
            return;
        }

        var minIndex = Math.Min(anchor.Row, end.Row);
        var maxIndex = Math.Max(anchor.Row, end.Row);
        var selectionBrush = EnsureSelection();

        var topIndex = _scrollOffset + visibleRows - 1;
        var bottomIndex = _scrollOffset;
        // Resolve which endpoint is visually on top (larger lineIndex)
        var anchorIsTop = anchor.Row > end.Row || (anchor.Row == end.Row && anchor.Column >= end.Column);
        var topColumn = anchorIsTop ? anchor.Column : end.Column;
        var bottomColumn = anchorIsTop ? end.Column : anchor.Column;

        for (var lineIndex = minIndex; lineIndex <= maxIndex; lineIndex++)
        {
            if (lineIndex < bottomIndex || lineIndex > topIndex)
            {
                continue;
            }

            var cells = screen.GetLine(lineIndex);
            if (cells is null)
            {
                continue;
            }

            var visualRow = topIndex - lineIndex;
            if (visualRow < 0 || visualRow >= visibleRows)
            {
                continue;
            }

            var y = visualRow * _cellHeight;
            if (minIndex == maxIndex)
            {
                var start = Math.Min(topColumn, bottomColumn);
                var endCol = Math.Min(Math.Max(topColumn, bottomColumn) + 1, cells.Length);
                if (endCol <= start) continue;
                drawingContext.DrawRectangle(selectionBrush, null, new Rect(start * _cellWidth, y, (endCol - start) * _cellWidth, _cellHeight));
            }
            else if (lineIndex == maxIndex)
            {
                var start = Math.Clamp(topColumn, 0, cells.Length);
                if (start < cells.Length)
                {
                    drawingContext.DrawRectangle(selectionBrush, null, new Rect(start * _cellWidth, y, (cells.Length - start) * _cellWidth, _cellHeight));
                }
            }
            else if (lineIndex == minIndex)
            {
                var endCol = Math.Clamp(bottomColumn + 1, 0, cells.Length);
                if (endCol > 0)
                {
                    drawingContext.DrawRectangle(selectionBrush, null, new Rect(0, y, endCol * _cellWidth, _cellHeight));
                }
            }
            else
            {
                drawingContext.DrawRectangle(selectionBrush, null, new Rect(0, y, cells.Length * _cellWidth, _cellHeight));
            }
        }
    }

    // ===== input =====

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var session = Session;
        if (session is null)
        {
            return;
        }

        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.Enter:
                session.WriteTextAsync("\r");
                e.Handled = true;
                return;
            case Key.Back:
                session.WriteTextAsync("\x7f");
                e.Handled = true;
                return;
            case Key.Tab:
                session.WriteTextAsync(shift ? "\x1b[Z" : "\t");
                e.Handled = true;
                return;
            case Key.Escape:
                session.WriteTextAsync("\x1b");
                e.Handled = true;
                return;
            case Key.Up:
                session.WriteTextAsync("\x1b[A");
                e.Handled = true;
                return;
            case Key.Down:
                session.WriteTextAsync("\x1b[B");
                e.Handled = true;
                return;
            case Key.Right:
                session.WriteTextAsync("\x1b[C");
                e.Handled = true;
                return;
            case Key.Left:
                session.WriteTextAsync("\x1b[D");
                e.Handled = true;
                return;
            case Key.Home:
                session.WriteTextAsync("\x1b[H");
                e.Handled = true;
                return;
            case Key.End:
                session.WriteTextAsync("\x1b[F");
                e.Handled = true;
                return;
            case Key.PageUp:
                session.WriteTextAsync("\x1b[5~");
                e.Handled = true;
                return;
            case Key.PageDown:
                session.WriteTextAsync("\x1b[6~");
                e.Handled = true;
                return;
            case Key.Delete:
                session.WriteTextAsync("\x1b[3~");
                e.Handled = true;
                return;
            case Key.C when control:
                if (CopySelection())
                {
                    ClearSelection();
                }
                else
                {
                    session.WriteTextAsync("\x03");
                }

                e.Handled = true;
                return;
            case Key.V when control:
                PasteClipboard();
                e.Handled = true;
                return;
        }

        if (control && e.Key is >= Key.A and <= Key.Z)
        {
            session.WriteTextAsync(((char)(e.Key - Key.A + 1)).ToString());
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (Session is not null && !e.Handled && !string.IsNullOrEmpty(e.Text))
        {
            Session.WriteTextAsync(e.Text);
            e.Handled = true;
            return;
        }

        base.OnTextInput(e);
    }

    // ===== selection =====

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Right)
        {
            // Match conventional terminal behavior: right-click copies an active selection;
            // otherwise it pastes. Never open a menu over the terminal surface.
            if (CopySelection())
            {
                ClearSelection();
            }
            else
            {
                PasteClipboard();
            }

            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _selectionAnchor = CellAt(e.GetPosition(this));
        _selectionEnd = _selectionAnchor;
        CaptureMouse();
        RequestRender();
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_selectionAnchor is null || !IsMouseCaptured)
        {
            return;
        }

        _selectionEnd = CellAt(e.GetPosition(this));
        RequestRender();
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        ReleaseMouseCapture();
        RequestRender();
    }

    private void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var screen = Session?.Screen;
        if (screen is null)
        {
            return;
        }

        var visibleRows = Math.Max(1, (int)(ActualHeight / _cellHeight));
        var maxOffset = Math.Max(0, screen.TotalLines - visibleRows);
        var newOffset = Math.Clamp(_scrollOffset + (e.Delta > 0 ? 1 : -1), 0, maxOffset);
        if (newOffset != _scrollOffset)
        {
            _scrollOffset = newOffset;
            // T2: 行视觉内容与 y 解耦(平移变换定位)后,滚动不再需要让行缓存失效。
        }

        RequestRender();
        e.Handled = true;
    }

    private (int Row, int Column) CellAt(Point position)
    {
        var screen = Session?.Screen;
        if (screen is not null)
        {
            var visibleRows = Math.Max(1, (int)(ActualHeight / _cellHeight));
            var topIndex = _scrollOffset + visibleRows - 1;
            var visualRow = (int)(position.Y / _cellHeight);
            var lineIndex = topIndex - visualRow;
            var row = Math.Clamp(lineIndex, 0, screen.TotalLines - 1);
            var column = Math.Clamp((int)(position.X / _cellWidth), 0, screen.Columns - 1);
            return (row, column);
        }

        return (0, 0);
    }

    private bool CopySelection()
    {
        if (_selectionAnchor is not { } anchor || _selectionEnd is not { } end)
        {
            return false;
        }

        var text = SelectedText(anchor, end);
        if (text.Length == 0)
        {
            return false;
        }

        System.Windows.Clipboard.SetText(text);
        return true;
    }

    private void ClearSelection()
    {
        _selectionAnchor = null;
        _selectionEnd = null;
        RequestRender();
    }

    private string SelectedText((int Row, int Column) anchor, (int Row, int Column) end)
    {
        var screen = Session?.Screen;
        if (screen is null)
        {
            return string.Empty;
        }

        var minIndex = Math.Min(anchor.Row, end.Row);
        var maxIndex = Math.Max(anchor.Row, end.Row);
        var anchorIsTop = anchor.Row > end.Row || (anchor.Row == end.Row && anchor.Column >= end.Column);
        var topColumn = anchorIsTop ? anchor.Column : end.Column;
        var bottomColumn = anchorIsTop ? end.Column : anchor.Column;
        var builder = new StringBuilder();
        // Visual top (max) to bottom (min)
        for (var lineIndex = maxIndex; lineIndex >= minIndex; lineIndex--)
        {
            if (screen.GetLine(lineIndex) is not { } line)
            {
                continue;
            }

            int start, endCol;
            if (minIndex == maxIndex)
            {
                start = Math.Min(topColumn, bottomColumn);
                endCol = Math.Min(Math.Max(topColumn, bottomColumn) + 1, line.Length);
            }
            else if (lineIndex == maxIndex)
            {
                start = Math.Clamp(topColumn, 0, line.Length);
                endCol = line.Length;
            }
            else if (lineIndex == minIndex)
            {
                start = 0;
                endCol = Math.Clamp(bottomColumn + 1, 0, line.Length);
            }
            else
            {
                start = 0;
                endCol = line.Length;
            }

            if (endCol <= start) continue;

            if (start == 0 && endCol == line.Length)
            {
                // T8: 整行选择复用屏幕行文本缓存(渲染与复制共用),不再逐行物化字符数组。
                var cached = screen.GetRenderedLine(lineIndex)?.TrimEnd() ?? string.Empty;
                if (builder.Length > 0) builder.Append(Environment.NewLine);
                builder.Append(cached);
                continue;
            }

            var chars = new char[endCol - start];
            for (var column = start; column < endCol; column++)
            {
                chars[column - start] = line[column].Char;
            }

            // Trim trailing NULs/spaces per line then join
            var segment = new string(chars).TrimEnd('\0').TrimEnd();
            if (builder.Length > 0) builder.Append(Environment.NewLine);
            builder.Append(segment);
        }

        return builder.ToString().TrimEnd();
    }

    private void PasteClipboard()
    {
        var session = Session;
        if (session is null)
        {
            return;
        }

        var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
        if (!string.IsNullOrEmpty(text))
        {
            session.WriteTextAsync(text);
        }
    }

    // ===== helpers =====

    private static Color ColorFromArgbInt(int argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static Brush? Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;
}
