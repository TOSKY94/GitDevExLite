using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Graph;

/// <summary>
/// Lane assignment for a commit list ordered newest-first AND topologically (children before
/// parents). Each lane "waits" for the SHA of the next commit expected on it.
/// </summary>
public static class GraphLayout
{
    public static GraphLayoutResult Build(IReadOnlyList<CommitInfo> commits, int paletteSize = 8)
    {
        var lanes = new List<string?>();
        var laneColors = new List<int>();
        var rows = new List<GraphRow>(commits.Count);
        var nextColor = 0;
        var maxLanes = 1;

        int NewColor() => nextColor++ % paletteSize;

        int FirstFreeLane()
        {
            var i = lanes.IndexOf(null);
            if (i >= 0) return i;
            lanes.Add(null);
            laneColors.Add(0);
            return lanes.Count - 1;
        }

        foreach (var commit in commits)
        {
            var matching = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] == commit.Sha) matching.Add(i);

            int nodeLane, nodeColor;
            if (matching.Count == 0)
            {
                nodeLane = FirstFreeLane();
                nodeColor = NewColor();
                laneColors[nodeLane] = nodeColor;
            }
            else
            {
                nodeLane = matching[0];
                nodeColor = laneColors[nodeLane];
            }

            // Top half: everything occupied before this row; matching lanes converge on the node.
            var top = new List<GraphEdge>();
            var occupiedBefore = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] == null) continue;
                occupiedBefore.Add(i);
                var to = lanes[i] == commit.Sha ? nodeLane : i;
                top.Add(new GraphEdge(i, to, laneColors[i]));
            }

            foreach (var i in matching) lanes[i] = null;

            // Bottom half: node -> parents, plus pass-through lanes.
            var bottom = new List<GraphEdge>();
            for (var p = 0; p < commit.ParentShas.Count; p++)
            {
                var parent = commit.ParentShas[p];
                var existing = lanes.IndexOf(parent);
                if (existing >= 0)
                {
                    bottom.Add(new GraphEdge(nodeLane, existing, laneColors[existing]));
                }
                else if (p == 0)
                {
                    lanes[nodeLane] = parent;
                    laneColors[nodeLane] = nodeColor;
                    bottom.Add(new GraphEdge(nodeLane, nodeLane, nodeColor));
                }
                else
                {
                    var k = FirstFreeLane();
                    lanes[k] = parent;
                    laneColors[k] = NewColor();
                    bottom.Add(new GraphEdge(nodeLane, k, laneColors[k]));
                }
            }

            foreach (var i in occupiedBefore)
                if (!matching.Contains(i))
                    bottom.Add(new GraphEdge(i, i, laneColors[i]));

            maxLanes = Math.Max(maxLanes, Math.Max(lanes.Count, nodeLane + 1));
            rows.Add(new GraphRow(commit, nodeLane, nodeColor, top, bottom));

            while (lanes.Count > 0 && lanes[^1] == null)
            {
                lanes.RemoveAt(lanes.Count - 1);
                laneColors.RemoveAt(laneColors.Count - 1);
            }
        }

        return new GraphLayoutResult(rows, maxLanes);
    }
}
