namespace Nornia.Desktop.Code;

/// <summary>
/// Pure geometry for the VS Code-style full-document minimap: maps every document line onto a
/// scaled map strip (each line gets at least one pixel, so large documents scroll inside the
/// strip) and converts map positions back to editor scroll offsets for the draggable viewport.
/// No WPF types, so the mapping stays unit-testable.
/// </summary>
public static class MinimapLayout
{
    public sealed record Map(
        /// <summary>Strip fraction occupied by a single document line (clamped to &gt;= 1px).</summary>
        double LineStripHeight,
        /// <summary>Exact normalized document-line scale used for navigation and viewport geometry.</summary>
        double DocumentLineHeight,
        /// <summary>First document line (1-based index) at the top of the strip window.</summary>
        int FirstVisibleLine,
        /// <summary>How many document lines are representable in the strip at 1px minimum.</summary>
        int VisibleLineCount,
        double ViewportTopInMap,
        double ViewportHeightInMap,
        int EditorLineCount);

    /// <summary>
    /// Computes the full-document map. <paramref name="editorScrollOffset"/> is in document pixels.
    /// <paramref name="mapStripHeight"/> is the minimap strip height in pixels.
    /// </summary>
    public static Map Compute(
        int editorLineCount,
        double lineHeight,
        double editorViewportHeight,
        double mapStripHeight,
        double editorScrollOffset)
    {
        if (editorLineCount <= 0 || lineHeight <= 0 || editorViewportHeight <= 0 || mapStripHeight <= 0)
        {
            return new Map(0, 0, 0, 0, 0, 0, editorLineCount);
        }

        // Whole-document thumbnail: each document line occupies 1/lineCount of the strip, clamped
        // to a one-pixel minimum so a large file scrolls inside the strip instead of collapsing.
        var lineStrip = Math.Max(1.0 / editorLineCount, 1.0 / mapStripHeight);
        var documentLineHeight = 1.0 / editorLineCount;
        // lineStrip is a normalized fraction of the strip, so its reciprocal is
        // the number of minimum-sized document lines that fit in the strip.
        var visibleLineCount = Math.Min(editorLineCount, Math.Max(1, (int)Math.Floor(1.0 / lineStrip)));

        var editorFirstLine = editorScrollOffset / lineHeight;
        var viewportTopInMap = editorFirstLine * documentLineHeight;
        var viewportHeightInMap = (editorViewportHeight / lineHeight) * documentLineHeight;

        return new Map(
            LineStripHeight: lineStrip,
            DocumentLineHeight: documentLineHeight,
            FirstVisibleLine: Math.Max(0, (int)Math.Floor(editorFirstLine)),
            VisibleLineCount: visibleLineCount,
            ViewportTopInMap: viewportTopInMap,
            ViewportHeightInMap: viewportHeightInMap,
            EditorLineCount: editorLineCount);
    }

    /// <summary>Map Y (0..1 relative to the strip) for a 0-based document line index
    /// (1-based callers pass <c>line - 1</c>).</summary>
    public static double MapY(int lineIndex, Map map) => lineIndex * map.DocumentLineHeight;

    /// <summary>Converts a map Y (0..1) into a fractional editor line number for viewport dragging.</summary>
    public static double EditorLineFromMapY(double mapY, Map map) => mapY / map.DocumentLineHeight;

    /// <summary>Editor scroll offset (document pixels) that places a fractional editor line at
    /// the vertical center of the viewport: minimap click/drag navigation shows the target line
    /// in the middle of the screen (VS Code parity). Lines near the document start clamp to 0;
    /// callers additionally clamp against the real scrollable extent (ScrollToVerticalOffset does).</summary>
    public static double ScrollOffsetForLine(double editorLine, double lineHeight, double viewportHeight)
    {
        if (lineHeight <= 0)
        {
            return 0;
        }

        var visibleLines = Math.Max(0, viewportHeight) / lineHeight;
        return Math.Max(0, (editorLine - visibleLines / 2) * lineHeight);
    }
}
