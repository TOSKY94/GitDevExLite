using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Graph;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

public partial class RepoTabViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly IDialogService _dialogs;
    private IReadOnlyList<CommitInfo> _commits = Array.Empty<CommitInfo>();
    private bool _suppressFileSelection;

    public RepoTabViewModel(IGitService git, IDialogService dialogs)
    {
        _git = git;
        _dialogs = dialogs;
        Selection.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedLineCount));
            OnPropertyChanged(nameof(SelectionActionText));
            StageSelectedCommand.NotifyCanExecuteChanged();
        };
        MenuProvider = BuildMenu;
    }

    public string? RepositoryPath => _git.RepositoryPath?.TrimEnd('/', '\\');

    public ObservableCollection<RefInfo> LocalBranches { get; } = new();
    public ObservableCollection<RefInfo> RemoteBranches { get; } = new();
    public ObservableCollection<RefInfo> Tags { get; } = new();
    public ObservableCollection<StashInfo> Stashes { get; } = new();
    public ObservableCollection<ChangedFile> CommitFiles { get; } = new();
    public ObservableCollection<FileChange> Unstaged { get; } = new();
    public ObservableCollection<FileChange> Staged { get; } = new();
    public LineSelection Selection { get; } = new();
    public Func<GraphRow, IReadOnlyList<ContextAction>> MenuProvider { get; }
    public int SelectedLineCount => Selection.Lines.Count;
    public string SelectionActionText => $"{(DiffStaged ? "Unstage" : "Stage")} selected lines ({SelectedLineCount})";

    [ObservableProperty] private string _header = "(empty)";
    [ObservableProperty] private Dictionary<string, List<RefInfo>> _refsByCommit = new();
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private GraphLayoutResult? _graph;
    [ObservableProperty] private GraphRow? _selectedRow;
    [ObservableProperty] private RefInfo? _selectedBranch;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private HashSet<string>? _matchedShas;
    [ObservableProperty] private string _searchSummary = "";

    [ObservableProperty] private ChangedFile? _selectedCommitFile;
    [ObservableProperty] private FileChange? _selectedUnstaged;
    [ObservableProperty] private FileChange? _selectedStaged;
    [ObservableProperty] private bool _showWorkingDirectory;

    [ObservableProperty] private FileDiff? _activeDiff;
    [ObservableProperty] private bool _diffStaged;
    [ObservableProperty] private bool _diffEditable;
    [ObservableProperty] private bool _sideBySide;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _commitMessage = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private bool _amend;

    public void Open(string path)
    {
        _git.Open(path);
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (_git.RepositoryPath is null) return;

        var refs = _git.GetRefs();
        Replace(LocalBranches, refs.Where(r => r.Kind == RefKind.LocalBranch).OrderBy(r => r.Name));
        Replace(RemoteBranches, refs.Where(r => r.Kind == RefKind.RemoteBranch).OrderBy(r => r.Name));
        Replace(Tags, refs.Where(r => r.Kind == RefKind.Tag).OrderByDescending(r => r.Name));
        Replace(Stashes, _git.GetStashes());
        RefsByCommit = refs.GroupBy(r => r.TargetSha).ToDictionary(g => g.Key, g => g.ToList());

        _commits = _git.GetCommits();
        Graph = GraphLayout.Build(_commits);
        SelectedRow = null;
        ApplySearch();
        RefreshStatus();

        Header = new DirectoryInfo(RepositoryPath!).Name;
        StatusText = $"{_commits.Count} commits, {Graph.LaneCount} lanes";
        OnPropertyChanged(nameof(RepositoryPath));
    }

    // ---- search -------------------------------------------------------------

    partial void OnSearchTextChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        MatchedShas = GraphSearch.Match(_commits, RefsByCommit, SearchText);
        SearchSummary = MatchedShas is null ? "" : $"{MatchedShas.Count} match(es)";
    }

    [RelayCommand]
    private void NextMatch() => StepMatch(1);

    [RelayCommand]
    private void PreviousMatch() => StepMatch(-1);

    private void StepMatch(int direction)
    {
        if (Graph is null || MatchedShas is not { Count: > 0 }) return;
        var rows = Graph.Rows;
        var start = SelectedRow is null ? (direction > 0 ? -1 : rows.Count) : rows.ToList().IndexOf(SelectedRow);
        for (var step = 1; step <= rows.Count; step++)
        {
            var i = ((start + direction * step) % rows.Count + rows.Count) % rows.Count;
            if (MatchedShas.Contains(rows[i].Commit.Sha))
            {
                SelectedRow = rows[i];
                return;
            }
        }
    }

    // ---- commit details -----------------------------------------------------

    partial void OnSelectedRowChanged(GraphRow? value)
    {
        CommitFiles.Clear();
        if (value is null) { SelectedCommitFile = null; return; }

        ShowWorkingDirectory = false;
        try
        {
            Replace(CommitFiles, _git.GetCommitChanges(value.Commit.Sha));
            SelectedCommitFile = CommitFiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    partial void OnSelectedCommitFileChanged(ChangedFile? value)
    {
        if (ShowWorkingDirectory) return;
        if (value is null || SelectedRow is null) { ActiveDiff = null; return; }
        try
        {
            ActiveDiff = _git.GetCommitDiff(SelectedRow.Commit.Sha, value.Path);
            DiffEditable = false;
            DiffStaged = false;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    // ---- working directory --------------------------------------------------

    partial void OnShowWorkingDirectoryChanged(bool value)
    {
        ActiveDiff = null;
        if (value)
        {
            RefreshStatus();
        }
        else if (SelectedCommitFile is { } f)
        {
            OnSelectedCommitFileChanged(f);
        }
    }

    partial void OnAmendChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(CommitMessage))
            CommitMessage = _git.GetHeadMessage() ?? "";
    }

    partial void OnSelectedUnstagedChanged(FileChange? value)
    {
        if (_suppressFileSelection || value is null) return;
        _suppressFileSelection = true;
        SelectedStaged = null;
        _suppressFileSelection = false;
        LoadWorkingDiff(value);
    }

    partial void OnSelectedStagedChanged(FileChange? value)
    {
        if (_suppressFileSelection || value is null) return;
        _suppressFileSelection = true;
        SelectedUnstaged = null;
        _suppressFileSelection = false;
        LoadWorkingDiff(value);
    }

    partial void OnActiveDiffChanged(FileDiff? value) => Selection.Clear();

    private void LoadWorkingDiff(FileChange file)
    {
        try
        {
            ActiveDiff = _git.GetWorkingDiff(file.Path, file.IsStaged);
            DiffStaged = file.IsStaged;
            DiffEditable = true;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void RefreshStatus(string? keepPath = null, bool keepStaged = false)
    {
        if (_git.RepositoryPath is null) return;

        var current = SelectedUnstaged ?? SelectedStaged;
        keepPath ??= current?.Path;
        if (current is not null && keepPath == current.Path) keepStaged = current.IsStaged;

        var status = _git.GetStatus();
        _suppressFileSelection = true;
        Replace(Unstaged, status.Where(s => !s.IsStaged));
        Replace(Staged, status.Where(s => s.IsStaged));
        SelectedUnstaged = null;
        SelectedStaged = null;

        FileChange? target = null;
        if (keepPath is not null)
        {
            var preferred = keepStaged ? Staged : Unstaged;
            var other = keepStaged ? Unstaged : Staged;
            target = preferred.FirstOrDefault(f => f.Path == keepPath) ?? other.FirstOrDefault(f => f.Path == keepPath);
        }
        if (target is not null)
        {
            if (target.IsStaged) SelectedStaged = target; else SelectedUnstaged = target;
        }
        _suppressFileSelection = false;

        if (target is not null && ShowWorkingDirectory) LoadWorkingDiff(target);
        else if (ShowWorkingDirectory) ActiveDiff = null;

        CommitCommand.NotifyCanExecuteChanged();
        WorkingTreeSummary = status.Count == 0 ? "Working directory clean" : $"{status.Count} change(s)";
    }

    [ObservableProperty] private string _workingTreeSummary = "";

    [RelayCommand]
    private void StageFile(FileChange? file) => Run(() => { if (file is not null) _git.Stage(file.Path); }, refreshStatusOnly: true);

    [RelayCommand]
    private void UnstageFile(FileChange? file) => Run(() => { if (file is not null) _git.Unstage(file.Path); }, refreshStatusOnly: true);

    [RelayCommand]
    private void StageAll() => Run(() =>
    {
        foreach (var f in Unstaged.ToList()) _git.Stage(f.Path);
    }, refreshStatusOnly: true);

    [RelayCommand]
    private void UnstageAll() => Run(() =>
    {
        foreach (var f in Staged.ToList()) _git.Unstage(f.Path);
    }, refreshStatusOnly: true);

    [RelayCommand]
    private void ApplyHunk(int hunk)
    {
        if (ActiveDiff is null || hunk < 0 || hunk >= ActiveDiff.Hunks.Count) return;
        ApplyLines(PatchBuilder.HunkChanges(ActiveDiff, hunk));
    }

    [RelayCommand(CanExecute = nameof(CanStageSelected))]
    private void StageSelected() => ApplyLines(new HashSet<LineRef>(Selection.Lines));

    private bool CanStageSelected() => DiffEditable && Selection.Lines.Count > 0;

    partial void OnDiffEditableChanged(bool value) => StageSelectedCommand.NotifyCanExecuteChanged();

    partial void OnDiffStagedChanged(bool value) => OnPropertyChanged(nameof(SelectionActionText));

    private void ApplyLines(IReadOnlySet<LineRef> lines)
    {
        if (ActiveDiff is null || lines.Count == 0) return;
        var diff = ActiveDiff;
        var staged = DiffStaged;
        try
        {
            if (staged) _git.UnstageLines(diff, lines); else _git.StageLines(diff, lines);
            RefreshStatus(diff.Path, staged);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit()
    {
        try
        {
            _git.Commit(CommitMessage.Trim(), Amend);
            CommitMessage = "";
            Amend = false;
            Refresh();
            StatusText = "Committed.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private bool CanCommit() => !string.IsNullOrWhiteSpace(CommitMessage) && (Staged.Count > 0 || Amend);

    // ---- branches -----------------------------------------------------------

    [RelayCommand]
    private void Checkout()
    {
        if (SelectedBranch is not { Kind: RefKind.LocalBranch } b) return;
        Run(() => _git.Checkout(b.Name));
    }

    private IReadOnlyList<ContextAction> BuildMenu(GraphRow row)
    {
        var sha = row.Commit.Sha;
        var actions = new List<ContextAction>
        {
            new("Create branch here...", () => CreateBranchAsync(sha, checkout: false)),
            new("Create branch here and check out...", () => CreateBranchAsync(sha, checkout: true)),
            new("Check out this commit (detached)", () => RunAsync(() => _git.CheckoutCommit(sha))),
        };

        if (RefsByCommit.TryGetValue(sha, out var refs))
        {
            foreach (var b in refs.Where(r => r.Kind == RefKind.LocalBranch).OrderBy(r => r.Name))
            {
                var branch = b;
                if (!branch.IsHead)
                    actions.Add(new($"Check out '{branch.Name}'", () => RunAsync(() => _git.Checkout(branch.Name))));
                actions.Add(new($"Rename '{branch.Name}'...", () => RenameBranchAsync(branch)));
                if (!branch.IsHead)
                    actions.Add(new($"Delete '{branch.Name}'...", () => DeleteBranchAsync(branch)));
            }
        }

        actions.Add(new("Copy SHA", () => _dialogs.CopyTextAsync(sha)));
        return actions;
    }

    private async Task CreateBranchAsync(string sha, bool checkout)
    {
        var name = (await _dialogs.PromptAsync("Create branch", "Branch name:"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        await RunAsync(() => _git.CreateBranch(name, sha, checkout));
    }

    private async Task RenameBranchAsync(RefInfo branch)
    {
        var name = (await _dialogs.PromptAsync("Rename branch", $"New name for '{branch.Name}':", branch.Name))?.Trim();
        if (string.IsNullOrEmpty(name) || name == branch.Name) return;
        await RunAsync(() => _git.RenameBranch(branch.Name, name));
    }

    private async Task DeleteBranchAsync(RefInfo branch)
    {
        if (!await _dialogs.ConfirmAsync("Delete branch", $"Delete branch '{branch.Name}'? Unmerged commits may become unreachable."))
            return;
        await RunAsync(() => _git.DeleteBranch(branch.Name));
    }

    // ---- helpers ------------------------------------------------------------

    private Task RunAsync(Action action)
    {
        Run(action);
        return Task.CompletedTask;
    }

    private void Run(Action action, bool refreshStatusOnly = false)
    {
        try
        {
            action();
            if (refreshStatusOnly) RefreshStatus(); else Refresh();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    public void Dispose() => _git.Dispose();
}
