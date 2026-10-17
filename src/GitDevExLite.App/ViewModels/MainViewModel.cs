using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly Func<IGitService> _gitFactory;
    private readonly IDialogService _dialogs;
    private readonly RepoSettingsStore _store;
    private readonly RepoSettings _settings;

    public MainViewModel(Func<IGitService> gitFactory, IDialogService dialogs, RepoSettingsStore store)
    {
        _gitFactory = gitFactory;
        _dialogs = dialogs;
        _store = store;
        _settings = store.Load();
        foreach (var r in _settings.RecentRepos) RecentRepos.Add(r);
    }

    public ObservableCollection<RepoTabViewModel> Tabs { get; } = new();
    public ObservableCollection<string> RecentRepos { get; } = new();

    [ObservableProperty] private RepoTabViewModel? _selectedTab;
    [ObservableProperty] private string _title = "GitDevExLite";
    [ObservableProperty] private string? _statusText = "Open a repository to begin.";

    partial void OnSelectedTabChanged(RepoTabViewModel? value)
    {
        Title = value is null ? "GitDevExLite" : $"{value.Header} - GitDevExLite";
        SaveTabs();
    }

    /// <summary>Opens the repo in a new tab, or focuses the tab that already shows it.</summary>
    public void OpenRepository(string path)
    {
        try
        {
            var existing = Tabs.FirstOrDefault(t => SamePath(t.RepositoryPath, path));
            if (existing is not null)
            {
                SelectedTab = existing;
                return;
            }

            var tab = new RepoTabViewModel(_gitFactory(), _dialogs);
            tab.Open(path);
            var resolved = tab.RepositoryPath!;

            // The picked folder may be a subfolder of a repo that is already open.
            var duplicate = Tabs.FirstOrDefault(t => SamePath(t.RepositoryPath, resolved));
            if (duplicate is not null)
            {
                tab.Dispose();
                SelectedTab = duplicate;
                return;
            }

            Tabs.Add(tab);
            SelectedTab = tab;
            _settings.AddRecent(resolved);
            RecentRepos.Clear();
            foreach (var r in _settings.RecentRepos) RecentRepos.Add(r);
            SaveTabs();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    /// <summary>Reopens tabs saved by the previous session, skipping repos that no longer open.</summary>
    public void RestoreTabs()
    {
        var active = _settings.ActiveTab;
        foreach (var path in _settings.OpenTabs.ToList()) OpenRepository(path);
        var tab = Tabs.FirstOrDefault(t => SamePath(t.RepositoryPath, active));
        if (tab is not null) SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(RepoTabViewModel? tab)
    {
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        var wasSelected = SelectedTab == tab;
        Tabs.Remove(tab);
        tab.Dispose();
        if (wasSelected || SelectedTab is null)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        SaveTabs();
    }

    [RelayCommand]
    private void RefreshSelected() => SelectedTab?.RefreshCommand.Execute(null);

    [RelayCommand]
    private void CheckoutSelected() => SelectedTab?.CheckoutCommand.Execute(null);

    private void SaveTabs()
    {
        _settings.OpenTabs = Tabs.Select(t => t.RepositoryPath).OfType<string>().ToList();
        _settings.ActiveTab = SelectedTab?.RepositoryPath;
        _store.Save(_settings);
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
}
