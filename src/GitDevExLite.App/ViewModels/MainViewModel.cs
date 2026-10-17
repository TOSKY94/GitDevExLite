using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly Func<IGitService> _gitFactory;
    private readonly IPullRequestService _pullRequests;
    private readonly ISecretScanner _secretScanner;
    private readonly IDialogService _dialogs;
    private readonly RepoSettingsStore _store;
    private readonly RepoSettings _settings;
    private readonly DispatcherTimer _autoFetchTimer;

    public MainViewModel(Func<IGitService> gitFactory, IPullRequestService pullRequests, ISecretScanner secretScanner, IDialogService dialogs, RepoSettingsStore store)
    {
        _gitFactory = gitFactory;
        _pullRequests = pullRequests;
        _secretScanner = secretScanner;
        _dialogs = dialogs;
        _store = store;
        _settings = store.Load();
        foreach (var r in _settings.RecentRepos) RecentRepos.Add(r);

        Options.AutoFetch = _settings.AutoFetch;
        Options.PruneOnFetch = _settings.PruneOnFetch;
        Options.AutoFetchMinutes = Math.Max(1, _settings.AutoFetchMinutes);
        Options.PropertyChanged += (_, _) => SaveTabs();

        _autoFetchTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Options.AutoFetchMinutes) };
        _autoFetchTimer.Tick += async (_, _) =>
        {
            if (Options.AutoFetch && SelectedTab is { } tab) await tab.AutoFetchAsync();
        };
        _autoFetchTimer.Start();
    }

    public RemoteOptions Options { get; } = new();

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

            var tab = new RepoTabViewModel(_gitFactory(), _pullRequests, _secretScanner, _dialogs, Options);
            tab.OpenRepositoryRequested += OpenRepository;
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

    [RelayCommand]
    private async Task Clone()
    {
        var parent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = await _dialogs.ShowCloneDialogAsync(RunCloneAsync, parent);
        if (path is not null) OpenRepository(path);
    }

    private Task RunCloneAsync(string url, string destination, IProgress<GitProgress> progress, CancellationToken ct) =>
        AuthRetry.RunAsync(_dialogs, () => url, () => GitClone.CloneAsync(url, destination, progress, ct), ct);

    private void SaveTabs()
    {
        _settings.OpenTabs = Tabs.Select(t => t.RepositoryPath).OfType<string>().ToList();
        _settings.ActiveTab = SelectedTab?.RepositoryPath;
        _settings.AutoFetch = Options.AutoFetch;
        _settings.PruneOnFetch = Options.PruneOnFetch;
        _store.Save(_settings);
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
}
