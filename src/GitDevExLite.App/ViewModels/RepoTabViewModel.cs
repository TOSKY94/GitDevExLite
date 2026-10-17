using System.Collections.ObjectModel;
using Avalonia.Threading;
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

    [ObservableProperty] private RefInfo? _selectedTag;
    [ObservableProperty] private string _operationText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _progressIndeterminate = true;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(FetchCommand), nameof(PullMergeCommand),
         nameof(PullRebaseCommand), nameof(PullFastForwardCommand), nameof(PushCommand))]
    private bool _isBusy;

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
        else if (ShowWorkingDirectory) ActiveDiff = null;

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
                actions.Add(new($"Set upstream for '{branch.Name}'...", () => SetUpstreamAsync(branch)));
                if (!branch.IsHead)
                    actions.Add(new($"Delete '{branch.Name}'...", () => DeleteBranchAsync(branch)));
            }
        }

        actions.Add(new("Create tag here...", () => CreateTagAsync(sha)));

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
        RunRemoteAsync($"Pull ({mode})", (p, ct) => _git.PullAsync(mode, p, ct), includeStatus: true);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task Push() => RunRemoteAsync("Push", (p, ct) => _git.PushAsync(p, ct), includeStatus: false);

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
