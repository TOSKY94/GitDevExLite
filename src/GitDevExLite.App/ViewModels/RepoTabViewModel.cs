using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Graph;
using GitDevExLite.Core.Hosting;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

public partial class RepoTabViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly IDialogService _dialogs;
    private readonly RemoteOptions _options;
    private CancellationTokenSource? _cts;
    private RepoWatcher? _watcher;
    private bool _disposed;
    private IReadOnlyList<CommitInfo> _commits = Array.Empty<CommitInfo>();
    private bool _suppressFileSelection;

    public RepoTabViewModel(IGitService git, IDialogService dialogs, RemoteOptions options)
    {
        _git = git;
        _dialogs = dialogs;
        _options = options;
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
    [ObservableProperty] private ConflictViewModel? _activeConflict;
    [ObservableProperty] private StashInfo? _selectedStash;
    [ObservableProperty] private string _detailHeading = "";
    private string? _detailSha;

    public event Action<string>? OpenRepositoryRequested;

    /// <summary>The file shown in the detail pane, for history and blame.</summary>
    public string? CurrentFilePath => ActiveConflict?.Path ?? ActiveDiff?.Path
        ?? (ShowWorkingDirectory ? (SelectedUnstaged ?? SelectedStaged)?.Path : SelectedCommitFile?.Path);

    public bool HasCurrentFile => CurrentFilePath is not null;

    [ObservableProperty] private RefInfo? _selectedTag;
    [ObservableProperty] private string _operationText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _progressIndeterminate = true;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(FetchCommand), nameof(PullMergeCommand),
         nameof(PullRebaseCommand), nameof(PullFastForwardCommand), nameof(PushCommand),
         nameof(ContinueOperationCommand), nameof(SkipOperationCommand), nameof(AbortOperationCommand))]
    private bool _isBusy;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOperation), nameof(OperationBanner), nameof(CanSkipOperation))]
    private RepoOperationState _operationState = RepoOperationState.None;

    public bool HasOperation => OperationState.InProgress;
    public bool CanSkipOperation => OperationState.CanSkip;
    public string OperationBanner =>
        $"{OperationState.Describe()}. Resolve any conflicts, stage the files, then Continue \u2014 or Abort.";

    /// <summary>Set after a background fetch hit an auth failure; cleared by the next successful manual operation.</summary>
    public bool AutoFetchPaused { get; private set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _commitMessage = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private bool _amend;

    public void Open(string path)
    {
        _git.Open(path);
        Refresh();
        StartWatching();
    }

    private void StartWatching()
    {
        if (_git.RepositoryPath is not { } work || _git.GitDirectory is not { } gitDir) return;
        try
        {
            _watcher = new RepoWatcher(work, gitDir);
            _watcher.Changed += change => Dispatcher.UIThread.Post(() => ApplyExternalChange(change));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Changes will only be picked up by Refresh and in-app actions.
        }
    }

    private void ApplyExternalChange(RepoChange change)
    {
        if (_disposed || IsBusy || _git.RepositoryPath is null) return;
        try
        {
            if (change.HasFlag(RepoChange.RepoState)) RefreshCore(keepSelection: true, includeStatus: true, quiet: true);
            else RefreshStatus();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void Refresh() => RefreshCore(keepSelection: false, includeStatus: true);

    private void RefreshCore(bool keepSelection, bool includeStatus, bool quiet = false)
    {
        if (_git.RepositoryPath is null) return;

        var keepSha = keepSelection && !ShowWorkingDirectory ? SelectedRow?.Commit.Sha : null;

        var refs = _git.GetRefs();
        Replace(LocalBranches, refs.Where(r => r.Kind == RefKind.LocalBranch).OrderBy(r => r.Name));
        Replace(RemoteBranches, refs.Where(r => r.Kind == RefKind.RemoteBranch).OrderBy(r => r.Name));
        Replace(Tags, refs.Where(r => r.Kind == RefKind.Tag).OrderByDescending(r => r.Name));
        Replace(Stashes, _git.GetStashes());
        RefsByCommit = refs.GroupBy(r => r.TargetSha).ToDictionary(g => g.Key, g => g.ToList());

        _commits = _git.GetCommits();
        Graph = GraphLayout.Build(_commits);
        SelectedRow = keepSha is null ? null : Graph.Rows.FirstOrDefault(r => r.Commit.Sha == keepSha);
        ApplySearch();
        if (includeStatus) RefreshStatus();
        OperationState = _git.GetOperationState();

        Header = new DirectoryInfo(RepositoryPath!).Name;
        if (!quiet) StatusText = $"{_commits.Count} commits, {Graph.LaneCount} lanes";
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
        DetailHeading = "";
        _detailSha = value?.Commit.Sha;
        if (value is null) { SelectedCommitFile = null; return; }

        SelectedStash = null;
        ShowWorkingDirectory = false;
        LoadDetailFiles(value.Commit.Sha);
    }

    partial void OnSelectedStashChanged(StashInfo? value)
    {
        if (value is null) return;
        SelectedRow = null;
        ShowWorkingDirectory = false;
        _detailSha = value.Sha;
        DetailHeading = $"Stash: {value.Message}";
        LoadDetailFiles(value.Sha);
    }

    // A stash is stored as a commit, so its files and diffs reuse the commit machinery.
    private void LoadDetailFiles(string sha)
    {
        try
        {
            Replace(CommitFiles, _git.GetCommitChanges(sha));
            SelectedCommitFile = CommitFiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    partial void OnSelectedCommitFileChanged(ChangedFile? value)
    {
        OnPropertyChanged(nameof(HasCurrentFile));
        if (ShowWorkingDirectory) return;
        if (value is null || _detailSha is null) { ActiveDiff = null; return; }
        try
        {
            ActiveDiff = _git.GetCommitDiff(_detailSha, value.Path);
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
        ActiveConflict = null;
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

    partial void OnActiveDiffChanged(FileDiff? value)
    {
        Selection.Clear();
        OnPropertyChanged(nameof(HasCurrentFile));
    }

    partial void OnActiveConflictChanged(ConflictViewModel? value) => OnPropertyChanged(nameof(HasCurrentFile));

    private void LoadWorkingDiff(FileChange file)
    {
        if (file is { Status: "Conflicted", IsStaged: false })
        {
            ActiveDiff = null;
            DiffEditable = false;
            // Keep a half-resolved result when a refresh re-selects the same file.
            if (ActiveConflict?.Path != file.Path) _ = LoadConflictAsync(file);
            return;
        }

        ActiveConflict = null;
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
        var newUnstaged = status.Where(s => !s.IsStaged).ToList();
        var newStaged = status.Where(s => s.IsStaged).ToList();
        UpdateSummary(status.Count);

        // Nothing structural changed (typical for an external edit to an already-listed file): keep the
        // selection and ticked lines, and only swap the diff if its content differs.
        if (newUnstaged.SequenceEqual(Unstaged) && newStaged.SequenceEqual(Staged))
        {
            ReloadDiffIfChanged();
            return;
        }

        _suppressFileSelection = true;
        Replace(Unstaged, newUnstaged);
        Replace(Staged, newStaged);
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
        else if (ShowWorkingDirectory)
        {
            ActiveDiff = null;
            ActiveConflict = null;
        }

        CommitCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSummary(int changeCount)
    {
        CommitCommand.NotifyCanExecuteChanged();
        WorkingTreeSummary = changeCount == 0 ? "Working directory clean" : $"{changeCount} change(s)";
    }

    private void ReloadDiffIfChanged()
    {
        if (!ShowWorkingDirectory || (SelectedUnstaged ?? SelectedStaged) is not { } file) return;
        if (file is { Status: "Conflicted" }) return;
        try
        {
            var diff = _git.GetWorkingDiff(file.Path, file.IsStaged);
            if (ActiveDiff is { } shown && SameDiff(shown, diff)) return;
            ActiveDiff = diff;
            DiffStaged = file.IsStaged;
            DiffEditable = true;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private static bool SameDiff(FileDiff a, FileDiff b) =>
        a.Path == b.Path && a.IsBinary == b.IsBinary && a.Hunks.Count == b.Hunks.Count &&
        a.Hunks.Zip(b.Hunks).All(p => p.First.Header == p.Second.Header && p.First.Lines.SequenceEqual(p.Second.Lines));

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
    private async Task Commit()
    {
        try
        {
            var findings = Amend && Staged.Count == 0 ? Array.Empty<SecretFinding>() : SecretScanner.ScanStaged(_git);
            if (findings.Count > 0)
            {
                var list = string.Join("\n", findings.Take(10).Select(f => $"\u2022 {f.Path}:{f.Line}  {f.Rule}  ({f.Preview})"));
                if (findings.Count > 10) list += $"\n\u2026and {findings.Count - 10} more";
                if (!await _dialogs.ConfirmAsync("Possible secrets detected",
                        $"The staged changes appear to contain secrets:\n\n{list}\n\nCommit anyway? (Add 'gitdevex:allow' to a line to ignore it.)"))
                {
                    StatusText = "Commit cancelled: possible secrets in staged changes.";
                    return;
                }
            }
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
                actions.Add(new($"Set upstream for '{branch.Name}'...", () => SetUpstreamAsync(branch)));
                if (!branch.IsHead)
                    actions.Add(new($"Delete '{branch.Name}'...", () => DeleteBranchAsync(branch)));
            }
        }

        actions.Add(new("Create tag here...", () => CreateTagAsync(sha)));

        if (!OperationState.InProgress) AddHistoryActions(actions, sha, refs);

        if (refs is not null)
        {
            foreach (var t in refs.Where(r => r.Kind == RefKind.Tag).OrderBy(r => r.Name))
            {
                var tag = t.Name;
                actions.Add(new($"Push tag '{tag}'", () => PushTagAsync(tag)));
                actions.Add(new($"Delete tag '{tag}'...", () => DeleteTagAsync(tag, remote: false)));
                actions.Add(new($"Delete tag '{tag}' from remote...", () => DeleteTagAsync(tag, remote: true)));
            }
        }

        actions.Add(new("Copy SHA", () => _dialogs.CopyTextAsync(sha)));
        return actions;
    }

    // ---- merge, rebase, cherry-pick, revert, reset ---------------------------

    private string CurrentBranchName => LocalBranches.FirstOrDefault(b => b.IsHead)?.Name ?? "HEAD";

    private void AddHistoryActions(List<ContextAction> actions, string sha, List<RefInfo>? refs)
    {
        var current = CurrentBranchName;
        var headSha = LocalBranches.FirstOrDefault(b => b.IsHead)?.TargetSha;
        var isHead = sha == headSha;

        foreach (var b in (refs ?? new()).Where(r => r.Kind != RefKind.Tag && !r.IsHead).OrderBy(r => r.Name))
        {
            var target = b.Name;
            actions.Add(new($"Merge '{target}' into '{current}'...", () => MergeAsync(target, MergeMode.Default)));
            actions.Add(new($"Merge '{target}' into '{current}' (squash)...", () => MergeAsync(target, MergeMode.Squash)));
        }

        if (!isHead)
        {
            actions.Add(new($"Rebase '{current}' onto this commit...", () => RebaseOntoAsync(sha)));
            actions.Add(new("Interactive rebase of the commits after this one...", () => InteractiveRebaseFromAsync(sha)));
            actions.Add(new("Cherry-pick this commit", () => RunOperationAsync(
                "Cherry-pick", (p, ct) => _git.CherryPickAsync(new[] { sha }, p, ct))));
        }

        actions.Add(new("Revert this commit...", () => RevertAsync(sha)));
        foreach (var mode in new[] { ResetMode.Soft, ResetMode.Mixed, ResetMode.Hard })
        {
            var m = mode;
            actions.Add(new($"Reset '{current}' to here ({m.ToString().ToLowerInvariant()})...", () => ResetAsync(sha, m)));
        }
    }

    // ---- stash, undo, history, tools ------------------------------------------

    [RelayCommand]
    private async Task Stash()
    {
        var message = await _dialogs.PromptAsync("Stash changes", "Message (optional):");
        if (message is null) return;
        var untracked = await _dialogs.ConfirmAsync("Stash changes", "Also stash untracked files?");
        await RunRemoteAsync("Stash", async (_, ct) => await _git.CreateStashAsync(message, untracked, ct), includeStatus: true);
    }

    private async Task ApplyStashAsync(StashInfo stash, bool drop)
    {
        GitOpResult? result = null;
        await RunRemoteAsync(drop ? "Pop stash" : "Apply stash",
            async (_, ct) => result = await _git.ApplyStashAsync(stash.Sha, drop, ct), includeStatus: true);
        if (result is { Success: false })
        {
            ShowWorkingDirectory = true;
            StatusText = "The stash conflicts with your changes. Resolve the files, then stage them.";
        }
    }

    [RelayCommand]
    private Task ApplySelectedStash() => SelectedStash is { } s ? ApplyStashAsync(s, drop: false) : Task.CompletedTask;

    [RelayCommand]
    private Task PopSelectedStash() => SelectedStash is { } s ? ApplyStashAsync(s, drop: true) : Task.CompletedTask;

    [RelayCommand]
    private async Task DropSelectedStash()
    {
        if (SelectedStash is not { } s) return;
        if (!await _dialogs.ConfirmAsync("Drop stash", $"Delete '{s.Message}'? Its changes cannot be recovered afterwards.")) return;
        await RunRemoteAsync("Drop stash", (_, ct) => _git.DropStashAsync(s.Sha, ct), includeStatus: false);
    }

    [RelayCommand]
    private async Task Undo()
    {
        var plan = _git.GetUndoPlan();
        if (plan is null)
        {
            StatusText = "There is nothing to undo.";
            return;
        }
        if (!await _dialogs.ConfirmAsync("Undo last operation",
                $"Undo '{plan.Description}'?\n\n{plan.Action}\n\nUndoing again redoes it.")) return;
        await RunRemoteAsync("Undo", (_, ct) => _git.UndoAsync(ct), includeStatus: true);
    }

    [RelayCommand]
    private Task ShowHistory() => OpenHistoryAsync(startOnBlame: false);

    [RelayCommand]
    private Task ShowBlame() => OpenHistoryAsync(startOnBlame: true);

    private async Task OpenHistoryAsync(bool startOnBlame)
    {
        if (CurrentFilePath is not { } path) return;
        FileHistoryViewModel history;
        try
        {
            history = new FileHistoryViewModel(_git, path, GoToCommit);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }
        await _dialogs.ShowFileHistoryAsync(history, startOnBlame);
    }

    private void GoToCommit(string sha)
    {
        var row = Graph?.Rows.FirstOrDefault(r => r.Commit.Sha == sha);
        if (row is null)
        {
            StatusText = "That commit is not in the loaded history.";
            return;
        }
        ShowWorkingDirectory = false;
        SelectedRow = row;
    }

    [RelayCommand]
    private async Task OpenTools()
    {
        var tools = new RepoToolsViewModel(_git, _dialogs, path => OpenRepositoryRequested?.Invoke(path));
        await _dialogs.ShowRepoToolsAsync(tools);
        TryRefresh(includeStatus: true);
    }

    // ---- conflicts and interactive rebase ------------------------------------

    private (string Ours, string Theirs) ConflictLabels() => OperationState.Kind switch
    {
        RepoOperation.Rebase => ("Ours \u2014 the branch being rebased onto", "Theirs \u2014 your commit being replayed"),
        RepoOperation.Merge => ("Ours \u2014 the current branch", "Theirs \u2014 the incoming branch"),
        _ => ("Ours \u2014 the current branch", "Theirs \u2014 the commit being applied")
    };

    private async Task LoadConflictAsync(FileChange file)
    {
        try
        {
            var data = await _git.GetConflictFileAsync(file.Path, CancellationToken.None);
            // The user may have picked another file while this one was loading.
            if ((SelectedUnstaged ?? SelectedStaged)?.Path != file.Path) return;

            var (ours, theirs) = ConflictLabels();
            ActiveConflict = new ConflictViewModel(
                data, ours, theirs, SaveResolutionAsync, TakeSideAsync,
                message => _dialogs.ConfirmAsync("Conflict markers remain", message));
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private Task SaveResolutionAsync(string path, string content) =>
        ResolveAsync(path, () => _git.ResolveFileAsync(path, content, CancellationToken.None));

    private Task TakeSideAsync(ConflictSide side) =>
        ActiveConflict is { } c
            ? ResolveAsync(c.Path, () => _git.ResolveWithSideAsync(c.Path, side, CancellationToken.None))
            : Task.CompletedTask;

    private async Task ResolveAsync(string path, Func<Task> resolve)
    {
        try
        {
            await resolve();
            ActiveConflict = null;
            ActiveDiff = null;
            SelectedUnstaged = null;
            RefreshStatus();
            OperationState = _git.GetOperationState();

            var next = Unstaged.FirstOrDefault(f => f.Status == "Conflicted");
            if (next is not null) SelectedUnstaged = next;
            StatusText = next is null
                ? $"Resolved {path}. No conflicts left" + (HasOperation ? " \u2014 press Continue." : ".")
                : $"Resolved {path}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task InteractiveRebaseFromAsync(string sha)
    {
        IReadOnlyList<RebaseEntry> entries;
        try
        {
            entries = _git.GetRebaseEntries(sha);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }

        var request = await _dialogs.ShowInteractiveRebaseAsync(entries, $"{CurrentBranchName} onto {sha[..7]}");
        if (request is null) return;
        await RunOperationAsync("Interactive rebase", (p, ct) => _git.InteractiveRebaseAsync(sha, request, p, ct));
    }

    private async Task MergeAsync(string target, MergeMode mode)
    {
        var current = CurrentBranchName;
        MergePreview preview;
        try
        {
            preview = await _git.PreviewMergeAsync(target, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }

        if (preview.IsUpToDate)
        {
            StatusText = $"'{current}' already contains '{target}'.";
            return;
        }

        var summary = !preview.CanPreview ? preview.Message ?? ""
            : preview.IsFastForward ? "This can be fast-forwarded."
            : preview.IsClean ? "No conflicts expected."
            : $"{preview.ConflictedFiles.Count} file(s) would conflict:\n" + ListFiles(preview.ConflictedFiles);
        if (mode == MergeMode.Squash) summary += "\nThe changes are staged, not committed.";

        if (!await _dialogs.ConfirmAsync("Merge", $"Merge '{target}' into '{current}'?\n\n{summary}")) return;
        await RunOperationAsync($"Merge {target}", (p, ct) => _git.MergeAsync(target, mode, p, ct));
    }

    private static string ListFiles(IReadOnlyList<string> files)
    {
        const int max = 10;
        var lines = files.Take(max).Select(f => $"  {f}");
        var more = files.Count > max ? $"\n  ...and {files.Count - max} more" : "";
        return string.Join("\n", lines) + more;
    }

    private async Task RebaseOntoAsync(string sha)
    {
        var current = CurrentBranchName;
        if (!await _dialogs.ConfirmAsync("Rebase",
                $"Rebase '{current}' onto {sha[..7]}?\n\nCommits on '{current}' are rewritten.")) return;
        await RunOperationAsync("Rebase", (p, ct) => _git.RebaseAsync(sha, p, ct));
    }

    private async Task RevertAsync(string sha)
    {
        if (!await _dialogs.ConfirmAsync("Revert", $"Create a new commit that undoes {sha[..7]}?")) return;
        await RunOperationAsync("Revert", (p, ct) => _git.RevertAsync(sha, p, ct));
    }

    private async Task ResetAsync(string sha, ResetMode mode)
    {
        var current = CurrentBranchName;
        if (mode == ResetMode.Hard)
        {
            var dirty = Staged.Count + Unstaged.Count;
            var lost = dirty == 0 ? "There are no uncommitted changes." : $"{dirty} uncommitted change(s) will be discarded.";
            if (!await _dialogs.ConfirmAsync("Hard reset",
                    $"Reset '{current}' to {sha[..7]}?\n\n{lost} Later commits stay reachable through the reflog.")) return;
        }
        await RunOperationAsync($"Reset ({mode.ToString().ToLowerInvariant()})", (p, ct) => _git.ResetAsync(sha, mode, p, ct));
    }

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task ContinueOperation() => RunOperationAsync("Continue", (p, ct) => _git.ContinueOperationAsync(p, ct));

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task SkipOperation() => RunOperationAsync("Skip", (p, ct) => _git.SkipOperationAsync(p, ct));

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private async Task AbortOperation()
    {
        if (!await _dialogs.ConfirmAsync("Abort", $"{OperationState.Describe()}. Abort and return to the state before it started?")) return;
        await RunOperationAsync("Abort", (p, ct) => _git.AbortOperationAsync(p, ct));
    }

    /// <summary>Runs a history-changing operation; a stop for conflicts switches to the working directory so they can be resolved.</summary>
    private async Task RunOperationAsync(string name, Func<IProgress<GitProgress>, CancellationToken, Task<GitOpResult>> operation)
    {
        GitOpResult? result = null;
        await RunRemoteAsync(name, async (p, ct) => result = await operation(p, ct), includeStatus: true);
        if (result is null) return;

        if (result.Paused)
        {
            ShowWorkingDirectory = true;
            StatusText = $"{name} stopped. Resolve conflicts, stage the files, then Continue \u2014 or Abort.";
        }
        else if (result.Success)
        {
            StatusText = $"{name} complete.";
        }
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

    // ---- remotes ------------------------------------------------------------

    private bool CanRemote() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task Fetch() =>
        RunRemoteAsync("Fetch", (p, ct) => _git.FetchAsync(_options.PruneOnFetch, p, ct), includeStatus: false);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task PullMerge() => PullAsync(PullMode.Merge);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task PullRebase() => PullAsync(PullMode.Rebase);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task PullFastForward() => PullAsync(PullMode.FastForwardOnly);

    private Task PullAsync(PullMode mode) =>
        RunOperationAsync($"Pull ({mode})", (p, ct) => _git.PullAsync(mode, p, ct));

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task Push() => RunRemoteAsync("Push", (p, ct) => _git.PushAsync(p, ct), includeStatus: false);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private async Task ForcePush()
    {
        if (!await _dialogs.ConfirmAsync("Force push",
                "Force push (with lease) rewrites the remote branch. It is refused if someone else pushed in the meantime. Continue?"))
            return;
        await RunRemoteAsync("Force push", (p, ct) => _git.PushAsync(p, ct, forceWithLease: true), includeStatus: false);
    }

    [RelayCommand]
    private async Task OpenPullRequests()
    {
        var vm = new PullRequestsViewModel(_git, _dialogs, branch => Run(() => _git.Checkout(branch)));
        await _dialogs.ShowPullRequestsAsync(vm);
        TryRefresh(includeStatus: true);
    }

    [RelayCommand]
    private void CancelOperation() => _cts?.Cancel();

    private async Task RunRemoteAsync(
        string name, Func<IProgress<GitProgress>, CancellationToken, Task> operation, bool includeStatus)
    {
        if (IsBusy) return;
        BeginOperation(name);
        var ct = _cts!.Token;
        var progress = new Progress<GitProgress>(p =>
        {
            OperationText = $"{name}: {p.Phase}";
            if (p.Percent is { } pct)
            {
                ProgressIndeterminate = false;
                ProgressPercent = pct;
            }
        });

        try
        {
            await AuthRetry.RunAsync(_dialogs, () => _git.GetRemoteUrl(), () => operation(progress, ct), ct);
            AutoFetchPaused = false;
            RefreshCore(keepSelection: true, includeStatus);
            StatusText = $"{name} complete.";
        }
        catch (OperationCanceledException)
        {
            TryRefresh(includeStatus);
            StatusText = $"{name} cancelled.";
        }
        catch (Exception ex)
        {
            TryRefresh(includeStatus);
            StatusText = ex is GitAuthException && GitCredentials.IsSshUrl(_git.GetRemoteUrl())
                ? $"{ex.Message} Load your key into ssh-agent (ssh-add) or configure it in ~/.ssh/config."
                : ex.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Fetches quietly for the timer: no dialogs, no sign-in windows, selection and working-tree state untouched.</summary>
    public async Task AutoFetchAsync()
    {
        if (IsBusy || AutoFetchPaused || _git.RepositoryPath is null || _git.GetRemoteNames().Count == 0) return;

        BeginOperation("Auto-fetch");
        try
        {
            await _git.FetchAsync(_options.PruneOnFetch, null, _cts!.Token, background: true);
            RefreshCore(keepSelection: true, includeStatus: false);
            StatusText = $"Auto-fetched at {DateTime.Now:t}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Auto-fetch cancelled.";
        }
        catch (GitAuthException)
        {
            AutoFetchPaused = true;
            StatusText = "Auto-fetch paused: sign-in required. Run Fetch to authenticate.";
        }
        catch (Exception ex)
        {
            StatusText = $"Auto-fetch failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void BeginOperation(string name)
    {
        // Remote operations rewrite refs and files and then refresh explicitly.
        _watcher?.Pause();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        OperationText = name;
        ProgressIndeterminate = true;
        ProgressPercent = 0;
    }

    private void EndOperation()
    {
        _cts?.Dispose();
        _cts = null;
        IsBusy = false;
        _watcher?.Resume();
    }

    private void TryRefresh(bool includeStatus)
    {
        try { RefreshCore(keepSelection: true, includeStatus); }
        catch (Exception) { /* keep the original error as the status */ }
    }

    private async Task SetUpstreamAsync(RefInfo branch)
    {
        var remote = _git.GetRemoteNames().FirstOrDefault() ?? "origin";
        var initial = branch.Upstream ?? $"{remote}/{branch.Name}";
        var upstream = (await _dialogs.PromptAsync("Set upstream", $"Upstream for '{branch.Name}' (remote/branch):", initial))?.Trim();
        if (string.IsNullOrEmpty(upstream)) return;
        await RunAsync(() => _git.SetUpstream(branch.Name, upstream));
    }

    // ---- tags ---------------------------------------------------------------

    private async Task CreateTagAsync(string sha)
    {
        var name = (await _dialogs.PromptAsync("Create tag", "Tag name:"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var message = await _dialogs.PromptAsync("Create tag", "Message (leave empty for a lightweight tag):");
        if (message is null) return;
        await RunAsync(() => _git.CreateTag(name, sha, message));
    }

    private Task PushTagAsync(string name) =>
        RunRemoteAsync($"Push tag {name}", (p, ct) => _git.PushTagAsync(name, p, ct), includeStatus: false);

    private async Task DeleteTagAsync(string name, bool remote)
    {
        var where = remote ? "from the remote" : "locally";
        if (!await _dialogs.ConfirmAsync("Delete tag", $"Delete tag '{name}' {where}?")) return;
        if (remote)
            await RunRemoteAsync($"Delete remote tag {name}", (p, ct) => _git.DeleteRemoteTagAsync(name, p, ct), includeStatus: false);
        else
            await RunAsync(() => _git.DeleteTag(name));
    }

    [RelayCommand]
    private Task PushSelectedTag() => SelectedTag is { } t ? PushTagAsync(t.Name) : Task.CompletedTask;

    [RelayCommand]
    private Task DeleteSelectedTag() => SelectedTag is { } t ? DeleteTagAsync(t.Name, remote: false) : Task.CompletedTask;

    [RelayCommand]
    private Task DeleteSelectedTagFromRemote() => SelectedTag is { } t ? DeleteTagAsync(t.Name, remote: true) : Task.CompletedTask;

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

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _cts?.Cancel();
        _git.Dispose();
    }
}
