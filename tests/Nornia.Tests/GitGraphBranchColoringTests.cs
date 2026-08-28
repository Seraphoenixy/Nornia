using Nornia.Core.Models;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary><see cref="GitViewModel.BuildCommitGraph(IReadOnlyList{GitCommitInfo}, IReadOnlyList{GitBranchInfo}?)"/>
/// 的分支语义着色测试:当前分支高亮(GraphCurrentBranchBrush)、本地分支取 GraphLane* 色相、
/// 远端分支取 GraphRemote* 色相、同 tip 按优先级(当前 &gt; 本地 &gt; 远端)取一色;
/// 不传分支时 LaneColorKeys 保持 null,单元格回退 lane % 6 的既有行为。</summary>
public sealed class GitGraphBranchColoringTests
{
    private static GitCommitInfo Commit(string hash, params string[] parents) =>
        new(hash, hash, hash, null, "A", "a@x", DateTimeOffset.UtcNow, parents);

    [Fact]
    public void CurrentBranchTipRow_UsesCurrentBranchHighlight()
    {
        var commits = new[]
        {
            Commit("c1", "c0"),
            Commit("c0"),
        };
        var branches = new[]
        {
            new GitBranchInfo("main", IsCurrent: true, TipHash: "c1"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        var tip = rows[0];
        Assert.Equal("GraphCurrentBranchBrush", tip.LaneColorKeys![tip.DotLane]);
        // 泳道延续的后续行保留起点色键(一条分支一条线一种色)。
        Assert.Equal("GraphCurrentBranchBrush", rows[1].LaneColorKeys![tip.DotLane]);
    }

    [Fact]
    public void LocalBranchTipRow_UsesLocalLanePalette()
    {
        var commits = new[]
        {
            Commit("c1", "c0"),
            Commit("c0"),
        };
        var branches = new[]
        {
            new GitBranchInfo("dev", IsCurrent: false, TipHash: "c1"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        var key = rows[0].LaneColorKeys![rows[0].DotLane];
        Assert.StartsWith("GraphLane", key, StringComparison.Ordinal);
        Assert.DoesNotContain("Remote", key, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteBranchTipRow_UsesRemotePalette()
    {
        var commits = new[]
        {
            Commit("c1", "c0"),
            Commit("c0"),
        };
        var branches = new[]
        {
            new GitBranchInfo("origin/dev", IsCurrent: false, IsRemote: true, TipHash: "c1"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        var key = rows[0].LaneColorKeys![rows[0].DotLane];
        Assert.StartsWith("GraphRemote", key, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoLocalBranchTips_UseDifferentLaneKeys()
    {
        // main 与 dev 的名称稳定哈希取余分别为 0 与 2(对应 GraphLane1 / GraphLane3),不撞色。
        var commits = new[]
        {
            Commit("b1", "base"),
            Commit("b2", "base"),
            Commit("base"),
        };
        var branches = new[]
        {
            new GitBranchInfo("main", IsCurrent: false, TipHash: "b1"),
            new GitBranchInfo("dev", IsCurrent: false, TipHash: "b2"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        var mainKey = rows[0].LaneColorKeys![rows[0].DotLane];
        var devKey = rows[1].LaneColorKeys![rows[1].DotLane];
        Assert.StartsWith("GraphLane", mainKey, StringComparison.Ordinal);
        Assert.StartsWith("GraphLane", devKey, StringComparison.Ordinal);
        Assert.NotEqual(mainKey, devKey);
    }

    [Fact]
    public void MixedLocalAndRemoteDivergedTips_ColorEachOwnLane()
    {
        // 本地 main(当前)与远端 origin/dev 从同一提交分叉:各自泳道分别取当前分支高亮与远端色相。
        var commits = new[]
        {
            Commit("c1", "base"),
            Commit("c2", "base"),
            Commit("base"),
        };
        var branches = new[]
        {
            new GitBranchInfo("main", IsCurrent: true, TipHash: "c1"),
            new GitBranchInfo("origin/dev", IsCurrent: false, IsRemote: true, TipHash: "c2"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        Assert.Equal("GraphCurrentBranchBrush", rows[0].LaneColorKeys![rows[0].DotLane]);
        Assert.StartsWith("GraphRemote", rows[1].LaneColorKeys![rows[1].DotLane], StringComparison.Ordinal);
    }

    [Fact]
    public void SameTip_CurrentBranchWinsOverRemote()
    {
        var commits = new[]
        {
            Commit("c1", "c0"),
            Commit("c0"),
        };
        var branches = new[]
        {
            new GitBranchInfo("origin/main", IsCurrent: false, IsRemote: true, TipHash: "c1"),
            new GitBranchInfo("main", IsCurrent: true, TipHash: "c1"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        Assert.Equal("GraphCurrentBranchBrush", rows[0].LaneColorKeys![rows[0].DotLane]);
    }

    [Fact]
    public void SameTip_LocalBranchWinsOverRemote()
    {
        var commits = new[]
        {
            Commit("c1", "c0"),
            Commit("c0"),
        };
        var branches = new[]
        {
            new GitBranchInfo("origin/dev", IsCurrent: false, IsRemote: true, TipHash: "c1"),
            new GitBranchInfo("dev", IsCurrent: false, TipHash: "c1"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        Assert.StartsWith("GraphLane", rows[0].LaneColorKeys![rows[0].DotLane], StringComparison.Ordinal);
    }

    [Fact]
    public void NullBranches_LeaveLaneColorKeysNull()
    {
        var commits = new[]
        {
            Commit("b1", "base"),
            Commit("b2", "base"),
            Commit("base"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits);

        Assert.All(rows, row =>
        {
            Assert.Null(row.LaneColorKeys);
            Assert.Null(row.LaneIncomingColorKeys);
        });
    }

    [Fact]
    public void SegmentSwitch_ColorChangesAtTheDot_LineBetweenDotsStaysSingleColored()
    {
        // 线的颜色由端点决定:相邻两个圆点之间的连线全程单色(上行下半段取上行出色,
        // 本行上半段取入色 = 上一行出色),颜色切换精确落在 tip 圆点上。
        var commits = new[]
        {
            Commit("c4", "c3"),
            Commit("c3", "c2"),
            Commit("c2", "c1"),
            Commit("c1"),
        };
        var branches = new[]
        {
            new GitBranchInfo("main", IsCurrent: true, TipHash: "c4"),
            new GitBranchInfo("origin/main", IsCurrent: false, IsRemote: true, TipHash: "c2"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        // 首行(c4, main tip):入色=出色=当前分支色。
        Assert.Equal("GraphCurrentBranchBrush", rows[0].LaneColorKeys![0]);
        Assert.Equal("GraphCurrentBranchBrush", rows[0].LaneIncomingColorKeys![0]);
        // c3 行(tip 之上):入=出=当前分支色。
        Assert.Equal("GraphCurrentBranchBrush", rows[1].LaneColorKeys![0]);
        Assert.Equal("GraphCurrentBranchBrush", rows[1].LaneIncomingColorKeys![0]);
        // c2 行(origin/main tip):圆点起为远端色(出色),但上方接入仍是当前分支色
        // → c3→c2 连线全程当前分支色,切换发生在 c2 圆点。
        var remoteKey = rows[2].LaneColorKeys![0];
        Assert.StartsWith("GraphRemote", remoteKey, StringComparison.Ordinal);
        Assert.Equal("GraphCurrentBranchBrush", rows[2].LaneIncomingColorKeys![0]);
        // c1 行:入=出=远端色 → c2→c1 连线全程远端色。
        Assert.Equal(remoteKey, rows[3].LaneColorKeys![0]);
        Assert.Equal(remoteKey, rows[3].LaneIncomingColorKeys![0]);
    }

    [Fact]
    public void LinearTracking_RemoteTipSwitchesLaneSegmentToRemoteColor()
    {
        // Nornia 实仓拓扑:线性历史,main(当前)领先 origin/main 三个提交。
        // 分段着色:origin/main tip 行以上为当前分支高亮,tip 行(含圆点)起切换为远端色并延续到底。
        var commits = new[]
        {
            Commit("c6", "c5"),
            Commit("c5", "c4"),
            Commit("c4", "c3"),
            Commit("c3", "c2"),
            Commit("c2", "c1"),
            Commit("c1"),
        };
        var branches = new[]
        {
            new GitBranchInfo("main", IsCurrent: true, TipHash: "c6"),
            new GitBranchInfo("origin/main", IsCurrent: false, IsRemote: true, TipHash: "c3"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        // tip 行以上:当前分支高亮。
        Assert.All(rows.Take(3), row => Assert.Equal("GraphCurrentBranchBrush", row.LaneColorKeys![row.DotLane]));
        // tip 行(第 3 行, c3)起:远端色族,且整段同一色键。
        Assert.StartsWith("GraphRemote", rows[3].LaneColorKeys![rows[3].DotLane], StringComparison.Ordinal);
        var segmentKey = rows[3].LaneColorKeys![rows[3].DotLane];
        Assert.All(rows.Skip(3), row => Assert.Equal(segmentKey, row.LaneColorKeys![row.DotLane]));
    }

    [Fact]
    public void DeeperLocalTipOnSameLane_SwitchesSegmentToLocalBranchColor()
    {
        // 分段着色不限于远端:更深的本地分支 tip 同样切换泳道段颜色。
        var commits = new[]
        {
            Commit("c3", "c2"),
            Commit("c2", "c1"),
            Commit("c1"),
        };
        var branches = new[]
        {
            new GitBranchInfo("main", IsCurrent: true, TipHash: "c3"),
            new GitBranchInfo("dev", IsCurrent: false, TipHash: "c1"),
        };

        var rows = GitViewModel.BuildCommitGraph(commits, branches);

        Assert.Equal("GraphCurrentBranchBrush", rows[0].LaneColorKeys![rows[0].DotLane]);
        Assert.Equal("GraphCurrentBranchBrush", rows[1].LaneColorKeys![rows[1].DotLane]);
        var devKey = rows[2].LaneColorKeys![rows[2].DotLane];
        Assert.StartsWith("GraphLane", devKey, StringComparison.Ordinal);
        Assert.NotEqual("GraphCurrentBranchBrush", devKey);
    }
}
