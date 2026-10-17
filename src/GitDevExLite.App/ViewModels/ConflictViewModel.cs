using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

/// <summary>State for resolving one conflicted file: read-only ours/theirs and an editable result.</summary>
public sealed partial class ConflictViewModel : ObservableObject
{
    private readonly Func<string, string, Task> _save;
    private readonly Func<ConflictSide, Task> _takeSide;
    private readonly Func<string, Task<bool>> _confirm;

    public ConflictViewModel(
        ConflictFile file, string oursLabel, string theirsLabel,
        Func<string, string, Task> save, Func<ConflictSide, Task> takeSide, Func<string, Task<bool>> confirm)
    {
        File = file;
        OursLabel = oursLabel;
        TheirsLabel = theirsLabel;
        _save = save;
        _takeSide = takeSide;
        _confirm = confirm;
        _resultText = file.Working;
        Recount();
    }

    public ConflictFile File { get; }
    public string Path => File.Path;
    public bool IsBinary => File.IsBinary;
    public string OursLabel { get; }
    public string TheirsLabel { get; }
    public string OursText => File.IsBinary ? "(binary file)" : File.Ours ?? "(this side deleted the file)";
    public string TheirsText => File.IsBinary ? "(binary file)" : File.Theirs ?? "(this side deleted the file)";
    public string UseOursText => File.Ours is null ? "Delete file (ours)" : "Use ours (whole file)";
    public string UseTheirsText => File.Theirs is null ? "Delete file (theirs)" : "Use theirs (whole file)";

    /// <summary>Raised with a 1-based line when the editor should scroll to a conflict block.</summary>
    public event Action<int>? FocusLineRequested;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))]
    private string _resultText;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary)), NotifyPropertyChangedFor(nameof(HasConflicts))]
    private int _remaining;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))]
    private int _currentIndex;

    public bool HasConflicts => Remaining > 0;

    public string Summary => IsBinary ? "Binary file: choose a side"
        : Remaining == 0 ? "No conflict markers remain"
        : $"Conflict {CurrentIndex + 1} of {Remaining}";

    partial void OnResultTextChanged(string value) => Recount();

    private void Recount()
    {
        Remaining = IsBinary ? 0 : ConflictParser.Count(ResultText);
        CurrentIndex = Remaining == 0 ? 0 : Math.Min(CurrentIndex, Remaining - 1);
    }

    private void FocusCurrent()
    {
        var block = ConflictParser.Parse(ResultText).Where(s => s.IsConflict).Skip(CurrentIndex).FirstOrDefault();
        if (block is not null) FocusLineRequested?.Invoke(block.StartLine + 1);
    }

    private void Apply(ConflictChoice choice)
    {
        if (Remaining == 0) return;
        ResultText = ConflictParser.Resolve(ResultText, CurrentIndex, choice);
        FocusCurrent();
    }

    [RelayCommand] private void AcceptOurs() => Apply(ConflictChoice.Ours);
    [RelayCommand] private void AcceptTheirs() => Apply(ConflictChoice.Theirs);
    [RelayCommand] private void AcceptBoth() => Apply(ConflictChoice.Both);

    [RelayCommand]
    private void NextConflict()
    {
        if (Remaining == 0) return;
        CurrentIndex = (CurrentIndex + 1) % Remaining;
        FocusCurrent();
    }

    [RelayCommand]
    private void PreviousConflict()
    {
        if (Remaining == 0) return;
        CurrentIndex = (CurrentIndex - 1 + Remaining) % Remaining;
        FocusCurrent();
    }

    [RelayCommand]
    private void ResetResult()
    {
        ResultText = File.Working;
        CurrentIndex = 0;
        FocusCurrent();
    }

    [RelayCommand]
    private async Task MarkResolved()
    {
        if (Remaining > 0 && !await _confirm($"{Remaining} conflict marker block(s) remain in the result. Mark the file resolved anyway?"))
            return;
        await _save(Path, ResultText);
    }

    [RelayCommand] private Task UseOurs() => _takeSide(ConflictSide.Ours);
    [RelayCommand] private Task UseTheirs() => _takeSide(ConflictSide.Theirs);

    public void RequestInitialFocus() => FocusCurrent();
}
