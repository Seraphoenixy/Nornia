using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Nornia.Core.Models;

namespace Nornia.Desktop.Code;

/// <summary>A changed token range within a rendered diff line.</summary>
public sealed record DiffIntralineChange(int Start, int Length);

/// <summary>FNV-1a 64-bit hashing with a 64-bit finalizer. Used for change signatures (D6) and
/// block-refinement cache keys (D2) where a cheap, stable hash of text pairs is enough.</summary>
internal static class TextHashing
{
    public const long Initial = unchecked((long)14695981039346656037);
    private const long Prime = 1099511628211L;

    public static long MixByte(long hash, byte value)
    {
        hash ^= value;
        return hash * Prime;
    }

    public static long MixInt(long hash, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var i = 0; i < 4; i++) hash = MixByte(hash, bytes[i]);
        return hash;
    }

    public static long MixText(long hash, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            hash ^= (byte)text[i];
            hash *= Prime;
            hash ^= (byte)(text[i] >> 8);
            hash *= Prime;
        }

        return hash;
    }

    /// <summary>Finalizes the running hash (fmix64) so equal inputs map to stable 64-bit values.</summary>
    public static long Finalize(long hash)
    {
        hash = (hash ^ (hash >> 30)) * unchecked((long)0xbf58476d1ce4e5b9);
        hash = (hash ^ (hash >> 27)) * unchecked((long)0x94d049bb133111eb);
        return hash ^ (hash >> 31);
    }
}

/// <summary>Document-level cumulative time budget for character-level refinement (D3). One
/// instance is created per build and passed to every replacement block; the per-block
/// <see cref="IntralineDiffBuilder.TimeBudget"/> (50ms) remains the floor a single block may spend,
/// while the total across all blocks is capped here. When exhausted, the remaining blocks are
/// skipped and the build reports "character-level diff omitted" so the caller can surface it.</summary>
public sealed class IntralineDiffBudget
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public IntralineDiffBudget(TimeSpan total)
    {
        Total = total;
    }

    public TimeSpan Total { get; }

    public TimeSpan Elapsed => _watch.Elapsed;

    public bool IsExhausted => _watch.Elapsed >= Total;
}

/// <summary>Bounded token diff for replacement blocks. It runs only inside Git-provided change
/// runs, so it never changes hunk context or source line-number authority. Tokens are deliberately
/// coarser than characters: identifiers/words and whitespace runs are compared as units while
/// punctuation and symbols retain their own boundaries. Raw ranges are normalized afterward so
/// short adjacent changes become one readable, non-overlapping range.</summary>
public static class IntralineDiffBuilder
{
    public const int MaxComparableCharacters = 16 * 1024;
    public const int MaxEditDistance = 512;

    /// <summary>Below this block similarity (and without a meaningful anchor) a replacement is
    /// left as a whole-line change.</summary>
    public const double MinBlockSimilarity = 0.08;

    public static readonly TimeSpan TimeBudget = TimeSpan.FromMilliseconds(50);
    private const int MaxUnchangedElementsToMerge = 2;

    /// <summary>Telemetry (D2): how many times the text-element tokenizer ran. A single
    /// <see cref="Refine"/> pass must add at most two (one per side); the pre-D2 path added up to
    /// six per block. Shared across threads, hence interlocked reads.</summary>
    internal static long TextElementsInvocations;

    /// <summary>Outcome of one single-pass block refinement (D2).</summary>
    public sealed record RefineResult
    {
        /// <summary>True when intraline ranges were produced for the block.</summary>
        public required bool Refined { get; init; }

        /// <summary>True when the block was skipped because the document-level budget (D3) ran
        /// out; unlike a gate rejection, the ranges are unknown and must be retried with time.</summary>
        public bool Omitted { get; init; }

        public IReadOnlyList<DiffIntralineChange> Old { get; init; } = [];
        public IReadOnlyList<DiffIntralineChange> New { get; init; } = [];
    }

    public static (IReadOnlyList<DiffIntralineChange> Old, IReadOnlyList<DiffIntralineChange> New) Build(string oldText, string newText)
    {
        if (oldText.Length + newText.Length > MaxComparableCharacters)
        {
            return ([], []);
        }

        var (old, next, _) = ComputeRanges(oldText, newText, TextElements(oldText), TextElements(newText), documentBudget: null);
        return (old, next);
    }

    /// <summary>Single-pass block refinement (D2): each text is tokenized exactly once and the
    /// element lists are shared across the similarity gate, the anchor gate, and the range
    /// computation — previously a block tokenized up to three times (Similarity, anchor, Build).
    /// The document-level budget (D3) is checked in addition to the per-block
    /// <see cref="TimeBudget"/>, and an exhausted budget reports <see cref="RefineResult.Omitted"/>
    /// so the builder can skip the remaining blocks and surface the hint.</summary>
    public static RefineResult Refine(string oldText, string newText, IntralineDiffBudget? documentBudget = null)
    {
        if (oldText.Length + newText.Length > MaxComparableCharacters)
        {
            return new RefineResult { Refined = false };
        }

        var oldElements = TextElements(oldText);
        var newElements = TextElements(newText);
        if (SimilarityFromElements(oldText, newText, oldElements, newElements) < MinBlockSimilarity
            && !HasMeaningfulAnchorFromElements(oldElements, newElements))
        {
            return new RefineResult { Refined = false };
        }

        if (documentBudget is { IsExhausted: true })
        {
            return new RefineResult { Refined = false, Omitted = true };
        }

        var (old, next, omitted) = ComputeRanges(oldText, newText, oldElements, newElements, documentBudget);
        if (omitted)
        {
            return new RefineResult { Refined = false, Omitted = true };
        }

        return new RefineResult { Refined = true, Old = old, New = next };
    }

    /// <summary>64-bit change signature for a (old, new) text pair — the cache key of
    /// <see cref="DiffRefinementCache"/> (D2).</summary>
    public static long ComputeTextPairHash(string oldText, string newText)
    {
        var hash = TextHashing.MixText(TextHashing.Initial, oldText);
        hash = TextHashing.MixByte(hash, 0x1E);
        hash = TextHashing.MixText(hash, newText);
        return TextHashing.Finalize(hash);
    }

    /// <summary>Scores two replacement lines for monotonic multi-line pairing. The score combines
    /// character boundaries with the safer word/token boundaries so a shared short prefix alone
    /// cannot make unrelated lines look like a replacement pair.</summary>
    internal static double Similarity(string oldText, string newText)
    {
        if (oldText.Length + newText.Length > MaxComparableCharacters)
        {
            return CharacterSimilarity(oldText, newText);
        }

        return SimilarityFromElements(oldText, newText, TextElements(oldText), TextElements(newText));
    }

    /// <summary>Returns true when the two texts share a meaningful word at the beginning or
    /// end. Block length can differ substantially in a one-to-many replacement, making the
    /// normalized similarity score too small even though VS Code can refine a local word change.
    /// Punctuation-only anchors are intentionally ignored.</summary>
    internal static bool HasMeaningfulAnchor(string oldText, string newText)
    {
        if (oldText.Length == 0 || newText.Length == 0)
        {
            return false;
        }

        return HasMeaningfulAnchorFromElements(TextElements(oldText), TextElements(newText));
    }

    private static double CharacterSimilarity(string oldText, string newText)
    {
        if (oldText.Length == 0 || newText.Length == 0)
        {
            return oldText.Length == newText.Length ? 1 : 0;
        }

        var characterPrefix = 0;
        while (characterPrefix < oldText.Length && characterPrefix < newText.Length
               && oldText[characterPrefix] == newText[characterPrefix])
        {
            characterPrefix++;
        }

        var characterSuffix = 0;
        while (characterSuffix < oldText.Length - characterPrefix
               && characterSuffix < newText.Length - characterPrefix
               && oldText[oldText.Length - characterSuffix - 1] == newText[newText.Length - characterSuffix - 1])
        {
            characterSuffix++;
        }

        return (double)(characterPrefix + characterSuffix) / Math.Max(oldText.Length, newText.Length);
    }

    private static double SimilarityFromElements(
        string oldText, string newText,
        IReadOnlyList<TextElement> oldElements, IReadOnlyList<TextElement> newElements)
    {
        var characterScore = CharacterSimilarity(oldText, newText);
        if (oldText.Length + newText.Length > MaxComparableCharacters)
        {
            return characterScore;
        }

        var tokenPrefix = 0;
        while (tokenPrefix < oldElements.Count && tokenPrefix < newElements.Count
               && oldElements[tokenPrefix].Value == newElements[tokenPrefix].Value)
        {
            tokenPrefix++;
        }

        var tokenSuffix = 0;
        while (tokenSuffix < oldElements.Count - tokenPrefix
               && tokenSuffix < newElements.Count - tokenPrefix
               && oldElements[oldElements.Count - tokenSuffix - 1].Value
                   == newElements[newElements.Count - tokenSuffix - 1].Value)
        {
            tokenSuffix++;
        }

        var tokenScore = (double)(tokenPrefix + tokenSuffix) / Math.Max(oldElements.Count, newElements.Count);
        return (characterScore + tokenScore) / 2;
    }

    private static bool HasMeaningfulAnchorFromElements(
        IReadOnlyList<TextElement> oldElements, IReadOnlyList<TextElement> newElements)
    {
        var prefix = 0;
        while (prefix < oldElements.Count && prefix < newElements.Count
               && oldElements[prefix].Value == newElements[prefix].Value)
        {
            if (oldElements[prefix].Kind == TextElementKind.Word && oldElements[prefix].Value.Length >= 2)
            {
                return true;
            }

            prefix++;
        }

        var suffix = 0;
        while (suffix < oldElements.Count - prefix && suffix < newElements.Count - prefix
               && oldElements[oldElements.Count - suffix - 1].Value
                   == newElements[newElements.Count - suffix - 1].Value)
        {
            if (oldElements[oldElements.Count - suffix - 1].Kind == TextElementKind.Word
                && oldElements[oldElements.Count - suffix - 1].Value.Length >= 2)
            {
                return true;
            }

            suffix++;
        }

        return false;
    }

    private static (IReadOnlyList<DiffIntralineChange> Old, IReadOnlyList<DiffIntralineChange> New, bool Omitted)
        ComputeRanges(
            string oldText, string newText,
            IReadOnlyList<TextElement> oldElements, IReadOnlyList<TextElement> newElements,
            IntralineDiffBudget? documentBudget)
    {
        var prefix = 0;
        while (prefix < oldElements.Count && prefix < newElements.Count
               && oldElements[prefix].Value == newElements[prefix].Value)
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldElements.Count - prefix && suffix < newElements.Count - prefix
               && oldElements[oldElements.Count - suffix - 1].Value == newElements[newElements.Count - suffix - 1].Value)
        {
            suffix++;
        }

        var oldCount = oldElements.Count - prefix - suffix;
        var newCount = newElements.Count - prefix - suffix;
        if (oldCount == 0 || newCount == 0)
        {
            return (
                NormalizeRanges(oldElements, prefix, AllChangedFlags(oldCount)),
                NormalizeRanges(newElements, prefix, AllChangedFlags(newCount)),
                false);
        }

        var oldChanged = new bool[oldCount];
        var newChanged = new bool[newCount];
        if (!TryComputeChanges(oldElements, prefix, oldCount, newElements, prefix, newCount, oldChanged, newChanged, documentBudget))
        {
            // The caller interprets an empty result as whole-line highlighting. This bounded
            // fallback keeps pathological generated/minified lines off the UI thread. When the
            // shared document budget (D3) is what stopped the computation, report the block as
            // omitted so the build can surface "character-level diff omitted".
            return ([], [], documentBudget is { IsExhausted: true });
        }

        return (
            NormalizeRanges(oldElements, prefix, oldChanged),
            NormalizeRanges(newElements, prefix, newChanged),
            false);
    }

    private static bool TryComputeChanges(
        IReadOnlyList<TextElement> oldElements, int oldStart, int oldCount,
        IReadOnlyList<TextElement> newElements, int newStart, int newCount,
        bool[] oldChanged, bool[] newChanged,
        IntralineDiffBudget? documentBudget)
    {
        // Like VS Code, use an exact dynamic-programming path for short replacements. Myers is
        // retained for longer text so the work and memory remain bounded for generated files.
        return oldCount + newCount < 500
            ? TryDynamicProgramming(oldElements, oldStart, oldCount, newElements, newStart, newCount, oldChanged, newChanged, documentBudget)
            : TryMyers(oldElements, oldStart, oldCount, newElements, newStart, newCount, oldChanged, newChanged, documentBudget);
    }

    private static bool TryDynamicProgramming(
        IReadOnlyList<TextElement> oldElements, int oldStart, int oldCount,
        IReadOnlyList<TextElement> newElements, int newStart, int newCount,
        bool[] oldChanged, bool[] newChanged,
        IntralineDiffBudget? documentBudget)
    {
        var watch = Stopwatch.StartNew();
        var columns = newCount + 1;
        var table = new int[(oldCount + 1) * columns];
        try
        {
            for (var oldIndex = oldCount - 1; oldIndex >= 0; oldIndex--)
            {
                for (var newIndex = newCount - 1; newIndex >= 0; newIndex--)
                {
                    if (oldElements[oldStart + oldIndex].Value == newElements[newStart + newIndex].Value)
                    {
                        table[oldIndex * columns + newIndex] = table[(oldIndex + 1) * columns + newIndex + 1] + 1;
                    }
                    else
                    {
                        table[oldIndex * columns + newIndex] = Math.Max(
                            table[(oldIndex + 1) * columns + newIndex],
                            table[oldIndex * columns + newIndex + 1]);
                    }
                }

                // D3: the per-block 50ms floor still applies, and the document-level cumulative
                // budget shared by every block is checked as well.
                if (watch.Elapsed > TimeBudget || (documentBudget?.IsExhausted == true))
                {
                    return false;
                }
            }

            var oldCursor = 0;
            var newCursor = 0;
            while (oldCursor < oldCount && newCursor < newCount)
            {
                var oldValue = oldElements[oldStart + oldCursor].Value;
                var newValue = newElements[newStart + newCursor].Value;
                if (oldValue == newValue)
                {
                    oldCursor++;
                    newCursor++;
                    continue;
                }

                var deleteScore = table[(oldCursor + 1) * columns + newCursor];
                var insertScore = table[oldCursor * columns + newCursor + 1];
                if (deleteScore >= insertScore)
                {
                    oldChanged[oldCursor++] = true;
                }
                else
                {
                    newChanged[newCursor++] = true;
                }
            }

            while (oldCursor < oldCount) oldChanged[oldCursor++] = true;
            while (newCursor < newCount) newChanged[newCursor++] = true;
            return true;
        }
        finally
        {
            // The table is deliberately small (at most 499 sequence elements in this path).
            // Clear it before releasing the reference so a failed computation cannot retain data.
            Array.Clear(table);
        }
    }

    private static bool TryMyers(
        IReadOnlyList<TextElement> oldElements, int oldStart, int oldCount,
        IReadOnlyList<TextElement> newElements, int newStart, int newCount,
        bool[] oldChanged, bool[] newChanged,
        IntralineDiffBudget? documentBudget)
    {
        var watch = Stopwatch.StartNew();
        var max = oldCount + newCount;
        var distanceLimit = Math.Min(max, MaxEditDistance);
        var width = max * 2 + 3;
        var pool = ArrayPool<int>.Shared;
        var frontier = pool.Rent(width);
        Array.Fill(frontier, -1, 0, width);
        var trace = new List<int[]>(Math.Min(distanceLimit + 1, 64));
        var offset = max + 1;
        frontier[offset + 1] = 0;

        try
        {
            var foundDistance = -1;
            for (var distance = 0; distance <= distanceLimit; distance++)
            {
                // D3: per-block floor plus the shared document-level cumulative budget.
                if (watch.Elapsed > TimeBudget || (documentBudget?.IsExhausted == true)) return false;
                var snapshot = pool.Rent(width);
                Array.Copy(frontier, snapshot, width);
                trace.Add(snapshot);

                for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
                {
                    var index = offset + diagonal;
                    var x = diagonal == -distance || diagonal != distance && frontier[index - 1] < frontier[index + 1]
                        ? frontier[index + 1]
                        : frontier[index - 1] + 1;
                    var y = x - diagonal;
                    while (x < oldCount && y < newCount
                           && oldElements[oldStart + x].Value == newElements[newStart + y].Value)
                    {
                        x++;
                        y++;
                    }

                    frontier[index] = x;
                    if (x >= oldCount && y >= newCount)
                    {
                        foundDistance = distance;
                        break;
                    }
                }

                if (foundDistance >= 0) break;
            }

            if (foundDistance < 0) return false;
            var oldIndex = oldCount;
            var newIndex = newCount;
            for (var distance = foundDistance; distance > 0; distance--)
            {
                var previous = trace[distance];
                var diagonal = oldIndex - newIndex;
                var previousDiagonal = diagonal == -distance || diagonal != distance && previous[offset + diagonal - 1] < previous[offset + diagonal + 1]
                    ? diagonal + 1
                    : diagonal - 1;
                var previousOld = previous[offset + previousDiagonal];
                var previousNew = previousOld - previousDiagonal;
                while (oldIndex > previousOld && newIndex > previousNew)
                {
                    oldIndex--;
                    newIndex--;
                }

                if (oldIndex == previousOld)
                {
                    newChanged[--newIndex] = true;
                }
                else
                {
                    oldChanged[--oldIndex] = true;
                }
            }

            return true;
        }
        finally
        {
            pool.Return(frontier, clearArray: false);
            foreach (var snapshot in trace) pool.Return(snapshot, clearArray: false);
        }
    }

    private static List<TextElement> TextElements(string text)
    {
        Interlocked.Increment(ref TextElementsInvocations);
        var result = new List<TextElement>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var start = enumerator.ElementIndex;
            var value = enumerator.GetTextElement();
            var kind = Classify(value);
            if (kind != TextElementKind.Other && result.Count > 0 && result[^1].Kind == kind)
            {
                var previous = result[^1];
                result[^1] = previous with
                {
                    Length = start + value.Length - previous.Offset,
                    Value = previous.Value + value,
                };
            }
            else
            {
                result.Add(new TextElement(start, value.Length, value, kind));
            }
        }

        return result;
    }

    private static TextElementKind Classify(string value)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(value, 0);
        if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation)
        {
            return TextElementKind.Word;
        }

        return category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            ? TextElementKind.Whitespace
            : TextElementKind.Other;
    }

    private static bool[] AllChangedFlags(int count) => count <= 0 ? [] : Enumerable.Repeat(true, count).ToArray();

    private static IReadOnlyList<DiffIntralineChange> NormalizeRanges(
        IReadOnlyList<TextElement> elements, int elementStart, IReadOnlyList<bool> flags)
    {
        if (flags.Count == 0)
        {
            return [];
        }

        var rawRanges = ToElementRanges(elementStart, flags);
        var mergedRanges = new List<ElementRange>(rawRanges.Count);
        foreach (var current in rawRanges)
        {
            if (mergedRanges.Count == 0)
            {
                mergedRanges.Add(current);
                continue;
            }

            var previous = mergedRanges[^1];
            var unchangedElements = current.Start - previous.End;
            if (unchangedElements <= MaxUnchangedElementsToMerge)
            {
                mergedRanges[^1] = new ElementRange(previous.Start, current.End);
            }
            else
            {
                mergedRanges.Add(current);
            }
        }

        var ranges = new List<DiffIntralineChange>(mergedRanges.Count);
        foreach (var range in mergedRanges)
        {
            // A formatting-only difference should not create a tiny floating highlight. If it
            // touches content, it has already been merged into that content range above.
            if (IsWhitespaceOnly(elements, range))
            {
                continue;
            }

            var first = elements[range.Start];
            var last = elements[range.End - 1];
            var start = Math.Max(0, first.Offset);
            var length = Math.Max(0, last.End - start);
            if (length > 0)
            {
                ranges.Add(new DiffIntralineChange(start, length));
            }
        }

        return ranges;
    }

    private static List<ElementRange> ToElementRanges(int elementStart, IReadOnlyList<bool> flags)
    {
        var ranges = new List<ElementRange>();
        for (var index = 0; index < flags.Count;)
        {
            if (!flags[index++]) continue;
            var start = index - 1;
            while (index < flags.Count && flags[index]) index++;
            ranges.Add(new ElementRange(elementStart + start, elementStart + index));
        }

        return ranges;
    }

    private static bool IsWhitespaceOnly(IReadOnlyList<TextElement> elements, ElementRange range)
    {
        for (var index = range.Start; index < range.End; index++)
        {
            if (elements[index].Kind != TextElementKind.Whitespace)
            {
                return false;
            }
        }

        return true;
    }

    private enum TextElementKind
    {
        Word,
        Whitespace,
        Other,
    }

    private sealed record TextElement(int Offset, int Length, string Value, TextElementKind Kind)
    {
        public int End => Offset + Length;
    }

    private readonly record struct ElementRange(int Start, int End);
}

/// <summary>Per-block refinement result cache (D2): maps the (old text, new text) hash pair to the
/// refined result, so a fold toggle — which re-runs the full build over identical block texts —
/// does not re-tokenize and re-diff every block. A key hit is verified against the stored texts
/// so a hash collision can never return a foreign result. Skips caused by hidden lines (D7) are
/// never cached; call <see cref="Invalidate"/> — or <see cref="SetProjectionSignature"/> with a
/// new signature — when the fold projection changes.</summary>
public sealed class DiffRefinementCache
{
    private const int MaxEntries = 1024;

    private readonly Dictionary<BlockKey, RefineEntry> _entries = [];
    private readonly List<BlockKey> _insertionOrder = [];
    private int _projectionSignature;
    private bool _projectionSignatureSet;

    /// <summary>Number of cached block results.</summary>
    public int Count => _entries.Count;

    /// <summary>Clears the cache when the fold projection signature changes (fold state toggled,
    /// lines hidden/unhidden); the same signature is a no-op, and the FIRST call only records the
    /// baseline (the projection existed before any cache entries were meant to survive).</summary>
    public void SetProjectionSignature(int signature)
    {
        if (_projectionSignatureSet && signature == _projectionSignature)
        {
            return;
        }

        _projectionSignature = signature;
        if (_projectionSignatureSet)
        {
            Invalidate();
        }

        _projectionSignatureSet = true;
    }

    public void Invalidate()
    {
        _entries.Clear();
        _insertionOrder.Clear();
    }

    public bool TryGet(string oldText, string newText, out IntralineDiffBuilder.RefineResult? result)
    {
        if (_entries.TryGetValue(KeyFor(oldText, newText), out var entry)
            && entry.OldText == oldText && entry.NewText == newText)
        {
            result = entry.Result;
            return true;
        }

        result = null;
        return false;
    }

    public void Set(string oldText, string newText, IntralineDiffBuilder.RefineResult result)
    {
        var key = KeyFor(oldText, newText);
        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
        {
            var oldest = _insertionOrder[0];
            _insertionOrder.RemoveAt(0);
            _entries.Remove(oldest);
        }

        _entries[key] = new RefineEntry(oldText, newText, result);
        if (!_insertionOrder.Contains(key))
        {
            _insertionOrder.Add(key);
        }
    }

    private static BlockKey KeyFor(string oldText, string newText) =>
        new(IntralineDiffBuilder.ComputeTextPairHash(oldText, newText), oldText.Length, newText.Length);

    private readonly record struct BlockKey(long Hash, int OldLength, int NewLength);

    private sealed record RefineEntry(string OldText, string NewText, IntralineDiffBuilder.RefineResult Result);
}

/// <summary>A non-destructive display projection. Context collapses never alter the parsed Git
/// model; navigation resolves the original index and expands the owning region first.</summary>
public sealed record DiffDisplayLine(DiffRenderLine? Source, int OriginalIndex, int HiddenLineCount = 0)
{
    public bool IsCollapsedContext => HiddenLineCount > 0;
    public string Text => IsCollapsedContext ? $"  … 展开 {HiddenLineCount} 行未更改内容 …" : Source?.Text ?? string.Empty;

    /// <summary>Materializes this projected row for an editor document. Side-by-side panes pass
    /// their own source list while sharing the same projection, so a collapsed prompt keeps the
    /// same hidden count and text on both sides.</summary>
    public DiffRenderLine ToRenderLine(IReadOnlyList<DiffRenderLine>? source = null, bool hideHunkHeader = false)
    {
        if (IsCollapsedContext)
        {
            return new DiffRenderLine(Text, GitDiffLineKind.None, null, null,
                HiddenLineCount: HiddenLineCount);
        }

        var result = source is null ? Source! : source[OriginalIndex];
        return hideHunkHeader && result.Kind == GitDiffLineKind.HunkHeader
            ? result with { Text = string.Empty }
            : result;
    }
}

public static class DiffContextProjection
{
    /// <summary>Context-run projection with VS Code-style fold thresholds (D7). A run that touches
    /// a document edge is foldable once it can keep <paramref name="contextLines"/> on the change
    /// side and still hide a line (length ≥ context+1); a run between two changes needs context on
    /// both sides (length ≥ 2·context+1). Hunk headers never count as context and do not split a
    /// run when no change lies between them. Callers may retain them as metadata rows when the
    /// surface needs them; side-by-side rendering omits its empty metadata rows. The display shape
    /// is unchanged:
    /// <paramref name="contextLines"/> on each side (clamped so at least one line stays hidden)
    /// with a single collapsed placeholder between them. Expanded runs are never folded.</summary>
    public static IReadOnlyList<DiffDisplayLine> Build(
        IReadOnlyList<DiffRenderLine> lines,
        ISet<int>? expandedContextStarts = null,
        int contextLines = 3,
        bool includeHunkHeaders = true)
    {
        if (contextLines < 0)
        {
            contextLines = 0;
        }

        // A unified diff can split one unchanged region into two context runs at a hunk header.
        // Only use the hunk-aware pass when there are real changes to anchor the logical run. The
        // no-change case intentionally keeps the old projection semantics for synthetic side rows
        // and metadata-only test documents.
        var hasChange = false;
        var hasHunkHeader = false;
        for (var index = 0; index < lines.Count; index++)
        {
            hasChange |= lines[index].IsChange;
            hasHunkHeader |= lines[index].Kind == GitDiffLineKind.HunkHeader;
            if (hasChange && hasHunkHeader)
            {
                return BuildAcrossHunkHeaders(lines, expandedContextStarts, contextLines, includeHunkHeaders);
            }
        }

        var display = new List<DiffDisplayLine>(lines.Count);
        for (var index = 0; index < lines.Count;)
        {
            if (lines[index].IsChange || lines[index].Kind is GitDiffLineKind.HunkHeader or GitDiffLineKind.Notice)
            {
                display.Add(new DiffDisplayLine(lines[index], index));
                index++;
                continue;
            }

            var start = index;
            while (index < lines.Count && !lines[index].IsChange && lines[index].Kind is not (GitDiffLineKind.HunkHeader or GitDiffLineKind.Notice)) index++;
            var count = index - start;
            var edge = Math.Min(contextLines, count / 2);
            var hidden = count - edge * 2;
            var atEdge = start == 0 || index == lines.Count;
            var minimumFoldable = atEdge ? contextLines + 1 : 2 * contextLines + 1;
            // 只有"确实藏得住行"(hidden > 0)才折叠:临界长度(如边缘 4 行)没有可藏内容时
            // 保持展开,与 VS Code 折叠阈值的可见语义一致。
            if (count >= minimumFoldable && hidden > 0
                && expandedContextStarts?.Contains(start) != true
                && expandedContextStarts?.Contains(start + edge) != true)
            {
                for (var line = start; line < start + edge; line++) display.Add(new DiffDisplayLine(lines[line], line));
                // The folded run is represented by exactly one display row. It has no source row:
                // the hidden count is display metadata and ToRenderLine materializes the prompt
                // only once for each editor surface.
                display.Add(new DiffDisplayLine(null, start + edge, hidden));
                for (var line = index - edge; line < index; line++) display.Add(new DiffDisplayLine(lines[line], line));
            }
            else
            {
                for (var line = start; line < index; line++) display.Add(new DiffDisplayLine(lines[line], line));
            }
        }

        return display;
    }

    private static IReadOnlyList<DiffDisplayLine> BuildAcrossHunkHeaders(
        IReadOnlyList<DiffRenderLine> lines,
        ISet<int>? expandedContextStarts,
        int contextLines,
        bool includeHunkHeaders)
    {
        var display = new List<DiffDisplayLine>(lines.Count);
        for (var index = 0; index < lines.Count;)
        {
            if (lines[index].IsChange || lines[index].Kind == GitDiffLineKind.Notice)
            {
                display.Add(new DiffDisplayLine(lines[index], index));
                index++;
                continue;
            }

            var start = index;
            while (index < lines.Count
                && !lines[index].IsChange
                && lines[index].Kind != GitDiffLineKind.Notice)
            {
                index++;
            }

            AddLogicalContextRun(display, lines, start, index, expandedContextStarts, contextLines, includeHunkHeaders);
        }

        return display;
    }

    private static void AddLogicalContextRun(
        List<DiffDisplayLine> display,
        IReadOnlyList<DiffRenderLine> lines,
        int start,
        int end,
        ISet<int>? expandedContextStarts,
        int contextLines,
        bool includeHunkHeaders)
    {
        var contextCount = 0;
        for (var index = start; index < end; index++)
        {
            if (lines[index].Kind != GitDiffLineKind.HunkHeader)
            {
                contextCount++;
            }
        }

        if (contextCount == 0)
        {
            AddSourceRange(display, lines, start, end, includeHunkHeaders);
            return;
        }

        var edge = Math.Min(contextLines, contextCount / 2);
        var hidden = contextCount - edge * 2;
        var hasChangeBefore = start > 0 && lines[start - 1].IsChange;
        var hasChangeAfter = end < lines.Count && lines[end].IsChange;
        var atEdge = !hasChangeBefore || !hasChangeAfter;
        var minimumFoldable = atEdge ? contextLines + 1 : 2 * contextLines + 1;

        var firstHiddenIndex = -1;
        var contextOrdinal = 0;
        for (var index = start; index < end; index++)
        {
            if (lines[index].Kind != GitDiffLineKind.HunkHeader)
            {
                if (contextOrdinal == edge)
                {
                    firstHiddenIndex = index;
                    break;
                }

                contextOrdinal++;
            }
        }

        var shouldFold = contextCount >= minimumFoldable
            && hidden > 0
            && firstHiddenIndex >= 0
            && expandedContextStarts?.Contains(firstHiddenIndex) != true;

        if (!shouldFold)
        {
            AddSourceRange(display, lines, start, end, includeHunkHeaders);
            return;
        }

        var insertedPrompt = false;
        contextOrdinal = 0;
        for (var index = start; index < end; index++)
        {
            // Hunk headers are metadata, not source context. Keep them in the projection for
            // sticky-header/navigation logic when requested. Side-by-side panes omit these rows
            // because their two materialized cells are both empty and would create phantom lines.
            if (lines[index].Kind == GitDiffLineKind.HunkHeader)
            {
                if (includeHunkHeaders)
                {
                    display.Add(new DiffDisplayLine(lines[index], index));
                }

                continue;
            }

            if (contextOrdinal < edge || contextOrdinal >= contextCount - edge)
            {
                display.Add(new DiffDisplayLine(lines[index], index));
            }
            else if (!insertedPrompt)
            {
                // The prompt owns the first hidden source index. Reusing this stable anchor lets
                // DiffDisplayMap expand the complete logical run, including context from both
                // sides of an intervening hunk header, on the next rebuild.
                display.Add(new DiffDisplayLine(null, firstHiddenIndex, hidden));
                insertedPrompt = true;
            }

            contextOrdinal++;
        }
    }

    private static void AddSourceRange(
        List<DiffDisplayLine> display,
        IReadOnlyList<DiffRenderLine> lines,
        int start,
        int end,
        bool includeHunkHeaders)
    {
        for (var index = start; index < end; index++)
        {
            if (!includeHunkHeaders && lines[index].Kind == GitDiffLineKind.HunkHeader)
            {
                continue;
            }

            display.Add(new DiffDisplayLine(lines[index], index));
        }
    }
}

/// <summary>Explicit bidirectional projection shared by inline and side-by-side surfaces. Display
/// rows never derive correspondence from two collection lengths.</summary>
public sealed class DiffDisplayMap
{
    private readonly IReadOnlyList<DiffRenderLine> _source;
    private readonly bool _includeHunkHeaders;
    private readonly HashSet<int> _expandedContextStarts = [];
    private Dictionary<int, int> _originalToDisplay = [];

    public DiffDisplayMap(IReadOnlyList<DiffRenderLine> source, bool collapseContext = true, bool includeHunkHeaders = true)
    {
        _source = source;
        _includeHunkHeaders = includeHunkHeaders;
        CollapseContext = collapseContext;
        Rebuild();
    }

    public bool CollapseContext { get; private set; }
    public IReadOnlyList<DiffDisplayLine> Lines { get; private set; } = [];

    public int DisplayIndexFromOriginal(int originalIndex) =>
        _originalToDisplay.TryGetValue(originalIndex, out var display) ? display : -1;

    public int OriginalIndexFromDisplay(int displayIndex) =>
        displayIndex >= 0 && displayIndex < Lines.Count ? Lines[displayIndex].OriginalIndex : -1;

    public bool ExpandAtDisplayIndex(int displayIndex)
    {
        if (displayIndex < 0 || displayIndex >= Lines.Count || !Lines[displayIndex].IsCollapsedContext) return false;
        _expandedContextStarts.Add(Lines[displayIndex].OriginalIndex);
        Rebuild();
        return true;
    }

    public int EnsureVisible(int originalIndex)
    {
        var display = DisplayIndexFromOriginal(originalIndex);
        if (display >= 0) return display;
        var placeholder = Lines.FirstOrDefault(line => line.IsCollapsedContext
            && originalIndex >= line.OriginalIndex
            && originalIndex < line.OriginalIndex + line.HiddenLineCount);
        if (placeholder is not null)
        {
            _expandedContextStarts.Add(placeholder.OriginalIndex);
            Rebuild();
        }

        return DisplayIndexFromOriginal(originalIndex);
    }

    public void SetCollapseContext(bool value)
    {
        // Turning the global projection off is a new folding session. Do not retain per-prompt
        // expansion anchors, otherwise turning it back on leaves the document unexpectedly fully
        // expanded and makes the setting appear to have no effect.
        if (!value)
        {
            _expandedContextStarts.Clear();
        }

        CollapseContext = value;
        Rebuild();
    }

    private void Rebuild()
    {
        Lines = CollapseContext
            ? DiffContextProjection.Build(_source, _expandedContextStarts, includeHunkHeaders: _includeHunkHeaders)
            : _source.Select((line, index) => new DiffDisplayLine(line, index)).ToArray();
        _originalToDisplay = new Dictionary<int, int>();
        for (var index = 0; index < Lines.Count; index++)
        {
            if (!Lines[index].IsCollapsedContext) _originalToDisplay[Lines[index].OriginalIndex] = index;
        }
    }
}

/// <summary>Precomputed hunk header index (D5). A one-time pass over the line list produces
/// sorted start arrays; scroll/overview/sticky lookups then resolve "which hunk is at line N"
/// with a binary search in O(log h) instead of rescanning 100k+ lines on every ScrollChanged.</summary>
public sealed class DiffHunkIndex
{
    private readonly int[] _startDisplayLines;
    private readonly int[] _oldStarts;
    private readonly int[] _oldEnds;
    private readonly int[] _newStarts;
    private readonly int[] _newEnds;

    private DiffHunkIndex(int[] startDisplayLines, int[] oldStarts, int[] oldEnds, int[] newStarts, int[] newEnds)
    {
        _startDisplayLines = startDisplayLines;
        _oldStarts = oldStarts;
        _oldEnds = oldEnds;
        _newStarts = newStarts;
        _newEnds = newEnds;
    }

    /// <summary>Single O(n) pass; the per-scroll cost afterwards is O(log h). When
    /// <paramref name="hunks"/> is supplied, the parsed hunk ranges (old/new start+count) are
    /// attached to the matching header in order; without it only display-line lookups work.</summary>
    public static DiffHunkIndex Build(IReadOnlyList<DiffRenderLine> lines, IReadOnlyList<GitDiffHunk>? hunks = null)
    {
        var starts = new List<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Kind == GitDiffLineKind.HunkHeader)
            {
                starts.Add(index);
            }
        }

        var count = starts.Count;
        var oldStarts = new int[count];
        var oldEnds = new int[count];
        var newStarts = new int[count];
        var newEnds = new int[count];
        for (var hunkIndex = 0; hunkIndex < count; hunkIndex++)
        {
            if (hunks is not null && hunkIndex < hunks.Count)
            {
                var hunk = hunks[hunkIndex];
                oldStarts[hunkIndex] = hunk.OldStart;
                oldEnds[hunkIndex] = hunk.OldStart + hunk.OldCount;
                newStarts[hunkIndex] = hunk.NewStart;
                newEnds[hunkIndex] = hunk.NewStart + hunk.NewCount;
            }
            else
            {
                oldStarts[hunkIndex] = -1;
                oldEnds[hunkIndex] = -1;
                newStarts[hunkIndex] = -1;
                newEnds[hunkIndex] = -1;
            }
        }

        return new DiffHunkIndex(starts.ToArray(), oldStarts, oldEnds, newStarts, newEnds);
    }

    /// <summary>Number of hunks (hunk header lines) in the document.</summary>
    public int Count => _startDisplayLines.Length;

    public int HunkStartDisplayLine(int hunkIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hunkIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hunkIndex, Count - 1);
        return _startDisplayLines[hunkIndex];
    }

    public int OldStart(int hunkIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hunkIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hunkIndex, Count - 1);
        return _oldStarts[hunkIndex];
    }

    public int NewStart(int hunkIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hunkIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hunkIndex, Count - 1);
        return _newStarts[hunkIndex];
    }

    /// <summary>Index of the last hunk whose header is at or before the given display line, or
    /// -1 when the line is before the first hunk. Binary search over the sorted header lines.</summary>
    public int PreviousHunkAtDisplayLine(int displayLine) => LastLessOrEqual(_startDisplayLines, displayLine);

    /// <summary>Index of the first hunk whose header is strictly after the given display line, or
    /// <see cref="Count"/> when none. Binary search over the sorted header lines.</summary>
    public int NextHunkAfterDisplayLine(int displayLine) => FirstGreaterThan(_startDisplayLines, displayLine);

    /// <summary>Hunk whose old-side line range contains the old source line number, or -1 when no
    /// hunk covers it (binary search on the sorted old starts).</summary>
    public int HunkForOldLine(int oldLineNumber) => ContainingHunk(_oldStarts, _oldEnds, oldLineNumber);

    /// <summary>Hunk whose new-side line range contains the new source line number, or -1 when no
    /// hunk covers it (binary search on the sorted new starts).</summary>
    public int HunkForNewLine(int newLineNumber) => ContainingHunk(_newStarts, _newEnds, newLineNumber);

    private static int LastLessOrEqual(int[] sorted, int value)
    {
        var lo = 0;
        var hi = sorted.Length - 1;
        var result = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] <= value)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    private static int FirstGreaterThan(int[] sorted, int value)
    {
        var lo = 0;
        var hi = sorted.Length - 1;
        var result = sorted.Length;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] > value)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return result;
    }

    private static int ContainingHunk(int[] starts, int[] ends, int line)
    {
        if (starts.Length == 0 || starts[0] < 0)
        {
            return -1;
        }

        var index = LastLessOrEqual(starts, line);
        return index >= 0 && line < ends[index] ? index : -1;
    }
}

/// <summary>Full-document overview geometry. Buckets are normalized against the entire diff,
/// independent of the current editor viewport.</summary>
public static class DiffOverviewLayout
{
    public sealed record Bucket(int Index, bool HasAdded, bool HasRemoved, bool HasCurrent, bool HasCollapsed = false);

    public static IReadOnlyList<Bucket> Build(IReadOnlyList<DiffRenderLine> lines, int bucketCount, int currentBlockStart = -1)
    {
        if (lines.Count == 0 || bucketCount <= 0) return [];
        return ScanBuckets(lines, bucketCount, currentBlockStart);
    }

    /// <summary>Overview with a precomputed hunk index (D5): same buckets, and when a hunk index
    /// is supplied the header bucket of the hunk that contains the current change is marked as
    /// current too — resolved by binary search, without any extra line scan.</summary>
    public static IReadOnlyList<Bucket> Build(
        IReadOnlyList<DiffRenderLine> lines,
        int bucketCount,
        int currentBlockStart,
        DiffHunkIndex? hunks)
    {
        if (lines.Count == 0 || bucketCount <= 0) return [];
        var buckets = ScanBuckets(lines, bucketCount, currentBlockStart);
        if (hunks is not null && currentBlockStart >= 0)
        {
            var hunk = hunks.PreviousHunkAtDisplayLine(currentBlockStart);
            if (hunk >= 0)
            {
                var headerBucket = BucketForLine(hunks.HunkStartDisplayLine(hunk), bucketCount, lines.Count);
                buckets[headerBucket] = buckets[headerBucket] with { HasCurrent = true };
            }
        }

        return buckets;
    }

    /// <summary>O(1) bucket index for a display line — the per-scroll overview update never needs
    /// the full line list (D5).</summary>
    public static int BucketForLine(int displayLine, int bucketCount, int lineCount) =>
        lineCount <= 0 || bucketCount <= 0 ? 0 : Math.Clamp(displayLine * bucketCount / lineCount, 0, bucketCount - 1);

    /// <summary>Sticky-header resolution (D5): the display line of the hunk header to pin for a
    /// viewport top — the last hunk whose header is at or before the line, via binary search over
    /// the precomputed index; -1 when the top is above the first hunk.</summary>
    public static int StickyHunkHeaderLine(DiffHunkIndex hunks, int topDisplayLine)
    {
        var hunk = hunks.PreviousHunkAtDisplayLine(topDisplayLine);
        return hunk < 0 ? -1 : hunks.HunkStartDisplayLine(hunk);
    }

    public static int DocumentLineFromY(double y, double height, int lineCount) =>
        height <= 0 || lineCount <= 0 ? 0 : Math.Clamp((int)Math.Floor(y / height * lineCount), 0, lineCount - 1);

    private static Bucket[] ScanBuckets(IReadOnlyList<DiffRenderLine> lines, int bucketCount, int currentBlockStart)
    {
        var buckets = Enumerable.Range(0, bucketCount).Select(index => new Bucket(index, false, false, false)).ToArray();
        for (var index = 0; index < lines.Count; index++)
        {
            var bucket = BucketForLine(index, bucketCount, lines.Count);
            var current = buckets[bucket];
            var line = lines[index];
            buckets[bucket] = current with
            {
                HasAdded = current.HasAdded || line.Kind == GitDiffLineKind.Added,
                HasRemoved = current.HasRemoved || line.Kind == GitDiffLineKind.Removed,
                HasCurrent = current.HasCurrent || index == currentBlockStart,
                // 折叠占位行(VS Code "… N 行已折叠")在概览中形成色带指示。
                HasCollapsed = current.HasCollapsed
                    || (line.Kind == GitDiffLineKind.None && line.Text.Contains('…')),
            };
        }

        return buckets;
    }
}

/// <summary>Scroll-incremental overview state (D5). The per-bucket color flags are computed once
/// from the full line list; every ScrollChanged then re-stamps only HasCurrent in O(buckets) and
/// resolves the current hunk with a binary search, instead of rescanning the whole line list
/// (100k+ lines) per frame.</summary>
public sealed class DiffOverviewState
{
    private readonly IReadOnlyList<DiffOverviewLayout.Bucket> _base;
    private readonly DiffHunkIndex? _hunks;
    private IReadOnlyList<DiffOverviewLayout.Bucket> _current;

    public DiffOverviewState(IReadOnlyList<DiffRenderLine> lines, int bucketCount, DiffHunkIndex? hunks = null)
    {
        LineCount = lines.Count;
        BucketCount = Math.Max(0, bucketCount);
        _hunks = hunks;
        _base = DiffOverviewLayout.Build(lines, BucketCount, currentBlockStart: -1);
        _current = _base;
    }

    public int LineCount { get; }

    public int BucketCount { get; }

    /// <summary>The current snapshot, including the last stamped HasCurrent.</summary>
    public IReadOnlyList<DiffOverviewLayout.Bucket> Buckets => _current;

    /// <summary>Re-stamps HasCurrent for the given display line (the current change block start)
    /// without scanning the line list; -1 clears the stamp.</summary>
    public IReadOnlyList<DiffOverviewLayout.Bucket> WithCurrent(int currentDisplayLine)
    {
        if (LineCount == 0 || BucketCount == 0)
        {
            return _current;
        }

        var bucket = currentDisplayLine >= 0 ? DiffOverviewLayout.BucketForLine(currentDisplayLine, BucketCount, LineCount) : -1;
        var stamped = new DiffOverviewLayout.Bucket[BucketCount];
        for (var index = 0; index < BucketCount; index++)
        {
            stamped[index] = _base[index] with { HasCurrent = index == bucket };
        }

        _current = stamped;
        return _current;
    }

    /// <summary>Index of the hunk containing the given display line (binary search), or -1 when
    /// there is no hunk index or the line is above the first hunk.</summary>
    public int CurrentHunkAtDisplayLine(int displayLine) => _hunks?.PreviousHunkAtDisplayLine(displayLine) ?? -1;
}
