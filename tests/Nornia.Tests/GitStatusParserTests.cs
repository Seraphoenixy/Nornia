using Nornia.Core.Models;
using Nornia.Git.Parsing;

namespace Nornia.Tests;

public sealed class GitStatusParserTests
{
    [Fact]
    public void Parse_TrackedModifiedAndStagedAdded()
    {
        var output = string.Join('\0',
        [
            "# branch.oid abc123",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 0 0 src/A.cs",
            "1 M. N... 100644 100644 100644 0 0 src/B.cs",
            "1 A. N... 0 100644 100644 0 0 src/New.cs",
            ""
        ]);

        var status = GitStatusParser.Parse(output);

        Assert.True(status.IsRepository);
        Assert.Equal("main", status.Branch);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(2, status.AheadCount);
        Assert.Equal(1, status.BehindCount);

        // src/A.cs modified in worktree only.
        var unstaged = Assert.Single(status.UnstagedChanges);
        Assert.Equal("src/A.cs", unstaged.Path);
        Assert.Equal(GitChangeStatus.Modified, unstaged.WorkTreeStatus);
        Assert.False(unstaged.IsStaged);

        // src/B.cs staged, src/New.cs staged added.
        Assert.Equal(2, status.StagedChanges.Count);
        Assert.Contains(status.StagedChanges, change => change.Path == "src/New.cs" && change.IndexStatus == GitChangeStatus.Added);
        Assert.DoesNotContain(status.StagedChanges, change => change.Path == "src/A.cs");
    }

    [Fact]
    public void Parse_UntrackedFile()
    {
        var output = string.Join('\0', ["# branch.head main", "? notes/todo.txt", ""]);

        var status = GitStatusParser.Parse(output);

        var untracked = Assert.Single(status.UnstagedChanges);
        Assert.Equal("notes/todo.txt", untracked.Path);
        Assert.True(untracked.IsUntracked);
        Assert.Empty(status.StagedChanges);
    }

    [Fact]
    public void Parse_RenamedEntryUsesOriginalPathFromNextRecord()
    {
        // Real porcelain v2 -z output: the record carries the new path, the next record the old path.
        var output = string.Join('\0',
        [
            "# branch.head main",
            "2 R. N... 100644 100644 100644 0 0 R100 new/name.cs",
            "old/name.cs",
            ""
        ]);

        var status = GitStatusParser.Parse(output);

        var renamed = Assert.Single(status.StagedChanges);
        Assert.Equal("new/name.cs", renamed.Path);
        Assert.Equal("old/name.cs", renamed.OriginalPath);
        Assert.Equal(GitChangeStatus.Renamed, renamed.IndexStatus);
    }

    [Fact]
    public void Parse_UnmergedEntryIsFlagged()
    {
        var output = string.Join('\0', ["# branch.head main", "u UU N... 100644 100644 100644 100644 0 0 0 src/conflict.cs", ""]);

        var status = GitStatusParser.Parse(output);

        var conflict = Assert.Single(status.UnstagedChanges);
        Assert.True(conflict.IsUnmerged);
    }

    [Fact]
    public void Parse_DetachedHeadHasNullBranch()
    {
        var output = string.Join('\0', ["# branch.head (detached)", ""]);

        var status = GitStatusParser.Parse(output);

        Assert.Equal("(detached)", status.Branch);
    }

    [Fact]
    public void Parse_CleanRepository()
    {
        var output = string.Join('\0', ["# branch.head main", ""]);

        var status = GitStatusParser.Parse(output);

        Assert.True(status.IsRepository);
        Assert.True(status.IsClean);
        Assert.Empty(status.StagedChanges);
        Assert.Empty(status.UnstagedChanges);
    }

    [Fact]
    public void Parse_PathContainingSpacesIsPreserved()
    {
        var output = string.Join('\0', ["# branch.head main", "? folder with space/file name.txt", ""]);

        var status = GitStatusParser.Parse(output);

        Assert.Equal("folder with space/file name.txt", Assert.Single(status.UnstagedChanges).Path);
    }

    [Fact]
    public void Parse_CapturesHeadAndIndexBlobIdsForDiffRevisionReuse()
    {
        // porcelain v2 field 6 = hH (HEAD blob), field 7 = hI (index blob): carrying them lets the
        // diff-revision identity skip rev-parse + ls-files subprocesses (G6).
        var output = string.Join('\0',
        [
            "# branch.head main",
            "1 .M N... 100644 100644 100644 aaaa1111 bbbb2222 src/A.cs",
            "1 M. N... 100644 100644 100644 cccc3333 dddd4444 src/B.cs",
            ""
        ]);

        var status = GitStatusParser.Parse(output);

        var a = Assert.Single(status.UnstagedChanges);
        Assert.Equal("aaaa1111", a.HeadBlobId);
        Assert.Equal("bbbb2222", a.IndexBlobId);
        var b = Assert.Single(status.StagedChanges, change => change.Path == "src/B.cs");
        Assert.Equal("cccc3333", b.HeadBlobId);
        Assert.Equal("dddd4444", b.IndexBlobId);
        Assert.NotNull(a.HeadBlobId); // sanity: blob ids are non-empty for real records
    }

    [Fact]
    public void Parse_EntryCapMarksTruncatedAndBoundsLists()
    {
        var output = string.Join('\0',
        [
            "# branch.head main",
            "1 .M N... 100644 100644 100644 0 0 src/A.cs",
            "1 M. N... 100644 100644 100644 0 0 src/B.cs",
            "1 .M N... 100644 100644 100644 0 0 src/C.cs",
            "1 M. N... 100644 100644 100644 0 0 src/D.cs",
            ""
        ]);

        var status = GitStatusParser.Parse(output, maxEntries: 2);

        Assert.True(status.Truncated);
        Assert.Equal(2, status.StagedChanges.Count + status.UnstagedChanges.Count);
    }

    [Fact]
    public void Parse_EntryCapNotReached_IsNotTruncated()
    {
        var output = string.Join('\0', ["# branch.head main", "1 .M N... 100644 100644 100644 0 0 src/A.cs", ""]);

        var status = GitStatusParser.Parse(output, maxEntries: 10_000);

        Assert.False(status.Truncated);
        Assert.Single(status.UnstagedChanges);
    }

    [Fact]
    public void Parse_RenameRecord_CarriesBlobIdsAndOriginalPath()
    {
        var output = string.Join('\0',
        [
            "# branch.head main",
            "2 R. N... 100644 100644 100644 eeee5555 ffff6666 R100 new/name.cs",
            "old/name.cs",
            ""
        ]);

        var status = GitStatusParser.Parse(output);

        var renamed = Assert.Single(status.StagedChanges);
        Assert.Equal("new/name.cs", renamed.Path);
        Assert.Equal("old/name.cs", renamed.OriginalPath);
        Assert.Equal("eeee5555", renamed.HeadBlobId);
        Assert.Equal("ffff6666", renamed.IndexBlobId);
    }
}