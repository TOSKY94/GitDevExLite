using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using GitDevExLite.App.ViewModels;
using GitDevExLite.Core.Models;
using TextMateSharp.Grammars;

namespace GitDevExLite.App.Controls;

/// <summary>Read-only diff viewer with inline and side-by-side layouts and optional hunk/line selection for staging.</summary>
public sealed class DiffView : UserControl
{
    public static readonly StyledProperty<FileDiff?> DiffProperty =
        AvaloniaProperty.Register<DiffView, FileDiff?>(nameof(Diff));

    public static readonly StyledProperty<bool> SideBySideProperty =
        AvaloniaProperty.Register<DiffView, bool>(nameof(SideBySide));

    /// <summary>Shows selection boxes and hunk buttons in the gutter.</summary>
    public static readonly StyledProperty<bool> EditableProperty =
        AvaloniaProperty.Register<DiffView, bool>(nameof(Editable));

    /// <summary>True when the diff is HEAD-to-index, so hunk buttons unstage instead of stage.</summary>
    public static readonly StyledProperty<bool> StagedProperty =
        AvaloniaProperty.Register<DiffView, bool>(nameof(Staged));

    public static readonly StyledProperty<LineSelection?> SelectionProperty =
        AvaloniaProperty.Register<DiffView, LineSelection?>(nameof(Selection));

    public static readonly StyledProperty<ICommand?> HunkCommandProperty =
        AvaloniaProperty.Register<DiffView, ICommand?>(nameof(HunkCommand));

    public FileDiff? Diff { get => GetValue(DiffProperty); set => SetValue(DiffProperty, value); }
    public bool SideBySide { get => GetValue(SideBySideProperty); set => SetValue(SideBySideProperty, value); }
    public bool Editable { get => GetValue(EditableProperty); set => SetValue(EditableProperty, value); }
    public bool Staged { get => GetValue(StagedProperty); set => SetValue(StagedProperty, value); }
    public LineSelection? Selection { get => GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public ICommand? HunkCommand { get => GetValue(HunkCommandProperty); set => SetValue(HunkCommandProperty, value); }

    private static readonly Lazy<RegistryOptions> Registry = new(() => new RegistryOptions(ThemeName.DarkPlus));

    private readonly Grid _host = new();
    private readonly TextBlock _message = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.6
    };
    private Pane? _inline, _left, _right;
    private bool _syncingScroll;
    private LineSelection? _subscribed;

    static DiffView()
    {
        DiffProperty.Changed.AddClassHandler<DiffView>((v, _) => v.Rebuild());
        SideBySideProperty.Changed.AddClassHandler<DiffView>((v, _) => v.Rebuild());
        EditableProperty.Changed.AddClassHandler<DiffView>((v, _) => v.InvalidatePanes());
        StagedProperty.Changed.AddClassHandler<DiffView>((v, _) => v.InvalidatePanes());
        SelectionProperty.Changed.AddClassHandler<DiffView>((v, e) => v.OnSelectionChanged(e));
    }

    public DiffView()
    {
        var root = new Grid();
        root.Children.Add(_host);
        root.Children.Add(_message);
        Content = root;
    }

    private void OnSelectionChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_subscribed is not null) _subscribed.Changed -= OnSelectionMutated;
        _subscribed = e.NewValue as LineSelection;
        if (_subscribed is not null) _subscribed.Changed += OnSelectionMutated;
    }

    private void OnSelectionMutated(object? sender, EventArgs e) => InvalidatePanes();

    private void InvalidatePanes()
    {
        _inline?.Margin.InvalidateVisual();
        _left?.Margin.InvalidateVisual();
        _right?.Margin.InvalidateVisual();
    }

    private void Rebuild()
    {
        var diff = Diff;
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();

        if (diff is null || diff.IsBinary || diff.Hunks.Count == 0)
        {
            _message.Text = diff is null ? "" : diff.IsBinary ? "Binary file" : "No textual changes";
            _message.IsVisible = true;
            return;
        }
        _message.IsVisible = false;

        if (!SideBySide)
        {
            _inline ??= new Pane(this, showOld: true, showNew: true);
            _inline.Load(BuildInline(diff), diff.Path);
            _host.Children.Add(_inline.Editor);
            return;
        }

        _left ??= new Pane(this, showOld: true, showNew: false);
        _right ??= new Pane(this, showOld: false, showNew: true);
        var (l, r) = BuildSideBySide(diff);
        _left.Load(l, diff.Path);
        _right.Load(r, diff.Path);

        _host.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        _host.ColumnDefinitions.Add(new ColumnDefinition(2, GridUnitType.Pixel));
        _host.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        Grid.SetColumn(_left.Editor, 0);
        Grid.SetColumn(_right.Editor, 2);
        var divider = new Border { Background = new SolidColorBrush(Color.Parse("#44FFFFFF")) };
        Grid.SetColumn(divider, 1);
        _host.Children.Add(_left.Editor);
        _host.Children.Add(divider);
        _host.Children.Add(_right.Editor);

        _left.LinkScroll(_right, () => _syncingScroll, v => _syncingScroll = v);
        _right.LinkScroll(_left, () => _syncingScroll, v => _syncingScroll = v);
    }

    private static List<DiffRow> BuildInline(FileDiff diff)
    {
        var rows = new List<DiffRow>();
        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            rows.Add(DiffRow.Header(hunk.Header, h));
            for (var j = 0; j < hunk.Lines.Count; j++)
            {
                var line = hunk.Lines[j];
                if (line.Kind == DiffLineKind.NoNewlineMarker) continue;
                rows.Add(new DiffRow(line.Kind, line.Text.TrimEnd('\r'), h, j, line.OldNo, line.NewNo, false, false));
            }
        }
        return rows;
    }

    private static (List<DiffRow>, List<DiffRow>) BuildSideBySide(FileDiff diff)
    {
        var left = new List<DiffRow>();
        var right = new List<DiffRow>();
        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            left.Add(DiffRow.Header(hunk.Header, h));
            right.Add(DiffRow.Header(hunk.Header, h));

            var i = 0;
            while (i < hunk.Lines.Count)
            {
                var line = hunk.Lines[i];
                if (line.Kind == DiffLineKind.NoNewlineMarker) { i++; continue; }
                if (line.Kind == DiffLineKind.Context)
                {
                    var row = new DiffRow(DiffLineKind.Context, line.Text.TrimEnd('\r'), h, i, line.OldNo, line.NewNo, false, false);
                    left.Add(row);
                    right.Add(row);
                    i++;
                    continue;
                }

                var removed = new List<int>();
                var added = new List<int>();
                while (i < hunk.Lines.Count && hunk.Lines[i].Kind == DiffLineKind.Removed) removed.Add(i++);
                while (i < hunk.Lines.Count && hunk.Lines[i].Kind == DiffLineKind.Added) added.Add(i++);

                for (var k = 0; k < Math.Max(removed.Count, added.Count); k++)
                {
                    left.Add(k < removed.Count ? ToRow(h, removed[k], hunk.Lines[removed[k]]) : DiffRow.Padding(h));
                    right.Add(k < added.Count ? ToRow(h, added[k], hunk.Lines[added[k]]) : DiffRow.Padding(h));
                }
            }
        }
        return (left, right);

        static DiffRow ToRow(int h, int j, DiffLine l) =>
            new(l.Kind, l.Text.TrimEnd('\r'), h, j, l.OldNo, l.NewNo, false, false);
    }

    internal readonly record struct DiffRow(
        DiffLineKind Kind, string Text, int Hunk, int Line, int? OldNo, int? NewNo, bool IsHeader, bool IsPadding)
    {
        public static DiffRow Header(string text, int hunk) =>
            new(DiffLineKind.Context, text, hunk, -1, null, null, true, false);

        public static DiffRow Padding(int hunk) =>
            new(DiffLineKind.Context, "", hunk, -1, null, null, false, true);

        public bool IsChange => !IsHeader && !IsPadding && Kind is DiffLineKind.Added or DiffLineKind.Removed;
    }

    private sealed class Pane
    {
        public TextEditor Editor { get; }
        public GutterMargin Margin { get; }
        public List<DiffRow> Rows { get; private set; } = new();

        private readonly TextMate.Installation _textMate;
        private string? _grammarExtension = "\0";
        private EventHandler? _scrollHandler;

        public Pane(DiffView owner, bool showOld, bool showNew)
        {
            Editor = new TextEditor
            {
                IsReadOnly = true,
                ShowLineNumbers = false,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                FontSize = 12,
                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
            Editor.Options.EnableHyperlinks = false;
            Editor.Options.EnableEmailHyperlinks = false;
            Editor.TextArea.Caret.CaretBrush = Brushes.Transparent;

            var view = Editor.TextArea.TextView;
            view.BackgroundRenderers.Add(new RowBackgroundRenderer(this));
            view.LineTransformers.Add(new HeaderColorizer(this));

            Margin = new GutterMargin(owner, this, showOld, showNew);
            Editor.TextArea.LeftMargins.Add(Margin);

            _textMate = Editor.InstallTextMate(Registry.Value);
        }

        public void Load(List<DiffRow> rows, string path)
        {
            Rows = rows;
            Editor.Document = new TextDocument(string.Join("\n", rows.Select(r => r.Text)));

            var ext = Path.GetExtension(path);
            if (ext != _grammarExtension)
            {
                _grammarExtension = ext;
                var lang = string.IsNullOrEmpty(ext) ? null : Registry.Value.GetLanguageByExtension(ext);
                _textMate.SetGrammar(lang is null ? null : Registry.Value.GetScopeByLanguageId(lang.Id));
            }
            Editor.ScrollToHome();
            Margin.InvalidateVisual();
        }

        public void LinkScroll(Pane other, Func<bool> isSyncing, Action<bool> setSyncing)
        {
            var view = Editor.TextArea.TextView;
            if (_scrollHandler is not null) view.ScrollOffsetChanged -= _scrollHandler;
            _scrollHandler = (_, _) =>
            {
                if (isSyncing()) return;
                setSyncing(true);
                try
                {
                    var offset = view.ScrollOffset;
                    other.Editor.ScrollToVerticalOffset(offset.Y);
                    other.Editor.ScrollToHorizontalOffset(offset.X);
                }
                finally { setSyncing(false); }
            };
            view.ScrollOffsetChanged += _scrollHandler;
        }
    }

    private sealed class RowBackgroundRenderer : IBackgroundRenderer
    {
        private static readonly IBrush Added = new SolidColorBrush(Color.Parse("#2A2E7D32"));
        private static readonly IBrush Removed = new SolidColorBrush(Color.Parse("#2AC62828"));
        private static readonly IBrush Header = new SolidColorBrush(Color.Parse("#22448AFF"));
        private static readonly IBrush Padding = new SolidColorBrush(Color.Parse("#14FFFFFF"));

        private readonly Pane _pane;
        public RowBackgroundRenderer(Pane pane) => _pane = pane;

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (!textView.VisualLinesValid) return;
            foreach (var vl in textView.VisualLines)
            {
                var n = vl.FirstDocumentLine.LineNumber;
                if (n > _pane.Rows.Count) continue;
                var row = _pane.Rows[n - 1];
                IBrush? brush = row.IsHeader ? Header
                    : row.IsPadding ? Padding
                    : row.Kind == DiffLineKind.Added ? Added
                    : row.Kind == DiffLineKind.Removed ? Removed
                    : null;
                if (brush is null) continue;
                var y = vl.VisualTop - textView.ScrollOffset.Y;
                context.DrawRectangle(brush, null, new Rect(0, y, Math.Max(textView.Bounds.Width, textView.DocumentHeight), vl.Height));
            }
        }
    }

    private sealed class HeaderColorizer : DocumentColorizingTransformer
    {
        private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8AB4F8"));
        private readonly Pane _pane;
        public HeaderColorizer(Pane pane) => _pane = pane;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.LineNumber > _pane.Rows.Count || !_pane.Rows[line.LineNumber - 1].IsHeader) return;
            ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(Muted));
        }
    }

    private sealed class GutterMargin : AbstractMargin
    {
        private const double NumberWidth = 40;
        private const double BoxArea = 22;

        private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#77FFFFFF"));
        private static readonly IBrush BoxBrush = new SolidColorBrush(Color.Parse("#8AB4F8"));

        private readonly DiffView _owner;
        private readonly Pane _pane;
        private readonly bool _showOld, _showNew;
        private TextView? _textView;

        public GutterMargin(DiffView owner, Pane pane, bool showOld, bool showNew)
        {
            _owner = owner;
            _pane = pane;
            _showOld = showOld;
            _showNew = showNew;
        }

        private double BoxLeft => (_showOld ? NumberWidth : 0) + (_showNew ? NumberWidth : 0);

        protected override Size MeasureOverride(Size availableSize) => new(BoxLeft + BoxArea, 0);

        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (_textView is not null)
            {
                _textView.VisualLinesChanged -= OnViewChanged;
                _textView.ScrollOffsetChanged -= OnViewChanged;
            }
            _textView = newTextView;
            if (_textView is not null)
            {
                _textView.VisualLinesChanged += OnViewChanged;
                _textView.ScrollOffsetChanged += OnViewChanged;
            }
            base.OnTextViewChanged(oldTextView, newTextView);
        }

        private void OnViewChanged(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            var view = _textView;
            if (view is null || !view.VisualLinesValid) return;

            var typeface = new Typeface(_pane.Editor.FontFamily);
            var selection = _owner.Selection;

            foreach (var vl in view.VisualLines)
            {
                var n = vl.FirstDocumentLine.LineNumber;
                if (n > _pane.Rows.Count) continue;
                var row = _pane.Rows[n - 1];
                var y = vl.VisualTop - view.ScrollOffset.Y;

                var x = 0.0;
                if (_showOld) DrawNumber(context, typeface, row.OldNo, x, y, vl.Height);
                if (_showOld) x += NumberWidth;
                if (_showNew) DrawNumber(context, typeface, row.NewNo, x, y, vl.Height);

                if (!_owner.Editable) continue;

                if (row.IsHeader)
                {
                    var glyph = Text(_owner.Staged ? "−" : "+", typeface, 13, BoxBrush);
                    context.DrawText(glyph, new Point(BoxLeft + (BoxArea - glyph.Width) / 2, y + (vl.Height - glyph.Height) / 2));
                }
                else if (row.IsChange)
                {
                    var box = new Rect(BoxLeft + 5, y + (vl.Height - 11) / 2, 11, 11);
                    var on = selection?.Lines.Contains(new LineRef(row.Hunk, row.Line)) == true;
                    context.DrawRectangle(on ? BoxBrush : null, new Pen(BoxBrush, 1), box, 2, 2);
                }
            }
        }

        private static void DrawNumber(DrawingContext context, Typeface typeface, int? no, double x, double y, double height)
        {
            if (no is null) return;
            var t = Text(no.Value.ToString(CultureInfo.InvariantCulture), typeface, 11, NumberBrush);
            context.DrawText(t, new Point(x + NumberWidth - t.Width - 6, y + (height - t.Height) / 2));
        }

        private static FormattedText Text(string s, Typeface typeface, double size, IBrush brush) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var view = _textView;
            if (!_owner.Editable || view is null || !view.VisualLinesValid) return;

            var p = e.GetPosition(this);
            if (p.X < BoxLeft) return;

            var vl = view.GetVisualLineFromVisualTop(p.Y + view.ScrollOffset.Y);
            if (vl is null) return;
            var n = vl.FirstDocumentLine.LineNumber;
            if (n > _pane.Rows.Count) return;
            var row = _pane.Rows[n - 1];

            if (row.IsHeader)
            {
                if (_owner.HunkCommand is { } cmd && cmd.CanExecute(row.Hunk)) cmd.Execute(row.Hunk);
            }
            else if (row.IsChange)
            {
                _owner.Selection?.Toggle(new LineRef(row.Hunk, row.Line));
            }
            e.Handled = true;
        }
    }
}
