using System.Text;
using Nornia.Git;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class GitServiceTests
{
    // 生产字段(内部注册 code-pages provider),避免测试类静态字段与 provider 注册的时序竞态。
    private static Encoding Gbk => Nornia.Core.Services.TextEncodingDetector.Gb18030;

    private static async Task<List<GitDiffEvent>> CollectDiffAsync(GitService service, string repositoryPath, string path, bool staged, bool isUntracked)
    {
        var events = new List<GitDiffEvent>();
        await foreach (var item in service.StreamDiffAsync(repositoryPath, path, staged, isUntracked))
        {
            events.Add(item);
        }

        return events;
    }

    /// <summary>Creates a temp directory used as the (not-a-repository) working directory for the
    /// untracked diff tests; the untracked path reads the worktree file directly.</summary>
    private static string CreateScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nornia-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public async Task GetStatusAsync_PrefixesEveryCommandWithWorkingDirectory()
    {
        var runner = new FakeProcessRunner((file, args) =>
        {
            Assert.Equal("git", file);
            Assert.Equal("-C", args[0]);
            Assert.Equal(@"C:\repo", args[1]);
            Assert.Contains("--porcelain=v2", args);
            return new Nornia.Core.Models.ProcessResult(0, "# branch.head main\0", string.Empty);
        });

        var status = await new GitService(runner).GetStatusAsync(@"C:\repo");

        Assert.True(status.IsRepository);
        Assert.Equal("main", status.Branch);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task GetStatusAsync_NonRepositoryYieldsNotARepository()
    {
        var runner = new FakeProcessRunner((_, _) => new Nornia.Core.Models.ProcessResult(128, string.Empty, "fatal: not a git repository"));
        var status = await new GitService(runner).GetStatusAsync(@"C:\repo");

        Assert.False(status.IsRepository);
    }

    [Fact]
    public async Task InitializeRepositoryAsync_RunsInitInTargetDirectory()
    {
        var runner = new FakeProcessRunner((file, args) =>
        {
            Assert.Equal("git", file);
            Assert.Equal(new[] { "-C", @"C:\repo", "init" }, args);
            return new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty);
        });

        await new GitService(runner).InitializeRepositoryAsync(@"C:\repo");

        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task GetLogAsync_RequestsTopologicalOrdering()
    {
        // --topo-order 与 git log --graph 同序:按日期(默认)排序会把其它分支的提交插进主线,
        // 泳道分配会把一条分支拆成多根线。
        var runner = new FakeProcessRunner((_, args) =>
        {
            Assert.Contains("--topo-order", args);
            return new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty);
        });

        await new GitService(runner).GetLogAsync(@"C:\repo");

        Assert.Contains(runner.Calls, call => call.Arguments.Contains("--topo-order"));
    }

    [Fact]
    public async Task GetLogAsync_ParsesRealGitOutputShape()
    {
        // 模拟真实 `git log --pretty=format:` 输出:记录间带 git 追加的换行(第 2 条起 hash 前有 \n)。
        var c2 = "c2".PadRight(40, '0');
        var c1 = "c1".PadRight(40, '0');
        var c0 = "c0".PadRight(40, '0');
        string Record(string hash, string parent) =>
            string.Join('\x1f', [hash, hash[..7], "subject", "Alice", "a@x", "2025-01-02T10:20:30+08:00", "", parent]) + "\x1e\n";
        var output = Record(c2, c1) + Record(c1, c0) + Record(c0, string.Empty);

        var runner = new FakeProcessRunner((_, _) => new Nornia.Core.Models.ProcessResult(0, output, string.Empty));
        var commits = await new GitService(runner).GetLogAsync(@"C:\repo");

        Assert.Equal(3, commits.Count);
        Assert.Equal(commits[1].Hash, commits[0].ParentList.Single()); // 父哈希与下一提交哈希一致(泳道连续)
        Assert.Equal(commits[2].Hash, commits[1].ParentList.Single());
    }

    [Fact]
    public async Task GetDiffAsync_UsesCachedVariantWhenStaged()
    {
        var runner = new FakeProcessRunner((_, args) =>
        {
            Assert.Contains("--cached", args);
            return new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty);
        });

        await new GitService(runner).GetDiffAsync(@"C:\repo", "src/A.cs", staged: true);

        Assert.Contains(runner.Calls, call => call.Arguments.Contains("--cached"));
    }

    // ===== Untracked diff: whole-content encoding detection (legacy GBK must not garble) =====
    // 未跟踪文件不走 git 进程而是直接读工作区文件。旧实现用 4KB 头探测整文件编码:
    // ASCII 头超过 4KB 的 GBK 文件被误判为 UTF-8,后文中的中文全部乱码。现在按
    // 只读预览同款策略全量校验(BOM → 严格 UTF-8 → GB18030 回退)。

    [Fact]
    public async Task StreamDiffAsync_UntrackedGb18030FileWithAsciiHead_DecodesChinese()
    {
        var directory = CreateScratchDirectory();
        try
        {
            var asciiHead = string.Concat(Enumerable.Range(0, 300).Select(i => $"// English line {i}\n"));
            const string chinese = "后文中文内容。";
            File.WriteAllText(Path.Combine(directory, "notes.md"), "# Demo\n" + asciiHead + "\n" + chinese + "\n", Gbk);

            var service = new GitService(new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty)));
            var events = await CollectDiffAsync(service, directory, "notes.md", staged: false, isUntracked: true);

            Assert.Contains(events, e => e is GitDiffMetadataEvent { IsBinary: false, IsNewFile: true });
            var added = events.OfType<GitDiffLineEvent>().Select(e => e.Line.Text).ToList();
            Assert.Contains("# Demo", added);
            Assert.Contains(chinese, added);
            Assert.DoesNotContain(added, line => line.Contains('\uFFFD'));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task StreamDiffAsync_UntrackedUtf8File_DecodesChinese()
    {
        var directory = CreateScratchDirectory();
        try
        {
            const string text = "# 中文标题\nUTF-8 内容。\n";
            File.WriteAllText(Path.Combine(directory, "notes.md"), text, new UTF8Encoding(false));

            var service = new GitService(new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty)));
            var events = await CollectDiffAsync(service, directory, "notes.md", staged: false, isUntracked: true);

            var added = events.OfType<GitDiffLineEvent>().Select(e => e.Line.Text).ToList();
            Assert.Equal(["# 中文标题", "UTF-8 内容。"], added);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task StreamDiffAsync_UntrackedFileWithUtf8Bom_StripsTheBom()
    {
        var directory = CreateScratchDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "notes.md"), "BOM 中文。\n", new UTF8Encoding(true));

            var service = new GitService(new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty)));
            var events = await CollectDiffAsync(service, directory, "notes.md", staged: false, isUntracked: true);

            var added = events.OfType<GitDiffLineEvent>().Select(e => e.Line.Text).ToList();
            Assert.Equal(["BOM 中文。"], added);
            Assert.DoesNotContain(added, line => line.StartsWith('\uFEFF'));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task StreamDiffAsync_UntrackedBinaryFile_ReportsBinaryWithoutContent()
    {
        var directory = CreateScratchDirectory();
        try
        {
            var bytes = new byte[64];
            bytes[10] = 0; // NUL inside the probe head
            File.WriteAllBytes(Path.Combine(directory, "blob.bin"), bytes);

            var service = new GitService(new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty)));
            var events = await CollectDiffAsync(service, directory, "blob.bin", staged: false, isUntracked: true);

            Assert.Contains(events, e => e is GitDiffMetadataEvent { IsBinary: true });
            Assert.DoesNotContain(events, e => e is GitDiffLineEvent);
            Assert.Contains(events, e => e is GitDiffCompletedEvent);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task StreamDiffAsync_UntrackedMissingFile_CompletesWithoutContent()
    {
        var directory = CreateScratchDirectory();
        try
        {
            var service = new GitService(new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty)));
            var events = await CollectDiffAsync(service, directory, "absent.md", staged: false, isUntracked: true);

            Assert.DoesNotContain(events, e => e is GitDiffLineEvent);
            Assert.Contains(events, e => e is GitDiffCompletedEvent);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task StageAsync_StagesGivenPaths()
    {
        IReadOnlyList<string>? seen = null;
        var runner = new FakeProcessRunner((_, args) =>
        {
            seen = args;
            return new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty);
        });

        await new GitService(runner).StageAsync(@"C:\repo", ["src/A.cs", "src/B.cs"]);

        Assert.Equal(new[] { "add", "--", "src/A.cs", "src/B.cs" }, seen!.Skip(2).ToArray());
    }

    [Fact]
    public async Task StageAsync_EmptyCollectionStagesAll()
    {
        IReadOnlyList<string>? seen = null;
        var runner = new FakeProcessRunner((_, args) =>
        {
            seen = args;
            return new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty);
        });

        await new GitService(runner).StageAsync(@"C:\repo", []);

        Assert.Equal(new[] { "add", "--all" }, seen!.Skip(2).ToArray());
    }

    // ===== G6: 复用 porcelain v2 的 hH/hI blob id,砍掉 diff 身份识别进程 =====

    [Fact]
    public async Task GetDiffRevisionWithBlobs_Staged_UsesCapturedBlobs_ZeroGitProcesses()
    {
        var runner = new FakeProcessRunner((_, _) => new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty));

        var revision = await new GitService(runner).GetDiffRevisionWithBlobsAsync(
            @"C:\repo", "src/A.cs", staged: true, isUntracked: false,
            headBlobId: "aaaa1111", indexBlobId: "bbbb2222", CancellationToken.None);

        Assert.Equal("staged|aaaa1111|bbbb2222", revision);
        Assert.Empty(runner.Calls); // 0 个 git 进程:身份完全来自 status 记录
    }

    [Fact]
    public async Task GetDiffRevisionWithBlobs_Unstaged_UsesCapturedIndexBlob_OneHashObjectOnly()
    {
        var runner = new FakeProcessRunner((_, args) =>
        {
            Assert.Contains("hash-object", args); // 唯一的进程:工作区内容哈希
            return new Nornia.Core.Models.ProcessResult(0, "wk1", string.Empty);
        });

        var revision = await new GitService(runner).GetDiffRevisionWithBlobsAsync(
            @"C:\repo", "src/A.cs", staged: false, isUntracked: false,
            headBlobId: null, indexBlobId: "bbbb2222", CancellationToken.None);

        Assert.Equal("worktree|bbbb2222|wk1", revision);
        Assert.Single(runner.Calls); // rev-parse / ls-files 均不再发起
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Any(arg => arg is "rev-parse" or "ls-files"));
    }

    [Fact]
    public async Task GetDiffRevisionWithBlobs_MissingBlobs_FallsBackToProcesses()
    {
        var runner = new FakeProcessRunner((_, args) =>
        {
            if (args.Contains("rev-parse")) return new Nornia.Core.Models.ProcessResult(0, "hd1", string.Empty);
            if (args.Contains("ls-files")) return new Nornia.Core.Models.ProcessResult(0, "ix2", string.Empty);
            return new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty);
        });

        var revision = await new GitService(runner).GetDiffRevisionWithBlobsAsync(
            @"C:\repo", "src/A.cs", staged: true, isUntracked: false,
            headBlobId: null, indexBlobId: null, CancellationToken.None);

        Assert.Equal("staged|hd1|ix2", revision);
        Assert.Contains(runner.Calls, call => call.Arguments.Contains("rev-parse"));
        Assert.Contains(runner.Calls, call => call.Arguments.Contains("ls-files"));
    }

    // ===== G8: 批量路径命令按字节分块,避开 Windows 命令行上限 =====

    [Fact]
    public async Task StageAsync_ManyPaths_SplitsIntoBoundedChunks()
    {
        // 3000 个 30 字节路径 ≈ 90KB:单一命令行必然超过 Windows 上限,必须分块。
        var paths = Enumerable.Range(0, 3000)
            .Select(i => $"very/long/path/segment/for/a/really/large/change/{i:D4}.cs")
            .ToArray();
        var runner = new FakeProcessRunner((_, _) => new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty));

        await new GitService(runner).StageAsync(@"C:\repo", paths);

        Assert.True(runner.Calls.Count > 1, "大量路径必须分成多个命令");
        foreach (var call in runner.Calls)
        {
            Assert.Equal("add", call.Arguments[2]);
            var pathBytes = call.Arguments.Skip(4).Sum(System.Text.Encoding.UTF8.GetByteCount);
            Assert.True(pathBytes <= 24_000, $"分块路径字节数超限: {pathBytes}");
        }

        // 所有路径恰好执行一次(不丢不重)。
        var executed = runner.Calls.SelectMany(call => call.Arguments.Skip(4)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(paths.Length, executed.Count);
    }

    [Fact]
    public async Task UnstageAsync_LongPathList_SplitsIntoBoundedChunks()
    {
        var paths = Enumerable.Range(0, 2000)
            .Select(i => $"paths/segment/{i:D4}/subfolder/{Guid.NewGuid():N}.cs")
            .ToArray();
        var runner = new FakeProcessRunner((_, _) => new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty));

        await new GitService(runner).UnstageAsync(@"C:\repo", paths);

        Assert.True(runner.Calls.Count > 1);
        foreach (var call in runner.Calls.Where(call => call.Arguments.Contains("restore")))
        {
            Assert.Contains("restore", call.Arguments);
            Assert.Contains("--staged", call.Arguments);
        }
    }

    [Fact]
    public async Task UnstageAsync_UnbornRepository_RemovesNewFilesFromIndexWithoutUsingHead()
    {
        var runner = new FakeProcessRunner((_, args) =>
            args.Contains("rev-parse")
                ? new Nornia.Core.Models.ProcessResult(1, string.Empty, string.Empty)
                : new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty));

        await new GitService(runner).UnstageAsync(@"C:\repo", ["new-file.txt"]);

        var unstage = Assert.Single(runner.Calls, call => call.Arguments.Contains("rm"));
        Assert.Equal(new[] { "rm", "--cached", "--", "new-file.txt" }, unstage.Arguments.Skip(2).ToArray());
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("restore"));
    }

    [Theory]
    [InlineData(false, GitHunkOperation.Stage, true, false)]
    [InlineData(true, GitHunkOperation.Unstage, true, true)]
    [InlineData(false, GitHunkOperation.Restore, false, true)]
    public async Task ApplyHunkAsync_UsesOnlySelectedHunkAndOperationFlags(
        bool staged,
        GitHunkOperation operation,
        bool expectsCached,
        bool expectsReverse)
    {
        var raw = string.Join('\n',
        [
            "diff --git a/src/A.cs b/src/A.cs",
            "index 1111111..2222222 100644",
            "--- a/src/A.cs",
            "+++ b/src/A.cs",
            "@@ -1,2 +1,2 @@",
            " keep",
            "-old",
            "+new",
            "@@ -10 +10 @@",
            " unchanged",
            "-old-two",
            "+new-two",
            string.Empty,
        ]);
        string? patchPath = null;
        string? selectedPatch = null;
        var runner = new FakeProcessRunner((_, args) =>
        {
            if (args.Contains("diff"))
            {
                return new ProcessResult(0, raw, string.Empty);
            }

            patchPath = args[^1];
            selectedPatch = File.ReadAllText(patchPath);
            return new ProcessResult(0, string.Empty, string.Empty);
        });
        var hunk = new GitDiffHunk(1, 2, 1, 2, "@@ -1,2 +1,2 @@", []);

        await new GitService(runner).ApplyHunkAsync(@"C:\repo", "src/A.cs", staged, hunk, operation);

        var apply = Assert.Single(runner.Calls, call => call.Arguments.Contains("apply"));
        Assert.Equal(expectsCached, apply.Arguments.Contains("--cached"));
        Assert.Equal(expectsReverse, apply.Arguments.Contains("--reverse"));
        Assert.Contains("--ignore-space-change", apply.Arguments);
        Assert.NotNull(selectedPatch);
        Assert.Contains("@@ -1,2 +1,2 @@", selectedPatch);
        Assert.DoesNotContain("\nindex ", selectedPatch);
        Assert.DoesNotContain("@@ -10 +10 @@", selectedPatch);
        Assert.DoesNotContain("old-two", selectedPatch);
        Assert.NotNull(patchPath);
        Assert.False(File.Exists(patchPath));
    }

    [Fact]
    public async Task ApplyHunkAsync_LegacyGb18030Diff_PassesContentBytesThroughUntouched()
    {
        // 内容行是 GBK 字节:任何文本往返(固定 UTF-8 解码再编码)都会破坏它,
        // `git apply` 也就无法匹配上下文。补丁必须原样携带文件原始字节。
        var head = Encoding.ASCII.GetBytes("diff --git a/notes.md b/notes.md\nindex 1111111..2222222 100644\n--- a/notes.md\n+++ b/notes.md\n@@ -1,2 +1,3 @@\n");
        var contextLine = Gbk.GetBytes(" 中文上下文行\n");
        var addedLine = Gbk.GetBytes("+新增的中文行\n");
        var tail = Gbk.GetBytes(" 旧中文行\n");
        var rawDiff = new byte[head.Length + contextLine.Length + addedLine.Length + tail.Length];
        var offset = 0;
        foreach (var part in new[] { head, contextLine, addedLine, tail })
        {
            Buffer.BlockCopy(part, 0, rawDiff, offset, part.Length);
            offset += part.Length;
        }

        byte[]? patchBytes = null;
        var runner = new FakeProcessRunner(
            (_, args) =>
            {
                if (args.Contains("apply"))
                {
                    patchBytes = File.ReadAllBytes(args[^1]);
                }

                return new ProcessResult(0, string.Empty, string.Empty);
            },
            (_, args) =>
            {
                Assert.Contains("diff", args);
                return rawDiff;
            });
        var hunk = new GitDiffHunk(1, 2, 1, 3, "@@ -1,2 +1,3 @@", []);

        await new GitService(runner).ApplyHunkAsync(@"C:\repo", "notes.md", staged: false, hunk, GitHunkOperation.Stage);

        Assert.NotNull(patchBytes);
        Assert.True(ContainsBytes(patchBytes!, Gbk.GetBytes("diff --git a/notes.md b/notes.md")), "patch must start with the diff header");
        Assert.True(ContainsBytes(patchBytes, contextLine), "GBK context line must pass through byte-identical");
        Assert.True(ContainsBytes(patchBytes, addedLine), "GBK added line must pass through byte-identical");
        Assert.True(ContainsBytes(patchBytes, tail), "GBK trailing context line must pass through byte-identical");
        Assert.False(ContainsBytes(patchBytes, new UTF8Encoding(false).GetBytes("中文上下文行")), "content must not be re-encoded as UTF-8");
        Assert.DoesNotContain("index ", Encoding.ASCII.GetString(patchBytes));
        var apply = Assert.Single(runner.Calls, call => call.Arguments.Contains("apply"));
        Assert.Contains("--cached", apply.Arguments);
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task CommitAsync_WithoutStagedChangesThrows()
    {
        var runner = new FakeProcessRunner((_, _) => new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty));
        var service = new GitService(runner);

        await Assert.ThrowsAsync<GitOperationException>(() => service.CommitAsync(@"C:\repo", "message"));
    }

    [Fact]
    public async Task CommitAsync_WithStagedChangesCommits()
    {
        var runner = new FakeProcessRunner((_, args) =>
        {
            // First call is the staged check (exit 1 = changes staged), second is the commit.
            return args.Contains("--cached") && args.Contains("--quiet")
                ? new Nornia.Core.Models.ProcessResult(1, string.Empty, string.Empty)
                : new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty);
        });

        await new GitService(runner).CommitAsync(@"C:\repo", "my message");

        Assert.Contains(runner.Calls, call => call.Arguments.Contains("commit") && call.Arguments.Contains("my message"));
    }

    [Fact]
    public async Task FailedCommand_ReturnsErrorPullResult()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(128, string.Empty, "fatal: something broke"));
        var service = new GitService(runner);

        var result = await service.PullAsync(@"C:\repo", GitPullOptions.SafeDefault);

        Assert.Equal(GitPullResultKind.Error, result.Kind);
        Assert.Contains("something broke", result.Message);
    }

    [Fact]
    public async Task DiscardAsync_DeletesUntrackedFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nornia-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var untracked = Path.Combine(directory, "scratch.txt");
        await File.WriteAllTextAsync(untracked, "data");
        try
        {
            var runner = new FakeProcessRunner((_, args) =>
                args.Contains("ls-files")
                    ? new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty)
                    : new Nornia.Core.Models.ProcessResult(0, string.Empty, string.Empty));

            await new GitService(runner).DiscardUnstagedAsync(directory, ["scratch.txt"]);

            Assert.False(File.Exists(untracked));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ===== G4: read-only git commands set GIT_OPTIONAL_LOCKS=0; write commands must not =====

    [Theory]
    [InlineData("status")]
    [InlineData("log")]
    [InlineData("stash")]
    [InlineData("for-each-ref")]
    [InlineData("rev-parse")]
    [InlineData("hash-object")]
    public async Task ReadOnlyCommands_DisableOptionalLocks(string subCommand)
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, string.Empty));
        var service = new GitService(runner);

        switch (subCommand)
        {
            case "status": await service.GetStatusAsync(@"C:\repo"); break;
            case "log": await service.GetLogAsync(@"C:\repo"); break;
            case "stash": await service.GetStashesAsync(@"C:\repo"); break;
            case "for-each-ref": await service.GetBranchesAsync(@"C:\repo"); break;
            case "rev-parse":
                // staged identity without captured blob ids: rev-parse HEAD:path + ls-files (all reads).
                await service.GetDiffRevisionWithBlobsAsync(@"C:\repo", "src/A.cs", staged: true, isUntracked: false,
                    headBlobId: null, indexBlobId: null);
                break;
            case "hash-object":
                // untracked identity: a single hash-object read.
                await service.GetDiffRevisionAsync(@"C:\repo", "src/A.cs", staged: true, isUntracked: true);
                break;
        }

        var call = Assert.Single(runner.Calls, c => c.Arguments.Contains(subCommand));
        Assert.True(call.Environment is not null, $"expected GIT_OPTIONAL_LOCKS env for '{subCommand}'");
        Assert.Equal("0", call.Environment!["GIT_OPTIONAL_LOCKS"]);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("restore")]
    [InlineData("commit")]
    [InlineData("push")]
    public async Task WriteCommands_DoNotDisableOptionalLocks(string subCommand)
    {
        var runner = new FakeProcessRunner((_, args) =>
        {
            // A verified HEAD returns an object id. The commit staged-check (diff --cached --quiet)
            // reports "changes staged" (exit 1); status-shape output keeps PushAsync in a repository.
            if (args.Contains("rev-parse")) return new ProcessResult(0, "head-object-id", string.Empty);
            return new ProcessResult(args.Contains("--quiet") ? 1 : 0, "# branch.head main\0", string.Empty);
        });
        var service = new GitService(runner);

        switch (subCommand)
        {
            case "add": await service.StageAsync(@"C:\repo", ["a"]); break;
            case "restore": await service.UnstageAsync(@"C:\repo", ["a"]); break;
            case "commit": await service.CommitAsync(@"C:\repo", "m"); break;
            case "push": await service.PushAsync(@"C:\repo"); break;
        }

        var writeCall = runner.Calls.Single(c => c.Arguments.Contains(subCommand));
        Assert.True(writeCall.Environment is null || !writeCall.Environment.ContainsKey("GIT_OPTIONAL_LOCKS"),
            $"'{subCommand}' is a write command and must not set GIT_OPTIONAL_LOCKS");
    }

    // ===== G5: per-repository serialization gate =====

    /// <summary>Runner that blocks every invocation until released and records the maximum
    /// number of concurrently in-flight git processes — both per repository and in total.</summary>
    private sealed class GatingProcessRunner : IProcessRunner
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _perRepository = new(StringComparer.OrdinalIgnoreCase);
        private int _maxPerRepository;
        private int _totalInFlight;
        private int _maxTotalInFlight;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxConcurrent => Volatile.Read(ref _maxPerRepository);
        public int MaxTotalInFlight => Volatile.Read(ref _maxTotalInFlight);
        public void Release() => _release.TrySetResult();

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IProgress<ProcessOutput>? progress = null,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            int? maximumOutputBytes = null)
        {
            var repo = arguments[1]; // -C <path>
            var perRepo = _perRepository.AddOrUpdate(repo, 1, (_, v) => v + 1);
            RaiseMax(ref _maxPerRepository, perRepo);
            RaiseMax(ref _maxTotalInFlight, Interlocked.Increment(ref _totalInFlight));

            async Task<ProcessResult> Blocked()
            {
                try
                {
                    await _release.Task;
                    return new ProcessResult(0, "# branch.head main\0", string.Empty);
                }
                finally
                {
                    Interlocked.Decrement(ref _totalInFlight);
                    _perRepository.AddOrUpdate(repo, 1, (_, v) => v - 1);
                }
            }

            return Blocked();
        }

        private static void RaiseMax(ref int slot, int candidate)
        {
            var observed = Volatile.Read(ref slot);
            while (candidate > observed && Interlocked.CompareExchange(ref slot, candidate, observed) != observed)
            {
                observed = Volatile.Read(ref slot);
            }
        }

        // The serialization tests only exercise RunAsync; streaming is not used by GitService here.
        public IAsyncEnumerable<Core.Models.ProcessStreamEvent> StreamLinesAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            int maximumLines = 1_000_000,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task ConcurrentCommandsForSameRepository_NeverOverlap()
    {
        var runner = new GatingProcessRunner();
        var service = new GitService(runner);

        var first = service.GetStatusAsync(@"C:\repo");
        var second = service.GetStatusAsync(@"C:\repo");
        var third = service.GetStatusAsync(@"C:\repo");

        await Task.Delay(50); // let the first acquire the gate and park, the rest queue behind it
        runner.Release();

        var results = await Task.WhenAll(first, second, third);

        Assert.True(runner.MaxConcurrent == 1, $"expected 1 concurrent git process per repository, saw {runner.MaxConcurrent}");
        Assert.All(results, r => Assert.True(r.IsRepository));
    }

    [Fact]
    public async Task CommandsForDifferentRepositories_DoNotBlockEachOther()
    {
        var runner = new GatingProcessRunner();
        var service = new GitService(runner);

        var a = service.GetStatusAsync(@"C:\repo-a");
        var b = service.GetStatusAsync(@"C:\repo-b");

        await Task.Delay(50); // both should have acquired their (distinct) gates and parked in the runner
        runner.Release();
        await Task.WhenAll(a, b);

        Assert.True(runner.MaxTotalInFlight == 2,
            $"expected 2 concurrent git processes across repositories, saw {runner.MaxTotalInFlight}");
    }
}
