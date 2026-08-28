using Nornia.Core.Models;
using System.Text;

namespace Nornia.Desktop.Code;

/// <summary>One rendered diff line for the editor-based diff viewers (text + git kind + line
/// numbers). Read-only views map these straight onto AvalonEdit documents.</summary>
public sealed record DiffRenderLine(
    string Text,
    GitDiffLineKind Kind,
    int? OldLineNumber,
    int? NewLineNumber,
    IReadOnlyList<DiffIntralineChange>? IntralineChanges = null,
    bool IsModified = false,
    int HunkIndex = -1)
{
    public bool IsChange => Kind is GitDiffLineKind.Added or GitDiffLineKind.Removed;
}

/// <summary>Options for the refined build path (D2/D3/D6/D7).</summary>
public sealed record DiffRefineOptions(
    /// <summary>Render-line indices hidden by the fold projection. A replacement block whose rows
    /// are all hidden is not refined at all (D7); the skip is never cached, so unfolding re-refines.
    /// </summary>
    IReadOnlyCollection<int>? HiddenOriginalIndexes = null,

    /// <summary>Document-level cumulative refinement budget shared by every block (D3). When it
    /// exhausts, the remaining blocks are skipped and the result reports <c>IntralineOmitted</c>.</summary>
    IntralineDiffBudget? Budget = null,

    /// <summary>Per-block (old text, new text) → refined result cache (D2). Fold toggles re-run the
    /// build over identical block texts and hit the cache instead of re-tokenizing.</summary>
    DiffRefinementCache? RefinementCache = null);

/// <summary>Result of an inline document build (D6): the render lines, the ready-joined full text
/// (consumers skip their own string.Join + full compare), the input change signature (document
/// version used to decide whether a rebuild is needed at all), and the "character-level diff
/// omitted" hint (D3).</summary>
public sealed record DiffInlineBuildResult(
    IReadOnlyList<DiffRenderLine> Lines,
    string FullText,
    long Signature,
    bool IntralineOmitted);

/// <summary>Result of a side-by-side document build (D6).</summary>
public sealed record DiffSideBySideBuildResult(
    IReadOnlyList<DiffRenderLine> Old,
    IReadOnlyList<DiffRenderLine> New,
    string OldFullText,
    string NewFullText,
    long Signature,
    bool IntralineOmitted);

/// <summary>Cached document build keyed by the input change signature (D6). A consumer compares
/// <see cref="Signature"/> with the version it last rendered and skips rebuilding the editor
/// documents when it is unchanged.</summary>
public sealed record DiffBuildResult(long Signature, DiffInlineBuildResult? Inline, DiffSideBySideBuildResult? SideBySide);

/// <summary>Bounded LRU store of built diff documents (D6). Results are shared by reference:
/// treat returned render lines as read-only after the build.</summary>
public sealed class DiffBuildCache
{
    private const int MaxEntries = 32;

    private readonly Dictionary<long, DiffBuildResult> _entries = [];
    private readonly List<long> _insertionOrder = [];

    public int Count => _entries.Count;

    public bool TryGet(long signature, out DiffBuildResult? result)
    {
        if (_entries.TryGetValue(signature, out var entry))
        {
            result = entry;
            return true;
        }

        result = null;
        return false;
    }

    public void Put(DiffBuildResult result)
    {
        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(result.Signature))
        {
            var oldest = _insertionOrder[0];
            _insertionOrder.RemoveAt(0);
            _entries.Remove(oldest);
        }

        _entries[result.Signature] = result;
        if (!_insertionOrder.Contains(result.Signature))
        {
            _insertionOrder.Add(result.Signature);
        }
    }

    /// <summary>Get-or-build in one call: builds via <paramref name="buildFactory"/> only on a miss.</summary>
    public DiffBuildResult GetOrPut(long signature, Func<DiffBuildResult> buildFactory)
    {
        if (TryGet(signature, out var existing) && existing is not null)
        {
            return existing;
        }

        var built = buildFactory();
        Put(built);
        return built;
    }

    public void Invalidate()
    {
        _entries.Clear();
        _insertionOrder.Clear();
    }
}

/// <summary>
/// Pure diff-document builders: convert the parsed git diff model into render lines and change
/// blocks. No WPF types, so line alignment, numbering and change counting stay unit-testable.
/// </summary>
public static class DiffDocumentBuilders
{
    /// <summary>Resolves a file path to its AvalonEdit highlighting definition name (empty string
    /// for plain text / unknown extensions). Shared by the code source view and the diff view so
    /// both render the same file with the same highlighting.</summary>
    public static string ResolveHighlightingName(string? path) =>
        string.IsNullOrEmpty(path) ? string.Empty : CodeFileTypeRegistry.Instance.FromPath(path).HighlightingName;

    /// <summary>Unified (inline) document: one render line per git diff line, 1:1 with the editor.</summary>
    public static IReadOnlyList<DiffRenderLine> BuildInline(IEnumerable<GitDiffLine> lines) =>
        BuildInlineCore(lines, options: null).Lines;

    /// <summary>Refined inline build (D2/D3/D6/D7): same mapping as <see cref="BuildInline"/>, plus
    /// the ready-joined <see cref="DiffInlineBuildResult.FullText"/>, the input change signature
    /// (document version) and the "character-level diff omitted" hint. Options control the
    /// hidden-line skip (D7), the document-level budget (D3) and the per-block result cache (D2).</summary>
    public static DiffInlineBuildResult BuildInlineRefined(IEnumerable<GitDiffLine> lines, DiffRefineOptions? options = null) =>
        BuildInlineCore(lines, options);

    /// <summary>Side-by-side documents: one render line per aligned row on each side. Rows without
    /// content on a side (adds/removes/none) become empty lines, so the line-number columns stay
    /// row-aligned and scrolling stays 1:1.</summary>
    public static (IReadOnlyList<DiffRenderLine> Old, IReadOnlyList<DiffRenderLine> New) BuildSideBySide(
        IEnumerable<GitSideBySideRow> rows)
    {
        var result = BuildSideBySideCore(rows, options: null);
        return (result.Old, result.New);
    }

    /// <summary>Refined side-by-side build (D2/D3/D6/D7): same rows as <see cref="BuildSideBySide"/>,
    /// plus both full texts, the input change signature and the omitted hint.</summary>
    public static DiffSideBySideBuildResult BuildSideBySideRefined(IEnumerable<GitSideBySideRow> rows, DiffRefineOptions? options = null) =>
        BuildSideBySideCore(rows, options);

    /// <summary>64-bit change signature (document version, D6) of the inline input. A consumer
    /// compares it with the version it last rendered; equal signatures mean the full texts and
    /// the refined result are unchanged, so the rebuild can be skipped.</summary>
    public static long ComputeSignature(IEnumerable<GitDiffLine> lines)
    {
        var hash = TextHashing.Initial;
        var count = 0;
        foreach (var line in lines)
        {
            hash = TextHashing.MixInt(hash, (int)line.Kind);
            hash = TextHashing.MixInt(hash, line.OldLineNumber ?? -1);
            hash = TextHashing.MixInt(hash, line.NewLineNumber ?? -1);
            hash = TextHashing.MixText(hash, line.Text);
            hash = TextHashing.MixByte(hash, 0x1F);
            count++;
        }

        return TextHashing.Finalize(TextHashing.MixInt(hash, count));
    }

    /// <summary>64-bit change signature (document version, D6) of the side-by-side input.</summary>
    public static long ComputeSignature(IEnumerable<GitSideBySideRow> rows)
    {
        var hash = TextHashing.Initial;
        var count = 0;
        foreach (var row in rows)
        {
            hash = TextHashing.MixInt(hash, (int)row.OldKind);
            hash = TextHashing.MixInt(hash, row.OldLineNumber ?? -1);
            hash = TextHashing.MixText(hash, row.OldText);
            hash = TextHashing.MixInt(hash, (int)row.NewKind);
            hash = TextHashing.MixInt(hash, row.NewLineNumber ?? -1);
            hash = TextHashing.MixText(hash, row.NewText);
            hash = TextHashing.MixInt(hash, row.HunkIndex);
            hash = TextHashing.MixByte(hash, 0x1F);
            count++;
        }

        return TextHashing.Finalize(TextHashing.MixInt(hash, count));
    }

    private static DiffInlineBuildResult BuildInlineCore(IEnumerable<GitDiffLine> lines, DiffRefineOptions? options)
    {
        var source = lines as IReadOnlyList<GitDiffLine> ?? lines.ToList();
        var result = new List<DiffRenderLine>(source.Count);
        var text = new StringBuilder();
        var hash = TextHashing.Initial;
        var hunkIndex = -1;
        for (var index = 0; index < source.Count; index++)
        {
            var line = source[index];
            if (line.Kind == GitDiffLineKind.HunkHeader)
            {
                hunkIndex++;
            }

            result.Add(new DiffRenderLine(line.Text, line.Kind, line.OldLineNumber, line.NewLineNumber, HunkIndex: hunkIndex));
            if (index > 0)
            {
                text.Append('\n');
            }

            text.Append(line.Text);
            hash = TextHashing.MixInt(hash, (int)line.Kind);
            hash = TextHashing.MixInt(hash, line.OldLineNumber ?? -1);
            hash = TextHashing.MixInt(hash, line.NewLineNumber ?? -1);
            hash = TextHashing.MixText(hash, line.Text);
            hash = TextHashing.MixByte(hash, 0x1F);
        }

        var array = result.ToArray();
        var intralineOmitted = false;
        ApplyIntralineChanges(array, options, ref intralineOmitted);
        return new DiffInlineBuildResult(array, text.ToString(), TextHashing.Finalize(TextHashing.MixInt(hash, source.Count)), intralineOmitted);
    }

    private static DiffSideBySideBuildResult BuildSideBySideCore(IEnumerable<GitSideBySideRow> rows, DiffRefineOptions? options)
    {
        var source = rows as IReadOnlyList<GitSideBySideRow> ?? rows.ToList();
        var old = new List<DiffRenderLine>(source.Count);
        var next = new List<DiffRenderLine>(source.Count);
        var oldText = new StringBuilder();
        var newText = new StringBuilder();
        var hash = TextHashing.Initial;
        for (var index = 0; index < source.Count; index++)
        {
            var row = source[index];
            var hasOld = row.HasOldContent;
            var hasNew = row.HasNewContent;
            old.Add(new DiffRenderLine(
                hasOld ? row.OldText : string.Empty,
                hasOld ? row.OldKind : GitDiffLineKind.None,
                hasOld ? row.OldLineNumber : null,
                null,
                HunkIndex: row.HunkIndex));
            next.Add(new DiffRenderLine(
                hasNew ? row.NewText : string.Empty,
                hasNew ? row.NewKind : GitDiffLineKind.None,
                null,
                hasNew ? row.NewLineNumber : null,
                HunkIndex: row.HunkIndex));
            if (index > 0)
            {
                oldText.Append('\n');
                newText.Append('\n');
            }

            oldText.Append(hasOld ? row.OldText : string.Empty);
            newText.Append(hasNew ? row.NewText : string.Empty);
            hash = TextHashing.MixInt(hash, (int)row.OldKind);
            hash = TextHashing.MixInt(hash, row.OldLineNumber ?? -1);
            hash = TextHashing.MixText(hash, row.OldText);
            hash = TextHashing.MixInt(hash, (int)row.NewKind);
            hash = TextHashing.MixInt(hash, row.NewLineNumber ?? -1);
            hash = TextHashing.MixText(hash, row.NewText);
            hash = TextHashing.MixInt(hash, row.HunkIndex);
            hash = TextHashing.MixByte(hash, 0x1F);
        }

        var intralineOmitted = false;
        ApplyIntralineChanges(old, next, options, ref intralineOmitted);

        // 改行着色(VS Code modified):同一显示行对 old=Removed / new=Added 视为"修改"而非纯增删,
        // 双端标记后用 DiffModified* 令牌渲染。
        var pairs = Math.Min(old.Count, next.Count);
        for (var i = 0; i < pairs; i++)
        {
            if (old[i].Kind == GitDiffLineKind.Removed && next[i].Kind == GitDiffLineKind.Added)
            {
                old[i] = old[i] with { IsModified = true };
                next[i] = next[i] with { IsModified = true };
            }
        }

        return new DiffSideBySideBuildResult(
            old,
            next,
            oldText.ToString(),
            newText.ToString(),
            TextHashing.Finalize(TextHashing.MixInt(hash, source.Count)),
            intralineOmitted);
    }

    /// <summary>Builds the row-level change mask used by context folding in side-by-side mode.
    /// An empty cell is not necessarily unchanged: it can be the old/new placeholder for a pure
    /// insertion or deletion. The shared mask keeps folding boundaries and placeholder text aligned
    /// with the unified view.</summary>
    internal static IReadOnlyList<DiffRenderLine> BuildSideBySideCollapseSource(
        IReadOnlyList<DiffRenderLine> old,
        IReadOnlyList<DiffRenderLine> next)
    {
        var count = Math.Max(old.Count, next.Count);
        var source = new List<DiffRenderLine>(count);
        for (var index = 0; index < count; index++)
        {
            var oldLine = index < old.Count ? old[index] : null;
            var newLine = index < next.Count ? next[index] : null;
            // ToSideBySideRows adds a both-empty spacer before every hunk. Keep it as a
            // display-only boundary so it cannot merge the previous hunk's context run.
            var isHunkSpacer = oldLine?.Kind == GitDiffLineKind.None
                && newLine?.Kind == GitDiffLineKind.None;
            var kind = isHunkSpacer
                ? GitDiffLineKind.HunkHeader
                : oldLine?.Kind is GitDiffLineKind.HunkHeader or GitDiffLineKind.Notice
                ? oldLine.Kind
                : newLine?.Kind is GitDiffLineKind.HunkHeader or GitDiffLineKind.Notice
                    ? newLine.Kind
                    : oldLine?.IsChange == true || newLine?.IsChange == true
                        ? GitDiffLineKind.Added
                        : GitDiffLineKind.Context;
            source.Add(new DiffRenderLine(string.Empty, kind, null, null, HunkIndex: Math.Max(oldLine?.HunkIndex ?? -1, newLine?.HunkIndex ?? -1)));
        }

        return source;
    }

    /// <summary>Change mask of the side-by-side display rows: one synthetic line per row, marked
    /// as a change when either side is changed. Row-aligned with both side documents (and with
    /// <see cref="BuildSideBySideCollapseSource"/>), so change-block starts resolved against it are
    /// valid display lines for both side editors. Unlike the unified document, a modified pair is a
    /// single side row, so block starts differ from the inline projection's (one fewer per
    /// modified pair before the block). Collapsed-context placeholder rows (Kind=None, no change
    /// on either side) never split a change run because folding only collapses context runs.</summary>
    public static IReadOnlyList<DiffRenderLine> BuildSideBySideChangeMask(
        IReadOnlyList<DiffRenderLine> oldLines,
        IReadOnlyList<DiffRenderLine> newLines)
    {
        var count = Math.Min(oldLines.Count, newLines.Count);
        var mask = new DiffRenderLine[count];
        for (var index = 0; index < count; index++)
        {
            var isChange = oldLines[index].IsChange || newLines[index].IsChange;
            mask[index] = new DiffRenderLine(
                string.Empty,
                isChange ? GitDiffLineKind.Added : GitDiffLineKind.Context,
                null,
                null);
        }

        return mask;
    }

    /// <summary>Number of change blocks: contiguous runs of added/removed lines. 行尾换行符
    /// 提示行(\ No newline at end of file)属于变更区域,不把一个变更拆成两块。</summary>
    public static int CountBlocks(IEnumerable<GitDiffLineKind> kinds)
    {
        var blocks = 0;
        var inBlock = false;
        foreach (var kind in kinds)
        {
            var isChange = kind is GitDiffLineKind.Added or GitDiffLineKind.Removed;
            if (isChange && !inBlock)
            {
                blocks++;
                inBlock = true;
            }
            else if (!isChange && kind != GitDiffLineKind.Notice)
            {
                inBlock = false;
            }
        }

        return blocks;
    }

    /// <summary>Returns the inclusive (start, end) line offsets of the Nth change block (0-based),
    /// or (-1, -1) when the index is out of range. 行尾换行符提示行不切断变更块(末尾
    /// “删行 + 提示 + 增行” 是一个变更,导航与计数按一块处理)。</summary>
    public static (int Start, int End) BlockAtIndex(IReadOnlyList<DiffRenderLine> lines, int blockIndex)
    {
        var currentBlock = -1;
        var inBlock = false;
        var blockStart = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var isChange = lines[i].IsChange;
            var isNotice = lines[i].Kind == GitDiffLineKind.Notice;
            if (isChange && !inBlock)
            {
                inBlock = true;
                blockStart = i;
                currentBlock++;
                if (currentBlock == blockIndex)
                {
                    var end = i;
                    while (end + 1 < lines.Count && (lines[end + 1].IsChange || lines[end + 1].Kind == GitDiffLineKind.Notice))
                    {
                        end++;
                    }

                    return (blockStart, end);
                }
            }
            else if (!isChange && !isNotice)
            {
                inBlock = false;
            }
        }

        return (-1, -1);
    }

    private static void ApplyIntralineChanges(DiffRenderLine[] lines, DiffRefineOptions? options, ref bool intralineOmitted)
    {
        for (var index = 0; index < lines.Length;)
        {
            if (!lines[index].IsChange)
            {
                index++;
                continue;
            }

            var start = index;
            while (index < lines.Length && lines[index].IsChange) index++;
            var removed = new List<int>();
            var added = new List<int>();
            for (var line = start; line < index; line++)
            {
                if (lines[line].Kind == GitDiffLineKind.Removed) removed.Add(line);
                else if (lines[line].Kind == GitDiffLineKind.Added) added.Add(line);
            }
            ApplyReplacementBlock(lines, removed, lines, added, IsRangeHidden(start, index, options), options, ref intralineOmitted);
        }
    }

    /// <summary>True when every row of the block sits inside the fold projection's hidden set
    /// (D7): such a block is not displayed, so it is skipped for refinement — and the skip is
    /// never cached, so unfolding re-refines it.</summary>
    private static bool IsRangeHidden(int start, int end, DiffRefineOptions? options)
    {
        var hidden = options?.HiddenOriginalIndexes;
        if (hidden is null || end <= start)
        {
            return false;
        }

        for (var index = start; index < end; index++)
        {
            if (!hidden.Contains(index))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Refines one contiguous Git replacement as a single text mapping. This mirrors
    /// VS Code's line-range mapping: a removed line can correspond to several added lines (and
    /// vice versa), so line-by-line pairing must not decide whether an intraline change exists.
    /// The resulting block ranges are projected back to the individual rendered lines.
    /// The gate + refinement runs as ONE single tokenize pass per side (D2), is served from the
    /// per-block cache when present (D2), and reports omitted blocks through
    /// <paramref name="intralineOmitted"/> (D3).</summary>
    private static void ApplyReplacementBlock(
        IList<DiffRenderLine> oldLines,
        IReadOnlyList<int> oldIndices,
        IList<DiffRenderLine> newLines,
        IReadOnlyList<int> newIndices,
        bool blockHidden,
        DiffRefineOptions? options,
        ref bool intralineOmitted)
    {
        if (oldIndices.Count == 0 || newIndices.Count == 0)
        {
            return;
        }

        if (blockHidden)
        {
            return;
        }

        // D3: once the shared document budget is exhausted, skip the remaining blocks entirely
        // (no tokenization, no cache writes) and report the hint.
        if (options?.Budget is { IsExhausted: true })
        {
            intralineOmitted = true;
            return;
        }

        var oldBlock = BuildBlockText(oldLines, oldIndices);
        var newBlock = BuildBlockText(newLines, newIndices);

        var cache = options?.RefinementCache;
        if (cache is not null && cache.TryGet(oldBlock.Text, newBlock.Text, out var cached) && cached is not null)
        {
            ApplyRefinedRanges(oldLines, oldIndices, newLines, newIndices, oldBlock, newBlock, cached);
            return;
        }

        // A completely unrelated replacement should remain a whole-line change. A small
        // amount of shared structure is enough to refine a one-to-many replacement; the gate
        // (similarity + anchor) now shares its tokenization with the refinement itself.
        var result = IntralineDiffBuilder.Refine(oldBlock.Text, newBlock.Text, options?.Budget);
        if (result.Omitted)
        {
            // D3: the document-level budget ran out mid/after this block. Report the hint and
            // leave the remaining blocks to be skipped the same way.
            intralineOmitted = true;
        }

        // Budget-dependent outcomes are never cached: a later pass with time to spend must
        // still be able to refine this block.
        if (cache is not null && !result.Omitted)
        {
            cache.Set(oldBlock.Text, newBlock.Text, result);
        }

        ApplyRefinedRanges(oldLines, oldIndices, newLines, newIndices, oldBlock, newBlock, result);
    }

    private static void ApplyRefinedRanges(
        IList<DiffRenderLine> oldLines,
        IReadOnlyList<int> oldIndices,
        IList<DiffRenderLine> newLines,
        IReadOnlyList<int> newIndices,
        (string Text, int[] Starts) oldBlock,
        (string Text, int[] Starts) newBlock,
        IntralineDiffBuilder.RefineResult result)
    {
        if (!result.Refined)
        {
            return;
        }

        ApplyProjectedRanges(oldLines, oldIndices, oldBlock.Starts, result.Old);
        ApplyProjectedRanges(newLines, newIndices, newBlock.Starts, result.New);
    }

    private static (string Text, int[] Starts) BuildBlockText(
        IList<DiffRenderLine> lines,
        IReadOnlyList<int> indices)
    {
        var starts = new int[indices.Count];
        var builder = new StringBuilder();
        for (var index = 0; index < indices.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('\n');
            }

            starts[index] = builder.Length;
            builder.Append(lines[indices[index]].Text);
        }

        return (builder.ToString(), starts);
    }

    private static void ApplyProjectedRanges(
        IList<DiffRenderLine> lines,
        IReadOnlyList<int> indices,
        IReadOnlyList<int> starts,
        IReadOnlyList<DiffIntralineChange> blockRanges)
    {
        for (var index = 0; index < indices.Count; index++)
        {
            var lineIndex = indices[index];
            var lineLength = lines[lineIndex].Text.Length;
            var lineStart = starts[index];
            var lineEnd = lineStart + lineLength;
            var ranges = new List<DiffIntralineChange>();
            foreach (var range in blockRanges)
            {
                var rangeStart = Math.Max(lineStart, range.Start);
                var rangeEnd = Math.Min(lineEnd, range.Start + range.Length);
                if (rangeEnd <= rangeStart)
                {
                    continue;
                }

                var projected = new DiffIntralineChange(rangeStart - lineStart, rangeEnd - rangeStart);
                if (ranges.Count > 0 && projected.Start <= ranges[^1].Start + ranges[^1].Length)
                {
                    var previous = ranges[^1];
                    var mergedEnd = Math.Max(previous.Start + previous.Length, projected.Start + projected.Length);
                    ranges[^1] = new DiffIntralineChange(previous.Start, mergedEnd - previous.Start);
                }
                else
                {
                    ranges.Add(projected);
                }
            }

            lines[lineIndex] = lines[lineIndex] with
            {
                IntralineChanges = ranges.Count == 0 ? null : ranges,
            };
        }
    }

    private static void ApplyIntralineChanges(
        List<DiffRenderLine> old, List<DiffRenderLine> next,
        DiffRefineOptions? options, ref bool intralineOmitted)
    {
        for (var index = 0; index < Math.Min(old.Count, next.Count);)
        {
            if (!old[index].IsChange && !next[index].IsChange)
            {
                index++;
                continue;
            }

            var start = index;
            while (index < Math.Min(old.Count, next.Count)
                   && (old[index].IsChange || next[index].IsChange))
            {
                index++;
            }

            var oldIndices = new List<int>();
            var newIndices = new List<int>();
            for (var row = start; row < index; row++)
            {
                if (old[row].Kind == GitDiffLineKind.Removed) oldIndices.Add(row);
                if (next[row].Kind == GitDiffLineKind.Added) newIndices.Add(row);
            }
            ApplyReplacementBlock(old, oldIndices, next, newIndices, IsRangeHidden(start, index, options), options, ref intralineOmitted);
        }
    }
}
