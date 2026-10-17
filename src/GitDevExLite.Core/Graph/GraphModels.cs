using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Graph;

/// <summary>A line segment inside one row. Top edges run from the row's top to its vertical
/// middle (where the commit node sits); bottom edges run from the middle to the row's bottom.</summary>
public sealed record GraphEdge(int FromLane, int ToLane, int Color);

public sealed record GraphRow(
    CommitInfo Commit,
    int Lane,
    int Color,
    IReadOnlyList<GraphEdge> TopEdges,
    IReadOnlyList<GraphEdge> BottomEdges);

public sealed record GraphLayoutResult(IReadOnlyList<GraphRow> Rows, int LaneCount);
