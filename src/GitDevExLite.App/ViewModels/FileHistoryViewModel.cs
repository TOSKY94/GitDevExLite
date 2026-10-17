using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

/// <summary>Commit list with diffs, and a blame view, for one file.</summary>
public sealed partial class FileHistoryViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly Action<string> _goToCommit;

    public FileHistoryViewModel(IGitService git, string path, Action<string> goToCommit)
    {
        _git = git;
        _goToCommit = goToCommit;
        Path = path;

        try
        {
            foreach (var e in git.GetFileHistory(path)) Entries.Add(e);
            SelectedEntry = Entries.FirstOrDefault();
        }
        catch (Exception ex)
        {
            HistoryError = ex.Message;
        }
        LoadBlame(path, null);
    }

    public string Path { get; }
    public ObservableCollection<FileHistoryEntry> Entries { get; } = new();
    public ObservableCollection<BlameLine> BlameLines { get; } = new();

    /// <summary>Raised when the window should close, for example after jumping to a commit in the graph.</summary>
    public event Action? CloseRequested;

    [ObservableProperty] private FileHistoryEntry? _selectedEntry;
    [ObservableProperty] private FileDiff? _activeDiff;
    [ObservableProperty] private bool _sideBySide;
    [ObservableProperty] private string _historyError = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(BlameDetail))]
    private BlameLine? _selectedBlame;

    [ObservableProperty] private string _blameError = "";
    [ObservableProperty] private string _blameRevision = "HEAD";

    public string BlameDetail => SelectedBlame is { } b
        ? $"{b.ShortSha}  {b.Author}  {b.When.LocalDateTime:yyyy-MM-dd}  \u2014  {b.Summary}"
        : "Select a line to see the commit that last changed it.";

    partial void OnSelectedEntryChanged(FileHistoryEntry? value)
    {
        if (value is null)
        {
            ActiveDiff = null;
            return;
        }
        try
        {
            ActiveDiff = _git.GetCommitDiff(value.Commit.Sha, value.Path);
            HistoryError = "";
        }
        catch (Exception ex)
        {
            HistoryError = ex.Message;
        }
    }

    private void LoadBlame(string path, string? rev)
    {
        BlameLines.Clear();
        SelectedBlame = null;
        try
        {
            foreach (var line in _git.GetBlame(path, rev)) BlameLines.Add(line);
            BlameRevision = rev is null ? "HEAD" : rev[..Math.Min(7, rev.Length)];
            BlameError = "";
        }
        catch (Exception ex)
        {
            BlameError = ex.Message;
        }
    }

    [RelayCommand]
    private void BlameAtSelectedCommit()
    {
        if (SelectedEntry is { } e) LoadBlame(e.Path, e.Commit.Sha);
    }

    [RelayCommand]
    private void BlameAtHead() => LoadBlame(Path, null);

    [RelayCommand]
    private void GoToHistoryCommit() => GoTo(SelectedEntry?.Commit.Sha);

    [RelayCommand]
    private void GoToBlameCommit() => GoTo(SelectedBlame?.Sha);

    private void GoTo(string? sha)
    {
        if (sha is null) return;
        _goToCommit(sha);
        CloseRequested?.Invoke();
    }
}
