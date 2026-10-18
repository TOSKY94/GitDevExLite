using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using GitDevExLite.App.ViewModels;
using TextMateSharp.Grammars;

namespace GitDevExLite.App.Controls;

/// <summary>Three-pane conflict editor: ours and theirs on top (read-only), the editable result below.</summary>
public sealed class ConflictView : UserControl
{
    private static readonly Lazy<RegistryOptions> Registry = new(() => new RegistryOptions(ThemeName.DarkPlus));

    private readonly TextEditor _ours = MakeEditor(readOnly: true);
    private readonly TextEditor _theirs = MakeEditor(readOnly: true);
    private readonly TextEditor _result = MakeEditor(readOnly: false);
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _oursHeader = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 2) };
    private readonly TextBlock _theirsHeader = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 2) };
    private readonly Button _acceptOurs = new() { Content = "Accept ours" };
    private readonly Button _acceptTheirs = new() { Content = "Accept theirs" };
    private readonly Button _acceptBoth = new() { Content = "Accept both" };
    private readonly Button _previous = new() { Content = "\u25B2" };
    private readonly Button _next = new() { Content = "\u25BC" };
    private readonly Button _reset = new() { Content = "Reset result" };
    private readonly Button _useOurs = new();
    private readonly Button _useTheirs = new();
    private readonly Button _resolved = new() { Content = "Mark resolved" };
    private readonly Control _blockButtons;
    private readonly Control _editors;
    private readonly TextMate.Installation[] _highlighters = new TextMate.Installation[3];
    private ConflictViewModel? _vm;
    private bool _syncing;

    public ConflictView()
    {
        _result.TextArea.TextView.LineTransformers.Add(new MarkerColorizer());
        _highlighters[0] = _ours.InstallTextMate(Registry.Value);
        _highlighters[1] = _theirs.InstallTextMate(Registry.Value);
        _highlighters[2] = _result.InstallTextMate(Registry.Value);

        _blockButtons = Wrap(_previous, _next, _acceptOurs, _acceptTheirs, _acceptBoth, _reset);
        var toolbar = new StackPanel { Margin = new Thickness(6, 4), Spacing = 3 };
        toolbar.Children.Add(Wrap(_title, _summary));
        toolbar.Children.Add(_blockButtons);
        toolbar.Children.Add(Wrap(_useOurs, _useTheirs, _resolved));

        var oursPane = Pane(_oursHeader, _ours);
        var theirsPane = Pane(_theirsHeader, _theirs);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
        Grid.SetColumn(oursPane, 0);
        Grid.SetColumn(theirsPane, 2);
        top.Children.Add(oursPane);
        top.Children.Add(new GridSplitter { [Grid.ColumnProperty] = 1 });
        top.Children.Add(theirsPane);

        var resultPane = Pane(new TextBlock { Text = "Result (editable)", FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 2) }, _result);
        var split = new Grid { RowDefinitions = new RowDefinitions("*,4,2*") };
        Grid.SetRow(top, 0);
        Grid.SetRow(resultPane, 2);
        split.Children.Add(top);
        split.Children.Add(new GridSplitter { [Grid.RowProperty] = 1, HorizontalAlignment = HorizontalAlignment.Stretch });
        split.Children.Add(resultPane);
        _editors = split;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(split, 1);
        root.Children.Add(toolbar);
        root.Children.Add(split);
        Content = root;

        _result.TextChanged += OnResultTyped;
        DataContextChanged += (_, _) => Attach(DataContext as ConflictViewModel);
    }

    private static TextEditor MakeEditor(bool readOnly)
    {
        var editor = new TextEditor
        {
            IsReadOnly = readOnly,
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        return editor;
    }

    private static WrapPanel Wrap(params Control[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            child.Margin = new Thickness(0, 0, 6, 2);
            panel.Children.Add(child);
        }
        return panel;
    }

    private static Grid Pane(Control header, Control editor)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(editor, 1);
        grid.Children.Add(header);
        grid.Children.Add(editor);
        return grid;
    }

    private void Attach(ConflictViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.FocusLineRequested -= OnFocusLine;
        }
        _vm = vm;
        if (vm is null) return;

        vm.PropertyChanged += OnViewModelChanged;
        vm.FocusLineRequested += OnFocusLine;

        _title.Text = vm.Path;
        _oursHeader.Text = vm.OursLabel;
        _theirsHeader.Text = vm.TheirsLabel;
        _useOurs.Content = vm.UseOursText;
        _useTheirs.Content = vm.UseTheirsText;
        _acceptOurs.Command = vm.AcceptOursCommand;
        _acceptTheirs.Command = vm.AcceptTheirsCommand;
        _acceptBoth.Command = vm.AcceptBothCommand;
        _previous.Command = vm.PreviousConflictCommand;
        _next.Command = vm.NextConflictCommand;
        _reset.Command = vm.ResetResultCommand;
        _useOurs.Command = vm.UseOursCommand;
        _useTheirs.Command = vm.UseTheirsCommand;
        _resolved.Command = vm.MarkResolvedCommand;
        _blockButtons.IsVisible = !vm.IsBinary;
        _resolved.IsVisible = !vm.IsBinary;
        _editors.IsVisible = !vm.IsBinary;
        _summary.Text = vm.Summary;

        _ours.Document = new TextDocument(vm.OursText);
        _theirs.Document = new TextDocument(vm.TheirsText);
        SetResult(vm.ResultText);
        Highlight(vm.Path);
        vm.RequestInitialFocus();
    }

    private void Highlight(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        var lang = string.IsNullOrEmpty(ext) ? null : Registry.Value.GetLanguageByExtension(ext);
        var scope = lang is null ? null : Registry.Value.GetScopeByLanguageId(lang.Id);
        foreach (var h in _highlighters) h.SetGrammar(scope);
    }

    private void SetResult(string text)
    {
        if (_result.Text == text) return;
        _syncing = true;
        try { _result.Text = text; }
        finally { _syncing = false; }
    }

    private void OnResultTyped(object? sender, EventArgs e)
    {
        if (!_syncing && _vm is not null && _vm.ResultText != _result.Text) _vm.ResultText = _result.Text;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(ConflictViewModel.ResultText)) SetResult(_vm.ResultText);
        if (e.PropertyName == nameof(ConflictViewModel.Summary)) _summary.Text = _vm.Summary;
    }

    private void OnFocusLine(int line)
    {
        var doc = _result.Document;
        if (doc is null || line < 1 || line > doc.LineCount) return;
        _result.ScrollToLine(line);
        _result.TextArea.Caret.Offset = doc.GetLineByNumber(line).Offset;
    }

    /// <summary>Tints the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>, <c>=======</c> and <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> lines.</summary>
    private sealed class MarkerColorizer : DocumentColorizingTransformer
    {
        private static readonly IBrush Marker = new SolidColorBrush(Color.Parse("#FFB74D"));

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length < 7) return;
            var text = CurrentContext.Document.GetText(line.Offset, 7);
            if (text is "<<<<<<<" or "=======" or ">>>>>>>" or "|||||||")
                ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(Marker));
        }
    }
}
