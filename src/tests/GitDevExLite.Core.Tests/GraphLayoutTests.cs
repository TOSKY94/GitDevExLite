using GitDevExLite.Core.Graph;
using GitDevExLite.Core.Models;
using Xunit;

namespace GitDevExLite.Core.Tests;

public class GraphLayoutTests
{
    private static CommitInfo C(string sha, params string[] parents) =>
        new(sha, sha, sha, "t", "t@t", DateTimeOffset.UnixEpoch, parents);

    [Fact]
    public void LinearHistory_StaysInLaneZero()
    {
        var r = GraphLayout.Build(new[] { C("c", "b"), C("b", "a"), C("a") });
        Assert.All(r.Rows, row => Assert.Equal(0, row.Lane));
        Assert.Equal(1, r.LaneCount);
    }

    [Fact]
    public void Merge_OpensSecondLane_AndConverges()
    {
        var r = GraphLayout.Build(new[] { C("M", "B", "C"), C("B", "R"), C("C", "R"), C("R") });

        Assert.Equal(0, r.Rows[0].Lane);
        Assert.Equal(0, r.Rows[1].Lane);
        Assert.Equal(1, r.Rows[2].Lane);
        Assert.Equal(0, r.Rows[3].Lane);
        Assert.Equal(2, r.LaneCount);
        Assert.Contains(r.Rows[2].BottomEdges, e => e.FromLane == 1 && e.ToLane == 0);
    }

    [Fact]
    public void ForkedBranches_ConvergeOnSharedParent()
    {
        var r = GraphLayout.Build(new[] { C("X", "P"), C("Y", "P"), C("P") });
        Assert.Equal(0, r.Rows[0].Lane);
        Assert.Equal(1, r.Rows[1].Lane);
        Assert.Equal(0, r.Rows[2].Lane);
        Assert.Contains(r.Rows[2].TopEdges, e => e.FromLane == 1 && e.ToLane == 0);
    }
}
