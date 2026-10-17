using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

/// <summary>Reflog, submodules and worktrees for the open repository.</summary>
public sealed partial class RepoToolsViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IDialogService _dialogs;
    private readonly Action<string> _openRepository;

    public RepoToolsViewModel(IGitService git, IDialogService dialogs, Action<string> openRepository)
    {
        _git = git;
        _dialogs = dialogs;
        _openRepository = openRepository;
    }

    public ObservableCollection<ReflogItem> Reflog { get; } = new();
    public ObservableCollection<SubmoduleInfo> Submodules { get; } = new();
    public ObservableCollection<WorktreeInfo> Worktrees { get; } = new();

    public event Action? CloseRequested;

    [ObservableProperty] private ReflogItem? _selectedReflog;
    [ObservableProperty] private SubmoduleInfo? _selectedSubmodule;
    [ObservableProperty] private WorktreeInfo? _selectedWorktree;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    public async Task LoadAsync()
    {
        try
        {
            Replace(Reflog, _git.GetReflog());
            Replace(Submodules, _git.GetSubmodules());
            Replace(Worktrees, await _git.GetWorktreesAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private async Task RunAsync(string doing, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = doing + "...";
        try
        {
            await action();
            await LoadAsync();
            Status = "Done.";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- reflog ----------------------------------------------------------------

    [RelayCommand]
    private Task ResetMixed() => ResetSelectedAsync(ResetMode.Mixed);

    [RelayCommand]
    private Task ResetHard() => ResetSelectedAsync(ResetMode.Hard);

    private async Task ResetSelectedAsync(ResetMode mode)
    {
        if (SelectedReflog is not { } item) return;
        if (_git.GetOperationState().InProgress)
        {
            Status = "Finish or abort the operation in progress first.";
            return;
        }
        if (mode == ResetMode.Hard &&
            !await _dialogs.ConfirmAsync("Hard reset", $"Move the current branch to {item.ShortSha}? Uncommitted changes are discarded."))
            return;
        await RunAsync($"Reset ({mode.ToString().ToLowerInvariant()}) to {item.ShortSha}",
            () => _git.ResetAsync(item.Sha, mode, null, CancellationToken.None));
    }

    // ---- submodules ------------------------------------------------------------

    [RelayCommand]
    private Task UpdateSubmodule() => SelectedSubmodule is { } s
        ? RunAsync($"Updating {s.Path}", () => _git.UpdateSubmodulesAsync(s.Path, null, CancellationToken.None))
        : Task.CompletedTask;

    [RelayCommand]
    private Task UpdateAllSubmodules() =>
        RunAsync("Updating submodules", () => _git.UpdateSubmodulesAsync(null, null, CancellationToken.None));

    [RelayCommand]
    private void OpenSubmodule()
    {
        if (SelectedSubmodule is not { Initialized: true } s) return;
        Open(System.IO.Path.Combine(_git.RepositoryPath!, s.Path));
    }

    // ---- worktrees -------------------------------------------------------------

    [RelayCommand]
    private async Task AddWorktree()
    {
        var branch = (await _dialogs.PromptAsync("Add worktree",
            "New branch name (leave empty to check out an existing branch or commit):"))?.Trim();
        if (branch is null) return;

        string? commitish = null;
        if (branch.Length == 0)
        {
            commitish = (await _dialogs.PromptAsync("Add worktree", "Existing branch or commit to check out:"))?.Trim();
            if (string.IsNullOrEmpty(commitish)) return;
        }

        var root = _git.RepositoryPath!.TrimEnd('/', '\\');
        var suggestion = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(root) ?? root,
            $"{System.IO.Path.GetFileName(root)}-{(branch.Length > 0 ? branch : commitish)}".Replace('/', '-'));
        var path = (await _dialogs.PromptAsync("Add worktree", "Folder for the new worktree:", suggestion))?.Trim();
        if (string.IsNullOrEmpty(path)) return;

        await RunAsync("Adding worktree",
            () => _git.AddWorktreeAsync(path, branch.Length > 0 ? branch : null, commitish, CancellationToken.None));
    }

    [RelayCommand]
    private async Task RemoveWorktree()
    {
        if (SelectedWorktree is not { } w) return;
        if (w.IsMain)
        {
            Status = "The main worktree cannot be removed.";
            return;
        }
        if (!await _dialogs.ConfirmAsync("Remove worktree", $"Remove the worktree at {w.Path}? Uncommitted changes there block this."))
            return;
        await RunAsync("Removing worktree", () => _git.RemoveWorktreeAsync(w.Path, force: false, CancellationToken.None));
    }

    [RelayCommand]
    private Task PruneWorktrees() => RunAsync("Pruning", () => _git.PruneWorktreesAsync(CancellationToken.None));

    [RelayCommand]
    private void OpenWorktree()
    {
        if (SelectedWorktree is { } w) Open(w.Path);
    }

    private void Open(string path)
    {
        _openRepository(path);
        CloseRequested?.Invoke();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
