using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Graph;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IGitService _git;

    public MainViewModel(IGitService git) => _git = git;

    public ObservableCollection<RefInfo> LocalBranches { get; } = new();
    public ObservableCollection<RefInfo> RemoteBranches { get; } = new();
    public ObservableCollection<RefInfo> Tags { get; } = new();
    public ObservableCollection<StashInfo> Stashes { get; } = new();

    [ObservableProperty] private Dictionary<string, List<RefInfo>> _refsByCommit = new();
    [ObservableProperty] private string _title = "GitDevExLite";
    [ObservableProperty] private string? _statusText = "Open a repository to begin.";
    [ObservableProperty] private GraphLayoutResult? _graph;
    [ObservableProperty] private GraphRow? _selectedRow;
    [ObservableProperty] private RefInfo? _selectedBranch;

    public void LoadRepository(string path)
    {
        try
        {
            _git.Open(path);
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
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

        var commits = _git.GetCommits();
        Graph = GraphLayout.Build(commits);
        SelectedRow = null;

        var dir = new DirectoryInfo(_git.RepositoryPath.TrimEnd('/', '\\'));
        Title = $"{dir.Name} - GitDevExLite";
        StatusText = $"{commits.Count} commits, {Graph.LaneCount} lanes";
    }

    [RelayCommand]
    private void Checkout()
    {
        if (SelectedBranch is not { Kind: RefKind.LocalBranch } b) return;
        try
        {
            _git.Checkout(b.Name);
            Refresh();
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
}
