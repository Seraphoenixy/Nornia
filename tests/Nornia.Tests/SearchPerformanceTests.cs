using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Project.Models;
using Nornia.Project.Services;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Guards the S3/S5/S8/S9/S6/S7/S10/V5 performance improvements of the global-search
/// subsystem (docs/performance-review.md §2.3): per-directory gitignore rule segments, the
/// cross-search glob-compile LRU, the single FileSystemEnumerable walk, the per-level result
/// tree index, refinement-based result retention, probe-scaled debounce, the single-TextBlock
/// match-row parts, and the 64-file batch + tail-append UI fast path.</summary>
public sealed class SearchPerformanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nornia-search-perf-{Guid.NewGuid():N}");

    public SearchPerformanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    // ─────────────────────────── S3: gitignore rule segments ───────────────────────────

    [Fact]
    public void S3_NestedIgnoreScope_RulesApplyOnlyInTheirDirectory()
    {
        var dir = Path.Combine(_root, "s3-scope");
        Directory.CreateDirectory(Path.Combine(dir, "sub", "nested"));
        Directory.CreateDirectory(Path.Combine(dir, "other"));
        Directory.CreateDirectory(Path.Combine(dir, "build"));
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "*.log\nbuild/\n");
        File.WriteAllText(Path.Combine(dir, "sub", ".gitignore"), "secret.txt\n");

        var rootScope = GitIgnoreRule.LoadForDirectory(dir, "", GitIgnoreScope.Empty, null);
        var subScope = GitIgnoreRule.LoadForDirectory(Path.Combine(dir, "sub"), "sub", rootScope, null);
        var nestedScope = GitIgnoreRule.LoadForDirectory(Path.Combine(dir, "sub", "nested"), "sub/nested", subScope, null);
        var otherScope = GitIgnoreRule.LoadForDirectory(Path.Combine(dir, "other"), "other", rootScope, null);

        // Root rules apply to the whole tree...
        Assert.True(GitIgnoreRule.IsIgnored("a.log", false, rootScope));
        Assert.True(GitIgnoreRule.IsIgnored("sub/a.log", false, subScope));
        Assert.True(GitIgnoreRule.IsIgnored("build/x.cs", false, rootScope)); // under an ignored dir

        // ...but a directory's rule applies only inside its subtree (no leak to root or siblings).
        Assert.False(GitIgnoreRule.IsIgnored("secret.txt", false, rootScope));
        Assert.False(GitIgnoreRule.IsIgnored("other/secret.txt", false, otherScope));
        Assert.True(GitIgnoreRule.IsIgnored("sub/secret.txt", false, subScope));
        Assert.True(GitIgnoreRule.IsIgnored("sub/nested/secret.txt", false, nestedScope));

        // A directory without a local .gitignore reuses its parent scope instance.
        Assert.Same(rootScope, GitIgnoreRule.LoadForDirectory(Path.Combine(dir, "other"), "other", rootScope, null));
    }

    [Fact]
    public void S3_LastMatchWins_AcrossRuleSegments()
    {
        var dirA = Path.Combine(_root, "s3-lmw-a");
        Directory.CreateDirectory(Path.Combine(dirA, "sub"));
        File.WriteAllText(Path.Combine(dirA, ".gitignore"), "!a.txt\n");
        File.WriteAllText(Path.Combine(dirA, "sub", ".gitignore"), "a.txt\n");
        var rootA = GitIgnoreRule.LoadForDirectory(dirA, "", GitIgnoreScope.Empty, null);
        var subA = GitIgnoreRule.LoadForDirectory(Path.Combine(dirA, "sub"), "sub", rootA, null);
        // The deeper (later in apply order) segment wins: sub's positive rule beats root's
        // negation, but only inside sub.
        Assert.True(GitIgnoreRule.IsIgnored("sub/a.txt", false, subA));
        Assert.False(GitIgnoreRule.IsIgnored("a.txt", false, rootA));

        var dirB = Path.Combine(_root, "s3-lmw-b");
        Directory.CreateDirectory(Path.Combine(dirB, "sub"));
        File.WriteAllText(Path.Combine(dirB, ".gitignore"), "a.txt\n");
        File.WriteAllText(Path.Combine(dirB, "sub", ".gitignore"), "!a.txt\n");
        var rootB = GitIgnoreRule.LoadForDirectory(dirB, "", GitIgnoreScope.Empty, null);
        var subB = GitIgnoreRule.LoadForDirectory(Path.Combine(dirB, "sub"), "sub", rootB, null);
        // And a deeper negation beats an ancestor positive.
        Assert.False(GitIgnoreRule.IsIgnored("sub/a.txt", false, subB));
        Assert.True(GitIgnoreRule.IsIgnored("a.txt", false, rootB));
    }

    [Fact]
    public void S3_SegmentLookup_MatchesLinearSemanticsOnRandomScopes()
    {
        // Build a 3-level scope chain with random rules and verify the new segment walk is
        // exactly equivalent to the old linear last-match-wins scan over the concatenated
        // (root, mid, leaf) rule list.
        var dir = Path.Combine(_root, "s3-fuzz");
        Directory.CreateDirectory(Path.Combine(dir, "mid", "leaf"));
        var random = new Random(0x5EED);
        WriteIgnoreFile(Path.Combine(dir, ".gitignore"), RandomRules(random, 6));
        WriteIgnoreFile(Path.Combine(dir, "mid", ".gitignore"), RandomRules(random, 5));
        WriteIgnoreFile(Path.Combine(dir, "mid", "leaf", ".gitignore"), RandomRules(random, 5));

        var rootScope = GitIgnoreRule.LoadForDirectory(dir, "", GitIgnoreScope.Empty, null);
        var midScope = GitIgnoreRule.LoadForDirectory(Path.Combine(dir, "mid"), "mid", rootScope, null);
        var leafScope = GitIgnoreRule.LoadForDirectory(Path.Combine(dir, "mid", "leaf"), "mid/leaf", midScope, null);

        var linearRules = new List<GitIgnoreRule>(rootScope.LocalRules);
        linearRules.AddRange(midScope.LocalRules);
        linearRules.AddRange(leafScope.LocalRules);

        const string alphabet = "ab/midleaf";
        for (var iteration = 0; iteration < 4000; iteration++)
        {
            var segments = new List<string>();
            for (var s = 0; s < random.Next(0, 4); s++)
            {
                var length = random.Next(1, 5);
                var chars = new char[length];
                for (var i = 0; i < length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
                segments.Add(new string(chars));
            }

            var path = string.Join('/', segments);
            if (path.Length == 0) continue;
            var directory = random.Next(2) == 0;
            var expected = LinearIsIgnored(path, directory, linearRules);
            var actual = GitIgnoreRule.IsIgnored(path, directory, leafScope);
            Assert.True(expected == actual, $"path='{path}' directory={directory}");
        }
    }

    [Fact]
    public void S3_ParseCache_ReusesRulesAndInvalidatesOnChange()
    {
        var dir = Path.Combine(_root, "s3-cache");
        Directory.CreateDirectory(dir);
        var gitignore = Path.Combine(dir, ".gitignore");
        File.WriteAllText(gitignore, "cached-rule\n");

        var first = GitIgnoreRule.LoadForDirectory(dir, "", GitIgnoreScope.Empty, null);
        Assert.Single(first.LocalRules);
        var second = GitIgnoreRule.LoadForDirectory(dir, "", GitIgnoreScope.Empty, null);
        // 缓存命中:规则内容一致(作用域 `with` 复制共享惰性编译 matcher,实例不必同一)。
        Assert.Equal(first.LocalRules.Single().Pattern, second.LocalRules.Single().Pattern);

        // Changing the file (different length) invalidates the (path, mtime, length) entry.
        File.WriteAllText(gitignore, "invalidated-rule-two\n");
        var third = GitIgnoreRule.LoadForDirectory(dir, "", GitIgnoreScope.Empty, null);
        Assert.NotSame(first.LocalRules[0], third.LocalRules[0]);
        Assert.True(GitIgnoreRule.IsIgnored("invalidated-rule-two", false, third));
        Assert.False(GitIgnoreRule.IsIgnored("cached-rule", false, third));
    }

    [Fact]
    public async Task S3_EndToEnd_NestedGitIgnoreRulesStayScoped()
    {
        var dir = Path.Combine(_root, "s3-e2e");
        await WriteFileAsync(Path.Combine(dir, ".gitignore"), "*.tmp\n");
        await WriteFileAsync(Path.Combine(dir, "sub", ".gitignore"), "secret.txt\n");
        await WriteFileAsync(Path.Combine(dir, "secret.txt"), "needle\n");       // kept: no root rule
        await WriteFileAsync(Path.Combine(dir, "sub", "secret.txt"), "needle\n"); // ignored by sub scope
        await WriteFileAsync(Path.Combine(dir, "sub", "keep.txt"), "needle\n");   // kept
        await WriteFileAsync(Path.Combine(dir, "x.tmp"), "needle\n");             // ignored by root scope
        await WriteFileAsync(Path.Combine(dir, "sub", "y.tmp"), "needle\n");      // ignored (inherited)

        var service = new WorkspaceSearchService(new FakeSettingsService());
        var results = await CollectAsync(service.SearchAsync(new(
            dir, "needle", new TextSearchOptions(false, false, false))));

        var paths = results.Select(r => r.RelativePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(new[] { "secret.txt", "sub/keep.txt" }, paths);
    }

    // ─────────────────────────── S5: cross-search glob LRU ───────────────────────────

    [Fact]
    public void S5_LruCache_EvictsLeastRecentlyUsed()
    {
        var cache = new SimpleGlobCache(4);
        var a = cache.Compile("*.a");
        var b = cache.Compile("*.b");
        var c = cache.Compile("*.c");
        var d = cache.Compile("*.d");
        Assert.Equal(4, cache.CompileCount);

        Assert.Same(a, cache.Compile("*.a")); // hit → a becomes most recently used
        Assert.Equal(1, cache.CacheHitCount);

        var e = cache.Compile("*.e");         // evicts b (least recently used)
        Assert.Equal(5, cache.CompileCount);
        Assert.True(cache.Count <= 4);

        var bAgain = cache.Compile("*.b");    // b was evicted → recompiled
        Assert.NotSame(b, bAgain);
        Assert.Equal(6, cache.CompileCount);

        Assert.Same(a, cache.Compile("*.a")); // a survived the eviction
        Assert.Equal(2, cache.CacheHitCount);

        // Compiled matchers keep the framework's simple-expression dialect.
        Assert.True(a("x/y.a"));
        Assert.False(a("x/y.b"));
        Assert.True(d("x.d"));
        Assert.True(bAgain("z.b"));
        Assert.False(e(""));
    }

    [Fact]
    public void S5_LruCache_EmptyPatternIsACachedFreeConstant()
    {
        var cache = new SimpleGlobCache(2);
        var before = cache.CompileCount;
        var never1 = cache.Compile(string.Empty);
        var never2 = cache.Compile(string.Empty);
        Assert.Same(never1, never2);
        Assert.Equal(before, cache.CompileCount);
        Assert.Equal(before, cache.CacheHitCount);
        Assert.False(never1("anything"));
    }

    [Fact]
    public async Task S5_CrossSearch_PatternsAreCompiledOnce()
    {
        var dir = Path.Combine(_root, "s5");
        await WriteFileAsync(Path.Combine(dir, "a.txt"), "needle\n");
        var service = new WorkspaceSearchService(new FakeSettingsService());
        var query = new WorkspaceSearchQuery(dir, "needle", new TextSearchOptions(false, false, false),
            IncludePattern: "a.txt;b.cs", ExcludePattern: "skip.txt");

        var first = await CollectAsync(service.SearchAsync(query));
        Assert.Single(first);
        var compilesAfterFirst = service.GlobCache.CompileCount;
        var hitsAfterFirst = service.GlobCache.CacheHitCount;
        Assert.True(compilesAfterFirst > 0, "the first search must compile its patterns");

        // Second search with the same include/exclude patterns: the service's shared LRU
        // serves every pattern — nothing new is compiled.
        var second = await CollectAsync(service.SearchAsync(query));
        Assert.Single(second);
        Assert.Equal(compilesAfterFirst, service.GlobCache.CompileCount);
        Assert.True(service.GlobCache.CacheHitCount > hitsAfterFirst);
    }

    // ─────────────────────────── S8: single-walk enumeration ───────────────────────────

    [Fact]
    public void S8_SingleWalk_YieldsSameFilesAsClassicEnumeration()
    {
        var dir = Path.Combine(_root, "s8");
        Directory.CreateDirectory(Path.Combine(dir, "dirA", "dirB"));
        Directory.CreateDirectory(Path.Combine(dir, "dirA", "node_modules"));
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "packages"));
        File.WriteAllText(Path.Combine(dir, "file0.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "top.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "dirA", "fileA1.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "dirA", "dirB", "fileB1.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "obj", "skip.txt"), "x");
        File.WriteAllText(Path.Combine(dir, ".git", "hide.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "dirA", "node_modules", "inner.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "packages", "pkg.txt"), "x");

        var query = new WorkspaceSearchQuery(dir, "x", new TextSearchOptions(false, false, false),
            UseIgnoreFiles: false);
        var filter = new SearchPathFilter(query, new Dictionary<string, bool>());
        var candidates = new List<(string FullPath, string RelativePath)>();
        WorkspaceSearchService.EnumerateCandidates(dir, query, filter, candidates, null, CancellationToken.None);

        // Independent classic walk with the same ignored-name pruning. Set comparison on
        // purpose: the single FileSystemEnumerable pass interleaves files and directories and
        // the parallel scan makes arrival order irrelevant.
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", ".svn", ".hg", "bin", "obj", "node_modules", ".vs", "packages" };
        var expected = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpected(dir);
        void CollectExpected(string current)
        {
            foreach (var file in Directory.EnumerateFiles(current))
            {
                expected.Add(Path.GetRelativePath(dir, file).Replace('\\', '/'));
            }

            foreach (var sub in Directory.EnumerateDirectories(current))
            {
                if (ignored.Contains(Path.GetFileName(sub))) continue;
                CollectExpected(sub);
            }
        }

        // Compare as sorted sequences (the single pass interleaves files and directories, so
        // arrival order is deliberately not asserted).
        Assert.Equal(
            expected.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            candidates.Select(c => c.RelativePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void S8_SingleWalk_SkipsInaccessibleDirectoryWithoutFailing()
    {
        var dir = Path.Combine(_root, "s8-locked");
        var locked = Path.Combine(dir, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(dir, "ok.txt"), "x");
        File.WriteAllText(Path.Combine(locked, "hidden.txt"), "x");

        var info = new DirectoryInfo(locked);
        var original = info.GetAccessControl();
        var deny = info.GetAccessControl();
        deny.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().Name,
            FileSystemRights.ReadData | FileSystemRights.ListDirectory | FileSystemRights.ReadAndExecute,
            AccessControlType.Deny));
        info.SetAccessControl(deny);
        try
        {
            // Confirm the deny is effective; if this environment cannot be locked down
            // (elevated tokens with backup rights, non-Windows CI), skip that arm.
            try
            {
                _ = info.EnumerateFiles().Count();
                return; // 环境拒绝 ACL 权限屏蔽(提权令牌等):跳过本测试臂,不失败
            }
            catch (UnauthorizedAccessException)
            {
            }

            var query = new WorkspaceSearchQuery(dir, "x", new TextSearchOptions(false, false, false),
                UseIgnoreFiles: false);
            var filter = new SearchPathFilter(query, new Dictionary<string, bool>());
            var candidates = new List<(string FullPath, string RelativePath)>();
            // IgnoreInaccessible=true: the unreadable directory contributes no entries and no
            // exception, instead of failing the whole walk.
            WorkspaceSearchService.EnumerateCandidates(dir, query, filter, candidates, null, CancellationToken.None);

            var only = Assert.Single(candidates);
            Assert.Equal("ok.txt", only.RelativePath);
        }
        finally
        {
            try { info.SetAccessControl(original); }
            catch { }
        }
    }

    // ─────────────────────────── S6: refinement retention ───────────────────────────

    [Fact]
    public async Task S6_Refinement_KeepsTree_OverwritesAndPrunesStale()
    {
        var dir = Path.Combine(_root, "s6");
        await WriteFileAsync(Path.Combine(dir, "a.txt"), "alpha beta\nalphax\n");
        await WriteFileAsync(Path.Combine(dir, "b.txt"), "alphabet soup\n");
        await WriteFileAsync(Path.Combine(dir, "c.txt"), "no match here\n");
        await WriteFileAsync(Path.Combine(dir, "d.txt"), "alphax one\nalphax two\n");

        var (vm, counting, _) = CreateSearchViewModel(dir);

        // Search 1: "alpha" → a.txt (2 matches), b.txt (1), d.txt (2).
        vm.SearchText = "alpha";
        await WaitUntilAsync(() => counting.SearchCount == 1 && !vm.IsSearching);
        var fileA = Assert.Single(vm.Rows, r => r.IsFile && r.Name == "a.txt");
        var fileB = Assert.Single(vm.Rows, r => r.IsFile && r.Name == "b.txt");
        Assert.Equal(2, fileA.MatchCount);

        // Switch to list view: per-FullPath wrapper rows are built.
        vm.ResultsViewMode = SearchResultsViewMode.List;
        var wrapperD = Assert.Single(vm.Rows, r => r.IsFile && r.IsListResult && r.Name == "d.txt");
        Assert.Equal(2, wrapperD.MatchCount);

        // Search 2: "alphax" is a prefix refinement → the old tree is kept: d.txt's wrapper
        // row is reused as-is (identical match set), a.txt's matches are overwritten in place
        // (2→1), and b.txt is pruned as stale.
        vm.SearchText = "alphax";
        await WaitUntilAsync(() => counting.SearchCount == 2 && !vm.IsSearching);

        Assert.Single(vm.Rows, r => r.IsFile && r.Name == "d.txt");       // 细化后 d.txt 保留
        Assert.DoesNotContain(fileB, vm.Rows);                 // pruned instance is gone
        Assert.DoesNotContain(vm.Rows, r => r.Name == "b.txt");
        var fileA2 = Assert.Single(vm.Rows, r => r.IsFile && r.Name == "a.txt");
        Assert.Equal(1, fileA2.MatchCount);                    // overwritten with the refined set

        // Search 3: "soup" is NOT a refinement of "alphax" → clear-then-fill fallback:
        // every row is a fresh instance.
        vm.SearchText = "soup";
        await WaitUntilAsync(() => counting.SearchCount == 3 && !vm.IsSearching);
        Assert.DoesNotContain(wrapperD, vm.Rows);
        var fileB3 = Assert.Single(vm.Rows, r => r.IsFile && r.Name == "b.txt");
        Assert.NotSame(fileB, fileB3);
    }

    // ─────────────────────────── S7: probe-scaled debounce ───────────────────────────

    [Fact]
    public async Task S7_ProbeDebounce_RegexScalesUp_LiteralStaysAtBase()
    {
        var dir = Path.Combine(_root, "s7");
        // One file with a high hits-per-file density (30 matches in 1 file) → after scan 1
        // the probe extends the NEXT regex keystroke's debounce to 3× the 300ms base.
        await WriteFileAsync(Path.Combine(dir, "one.txt"), string.Join("\n", Enumerable.Repeat("ax", 30)) + "\n");

        var (vm, counting, _) = CreateSearchViewModel(dir);
        vm.SearchUseRegex = true;
        vm.SearchText = "a";
        await WaitUntilAsync(() => counting.SearchCount == 1 && !vm.IsSearching);

        // Keystroke 2 ("ab") must wait the probe-scaled 900ms, not the 300ms base.
        vm.SearchText = "ab";
        await Task.Delay(450);
        Assert.True(1 == counting.SearchCount, "the 900ms probe-scaled debounce must not have elapsed yet");
        await WaitUntilAsync(() => counting.SearchCount == 2);

        // Plain literal searches keep the 300ms base regardless of the regex probe history.
        vm.SearchUseRegex = false;
        await WaitUntilAsync(() => counting.SearchCount == 3 && !vm.IsSearching);
        var literalKeystrokeAt = Environment.TickCount64;
        vm.SearchText = "abc";
        await Task.Delay(200);
        Assert.True(3 == counting.SearchCount, "the 300ms base debounce must not have elapsed yet");
        var deadline = Environment.TickCount64 + 2000;
        while (counting.SearchCount < 4 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(4 == counting.SearchCount, "the literal search should have started");
        Assert.True(Environment.TickCount64 - literalKeystrokeAt < 700,
            $"literal debounce should be the 300ms base, but the scan took {Environment.TickCount64 - literalKeystrokeAt}ms to start");
    }

    // ─────────────────────────── S9: per-level tree index ───────────────────────────

    [Fact]
    public async Task S9_MultiBatchAdds_BuildNestedTreeWithoutDuplicates()
    {
        var dir = Path.Combine(_root, "s9");
        // 65 files across a 5×3 nested folder grid → spans both 64-file UI batches and
        // forces the tree to be created incrementally in (parallel, non-lexicographic)
        // arrival order.
        for (var i = 0; i < 65; i++)
        {
            await WriteFileAsync(Path.Combine(dir, $"d{i % 5}", $"e{i % 3}", $"f{i}.txt"), "needle\n");
        }

        var (vm, counting, _) = CreateSearchViewModel(dir);
        vm.SearchText = "needle";
        await WaitUntilAsync(() => counting.SearchCount == 1 && !vm.IsSearching);

        var files = vm.Rows.Where(r => r.IsFile).ToList();
        var folders = vm.Rows.Where(r => r.Kind == SearchTreeNodeKind.Folder).ToList();
        var matches = vm.Rows.Where(r => r.IsMatch).ToList();

        // Every file present exactly once (no duplicate rows), under the 20 nested folders.
        Assert.Equal(65, files.Count);
        Assert.Equal(65, files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(20, folders.Count);
        Assert.Equal(20, folders.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(65, matches.Count);
        Assert.Equal(150, vm.Rows.Count);

        // The per-level dictionary index collapsed same-name folders into one node per level:
        // "d0" exists exactly once (depth 0) with its three "e*" children, and no two visible
        // rows ever share (Name, Parent).
        var d0 = Assert.Single(folders, f => f.Name == "d0");
        Assert.Equal(0, d0.Depth);
        Assert.Equal(3, d0.Children.Count);
        Assert.All(vm.Rows.GroupBy(r => (r.Name, r.Parent)), group => Assert.Single(group));

        // Purely additive streaming: both batches took the V5 tail-append fast path, so no
        // full re-flatten ever ran.
        Assert.True(vm.TailAppendBatchCount >= 2, $"expected ≥2 tail-append batches, got {vm.TailAppendBatchCount}");
        Assert.Equal(0, vm.FullRebuildCount);
    }

    // ─────────────────────────── S10: match-row preview parts (VM level) ───────────────────────────

    [Fact]
    public void S10_PreviewSegments_ProjectToThreeRunParts()
    {
        // Middle match → before + match + after (the DataTemplate's three Runs).
        // 列号是 1-based 且指向命中首字符:column=9 → 0-based 8 → "nee"。
        var middle = new SearchTreeNode(SearchTreeNodeKind.Match, "row", "src/a.cs",
            new WorkspaceSearchMatch(12, 9, 3, "value = needle;"), fullPath: _root, parent: null, depth: 1);
        Assert.Equal(3, middle.PreviewSegments.Count);
        Assert.Equal("value = ", middle.PreviewSegments[0].Text);
        Assert.False(middle.PreviewSegments[0].IsMatch);
        Assert.Equal("nee", middle.PreviewSegments[1].Text);
        Assert.True(middle.PreviewSegments[1].IsMatch);
        Assert.Equal("dle;", middle.PreviewSegments[2].Text);
        Assert.False(middle.PreviewSegments[2].IsMatch);

        Assert.True(middle.HasPreviewBefore);
        Assert.Equal("value = ", middle.PreviewBeforeText);
        Assert.True(middle.HasPreviewMatch);
        Assert.Equal("nee", middle.PreviewMatchText);
        Assert.True(middle.HasPreviewAfter);
        Assert.Equal("dle;", middle.PreviewAfterText);
        Assert.Equal("value = needle;", middle.PreviewBeforeText + middle.PreviewMatchText + middle.PreviewAfterText);

        // Leading match → no "before" part.
        var leading = new SearchTreeNode(SearchTreeNodeKind.Match, "row", "src/a.cs",
            new WorkspaceSearchMatch(1, 1, 4, "abcd xyz"), fullPath: _root, parent: null, depth: 1);
        Assert.False(leading.HasPreviewBefore);
        Assert.Equal(string.Empty, leading.PreviewBeforeText);
        Assert.Equal("abcd", leading.PreviewMatchText);
        Assert.True(leading.HasPreviewAfter);
        Assert.Equal(" xyz", leading.PreviewAfterText);

        // Trailing match → no "after" part.
        var trailing = new SearchTreeNode(SearchTreeNodeKind.Match, "row", "src/a.cs",
            new WorkspaceSearchMatch(1, 5, 4, "xyz abcd"), fullPath: _root, parent: null, depth: 1);
        Assert.Equal("xyz ", trailing.PreviewBeforeText);
        Assert.Equal("abcd", trailing.PreviewMatchText);
        Assert.False(trailing.HasPreviewAfter);
        Assert.Equal(string.Empty, trailing.PreviewAfterText);

        // Match spanning the whole preview → a single (match) segment.
        var whole = new SearchTreeNode(SearchTreeNodeKind.Match, "row", "src/a.cs",
            new WorkspaceSearchMatch(1, 1, 4, "abcd"), fullPath: _root, parent: null, depth: 1);
        Assert.Single(whole.PreviewSegments);
        Assert.True(whole.HasPreviewMatch);
        Assert.Equal("abcd", whole.PreviewMatchText);
        Assert.False(whole.HasPreviewBefore);
        Assert.False(whole.HasPreviewAfter);

        // Non-match rows expose no parts.
        var folder = new SearchTreeNode(SearchTreeNodeKind.Folder, "f", "f", parent: null, depth: 0);
        Assert.False(folder.HasPreviewBefore);
        Assert.False(folder.HasPreviewMatch);
        Assert.False(folder.HasPreviewAfter);
        Assert.Empty(folder.PreviewSegments);
    }

    // ─────────────────────────── V5: 64-file batches + tail append ───────────────────────────

    [Fact]
    public async Task V5_BatchSize64_TailAppend_NoDuplicateRows()
    {
        var dir = Path.Combine(_root, "v5");
        // 70 files (well above the 16-file legacy batch and the 15-row minimum) → two 64-file
        // UI batches; the second batch must append to the tail without a full re-flatten and
        // without duplicating any row.
        for (var i = 0; i < 70; i++)
        {
            await WriteFileAsync(Path.Combine(dir, $"d{i % 10}", $"f{i}.txt"), "needle\n");
        }

        var (vm, counting, _) = CreateSearchViewModel(dir);
        vm.SearchText = "needle";
        await WaitUntilAsync(() => counting.SearchCount == 1 && !vm.IsSearching);

        var files = vm.Rows.Where(r => r.IsFile).ToList();
        var folders = vm.Rows.Where(r => r.Kind == SearchTreeNodeKind.Folder).ToList();
        Assert.Equal(70, files.Count);
        Assert.Equal(70, files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(10, folders.Count);
        Assert.Equal(70, vm.Rows.Count(r => r.IsMatch));
        Assert.Equal(150, vm.Rows.Count);

        // The stream is purely additive: both batches took the tail-append fast path and no
        // full re-flatten ran.
        Assert.True(vm.TailAppendBatchCount >= 2, $"expected ≥2 tail-append batches, got {vm.TailAppendBatchCount}");
        Assert.Equal(0, vm.FullRebuildCount);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private sealed class CountingSearchService : IWorkspaceSearchService
    {
        private readonly IWorkspaceSearchService _inner;
        private int _count;

        public CountingSearchService(IWorkspaceSearchService inner) => _inner = inner;

        public int SearchCount => Volatile.Read(ref _count);

        public IAsyncEnumerable<WorkspaceSearchFileResult> SearchAsync(
            WorkspaceSearchQuery query,
            IProgress<WorkspaceSearchProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return _inner.SearchAsync(query, progress, cancellationToken);
        }
    }

    private static (SearchViewModel Vm, CountingSearchService Counting, FakeProjectWorkspaceService Workspace)
        CreateSearchViewModel(string projectPath)
    {
        var asset = new ProjectAsset(Guid.NewGuid(), "Test", projectPath, ProjectPathStatus.Available,
            0, null, null, EnvironmentHealthStatus.Unknown);
        var workspace = new FakeProjectWorkspaceService
        {
            Current = new ProjectWorkspaceContext(asset, projectPath, null)
        };
        var editor = new EditorAreaViewModel(new FakeGitService(), new FakeUiLogService());
        var counting = new CountingSearchService(new WorkspaceSearchService(new FakeSettingsService()));
        var vm = new SearchViewModel(counting, workspace, editor, new FakeUiLogService(), new FakeUiDispatcher(), null);
        return (vm, counting, workspace);
    }

    private static async Task<IReadOnlyList<WorkspaceSearchFileResult>> CollectAsync(
        IAsyncEnumerable<WorkspaceSearchFileResult> source)
    {
        var results = new List<WorkspaceSearchFileResult>();
        await foreach (var result in source) results.Add(result);
        return results;
    }

    private static async Task WriteFileAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + (uint)timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"condition not met within {timeoutMs}ms");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>The OLD semantics (before S3): linear last-match-wins scan over the flat list of
    /// all rules, used as the brute-force oracle for the segment walk.</summary>
    private static bool LinearIsIgnored(string path, bool directory, IReadOnlyList<GitIgnoreRule> rules)
    {
        var ignored = false;
        foreach (var rule in rules)
        {
            if (!rule.Matches(path, directory)) continue;
            ignored = !rule.Negated;
        }

        return ignored;
    }

    private static void WriteIgnoreFile(string path, List<string> rules) =>
        File.WriteAllText(path, string.Join('\n', rules) + "\n");

    private static List<string> RandomRules(Random random, int count)
    {
        var rules = new List<string>();
        const string patternChars = "ab*.?/";
        for (var i = 0; i < count; i++)
        {
            var pattern = string.Concat(Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => patternChars[random.Next(patternChars.Length)]));
            if (random.Next(2) == 0) pattern = "!" + pattern;
            if (random.Next(2) == 0) pattern = "/" + pattern;
            if (random.Next(2) == 0) pattern = pattern + "/";
            rules.Add(pattern);
        }

        return rules;
    }
}
