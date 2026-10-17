using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitDevExLite.Core.Graph;
using GitDevExLite.Core.Models;

namespace GitDevExLite.App.Controls;

/// <summary>
/// Draws the commit graph and commit list in one custom control. Only rows inside the
/// parent ScrollViewer's viewport are drawn, so thousands of commits stay cheap.
/// </summary>
public sealed class CommitGraphControl : Control
{
    private const double RowHeight = 28;
    private const double LaneWidth = 18;
    private const double NodeRadius = 5;
    private const double GraphPadding = 12;
    private const double MinWidth_ = 900;

    private static readonly IBrush[] Palette =
    {
        Brush.Parse("#4FC3F7"), Brush.Parse("#81C784"), Brush.Parse("#FFB74D"), Brush.Parse("#BA68C8"),
        Brush.Parse("#E57373"), Brush.Parse("#4DB6AC"), Brush.Parse("#F06292"), Brush.Parse("#AED581"),
    };

    public static readonly StyledProperty<GraphLayoutResult?> GraphProperty =
        AvaloniaProperty.Register<CommitGraphControl, GraphLayoutResult?>(nameof(Graph));

    public static readonly StyledProperty<Dictionary<string, List<RefInfo>>?> RefsByCommitProperty =
        AvaloniaProperty.Register<CommitGraphControl, Dictionary<string, List<RefInfo>>?>(nameof(RefsByCommit));

    public static readonly StyledProperty<GraphRow?> SelectedRowProperty =
        AvaloniaProperty.Register<CommitGraphControl, GraphRow?>(
            nameof(SelectedRow), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public GraphLayoutResult? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public Dictionary<string, List<RefInfo>>? RefsByCommit { get => GetValue(RefsByCommitProperty); set => SetValue(RefsByCommitProperty, value); }
    public GraphRow? SelectedRow { get => GetValue(SelectedRowProperty); set => SetValue(SelectedRowProperty, value); }

    private ScrollViewer? _scroll;

    static CommitGraphControl()
    {
        AffectsRender<CommitGraphControl>(GraphProperty, RefsByCommitProperty, SelectedRowProperty);
        AffectsMeasure<CommitGraphControl>(GraphProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll != null) _scroll.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroll != null) _scroll.ScrollChanged -= OnScrollChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) =>
        new(MinWidth_, (Graph?.Rows.Count ?? 0) * RowHeight);

    private double GraphWidth => GraphPadding * 2 + (Graph?.LaneCount ?? 1) * LaneWidth;
    private static double LaneX(int lane) => GraphPadding + lane * LaneWidth + LaneWidth / 2;

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        if (Graph is null || Graph.Rows.Count == 0) return;

        var rows = Graph.Rows;
        var top = _scroll?.Offset.Y ?? 0;
        var height = _scroll?.Viewport.Height ?? Bounds.Height;
        var first = Math.Max(0, (int)(top / RowHeight) - 1);
        var last = Math.Min(rows.Count - 1, (int)((top + height) / RowHeight) + 1);

        var textLeft = GraphWidth + 8;
        var width = Math.Max(Bounds.Width, MinWidth_);

        for (var i = first; i <= last; i++)
        {
            var row = rows[i];
            var y = i * RowHeight;
            var mid = y + RowHeight / 2;

            if (ReferenceEquals(row, SelectedRow))
                ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#334FC3F7")), null, new Rect(0, y, width, RowHeight));

            foreach (var e in row.TopEdges) DrawEdge(ctx, e, y, mid);
            foreach (var e in row.BottomEdges) DrawEdge(ctx, e, mid, y + RowHeight);

            var nodeBrush = Palette[row.Color % Palette.Length];
            var center = new Point(LaneX(row.Lane), mid);
            var isMerge = row.Commit.ParentShas.Count > 1;
            ctx.DrawEllipse(isMerge ? Brushes.Transparent : nodeBrush, new Pen(nodeBrush, 2), center, NodeRadius, NodeRadius);

            var x = textLeft;
            if (RefsByCommit != null && RefsByCommit.TryGetValue(row.Commit.Sha, out var refs))
            {
                foreach (var r in refs.OrderBy(r => r.Kind))
                    x += DrawBadge(ctx, r, x, y) + 4;
            }

            var summary = Text(row.Commit.Summary, 13, Brushes.White, Math.Max(50, width - x - 360));
            ctx.DrawText(summary, new Point(x, y + (RowHeight - summary.Height) / 2));

            var meta = Text($"{row.Commit.AuthorName}   {row.Commit.When.LocalDateTime:yyyy-MM-dd HH:mm}   {row.Commit.ShortSha}",
                12, new SolidColorBrush(Color.Parse("#99FFFFFF")), 340);
            ctx.DrawText(meta, new Point(width - 350, y + (RowHeight - meta.Height) / 2));
        }
    }

    private static void DrawEdge(DrawingContext ctx, GraphEdge e, double y0, double y1)
    {
        var pen = new Pen(Palette[e.Color % Palette.Length], 2);
        var p0 = new Point(LaneX(e.FromLane), y0);
        var p1 = new Point(LaneX(e.ToLane), y1);

        if (e.FromLane == e.ToLane)
        {
            ctx.DrawLine(pen, p0, p1);
            return;
        }

        var midY = (y0 + y1) / 2;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(p0, false);
            c.CubicBezierTo(new Point(p0.X, midY), new Point(p1.X, midY), p1);
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    private static double DrawBadge(DrawingContext ctx, RefInfo r, double x, double y)
    {
        var color = r.Kind switch
        {
            RefKind.LocalBranch => "#2E7D32",
            RefKind.RemoteBranch => "#1565C0",
            _ => "#8D6E63"
        };
        var label = Text((r.IsHead ? "● " : "") + r.Name, 11, Brushes.White, 220);
        var w = label.Width + 12;
        ctx.DrawRectangle(Brush.Parse(color), null, new Rect(x, y + 5, w, RowHeight - 10), 4, 4);
        ctx.DrawText(label, new Point(x + 6, y + (RowHeight - label.Height) / 2));
        return w;
    }

    private static FormattedText Text(string s, double size, IBrush brush, double maxWidth) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = RowHeight,
            Trimming = TextTrimming.CharacterEllipsis
        };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Graph is null) return;
        var index = (int)(e.GetPosition(this).Y / RowHeight);
        if (index >= 0 && index < Graph.Rows.Count) SelectedRow = Graph.Rows[index];
    }
}
