using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Core.Services;
using Nornia.Git.Parsing;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace Nornia.Git;

/// <summary>Git operations over the git CLI via <see cref="IProcessRunner"/>. Every command is
/// prefixed with <c>-C &lt;repositoryPath&gt;</c> so the caller never needs to change the process
/// working directory.</summary>
public sealed partial class GitService(IProcessRunner processRunner) : IGitService
{
    private const string GitExecutable = "git";

    /// <summary>NUL-byte probe window for classifying an untracked worktree file as binary — the
    /// same head size the read-only preview decoder uses (BOM-less files with a NUL in the head
    /// are binary; UTF-16/32 text is caught by its NULs before any BOM logic applies).</summary>
    private const int BinaryProbeLength = 4096;

    /// <summary>Upper bound for a single <c>git status</c> stdout capture. <c>--untracked-files=all</c>
    /// has no entry limit, so a repository with tens of thousands of untracked files can emit tens
    /// of MB; the cap bounds memory and the truncation flag lets the caller degrade instead of
    /// buffering unbounded output.</summary>
    internal const int StatusMaximumOutputBytes = 32 * 1024 * 1024;

    /// <summary>Entry cap for one status parse (VS Code's statusLimit). Beyond it
    /// <see cref="GitRepositoryStatus.Truncated"/> is set and the UI degrades gracefully.</summary>
    public const int StatusMaximumEntries = GitStatusParser.DefaultMaxEntries;

    /// <summary>Disables git's optional index-lock side effects for read-only commands (VS Code sets
    /// the same environment for <c>git status</c>): without it a read pass writes the index stat
    /// cache back to disk, causing I/O and self-triggering the file watcher.</summary>
    internal static readonly IReadOnlyDictionary<string, string> OptionalLocksDisabled =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_OPTIONAL_LOCKS"] = "0" };

    // Per-repository serialization gate (1 concurrent git process per repository path). User write
    // operations and silent watcher-driven status refreshes must never overlap: concurrent
    // processes can hit .git\index.lock or read a half-updated index. The gate serializes every
    // command issued for the same repository, so the two classes of operations are mutually
    // exclusive by construction (a deferred silent refresh simply queues behind the write).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repositoryGates = new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim RepositoryGate(string repositoryPath) =>
        _repositoryGates.GetOrAdd(Path.GetFullPath(repositoryPath), _ => new SemaphoreSlim(1, 1));

    public async Task<GitRepositoryStatus> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            repositoryPath,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"],
            cancellationToken,
            allowFailure: true,
            readOnly: true,
            maximumOutputBytes: StatusMaximumOutputBytes);
        return result.IsSuccess
            ? GitStatusParser.Parse(result.StandardOutput, StatusMaximumEntries)
            : GitRepositoryStatus.NotARepository;
    }

    /// <summary>Creates local Git metadata only. Branch naming remains controlled by the user's
    /// Git configuration; no files are staged and no remote is added.</summary>
    public async Task InitializeRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        await RunGitAsync(repositoryPath, ["init"], cancellationToken);
    }

    public async Task<GitFileDiff?> GetDiffAsync(
        string repositoryPath,
        string path,
        bool staged,
        bool isUntracked = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return await CollectDiffAsync(StreamDiffAsync(repositoryPath, path, staged, cancellationToken: cancellationToken), cancellationToken);
    }

    public async Task<string> GetDiffRevisionAsync(
        string repositoryPath,
        string path,
        bool staged,
        bool isUntracked = false,
        CancellationToken cancellationToken = default) =>
        await GetDiffRevisionWithBlobsAsync(repositoryPath, path, staged, isUntracked, null, null, cancellationToken);

    /// <summary>Diff-revision identity that reuses the porcelain v2 blob ids captured by the status
    /// parser (G6): a staged change needs 0 git processes (HEAD blob + index blob are in the record),
    /// an unstaged change keeps the single worktree <c>hash-object</c>, and untracked the same —
    /// instead of the previous 3 processes (<c>hash-object</c> + <c>rev-parse</c> + <c>ls-files</c>).
    /// When <paramref name="headBlobId"/>/<paramref name="indexBlobId"/> are absent (e.g. record kinds
    /// without them, or callers without a status record) the missing pieces fall back to the
    /// corresponding git query.</summary>
    public async Task<string> GetDiffRevisionWithBlobsAsync(
        string repositoryPath,
        string path,
        bool staged,
        bool isUntracked,
        string? headBlobId,
        string? indexBlobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var worktree = isUntracked || !staged
            ? await ReadWorkingTreeObjectAsync(repositoryPath, path, cancellationToken)
            : "<not-used>";
        if (isUntracked)
        {
            return $"untracked|{worktree}";
        }

        if (staged)
        {
            // Staged identity: HEAD blob + index blob. Both are carried by the status record for
            // ordinary ('1') changes — reused here with zero extra processes.
            var head = headBlobId is { Length: > 0 }
                ? headBlobId
                : await ReadGitObjectAsync(repositoryPath, ["rev-parse", "--verify", $"HEAD:{path}"], cancellationToken);
            var index = indexBlobId is { Length: > 0 }
                ? indexBlobId
                : await ReadGitObjectAsync(repositoryPath, ["ls-files", "-s", "--", path], cancellationToken);
            return $"staged|{head}|{index}";
        }

        // Unstaged identity: index blob (reused from the status record when present) + worktree
        // hash — the HEAD blob is not part of the identity, so it is never queried here.
        var unstagedIndex = indexBlobId is { Length: > 0 }
            ? indexBlobId
            : await ReadGitObjectAsync(repositoryPath, ["ls-files", "-s", "--", path], cancellationToken);
        return $"worktree|{unstagedIndex}|{worktree}";
    }

    public async IAsyncEnumerable<GitDiffEvent> StreamDiffAsync(
        string repositoryPath,
        string path,
        bool staged,
        bool isUntracked = false,
        int maximumLines = 500_000,
        bool ignoreWhitespaceEndOfLine = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (isUntracked)
        {
            var fullPath = Path.Combine(repositoryPath, path);
            if (!File.Exists(fullPath))
            {
                yield return new GitDiffMetadataEvent(path, null, staged, false, true);
                yield return new GitDiffCompletedEvent(0, false);
                yield break;
            }

            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // NUL probe: fill the head in a loop — a single ReadAsync may return a partial
            // buffer whose zero-filled tail would misclassify a text file as binary.
            var head = new byte[Math.Min(BinaryProbeLength, stream.Length)];
            var filled = 0;
            while (filled < head.Length)
            {
                var read = await stream.ReadAsync(head.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                filled += read;
            }
            var isBinary = head.AsSpan(0, filled).Contains((byte)0);
            yield return new GitDiffMetadataEvent(path, null, staged, isBinary, true);
            if (isBinary) { yield return new GitDiffCompletedEvent(0, false); yield break; }
            stream.Position = 0;
            // The old 4 KB head probe classified the whole file from its first bytes: a legacy
            // GBK file with a long ASCII head was misread as UTF-8 and its later Chinese garbled.
            // Validate the entire content instead (same strategy as the read-only preview decoder).
            var (encoding, bomLength) = await DetectFileEncodingAsync(stream, cancellationToken).ConfigureAwait(false);
            if (bomLength > 0) stream.Position = bomLength;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, 64 * 1024, leaveOpen: false);
            yield return new GitDiffHunkEvent(0, 0, 1, (int)Math.Min(int.MaxValue, stream.Length), "@@ -0,0 +1 @@");
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (++lineNumber > maximumLines) { yield return new GitDiffCompletedEvent(0, true); yield break; }
                yield return new GitDiffLineEvent(new GitDiffLine(GitDiffLineKind.Added, null, lineNumber, line));
            }
            yield return new GitDiffCompletedEvent(0, false);
            yield break;
        }

        var arguments = new List<string> { "-C", repositoryPath, "diff" };
        if (staged) arguments.Add("--cached");
        arguments.Add("--no-ext-diff");
        if (ignoreWhitespaceEndOfLine) arguments.Add("--ignore-space-at-eol");
        arguments.Add("--unified=3");
        arguments.Add("--");
        arguments.Add(path);
        var parser = new GitDiffStreamParser(path, staged);
        ProcessStreamEvent? completion = null;
        var error = new StringBuilder();
        // `git diff` reads the index, so it must not overlap another command's write pass for the
        // same repository (same per-repository gate as RunGitAsync).
        var gate = RepositoryGate(repositoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Diff content lines carry the file's raw bytes; a legacy GBK/ANSI file garbles under
            // the fixed UTF-8 decode, so this goes through the strict-UTF-8→GB18030 fallback.
            await foreach (var item in processRunner.StreamLinesWithTextFallbackAsync(GitExecutable, arguments, maximumLines, OptionalLocksDisabled, cancellationToken).ConfigureAwait(false))
            {
                if (item.IsCompleted) { completion = item; break; }
                if (item.IsError)
                {
                    if (error.Length < 4096) error.AppendLine(item.Text);
                    continue;
                }
                foreach (var parsed in parser.Accept(item.Text ?? string.Empty)) yield return parsed;
            }
        }
        finally
        {
            gate.Release();
        }

        yield return parser.Metadata;
        yield return new GitDiffCompletedEvent(completion?.ExitCode ?? -1, completion?.OutputLimitReached == true, error.Length == 0 ? null : error.ToString());
    }

    /// <summary>Whole-content encoding decision for an untracked worktree file. A UTF-8 BOM is an
    /// authoritative declaration (decode as UTF-8, replacement-tolerant); otherwise the stream is
    /// scanned as strict UTF-8 in 64 KB chunks with carry-over across boundaries, stopping at the
    /// first invalid sequence — legacy GBK content then decodes as GB18030. Memory stays constant:
    /// the file is scanned, never buffered whole.</summary>
    private static async Task<(Encoding Encoding, int BomLength)> DetectFileEncodingAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var bomHead = new byte[3];
        var bomRead = 0;
        while (bomRead < bomHead.Length)
        {
            var read = await stream.ReadAsync(bomHead.AsMemory(bomRead), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            bomRead += read;
        }

        var bomLength = bomRead == 3 ? TextEncodingDetector.Utf8BomLength(bomHead) : 0;
        if (bomLength > 0)
        {
            return (TextEncodingDetector.Utf8Replacement, bomLength);
        }

        stream.Position = 0;
        var isStrictUtf8 = true;
        byte[] carry = [];
        var chunk = new byte[64 * 1024];
        while (isStrictUtf8)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            isStrictUtf8 = TextEncodingDetector.IsStrictUtf8Chunk(chunk.AsSpan(0, read), carry, out carry);
        }

        stream.Position = 0;
        return (isStrictUtf8 ? TextEncodingDetector.Utf8Strict : TextEncodingDetector.Gb18030, 0);
    }

    public async Task<string> GetRawDiffAsync(
        string repositoryPath,
        bool staged,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "diff" };
        if (staged)
        {
            arguments.Add("--cached");
        }

        arguments.AddRange(["--no-ext-diff", "--unified=3"]);
        if (paths is { Count: > 0 })
        {
            arguments.Add("--");
            arguments.AddRange(paths);
        }

        var result = await RunGitAsync(repositoryPath, arguments, cancellationToken: cancellationToken, readOnly: true);
        return result.StandardOutput;
    }

    /// <summary>Like <see cref="GetRawDiffAsync"/> but returns the diff as raw bytes: content
    /// lines carry the file's original encoding, and only byte-exact content can be fed back to
    /// <c>git apply</c> for legacy-encoded files (ApplyHunkAsync).</summary>
    private async Task<byte[]> GetRawDiffBytesAsync(
        string repositoryPath,
        bool staged,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "diff" };
        if (staged)
        {
            arguments.Add("--cached");
        }

        arguments.AddRange(["--no-ext-diff", "--unified=3"]);
        if (paths is { Count: > 0 })
        {
            arguments.Add("--");
            arguments.AddRange(paths);
        }

        var result = await RunGitRawAsync(repositoryPath, arguments, cancellationToken, readOnly: true).ConfigureAwait(false);
        return result.StandardOutput;
    }

    public async Task ApplyHunkAsync(
        string repositoryPath,
        string path,
        bool staged,
        GitDiffHunk hunk,
        GitHunkOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(hunk);

        if (operation == GitHunkOperation.Stage && staged)
        {
            throw new GitOperationException("暂存块只能作用于未暂存 Diff。");
        }

        if (operation == GitHunkOperation.Unstage && !staged)
        {
            throw new GitOperationException("取消暂存块只能作用于已暂存 Diff。");
        }

        if (operation == GitHunkOperation.Restore && staged)
        {
            throw new GitOperationException("还原块只能作用于未暂存 Diff。");
        }

        // Re-read the raw patch immediately before applying. The tab is a review surface and can
        // be stale after an external edit or another Git operation; coordinates and header must
        // still identify the same hunk before anything is mutated. The diff is kept as raw
        // bytes end to end: legacy-encoded (GBK/ANSI) content lines must reach `git apply`
        // byte-identical, so re-encoding them through a text codec here would corrupt the
        // context and the hunk could no longer be located.
        var raw = await GetRawDiffBytesAsync(repositoryPath, staged, [path], cancellationToken).ConfigureAwait(false);
        var patch = SelectHunkPatchBytes(raw, hunk);
        if (patch is null)
        {
            throw new GitOperationException("当前文件的 Diff 已发生变化，无法安全定位该块；请刷新后重试。");
        }

        var patchPath = Path.Combine(Path.GetTempPath(), $"nornia-hunk-{Guid.NewGuid():N}.patch");
        try
        {
            await File.WriteAllBytesAsync(patchPath, patch, cancellationToken).ConfigureAwait(false);
            var arguments = new List<string> { "apply" };
            if (operation is GitHunkOperation.Stage or GitHunkOperation.Unstage)
            {
                arguments.Add("--cached");
            }

            if (operation is GitHunkOperation.Unstage or GitHunkOperation.Restore)
            {
                arguments.Add("--reverse");
            }

            // A partial hunk can be applied while the same file still has other local hunks.
            // On Windows the index/worktree line-ending representation may differ even though
            // the displayed context is identical; ignore only whitespace while locating context.
            arguments.AddRange(["--recount", "--ignore-space-change", "--whitespace=nowarn", patchPath]);
            await RunGitAsync(repositoryPath, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(patchPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Selects one hunk's patch region from a raw (undecoded) unified diff and returns
    /// the original bytes — legacy-encoded (GBK/ANSI) content lines pass through byte-identical
    /// so <c>git apply</c> matches the file's actual bytes. Structure decisions (hunk header,
    /// diff header, index line) are made on the ASCII markers; the hunk header's text comparison
    /// decodes that single line with the same strict-UTF-8→GB18030 strategy the diff view uses,
    /// so a legacy-encoded header still matches the tab's properly decoded header.</summary>
    private static byte[]? SelectHunkPatchBytes(byte[] raw, GitDiffHunk target)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        // Line starts: offset 0 plus the byte after every '\n'. Line k spans
        // [lineStarts[k], nextStart) and its trailing '\n' (when present) is the last byte of
        // that span, so line content never contains a separator.
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        static ReadOnlySpan<byte> Line(byte[] data, List<int> starts, int index)
        {
            var start = starts[index];
            var end = index + 1 < starts.Count ? starts[index + 1] - 1 : data.Length;
            return data[start..end];
        }

        var hunkStart = -1;
        for (var index = 0; index < lineStarts.Count; index++)
        {
            var line = Line(raw, lineStarts, index);
            if (line.Length == 0 || line[0] != (byte)'@')
            {
                continue;
            }

            var headerText = DecodeLineForComparison(line);
            if (!TryParseHunkHeader(headerText, out var oldStart, out var oldCount, out var newStart, out var newCount))
            {
                continue;
            }

            if (oldStart == target.OldStart
                && oldCount == target.OldCount
                && newStart == target.NewStart
                && newCount == target.NewCount
                && (string.IsNullOrEmpty(target.Header) || string.Equals(headerText, target.Header, StringComparison.Ordinal)))
            {
                hunkStart = index;
                break;
            }
        }

        if (hunkStart < 0)
        {
            return null;
        }

        var patchStart = -1;
        for (var index = 0; index < lineStarts.Count; index++)
        {
            var line = Line(raw, lineStarts, index);
            if (line.StartsWith(DiffGitPrefix) || line.StartsWith(DashDashPrefix))
            {
                patchStart = index;
                break;
            }
        }

        if (patchStart < 0 || patchStart > hunkStart)
        {
            return null;
        }

        var end = hunkStart + 1;
        while (end < lineStarts.Count
               && !Line(raw, lineStarts, end).StartsWith(HunkPrefix)
               && !Line(raw, lineStarts, end).StartsWith(DiffGitPrefix))
        {
            end++;
        }

        // A trailing synthetic empty line (the one after Git's final newline) is not a blank
        // context line — real blank context lines start with a space — and keeping it makes
        // --recount reject the selected hunk as having one extra line.
        while (end > hunkStart + 1 && Line(raw, lineStarts, end - 1).IsEmpty)
        {
            end--;
        }

        // The index line contains blob ids for the complete file. It is not valid metadata for
        // a patch that intentionally contains only one hunk: when another hunk is left unstaged,
        // Git can reject the otherwise matching partial patch at its target line. Keep the full
        // diff/path headers and remove only this whole-file identity line.
        using var patch = new MemoryStream();
        for (var index = patchStart; index < end; index++)
        {
            var line = Line(raw, lineStarts, index);
            if (index > patchStart && line.StartsWith(IndexPrefix))
            {
                continue;
            }

            patch.Write(line);
            if (index + 1 < lineStarts.Count)
            {
                patch.WriteByte((byte)'\n'); // the original separator
            }
        }

        if (patch.Length == 0 || patch.GetBuffer()[(int)patch.Length - 1] != (byte)'\n')
        {
            patch.WriteByte((byte)'\n');
        }

        return patch.ToArray();
    }

    /// <summary>Decodes one diff line for text comparison, stripping a trailing CR. Uses the same
    /// strict-UTF-8→GB18030 strategy as the diff view so a legacy-encoded header still matches
    /// the tab's properly decoded header; pure ASCII lines (the common case) decode identically
    /// either way.</summary>
    private static string DecodeLineForComparison(ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        return TextEncodingDetector.IsStrictUtf8(line)
            ? TextEncodingDetector.Utf8Strict.GetString(line)
            : TextEncodingDetector.Gb18030.GetString(line);
    }

    private static bool TryParseHunkHeader(
        string line,
        out int oldStart,
        out int oldCount,
        out int newStart,
        out int newCount)
    {
        var match = HunkHeaderRegex().Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups["oldStart"].Value, out oldStart)
            || !int.TryParse(match.Groups["newStart"].Value, out newStart))
        {
            oldStart = oldCount = newStart = newCount = 0;
            return false;
        }

        oldCount = match.Groups["oldCount"].Success && int.TryParse(match.Groups["oldCount"].Value, out var parsedOldCount)
            ? parsedOldCount
            : 1;
        newCount = match.Groups["newCount"].Success && int.TryParse(match.Groups["newCount"].Value, out var parsedNewCount)
            ? parsedNewCount
            : 1;
        return true;
    }

    public Task StageAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return RunGitAsync(repositoryPath, ["add", "--all"], cancellationToken: cancellationToken);
        }

        return RunPathChunkedAsync(repositoryPath, ["add"], paths, cancellationToken);
    }

    public async Task UnstageAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        // `git restore --staged` restores the index from HEAD. A repository created by `git init`
        // has an unborn HEAD until its first commit, so newly-added files must instead be removed
        // from the index while preserving their worktree copies.
        if (!await HasHeadAsync(repositoryPath, cancellationToken))
        {
            if (paths.Count == 0)
            {
                await RunGitAsync(repositoryPath, ["rm", "--cached", "-r", "--", "."], cancellationToken: cancellationToken);
                return;
            }

            await RunPathChunkedAsync(repositoryPath, ["rm", "--cached"], paths, cancellationToken);
            return;
        }

        if (paths.Count == 0)
        {
            await RunGitAsync(repositoryPath, ["restore", "--staged", "--", "."], cancellationToken: cancellationToken);
            return;
        }

        await RunPathChunkedAsync(repositoryPath, ["restore", "--staged"], paths, cancellationToken);
    }

    /// <summary>Checks whether the current branch has a commit. An unborn branch is a valid Git
    /// repository but cannot supply HEAD as the source for restore/reset operations.</summary>
    private async Task<bool> HasHeadAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", "HEAD"],
            cancellationToken, allowFailure: true, readOnly: true);
        return result.IsSuccess && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    public async Task DiscardAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<string> tracked;
        IReadOnlyCollection<string> untracked;

        if (paths.Count == 0)
        {
            var status = await GetStatusAsync(repositoryPath, cancellationToken);
            if (!status.IsRepository)
            {
                throw new GitOperationException("Not a git repository.");
            }

            tracked = []; // restore everything below
            untracked = status.UnstagedChanges.Where(change => change.IsUntracked).Select(change => change.Path).ToArray();
        }
        else
        {
            var listed = await RunGitAsync(repositoryPath, ["ls-files", "-z", "--", .. paths], cancellationToken, allowFailure: true, readOnly: true);
            var listedSet = listed.IsSuccess
                ? listed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
                : [];
            tracked = paths.Where(path => listedSet.Contains(path)).ToArray();
            untracked = paths.Where(path => !listedSet.Contains(path)).ToArray();
        }

        if (paths.Count == 0 || tracked.Count > 0)
        {
            if (tracked.Count == 0)
            {
                await RunGitAsync(repositoryPath, ["restore", "--staged", "--worktree", "--", "."], cancellationToken: cancellationToken);
            }
            else
            {
                await RunPathChunkedAsync(repositoryPath, ["restore", "--staged", "--worktree"], tracked, cancellationToken);
            }
        }

        foreach (var path in untracked)
        {
            var fullPath = Path.Combine(repositoryPath, path);
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new GitOperationException($"Unable to discard untracked file '{path}': {ex.Message}", ex.Message);
            }
        }
    }

    public async Task DiscardUnstagedAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        // 只还原工作区(不触碰暂存区):暂存内容必须显式"取消暂存"后再丢弃,不能让
        // 未暂存列表的"丢弃"直接抹掉已暂存内容。
        IReadOnlyCollection<string> tracked;
        IReadOnlyCollection<string> untracked;

        if (paths.Count == 0)
        {
            var status = await GetStatusAsync(repositoryPath, cancellationToken);
            if (!status.IsRepository)
            {
                throw new GitOperationException("Not a git repository.");
            }

            tracked = status.UnstagedChanges.Where(change => !change.IsUntracked).Select(change => change.Path).ToArray();
            untracked = status.UnstagedChanges.Where(change => change.IsUntracked).Select(change => change.Path).ToArray();
        }
        else
        {
            var listed = await RunGitAsync(repositoryPath, ["ls-files", "-z", "--", .. paths], cancellationToken, allowFailure: true, readOnly: true);
            var listedSet = listed.IsSuccess
                ? listed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
                : [];
            tracked = paths.Where(path => listedSet.Contains(path)).ToArray();
            untracked = paths.Where(path => !listedSet.Contains(path)).ToArray();
        }

        if (tracked.Count > 0)
        {
            // git restore --worktree: 用暂存区内容还原工作区,已暂存的修改保持不动。
            await RunPathChunkedAsync(repositoryPath, ["restore", "--worktree"], tracked, cancellationToken);
        }

        foreach (var path in untracked)
        {
            var fullPath = Path.Combine(repositoryPath, path);
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new GitOperationException($"Unable to discard untracked file '{path}': {ex.Message}", ex.Message);
            }
        }
    }

    public async Task CommitAsync(string repositoryPath, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var stagedCheck = await RunGitAsync(repositoryPath, ["diff", "--cached", "--quiet"], cancellationToken, allowFailure: true, readOnly: true);
        if (!stagedCheck.IsSuccess && stagedCheck.ExitCode != 1)
        {
            throw GitOperationException.FromResult("git diff --cached", stagedCheck.StandardOutput, stagedCheck.StandardError);
        }

        if (stagedCheck.ExitCode == 0)
        {
            throw new GitOperationException("No staged changes to commit.");
        }

        await RunGitAsync(repositoryPath, ["commit", "-m", message], cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repositoryPath, int count = 30, CancellationToken cancellationToken = default)
    {
        // --topo-order 与 git log --graph 同序(父提交出现在其所有子提交之后):按提交日期排序会把
        // 其它分支的提交插进主线之中,泳道分配会把主线拆成多根线("一条分支多根线")。
        // --decorate=full 让 %D 输出完整 refname,供 GitLogParser 按 refs/heads|remotes 分类;
        // --shortstat 为每条提交追加文件/新增/删除行统计(悬浮窗短统计段)。
        var result = await RunGitAsync(
            repositoryPath,
            ["log", "-n", Math.Clamp(count, 1, 200).ToString(), "--topo-order", "--decorate=full", "--shortstat", $"--pretty=format:{GitLogParser.Format}"],
            cancellationToken: cancellationToken,
            readOnly: true);
        return GitLogParser.Parse(result.StandardOutput);
    }

    public Task<IReadOnlyList<GitCommitInfo>> GetIncomingCommitsAsync(
        string repositoryPath, string upstreamReference, int count = 30, CancellationToken cancellationToken = default) =>
        GetRangeLogAsync(repositoryPath, $"HEAD..{upstreamReference}", count, cancellationToken);

    public Task<IReadOnlyList<GitCommitInfo>> GetOutgoingCommitsAsync(
        string repositoryPath, string upstreamReference, int count = 30, CancellationToken cancellationToken = default) =>
        GetRangeLogAsync(repositoryPath, $"{upstreamReference}..HEAD", count, cancellationToken);

    private async Task<IReadOnlyList<GitCommitInfo>> GetRangeLogAsync(
        string repositoryPath, string range, int count, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            repositoryPath,
            ["log", "-n", Math.Clamp(count, 1, 200).ToString(), "--topo-order", "--decorate=full", "--shortstat", $"--pretty=format:{GitLogParser.Format}", range],
            cancellationToken,
            allowFailure: true,
            readOnly: true);
        return result.IsSuccess ? GitLogParser.Parse(result.StandardOutput) : [];
    }

    public async Task<IReadOnlyList<GitStashInfo>> GetStashesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            repositoryPath,
            ["stash", "list", "--format=%gd%x1f%s"],
            cancellationToken,
            allowFailure: true,
            readOnly: true);
        if (!result.IsSuccess)
        {
            return [];
        }

        var stashes = new List<GitStashInfo>();
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('\x1f');
            if (separator <= 0)
            {
                continue;
            }

            var reference = line[..separator];
            var open = reference.IndexOf('{');
            var close = reference.IndexOf('}', open + 1);
            if (open < 0 || close <= open || !int.TryParse(reference[(open + 1)..close], out var index))
            {
                continue;
            }

            stashes.Add(new GitStashInfo(index, line[(separator + 1)..]));
        }

        return stashes;
    }

    public Task StashAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        RunGitAsync(repositoryPath, ["stash", "push", "--include-untracked"], cancellationToken: cancellationToken);

    public Task PopStashAsync(string repositoryPath, int index, CancellationToken cancellationToken = default) =>
        RunGitAsync(repositoryPath, ["stash", "pop", $"stash@{{{index}}}"], cancellationToken: cancellationToken);

    public Task DropStashAsync(string repositoryPath, int index, CancellationToken cancellationToken = default) =>
        RunGitAsync(repositoryPath, ["stash", "drop", $"stash@{{{index}}}"], cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            repositoryPath,
            ["for-each-ref", "--format=%(refname:short)%09%(upstream:short)%09%(upstream:track)%09%(HEAD)%09%(objectname)%00", "refs/heads"],
            cancellationToken: cancellationToken,
            readOnly: true);

        var branches = new List<GitBranchInfo>();
        foreach (var record in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\t');
            if (fields.Length == 0 || string.IsNullOrWhiteSpace(fields[0]))
            {
                continue;
            }

            var (ahead, behind) = ParseTrack(fields.Length > 2 ? fields[2] : string.Empty);
            branches.Add(new GitBranchInfo(
                fields[0],
                IsCurrent: fields.Length > 3 && fields[3] == "*",
                fields.Length > 1 && !string.IsNullOrWhiteSpace(fields[1]) ? fields[1] : null,
                ahead,
                behind,
                IsRemote: false,
                fields.Length > 4 && !string.IsNullOrWhiteSpace(fields[4]) ? fields[4].Trim() : null));
        }

        return branches;
    }

    public async Task<IReadOnlyList<GitBranchInfo>> GetRemoteBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            repositoryPath,
            ["for-each-ref", "--format=%(refname:short)%09%(objectname)%00", "refs/remotes"],
            cancellationToken: cancellationToken,
            allowFailure: true,
            readOnly: true);
        if (!result.IsSuccess)
        {
            return [];
        }

        var branches = new List<GitBranchInfo>();
        foreach (var record in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\t');
            if (fields.Length == 0 || string.IsNullOrWhiteSpace(fields[0]))
            {
                continue;
            }

            var name = fields[0].Trim();
            // refs/remotes/origin/HEAD 的 %(refname:short) 为 "origin"（不含 /HEAD），需按是否含 '/' 过滤；仅保留 origin/* 形式。
            if (!name.Contains('/'))
            {
                continue;
            }

            if (name.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue;
            }

            branches.Add(new GitBranchInfo(
                name,
                IsCurrent: false,
                Upstream: null,
                AheadCount: 0,
                BehindCount: 0,
                IsRemote: true,
                TipHash: fields.Length > 1 && !string.IsNullOrWhiteSpace(fields[1]) ? fields[1].Trim() : null));
        }

        return branches;
    }

    public Task CreateBranchAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        return RunGitAsync(repositoryPath, ["checkout", "-b", branchName], cancellationToken: cancellationToken);
    }

    public Task SwitchBranchAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        return RunGitAsync(repositoryPath, ["checkout", branchName], cancellationToken: cancellationToken);
    }

    public Task FetchAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        RunGitAsync(repositoryPath, ["fetch", "--prune"], cancellationToken: cancellationToken);

    public Task PullAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        PullAsync(repositoryPath, GitPullOptions.SafeDefault, cancellationToken);

    public async Task<GitPullResult> PullAsync(string repositoryPath, GitPullOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var beforeHead = await ResolveHeadAsync(repositoryPath, cancellationToken);

        if (options.CheckFirst)
        {
            var fetchResult = await RunGitAsync(repositoryPath, ["fetch", "--prune"], cancellationToken, allowFailure: true);
            if (!fetchResult.IsSuccess)
            {
                return new GitPullResult(GitPullResultKind.Error, 0,
                    $"git fetch failed: {Truncate(fetchResult.StandardError ?? fetchResult.StandardOutput)}", []);
            }
        }

        var args = options.BuildArguments().ToList();
        var pullResult = await RunGitAsync(repositoryPath, args, cancellationToken, allowFailure: true);
        if (!pullResult.IsSuccess)
        {
            var stderr = pullResult.StandardError ?? string.Empty;
            var kind = stderr.Contains("Not possible to fast-forward", StringComparison.OrdinalIgnoreCase)
                       || stderr.Contains("Fast forward", StringComparison.OrdinalIgnoreCase)
                ? GitPullResultKind.RefusedFastForward
                : stderr.Contains("CONFLICT", StringComparison.Ordinal)
                    ? GitPullResultKind.MergeConflict
                    : GitPullResultKind.Error;
            return new GitPullResult(kind, 0, Truncate(stderr), []);
        }

        var afterHead = await ResolveHeadAsync(repositoryPath, cancellationToken);
        var commits = beforeHead is not null && afterHead is not null && beforeHead != afterHead
            ? await CountAddedCommitsAsync(repositoryPath, beforeHead, afterHead, cancellationToken)
            : 0;
        var paths = commits > 0
            ? await GetChangedPathsBetweenAsync(repositoryPath, beforeHead!, afterHead!, cancellationToken)
            : [];

        var stdout = pullResult.StandardOutput ?? string.Empty;
        var resultKind = commits == 0
            ? GitPullResultKind.AlreadyUpToDate
            : options.Rebase
                ? GitPullResultKind.Rebased
                : stdout.Contains("Merge made by", StringComparison.Ordinal)
                    ? GitPullResultKind.Merged
                    : GitPullResultKind.FastForwarded;

        return new GitPullResult(resultKind, commits, Truncate(stdout), paths);
    }

    public Task PushAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        PushAsync(repositoryPath, new GitPushOptions(), cancellationToken);

    public async Task PushAsync(string repositoryPath, GitPushOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var status = await GetStatusAsync(repositoryPath, cancellationToken);
        if (!status.IsRepository)
        {
            throw new GitOperationException("Not a git repository.");
        }

        var currentBranch = status.Branch;
        if (string.IsNullOrWhiteSpace(currentBranch))
        {
            throw new GitOperationException("Push requires a checked-out branch (detached HEAD cannot be pushed).");
        }

        var args = options.BuildArguments(currentBranch).ToList();
        await RunGitAsync(repositoryPath, args, cancellationToken: cancellationToken);
    }

    private async Task<string?> ResolveHeadAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repositoryPath, ["rev-parse", "--verify", "HEAD"], cancellationToken, allowFailure: true, readOnly: true);
        return result.IsSuccess ? result.StandardOutput.Trim() : null;
    }

    private async Task<int> CountAddedCommitsAsync(string repositoryPath, string from, string to, CancellationToken cancellationToken)
    {
        var range = $"{from}..{to}";
        var result = await RunGitAsync(repositoryPath, ["rev-list", "--count", range], cancellationToken, allowFailure: true, readOnly: true);
        return result.IsSuccess && int.TryParse(result.StandardOutput.Trim(), out var count) ? count : 0;
    }

    private async Task<IReadOnlyList<string>> GetChangedPathsBetweenAsync(string repositoryPath, string from, string to, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repositoryPath, ["diff", "--name-only", "-z", from, to], cancellationToken, allowFailure: true, readOnly: true);
        if (!result.IsSuccess) return [];
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToArray();
    }

    private static string Truncate(string value, int maxLen = 500) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty
        : value.Length <= maxLen ? value
        : string.Concat(value.AsSpan(0, maxLen - 3), "...");

    public async Task<IReadOnlyList<GitFileChange>> GetCommitFilesAsync(string repositoryPath, string commitHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitHash);
        // "A...B" 范围(同步折叠栏的合并视图,如 HEAD...origin/main)不能用 git show,走 diff;
        // 普通提交哈希保持 git show(该提交涉及的文件)。
        string[] arguments = commitHash.Contains("...")
            ? ["diff", "--name-status", "--format=", commitHash]
            : ["show", "--name-status", "--format=", commitHash];
        var result = await RunGitAsync(
            repositoryPath,
            arguments,
            cancellationToken: cancellationToken,
            readOnly: true);

        return ParseNameStatus(result.StandardOutput);
    }

    public async Task<IReadOnlyList<GitFileChange>> GetIncomingFilesAsync(
        string repositoryPath,
        string upstreamReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamReference);
        var result = await RunGitAsync(
            repositoryPath,
            ["diff", "--name-status", "--format=", $"HEAD...{upstreamReference}"],
            cancellationToken: cancellationToken,
            readOnly: true);

        return ParseNameStatus(result.StandardOutput);
    }

    public async Task<IReadOnlyList<GitFileChange>> GetOutgoingFilesAsync(
        string repositoryPath,
        string upstreamReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamReference);
        var result = await RunGitAsync(
            repositoryPath,
            ["diff", "--name-status", "--format=", $"{upstreamReference}...HEAD"],
            cancellationToken: cancellationToken,
            readOnly: true);

        return ParseNameStatus(result.StandardOutput);
    }

    /// <summary>name-status 输出的逐行解析(状态字母 + 路径;重命名行为 状态/新路径/原路径)。</summary>
    private static IReadOnlyList<GitFileChange> ParseNameStatus(string output)
    {
        var changes = new List<GitFileChange>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length < 2 || fields[0].Length == 0)
            {
                continue;
            }

            var status = GitChangeStatusExtensions.FromStatusLetter(fields[0][0]);
            if (status == GitChangeStatus.Unmodified)
            {
                continue;
            }

            changes.Add(new GitFileChange(
                fields[^1],
                status,
                GitChangeStatus.Unmodified,
                fields.Length > 2 ? fields[1] : null));
        }

        return changes;
    }

    public async Task<GitFileDiff?> GetCommitFileDiffAsync(string repositoryPath, string commitHash, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // "A...B" 形式的范围(如 HEAD...origin/main 的传入合并视图)不能用 git show,走 git diff;
        // 普通提交哈希保持 git show(单文件在该提交中的变化)。
        return await CollectDiffAsync(StreamCommitFileDiffAsync(repositoryPath, commitHash, path, cancellationToken: cancellationToken), cancellationToken);
    }

    public async IAsyncEnumerable<GitDiffEvent> StreamCommitFileDiffAsync(
        string repositoryPath,
        string commitHash,
        string path,
        int maximumLines = 500_000,
        bool ignoreWhitespaceEndOfLine = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var whitespaceArgument = ignoreWhitespaceEndOfLine ? "--ignore-space-at-eol" : null;
        string[] diffArguments;
        if (commitHash.Contains("...")) diffArguments = whitespaceArgument is null
            ? ["diff", "--no-ext-diff", "--unified=3", commitHash, "--", path]
            : ["diff", "--no-ext-diff", "--ignore-space-at-eol", "--unified=3", commitHash, "--", path];
        else diffArguments = whitespaceArgument is null
            ? ["show", "--no-ext-diff", "--unified=3", "--format=", commitHash, "--", path]
            : ["show", "--no-ext-diff", "--ignore-space-at-eol", "--unified=3", "--format=", commitHash, "--", path];
        var arguments = new List<string> { "-C", repositoryPath };
        arguments.AddRange(diffArguments);
        var parser = new GitDiffStreamParser(path, false);
        ProcessStreamEvent? completion = null;
        var error = new StringBuilder();
        var gate = RepositoryGate(repositoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Same legacy-encoding fallback as StreamDiffAsync: diff content lines carry the
            // file's raw bytes and must not be fixed-decoded as UTF-8.
            await foreach (var item in processRunner.StreamLinesWithTextFallbackAsync(GitExecutable, arguments, maximumLines, OptionalLocksDisabled, cancellationToken).ConfigureAwait(false))
            {
                if (item.IsCompleted) { completion = item; break; }
                if (item.IsError) { if (error.Length < 4096) error.AppendLine(item.Text); continue; }
                foreach (var parsed in parser.Accept(item.Text ?? string.Empty)) yield return parsed;
            }
        }
        finally
        {
            gate.Release();
        }
        yield return parser.Metadata;
        yield return new GitDiffCompletedEvent(completion?.ExitCode ?? -1, completion?.OutputLimitReached == true, error.Length == 0 ? null : error.ToString());
    }

    private static async Task<GitFileDiff?> CollectDiffAsync(
        IAsyncEnumerable<GitDiffEvent> events,
        CancellationToken cancellationToken)
    {
        var hunks = new List<GitDiffHunk>();
        GitDiffHunkEvent? current = null;
        List<GitDiffLine>? lines = null;
        GitDiffMetadataEvent? metadata = null;
        await foreach (var item in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (item)
            {
                case GitDiffMetadataEvent value:
                    metadata = value;
                    break;
                case GitDiffHunkEvent value:
                    Flush();
                    current = value;
                    lines = [new GitDiffLine(GitDiffLineKind.HunkHeader, null, null, value.Header)];
                    break;
                case GitDiffLineEvent value when lines is not null:
                    lines.Add(value.Line);
                    break;
                case GitDiffCompletedEvent { ExitCode: not 0 }:
                    return null;
            }
        }

        Flush();
        var hasMetadata = metadata is not null;
        metadata ??= new GitDiffMetadataEvent(string.Empty, null, false, false, false);
        return new GitFileDiff(metadata.Path, metadata.OldPath, metadata.IsStaged, metadata.IsBinary,
            metadata.IsNewFile, hunks, HasMetadata: hasMetadata);

        void Flush()
        {
            if (current is null || lines is null) return;
            hunks.Add(new GitDiffHunk(current.OldStart, current.OldCount, current.NewStart, current.NewCount, current.Header, lines));
            current = null;
            lines = null;
        }
    }

    private static (int Ahead, int Behind) ParseTrack(string track)
    {
        var match = TrackRegex().Match(track);
        if (!match.Success)
        {
            return (0, 0);
        }

        var ahead = match.Groups["ahead"].Success ? int.Parse(match.Groups["ahead"].Value) : 0;
        var behind = match.Groups["behind"].Success ? int.Parse(match.Groups["behind"].Value) : 0;
        return (ahead, behind);
    }

    [GeneratedRegex("\\[ahead (?<ahead>\\d+)(?:, behind (?<behind>\\d+))?\\]|\\[behind (?<behind>\\d+)\\]")]
    private static partial Regex TrackRegex();

    [GeneratedRegex("^@@ -(?<oldStart>\\d+)(?:,(?<oldCount>\\d+))? \\+(?<newStart>\\d+)(?:,(?<newCount>\\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    // ASCII structural markers for the byte-level hunk selection (SelectHunkPatchBytes).
    private static readonly byte[] HunkPrefix = Encoding.ASCII.GetBytes("@@ ");
    private static readonly byte[] DiffGitPrefix = Encoding.ASCII.GetBytes("diff --git ");
    private static readonly byte[] DashDashPrefix = Encoding.ASCII.GetBytes("--- ");
    private static readonly byte[] IndexPrefix = Encoding.ASCII.GetBytes("index ");

    private async Task<string> ReadWorkingTreeObjectAsync(
        string repositoryPath,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            repositoryPath,
            ["hash-object", "--no-filters", "--", path],
            cancellationToken,
            allowFailure: true,
            readOnly: true);
        return result.IsSuccess && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput.Trim()
            : "<missing>";
    }

    private async Task<string> ReadGitObjectAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repositoryPath, arguments, cancellationToken, allowFailure: true, readOnly: true);
        return result.IsSuccess && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput.Trim()
            : "<missing>";
    }

    /// <summary>Runs a git command whose <c>-- &lt;paths&gt;</c> argument list is split into chunks
    /// whose cumulative path bytes stay under the Windows command-line limit (G8, VS Code's
    /// MAX_CLI_LENGTH=30000 with a conservative margin): thousands of paths in one command line can
    /// make the process fail to start. Each chunk runs as its own git invocation; an empty chunk
    /// list is not executed.</summary>
    private async Task RunPathChunkedAsync(
        string repositoryPath,
        IReadOnlyList<string> headArguments,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in ChunkPathsByBytes(paths))
        {
            var arguments = new List<string>(headArguments.Count + 1 + chunk.Count);
            arguments.AddRange(headArguments);
            arguments.Add("--");
            arguments.AddRange(chunk);
            await RunGitAsync(repositoryPath, arguments, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Windows 命令行上限的保守预算(VS Code 30000,这里留出其余参数的余量)。</summary>
    internal const int MaximumPathCommandBytes = 24_000;

    internal static IEnumerable<IReadOnlyList<string>> ChunkPathsByBytes(IEnumerable<string> paths)
    {
        var current = new List<string>();
        var bytes = 0;
        foreach (var path in paths)
        {
            var size = Encoding.UTF8.GetByteCount(path) + 1; // path + NUL terminator
            if (current.Count > 0 && bytes + size > MaximumPathCommandBytes)
            {
                yield return current;
                current = [];
                bytes = 0;
            }

            current.Add(path);
            bytes += size;
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private async Task<ProcessResult> RunGitAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowFailure = false,
        bool readOnly = false,
        int? maximumOutputBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var args = new List<string>(arguments.Count + 2) { "-C", repositoryPath };
        args.AddRange(arguments);

        var gate = RepositoryGate(repositoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await processRunner.RunAsync(
                GitExecutable,
                args,
                cancellationToken: cancellationToken,
                environmentVariables: readOnly ? OptionalLocksDisabled : null,
                maximumOutputBytes: maximumOutputBytes);
            if (!result.IsSuccess && !allowFailure)
            {
                throw GitOperationException.FromResult($"git {arguments[0]}", result.StandardOutput, result.StandardError);
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Like <see cref="RunGitAsync"/> but keeps stdout as raw bytes (never decoded) so
    /// legacy-encoded diff content can round-trip to <c>git apply</c> byte-exact. Same
    /// per-repository gate and error semantics as the text variant.</summary>
    private async Task<ProcessRawResult> RunGitRawAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var args = new List<string>(arguments.Count + 2) { "-C", repositoryPath };
        args.AddRange(arguments);

        var gate = RepositoryGate(repositoryPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await processRunner.RunRawAsync(
                GitExecutable,
                args,
                readOnly ? OptionalLocksDisabled : null,
                cancellationToken);
            if (!result.IsSuccess)
            {
                throw GitOperationException.FromResult($"git {arguments[0]}", string.Empty, result.StandardError);
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>Raised when a git command exits unsuccessfully. <see cref="StandardError"/> carries the
/// tool's own diagnostics so the UI/CLI can surface the real reason (merge conflicts, authentication
/// failures, dirty-worktree checkout errors, ...).</summary>
public sealed class GitOperationException(string message, string? standardError = null) : Exception(message)
{
    public string? StandardError { get; } = standardError;

    public static GitOperationException FromResult(string operation, string output, string error) =>
        new(
            $"{operation} failed: {FirstLine(error)}",
            error);

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
        return lineBreak < 0 ? trimmed : trimmed[..lineBreak];
    }
}
