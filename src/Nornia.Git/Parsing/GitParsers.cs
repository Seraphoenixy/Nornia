using Nornia.Core.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Nornia.Git.Parsing;

/// <summary>Parses <c>git status --porcelain=v2 --branch -z</c> output (NUL-terminated records) into a
/// <see cref="GitRepositoryStatus"/>. Pure text parsing so it can be unit-tested without git.
/// Records are consumed as <see cref="ReadOnlySpan{T}"/> slices — no per-record <c>Split</c>
/// allocations (G10); the porcelain v2 <c>hH</c>/<c>hI</c> blob ids (fields 6/7) are captured onto
/// <see cref="GitFileChange.HeadBlobId"/>/<see cref="GitFileChange.IndexBlobId"/> so diff-revision
/// identity needs no extra git processes (G6); and <paramref name="maxEntries"/> caps the change
/// lists, flagging <see cref="GitRepositoryStatus.Truncated"/> when the cap is hit (G2).</summary>
public static class GitStatusParser
{
    /// <summary>VS Code's statusLimit: beyond this many entries the UI degrades instead of keeping
    /// unbounded lists alive (repos with tens of thousands of untracked files).</summary>
    public const int DefaultMaxEntries = 10_000;

    public static GitRepositoryStatus Parse(string output, int maxEntries = DefaultMaxEntries)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return GitRepositoryStatus.NotARepository;
        }

        string? branch = null;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;

        var staged = new List<GitFileChange>(Math.Min(512, maxEntries));
        var unstaged = new List<GitFileChange>(Math.Min(512, maxEntries));
        var truncated = false;

        var text = output.AsSpan();
        var position = 0;
        while (position < text.Length)
        {
            var nul = text[position..].IndexOf('\0');
            var length = nul < 0 ? text.Length - position : nul;
            var record = text.Slice(position, length);
            if (!record.IsEmpty && record[0] == '2')
            {
                // Rename/copy records: the original path is the FOLLOWING NUL record (git -z).
                var originalLength = 0;
                var after = position + length + 1;
                if (after < text.Length)
                {
                    var nextNul = text[after..].IndexOf('\0');
                    originalLength = nextNul < 0 ? text.Length - after : nextNul;
                }

                var originalPath = originalLength > 0 ? text.Slice(after, originalLength).ToString() : null;
                ParseRenameEntry(record, originalPath, staged, unstaged, maxEntries, ref truncated);
                position = after + originalLength + 1;
                continue;
            }

            ParseRecord(record, ref branch, ref upstream, ref ahead, ref behind, staged, unstaged, maxEntries, ref truncated);
            position += length + 1;
        }

        return new GitRepositoryStatus(IsRepository: true, branch, upstream, ahead, behind, staged, unstaged, truncated);
    }

    private static void ParseRecord(
        ReadOnlySpan<char> record,
        ref string? branch,
        ref string? upstream,
        ref int ahead,
        ref int behind,
        List<GitFileChange> staged,
        List<GitFileChange> unstaged,
        int maxEntries,
        ref bool truncated)
    {
        if (record.IsEmpty)
        {
            return;
        }

        switch (record[0])
        {
            case '#':
                ParseHeader(record, ref branch, ref upstream, ref ahead, ref behind);
                break;
            case '1':
                ParseSingleEntry(record, staged, unstaged, maxEntries, ref truncated);
                break;
            case 'u':
                ParseUnmergedEntry(record, staged, unstaged, maxEntries, ref truncated);
                break;
            case '?':
                // Untracked: index Unmodified ('.') + worktree Untracked ('?').
                AddEntry(record[2..].ToString(), ".?", null, null, null, staged, unstaged, maxEntries, ref truncated);
                break;
            case '!':
                // Ignored files are not listed by the UI.
                break;
        }
    }

    private static void ParseSingleEntry(
        ReadOnlySpan<char> record,
        List<GitFileChange> staged,
        List<GitFileChange> unstaged,
        int maxEntries,
        ref bool truncated)
    {
        // "1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>": fields 6/7 are the HEAD/index blob ids.
        var xy = SliceField(record, 1);
        if (xy.Length != 2)
        {
            return;
        }

        var position = 2 + xy.Length + 1; // after '<kind> <XY>'
        for (var i = 0; i < 4; i++) _ = SliceField(record, ref position); // sub, mH, mI, mW
        var headBlob = SliceField(record, ref position); // hH
        var indexBlob = SliceField(record, ref position); // hI
        var path = record[position..];
        if (path.IsEmpty)
        {
            return;
        }

        AddEntry(path.ToString(), xy.ToString(), headBlob.ToStringOrNull(), indexBlob.ToStringOrNull(),
            null, staged, unstaged, maxEntries, ref truncated);
    }

    private static void ParseUnmergedEntry(
        ReadOnlySpan<char> record,
        List<GitFileChange> staged,
        List<GitFileChange> unstaged,
        int maxEntries,
        ref bool truncated)
    {
        // "u <xy> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>"
        var xy = SliceField(record, 1);
        if (xy.Length != 2)
        {
            return;
        }

        var position = 2 + xy.Length + 1;
        for (var i = 0; i < 8; i++) _ = SliceField(record, ref position); // sub, m1..m3, mW, h1..h3
        var path = record[position..];
        if (path.IsEmpty)
        {
            return;
        }

        AddEntry(path.ToString(), xy.ToString(), null, null, null, staged, unstaged, maxEntries, ref truncated);
    }

    private static void ParseRenameEntry(
        ReadOnlySpan<char> record,
        string? originalPath,
        List<GitFileChange> staged,
        List<GitFileChange> unstaged,
        int maxEntries,
        ref bool truncated)
    {
        // "2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>": blobs at fields 6/7.
        var xy = SliceField(record, 1);
        if (xy.Length != 2)
        {
            return;
        }

        var position = 2 + xy.Length + 1;
        for (var i = 0; i < 4; i++) _ = SliceField(record, ref position); // sub, mH, mI, mW
        var headBlob = SliceField(record, ref position); // hH
        var indexBlob = SliceField(record, ref position); // hI
        _ = SliceField(record, ref position); // score
        var path = record[position..];
        if (path.IsEmpty)
        {
            return;
        }

        AddEntry(path.ToString(), xy.ToString(), headBlob.ToStringOrNull(), indexBlob.ToStringOrNull(),
            originalPath, staged, unstaged, maxEntries, ref truncated);
    }

    /// <summary>Returns the n-th space-delimited field of the record (0-based), or empty.</summary>
    private static ReadOnlySpan<char> SliceField(ReadOnlySpan<char> record, int fieldIndex)
    {
        var position = 0;
        for (var i = 0; i < fieldIndex; i++)
        {
            var space = record[position..].IndexOf(' ');
            if (space < 0) return default;
            position += space + 1;
        }

        var next = record[position..].IndexOf(' ');
        return next < 0 ? record[position..] : record.Slice(position, next);
    }

    private static ReadOnlySpan<char> SliceField(ReadOnlySpan<char> record, ref int position)
    {
        var next = record[position..].IndexOf(' ');
        var field = next < 0 ? record[position..] : record.Slice(position, next);
        position = next < 0 ? record.Length : position + next + 1;
        return field;
    }

    private static string? ToStringOrNull(this ReadOnlySpan<char> value) =>
        value.IsEmpty || value.Equals("-".AsSpan(), StringComparison.Ordinal) ? null : value.ToString();

    private static void AddEntry(
        string path,
        string xy,
        string? headBlob,
        string? indexBlob,
        string? originalPath,
        List<GitFileChange> staged,
        List<GitFileChange> unstaged,
        int maxEntries,
        ref bool truncated)
    {
        if (xy.Length < 2)
        {
            return;
        }

        var index = GitChangeStatusExtensions.FromStatusLetter(xy[0]);
        var worktree = GitChangeStatusExtensions.FromStatusLetter(xy[1]);
        var change = new GitFileChange(path, index, worktree, originalPath, headBlob, indexBlob);
        AddChange(change, staged, unstaged, maxEntries, ref truncated);
    }

    private static void AddChange(
        GitFileChange change,
        List<GitFileChange> staged,
        List<GitFileChange> unstaged,
        int maxEntries,
        ref bool truncated)
    {
        if (staged.Count + unstaged.Count >= maxEntries)
        {
            truncated = true;
            return;
        }

        if (change.IsStaged)
        {
            staged.Add(change);
        }

        if (change.WorkTreeStatus != GitChangeStatus.Unmodified || change.IsUntracked || change.IsUnmerged)
        {
            unstaged.Add(change);
        }
    }

    // Kept for callers that previously used the split-based record loop.
    private static void ParseHeader(
        ReadOnlySpan<char> record,
        ref string? branch,
        ref string? upstream,
        ref int ahead,
        ref int behind)
    {
        if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
        {
            branch = record["# branch.head ".Length..].ToString();
        }
        else if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
        {
            upstream = record["# branch.upstream ".Length..].ToString();
        }
        else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
        {
            var ab = record["# branch.ab ".Length..];
            var plus = ab.IndexOf('+');
            if (plus < 0)
            {
                return;
            }

            var afterPlus = ab[(plus + 1)..];
            var minus = afterPlus.IndexOf('-');
            if (minus < 0)
            {
                return;
            }

            if (int.TryParse(afterPlus[..minus], out ahead) &&
                int.TryParse(afterPlus[(minus + 1)..], out behind))
            {
                // ahead/behind parsed.
            }
        }
    }
}

/// <summary>Parses <c>git diff</c>/<c>git show</c> unified output for a single file into
/// <see cref="GitFileDiff"/>. Pure text parsing so it can be unit-tested without git.</summary>
public static partial class GitDiffParser
{
    [GeneratedRegex("^@@ -(?<oldStart>\\d+)(?:,(?<oldCount>\\d+))? \\+(?<newStart>\\d+)(?:,(?<newCount>\\d+))? @@(?<header>.*)$")]
    private static partial Regex HunkHeaderRegex();

    public static GitFileDiff Parse(string output, string path, bool staged = false)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.None);
        var hunks = new List<GitDiffHunk>();
        var isBinary = lines.Any(line =>
            line.StartsWith("Binary files ", StringComparison.Ordinal)
            || line.StartsWith("GIT binary patch", StringComparison.Ordinal));
        var isNewFile = lines.Any(line =>
            line.StartsWith("new file mode", StringComparison.Ordinal)
            || string.Equals(line, "--- /dev/null", StringComparison.Ordinal));

        if (isBinary)
        {
            return new GitFileDiff(path, ExtractOldPath(lines), staged, IsBinary: true, isNewFile, hunks);
        }

        string? oldPath = ExtractOldPath(lines);
        List<GitDiffLine>? currentHunkLines = null;
        var oldLineNumber = 0;
        var newLineNumber = 0;
        var oldStart = 0;
        var newStart = 0;

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '@' && line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (currentHunkLines is not null)
                {
                    hunks.Add(new GitDiffHunk(oldStart, ComputeCount(oldLineNumber, oldStart), newStart, ComputeCount(newLineNumber, newStart), string.Empty, currentHunkLines));
                }

                var match = HunkHeaderRegex().Match(line);
                if (!match.Success)
                {
                    currentHunkLines = null;
                    continue;
                }

                oldStart = int.Parse(match.Groups["oldStart"].Value);
                newStart = int.Parse(match.Groups["newStart"].Value);
                oldLineNumber = oldStart;
                newLineNumber = newStart;
                currentHunkLines = [];
                currentHunkLines.Add(new GitDiffLine(GitDiffLineKind.HunkHeader, null, null, line.TrimEnd()));
                continue;
            }

            if (currentHunkLines is null)
            {
                continue;
            }

            switch (line[0])
            {
                case '+':
                    currentHunkLines.Add(new GitDiffLine(GitDiffLineKind.Added, null, newLineNumber, line[1..]));
                    newLineNumber++;
                    break;
                case '-':
                    currentHunkLines.Add(new GitDiffLine(GitDiffLineKind.Removed, oldLineNumber, null, line[1..]));
                    oldLineNumber++;
                    break;
                case ' ':
                    currentHunkLines.Add(new GitDiffLine(GitDiffLineKind.Context, oldLineNumber, newLineNumber, line[1..]));
                    oldLineNumber++;
                    newLineNumber++;
                    break;
                case '\\':
                    currentHunkLines.Add(new GitDiffLine(GitDiffLineKind.Notice, null, null, line));
                    break;
            }
        }

        if (currentHunkLines is not null)
        {
            hunks.Add(new GitDiffHunk(oldStart, ComputeCount(oldLineNumber, oldStart), newStart, ComputeCount(newLineNumber, newStart), string.Empty, currentHunkLines));
        }

        return new GitFileDiff(path, oldPath, staged, IsBinary: false, isNewFile, hunks);
    }

    private static int ComputeCount(int endLineNumber, int startLineNumber) => endLineNumber - startLineNumber;

    private static string? ExtractOldPath(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal) && !string.Equals(line, "--- /dev/null", StringComparison.Ordinal))
            {
                return Unquote(StripPrefix(line["--- ".Length..], 'a'));
            }
        }

        return null;
    }

    private static string StripPrefix(string path, char prefix)
    {
        if (path.StartsWith($"{prefix}/", StringComparison.Ordinal))
        {
            return path[2..];
        }

        return path;
    }

    private static string Unquote(string path)
    {
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            return path[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return path;
    }
}

/// <summary>Incremental unified-diff state machine (D9). It retains only line counters; consumers
/// decide whether events stay in memory or are written to a spool. Lines can be fed whole
/// (<see cref="Accept"/>, the pre-existing entry point) or as arbitrary chunks
/// (<see cref="AcceptChunk"/>): the stream may split a line anywhere, so the unfinished tail is
/// carried in a reusable char buffer across calls. The per-line <c>line[1..]</c> copy is avoided
/// by working on a span with the one-char prefix offset tracked, and each line text is
/// materialized exactly once, when its event is produced.</summary>
public sealed partial class GitDiffStreamParser(string path, bool staged)
{
    private int _oldLine;
    private int _newLine;
    private bool _inHunk;
    private bool _isBinary;
    private bool _isNewFile;
    private string? _oldPath;
    // 跨块未完成行(D9):只有块边界截断行才持有内容;整行消费后立即置空。
    private string _pendingLine = string.Empty;

    [GeneratedRegex("^@@ -(?<oldStart>\\d+)(?:,(?<oldCount>\\d+))? \\+(?<newStart>\\d+)(?:,(?<newCount>\\d+))? @@(?<header>.*)$")]
    private static partial Regex StreamHunkRegex();

    /// <summary>Feeds one complete line (no newline terminator), as the process line reader
    /// delivers it. Line counters, hunk context, and metadata flags persist across calls exactly
    /// as before; the returned events are already materialized.</summary>
    public IEnumerable<GitDiffEvent> Accept(string line)
    {
        var events = new List<GitDiffEvent>(2);
        ProcessLine(line.AsSpan(), line, events);
        return events;
    }

    /// <summary>Feeds an arbitrary chunk of the git output (D9). The chunk may end mid-line — the
    /// unfinished tail stays in <see cref="_pendingLine"/> until the next chunk completes the line.
    /// A '\r' directly before each '\n' is treated as part of the line terminator. Each line's text
    /// is materialized exactly once, when its event is produced (the pending carry is only held for
    /// chunk-boundary-split lines).</summary>
    public IEnumerable<GitDiffEvent> AcceptChunk(ReadOnlySpan<char> chunk)
    {
        var events = new List<GitDiffEvent>();
        if (chunk.Length == 0)
        {
            return events;
        }

        var text = _pendingLine + chunk.ToString();
        _pendingLine = string.Empty;
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                _pendingLine = text[start..]; // 跨块尾段:迟分配,保留到下一块
                return events;
            }

            var lineEnd = newline;
            if (lineEnd > start && text[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            ProcessLine(text.AsSpan(start, lineEnd - start), null, events);
            start = newline + 1;
        }
    }

    public IEnumerable<GitDiffEvent> AcceptChunk(string chunk) => AcceptChunk(chunk.AsSpan());

    /// <summary>Flushes the partial line left by the last <see cref="AcceptChunk"/> call — the
    /// final line of the stream usually has no trailing newline. Returns no events when nothing
    /// is pending.</summary>
    public IEnumerable<GitDiffEvent> Finish()
    {
        var events = new List<GitDiffEvent>(1);
        if (_pendingLine.Length > 0)
        {
            ProcessLine(_pendingLine, _pendingLine, events);
            _pendingLine = string.Empty;
        }

        return events;
    }

    public GitDiffMetadataEvent Metadata => new(path, _oldPath, staged, _isBinary, _isNewFile);

    /// <summary>Single-line state machine shared by <see cref="Accept"/> and
    /// <see cref="AcceptChunk"/>. When <paramref name="lineString"/> is supplied (whole-line
    /// entry) header events reuse the caller's string without copying; chunk entries materialize
    /// the header text once from the buffer. Content lines always materialize their text exactly
    /// once, sliced from the prefix offset instead of copying <c>line[1..]</c> of a pre-made
    /// string.</summary>
    private void ProcessLine(ReadOnlySpan<char> line, string? lineString, List<GitDiffEvent> events)
    {
        // 统一的换行符语义:行尾的 '\r'(CRLF 行终止符)不是内容。AcceptChunk 在 '\n' 之前
        // 已剥离它;在此再剥离一次,使 Accept 与 Finish 的 flush 对 CRLF 交付的行适用同一条
        // 规则(否则同一份 CRLF 文本经整行/分块两条路径解析会得到不同的事件文本)。
        // 生产路径的 StreamReader.ReadLine 同样把行尾 '\r' 当作终止符,行为一致。
        if (line.Length > 0 && line[^1] == '\r')
        {
            line = line[..^1];
        }
        if (lineString is not null && lineString.Length > 0 && lineString[^1] == '\r')
        {
            lineString = lineString[..^1];
        }

        if (line.StartsWith("Binary files ".AsSpan(), StringComparison.Ordinal) || line.StartsWith("GIT binary patch".AsSpan(), StringComparison.Ordinal))
        {
            _isBinary = true;
            return;
        }

        if (line.StartsWith("new file mode".AsSpan(), StringComparison.Ordinal) || line == "--- /dev/null".AsSpan()) _isNewFile = true;
        if (line.StartsWith("--- ".AsSpan(), StringComparison.Ordinal) && line != "--- /dev/null".AsSpan())
        {
            var candidate = line[4..];
            _oldPath = candidate.StartsWith("a/".AsSpan(), StringComparison.Ordinal) ? candidate[2..].ToString() : candidate.ToString();
        }

        if (line.StartsWith("@@ ".AsSpan(), StringComparison.Ordinal))
        {
            var header = lineString ?? line.ToString();
            var match = StreamHunkRegex().Match(header);
            if (!match.Success) { _inHunk = false; return; }
            _oldLine = int.Parse(match.Groups["oldStart"].Value, CultureInfo.InvariantCulture);
            _newLine = int.Parse(match.Groups["newStart"].Value, CultureInfo.InvariantCulture);
            var oldCount = match.Groups["oldCount"].Success ? int.Parse(match.Groups["oldCount"].Value, CultureInfo.InvariantCulture) : 1;
            var newCount = match.Groups["newCount"].Success ? int.Parse(match.Groups["newCount"].Value, CultureInfo.InvariantCulture) : 1;
            _inHunk = true;
            events.Add(new GitDiffHunkEvent(_oldLine, oldCount, _newLine, newCount, header));
            return;
        }

        if (!_inHunk || line.IsEmpty) return;
        switch (line[0])
        {
            case '+': events.Add(new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Added, null, _newLine++, line[1..].ToString()))); break;
            case '-': events.Add(new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Removed, _oldLine++, null, line[1..].ToString()))); break;
            case ' ': events.Add(new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Context, _oldLine++, _newLine++, line[1..].ToString()))); break;
            case '\\': events.Add(new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Notice, null, null, line.ToString()))); break;
        }
    }
}

/// <summary>Parses <c>git log --pretty=format:...</c> output into commit entries. Fields are separated
/// by U+001F and records by U+001E so multi-line bodies survive the parse. With <c>--shortstat</c>
/// git appends the per-commit stat line after each record separator, so every chunk after the first
/// begins with the previous commit's stats lines before the next record line; those stats lines are
/// extracted separately and attached to the preceding commit. Pure text parsing for unit tests
/// without git.</summary>
public static class GitLogParser
{
    /// <summary>字段 8 为 <c>git log --decorate=full</c> 下的 <c>%D</c> 引用清单,形如
    /// <c>HEAD -&gt; refs/heads/main, refs/remotes/origin/main</c>;字段 9 的 <c>%B</c>
    /// 保留完整提交消息及原始换行,避免 <c>%s</c> 把同一段落中的列表折叠成一行。</summary>
    public const string Format =
        "%H%x1f%h%x1f%s%x1f%an%x1f%ae%x1f%aI%x1f%b%x1f%P%x1f%D%x1f%B%x1e";

    public static IReadOnlyList<GitCommitInfo> Parse(string output)
    {
        var chunks = output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries);
        var chunkStats = new List<string>();
        var commits = new List<GitCommitInfo>();
        var commitChunks = new List<int>();

        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            // --shortstat 时,git 在每条记录的 %x1e 之后追加该提交的统计行,再跟下一条记录:
            // 因此当前块以“上一条提交的统计行”开头(首块除外),纯统计块(末尾)没有记录行。
            var lines = chunks[chunkIndex].Split('\n');
            var recordLineIndex = 0;
            while (recordLineIndex < lines.Length && !lines[recordLineIndex].Contains('\x1f'))
            {
                recordLineIndex++;
            }

            chunkStats.Add(recordLineIndex > 0
                ? string.Join("\n", lines.Take(recordLineIndex))
                : string.Empty);

            if (recordLineIndex >= lines.Length)
            {
                continue;
            }

            // 记录文本从记录行一直延伸到块尾:正文(%b)可能跨多行,不能只取记录行。
            var fields = string.Join("\n", lines[recordLineIndex..]).TrimStart('\r').Split('\x1f');
            if (fields.Length < 6 || string.IsNullOrWhiteSpace(fields[0]))
            {
                continue;
            }

            if (!DateTimeOffset.TryParse(
                    fields[5].Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var authorDate))
            {
                continue;
            }

            commits.Add(new GitCommitInfo(
                fields[0],
                fields[1],
                fields[2],
                fields.Length > 6 ? fields[6] : null,
                fields[3],
                fields[4],
                authorDate,
                fields.Length > 7
                    ? fields[7].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    : null,
                fields.Length > 8 ? ParseRefs(fields[8]) : null,
                Message: fields.Length > 9 ? fields[9].TrimEnd('\r', '\n') : null));
            commitChunks.Add(chunkIndex);
        }

        // 每条提交的统计来自其记录块之后的那一块的头部(末尾纯统计块属于最后一条提交)。
        for (var i = 0; i < commits.Count; i++)
        {
            var statsChunk = commitChunks[i] + 1;
            if (statsChunk >= chunkStats.Count)
            {
                continue;
            }

            var stats = ParseStats(chunkStats[statsChunk]);
            if (stats is not null)
            {
                commits[i] = commits[i] with { Stats = stats };
            }
        }

        return commits;
    }

    /// <summary>解析 <c>--shortstat</c> 统计文本(<c>N files? changed, N insertions?(+),
    /// N deletions?(-)</c>,缺失项记 0),空文本返回 null。</summary>
    private static GitCommitStats? ParseStats(string statsText)
    {
        if (string.IsNullOrWhiteSpace(statsText))
        {
            return null;
        }

        return new GitCommitStats(
            MatchNumber(statsText, @"(\d+)\s+files?\s+changed"),
            MatchNumber(statsText, @"(\d+)\s+insertions?\(\+\)"),
            MatchNumber(statsText, @"(\d+)\s+deletions?\(-\)"));
    }

    private static int MatchNumber(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }

    /// <summary>把 <c>%D</c> 引用清单解析为 <see cref="GitRefInfo"/>:剔除 HEAD 装饰,
    /// 按完整 refname 前缀分类(refs/heads → 本地分支,refs/remotes → 远端分支,
    /// refs/tags → 标签),其余(如 HEAD 孤立项或未知前缀)跳过。旧格式输出(不含字段 8)
    /// 或空清单返回空列表。</summary>
    private static IReadOnlyList<GitRefInfo> ParseRefs(string decoration)
    {
        if (string.IsNullOrWhiteSpace(decoration))
        {
            return [];
        }

        var refs = new List<GitRefInfo>();
        foreach (var token in decoration.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = token.Trim();
            if (name.Length == 0 || name == "HEAD")
            {
                continue;
            }

            // --decorate=full 下 HEAD 指向以 "HEAD -> " 开头、标签以 "tag: " 开头,
            // 剔除后再按前缀分类;HEAD 指向的分支记录为 IsHead(当前分支高亮色)。
            var isHead = false;
            const string HeadArrow = "HEAD -> ";
            if (name.StartsWith(HeadArrow, StringComparison.Ordinal))
            {
                name = name[HeadArrow.Length..].Trim();
                isHead = true;
            }
            else if (name.StartsWith("tag: ", StringComparison.Ordinal))
            {
                name = name["tag: ".Length..].Trim();
            }

            if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                refs.Add(new GitRefInfo(name["refs/heads/".Length..], GitRefKind.LocalBranch, isHead));
            }
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                // 远端符号引用 refs/remotes/*/HEAD 指向远端默认分支,与 VS Code git 扩展
                // 一致跳过(否则每个远端都会多一个 "origin/HEAD" 噪声徽标)。
                var remoteName = name["refs/remotes/".Length..];
                if (remoteName.EndsWith("/HEAD", StringComparison.Ordinal))
                {
                    continue;
                }
                refs.Add(new GitRefInfo(remoteName, GitRefKind.RemoteBranch));
            }
            else if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                refs.Add(new GitRefInfo(name["refs/tags/".Length..], GitRefKind.Tag));
            }
        }

        return refs;
    }
}
