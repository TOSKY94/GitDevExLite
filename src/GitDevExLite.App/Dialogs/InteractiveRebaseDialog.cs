using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.Dialogs;

/// <summary>Edits an interactive rebase todo: action per commit, order, and the messages for rewords and squashes.</summary>
public sealed class InteractiveRebaseDialog : Window
{
    private sealed class Row
    {
        public required RebaseEntry Entry { get; init; }
        public RebaseAction Action { get; set; } = RebaseAction.Pick;
    }

    private readonly List<Row> _rows;
    private readonly Dictionary<string, string> _edited = new();
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly TextBlock _error = new() { Foreground = Avalonia.Media.Brushes.OrangeRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _start = new() { Content = "Start rebase", IsDefault = true };

    public RebaseRequest? Result { get; private set; }

    public InteractiveRebaseDialog(IReadOnlyList<RebaseEntry> entries, string description)
    {
        _rows = entries.Select(e => new Row { Entry = e }).ToList();

        Title = "Interactive rebase";
        Width = 760;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        _start.Click += (_, _) =>
        {
            Result = BuildRequest();
            Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { _start, cancel }
        };
        var footer = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { _error, buttons } };
        DockPanel.SetDock(footer, Dock.Bottom);

        var header = new TextBlock
        {
            Text = $"Rebase {description}. Commits are applied from the top (oldest) down.\n" +
                   "Squash and fixup merge a commit into the one above it; fixup discards its message.",
            Margin = new Thickness(12),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        DockPanel.SetDock(header, Dock.Top);

        Content = new DockPanel
        {
            Children =
            {
                header,
                footer,
                new ScrollViewer { Content = _list, Padding = new Thickness(12, 0) }
            }
        };

        Render();
    }

    private IReadOnlyList<RebaseEntry> CurrentEntries() =>
        _rows.Select(r => r.Entry with { Action = r.Action }).ToList();

    private RebaseRequest BuildRequest()
    {
        var entries = CurrentEntries();
        var messages = new Dictionary<string, string>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Action is RebaseAction.Squash or RebaseAction.Fixup or RebaseAction.Drop) continue;
            if (!RebasePlanner.NeedsMessage(entries, i)) continue;
            var sha = entries[i].Sha;
            messages[sha] = _edited.TryGetValue(sha, out var text) ? text : RebasePlanner.DefaultMessage(entries, i);
        }
        return new RebaseRequest(entries, messages);
    }

    private void Render()
    {
        _list.Children.Clear();
        var entries = CurrentEntries();

        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var index = i;

            var action = new ComboBox { ItemsSource = Enum.GetValues<RebaseAction>(), SelectedItem = row.Action, Width = 110 };
            action.SelectionChanged += (_, _) =>
            {
                if (action.SelectedItem is not RebaseAction chosen || chosen == row.Action) return;
                row.Action = chosen;
                // Rebuilding inside the handler would dispose the control that raised it.
                Dispatcher.UIThread.Post(Render);
            };

            var up = new Button { Content = "\u25B2", IsEnabled = i > 0, Padding = new Thickness(8, 2) };
            up.Click += (_, _) => Move(index, -1);
            var down = new Button { Content = "\u25BC", IsEnabled = i < _rows.Count - 1, Padding = new Thickness(8, 2) };
            down.Click += (_, _) => Move(index, 1);

            var text = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = row.Entry.Sha[..7], FontFamily = new Avalonia.Media.FontFamily("Consolas,Menlo,monospace"), Opacity = 0.6 },
                    new TextBlock { Text = row.Entry.Summary }
                }
            };

            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Opacity = row.Action == RebaseAction.Drop ? 0.45 : 1 };
            Grid.SetColumn(text, 1);
            Grid.SetColumn(up, 2);
            Grid.SetColumn(down, 3);
            text.Margin = new Thickness(8, 0);
            line.Children.Add(action);
            line.Children.Add(text);
            line.Children.Add(up);
            line.Children.Add(down);
            _list.Children.Add(line);

            if (row.Action is RebaseAction.Squash or RebaseAction.Fixup or RebaseAction.Drop) continue;
            if (!RebasePlanner.NeedsMessage(entries, i)) continue;

            var sha = row.Entry.Sha;
            var box = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MinHeight = 80,
                Margin = new Thickness(118, 0, 0, 6),
                Text = _edited.TryGetValue(sha, out var edited) ? edited : RebasePlanner.DefaultMessage(entries, i),
                Watermark = "Commit message"
            };
            box.TextChanged += (_, _) => _edited[sha] = box.Text ?? "";
            _list.Children.Add(box);
        }

        var error = RebasePlanner.Validate(entries);
        _error.Text = error ?? "";
        _start.IsEnabled = error is null;
    }

    private void Move(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _rows.Count) return;
        (_rows[index], _rows[target]) = (_rows[target], _rows[index]);
        Render();
    }
}
