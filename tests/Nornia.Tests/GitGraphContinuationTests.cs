using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;
using Nornia.Core.Models;

namespace Nornia.Tests;

public sealed class GitGraphContinuationTests
{
    [Fact]
    public void MergeExpansion_ContinuesEveryParentLane()
    {
        var graph = GitViewModel.BuildCommitGraph([
            Commit("merge", "main side"), Commit("main", "root"),
            Commit("side", "root"), Commit("root", null)]);
        var merge = graph[0];
        var outgoing = GitGraphContinuation.OutgoingLanes(merge.PassLanes, merge.Links.Select(x => x.ToLane));
        Assert.Equal(2, outgoing.Count);
        Assert.All(merge.Links, link => Assert.Contains(link.ToLane, outgoing));
    }

    [Fact]
    public void TerminatedDotLane_IsNotExtendedIntoFileList()
    {
        var row = new GitGraphRow(1, [2], [new GitGraphLink(1, 0)], 3, true);
        Assert.Equal(new[] { 0, 2 }, GitGraphContinuation.OutgoingLanes(row.PassLanes, row.Links.Select(x => x.ToLane)));
    }

    [Fact]
    public void RootExpansion_HasNoDanglingDotLine()
    {
        Assert.Empty(GitGraphContinuation.OutgoingLanes([], []));
        Assert.Equal(new[] { 2 }, GitGraphContinuation.OutgoingLanes([2], []));
    }

    [Fact]
    public void SharedParentLane_IsDrawnOnlyOnce()
    {
        Assert.Equal(new[] { 0, 1, 2 }, GitGraphContinuation.OutgoingLanes([1, 2], [0, 1, 2]));
    }

    private static GitCommitInfo Commit(string hash, string? parents) =>
        new(hash, hash, hash, null, "A", "a@x", DateTimeOffset.UtcNow,
            parents?.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
