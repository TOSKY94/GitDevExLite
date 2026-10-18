using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using GitDevExLite.App.ViewModels;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App;

public partial class MainWindow : Window, IDialogService
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
    }

    private void AttachViewModel()
    {
        if (_vm is not null) _vm.RecentRepos.CollectionChanged -= OnRecentChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.RecentRepos.CollectionChanged += OnRecentChanged;
        RebuildRecentMenu();
    }

    private void OnRecentChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRecentMenu();

    private void RebuildRecentMenu()
    {
        var items = new List<MenuItem>();
        foreach (var path in _vm!.RecentRepos)
        {
            var item = new MenuItem { Header = path.Replace("_", "__") };
            item.Click += (_, _) => _vm.OpenRepository(path);
            items.Add(item);
        }
        if (items.Count == 0) items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        RecentMenu.ItemsSource = items;
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open git repository",
            AllowMultiple = false
        });

        if (folders.Count > 0 && _vm is not null && folders[0].TryGetLocalPath() is { } path)
            _vm.OpenRepository(path);
    }

    public async Task<string?> PromptAsync(string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = Dialog(title, new StackPanel
        {
            Spacing = 8,
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new TextBlock { Text = label },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel }
                }
            }
        });
        ok.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var yes = new Button { Content = "Yes", IsDefault = true };
        var no = new Button { Content = "No", IsCancel = true };
        var dialog = Dialog(title, new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 360 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { yes, no }
                }
            }
        });
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    public async Task<PullRequestMergeStrategy?> ChoosePullRequestMergeStrategyAsync(
        IReadOnlyList<PullRequestMergeStrategy> supportedStrategies)
    {
        var options = supportedStrategies.Select(strategy => new ComboBoxItem
        {
            Content = strategy switch
            {
                PullRequestMergeStrategy.MergeCommit => "Merge commit",
                PullRequestMergeStrategy.Squash => "Squash",
                PullRequestMergeStrategy.Rebase => "Rebase",
                _ => strategy.ToString()
            },
            Tag = strategy
        }).ToArray();
        if (options.Length == 0) return null;

        var choice = new ComboBox
        {
            ItemsSource = options,
            SelectedIndex = Math.Max(0, Array.FindIndex(options, option =>
                (PullRequestMergeStrategy)option.Tag! == PullRequestMergeStrategy.Squash)),
            MinWidth = 260
        };
        var ok = new Button { Content = "Continue", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = Dialog("Merge strategy", new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new TextBlock { Text = "Choose how to merge this pull request." },
                choice,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel }
                }
            }
        });
        ok.Click += (_, _) => dialog.Close(choice.SelectedItem is ComboBoxItem { Tag: PullRequestMergeStrategy strategy } ? strategy : null);
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<PullRequestMergeStrategy?>(this);
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task<CredentialChoice?> PromptCredentialsAsync(CredentialRequest request)
    {
        var message = new TextBlock
        {
            Text = request.Message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 440,
            Opacity = 0.8
        };
        var ok = new Button { Content = request.IsSsh ? "Close" : "Continue", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var panel = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Authentication required", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(message);

        if (request.IsSsh)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "This remote uses SSH. Load your key into ssh-agent (ssh-add) or configure it in ~/.ssh/config, then retry.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 440
            });
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            var sshDialog = Dialog("Authentication required", panel);
            ok.Click += (_, _) => sshDialog.Close(null);
            await sshDialog.ShowDialog<object?>(this);
            return null;
        }

        GitCredentials.TryParseHttp(request.Url, out _, out var host);
        var canUseGcm = request.GcmAvailable && host.Length > 0;
        var gcm = new RadioButton { Content = $"Sign in to {host} with Git Credential Manager", GroupName = "auth", IsChecked = canUseGcm, IsVisible = canUseGcm };
        var token = new RadioButton { Content = "Use a personal access token", GroupName = "auth", IsChecked = !canUseGcm };
        var user = new TextBox { Watermark = "Username", Text = "PAT" };
        var secret = new TextBox { Watermark = "Token", PasswordChar = '\u2022', MinWidth = 320 };

        void Sync()
        {
            var useToken = token.IsChecked == true;
            user.IsEnabled = secret.IsEnabled = useToken;
        }
        gcm.IsCheckedChanged += (_, _) => Sync();
        token.IsCheckedChanged += (_, _) => Sync();
        Sync();

        panel.Children.Add(gcm);
        panel.Children.Add(token);
        panel.Children.Add(user);
        panel.Children.Add(secret);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var dialog = Dialog("Authentication required", panel);
        ok.Click += (_, _) =>
        {
            if (gcm.IsChecked == true) { dialog.Close(new CredentialChoice(true, null, null)); return; }
            if (string.IsNullOrWhiteSpace(user.Text) || string.IsNullOrEmpty(secret.Text)) return;
            dialog.Close(new CredentialChoice(false, user.Text.Trim(), secret.Text));
        };
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<CredentialChoice?>(this);
    }

    public async Task<string?> ShowCloneDialogAsync(CloneRunner clone, string defaultParentFolder)
    {
        var url = new TextBox { Watermark = "Repository URL", MinWidth = 420 };
        var parent = new TextBox { Text = defaultParentFolder, MinWidth = 340 };
        var browse = new Button { Content = "Browse..." };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 440, Opacity = 0.8 };
        var bar = new ProgressBar { Maximum = 100, IsVisible = false };
        var go = new Button { Content = "Clone", IsDefault = true };
        var cancel = new Button { Content = "Cancel" };

        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { parent, browse } };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { go, cancel }
        };
        var dialog = Dialog("Clone repository", new StackPanel
        {
            Spacing = 8,
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new TextBlock { Text = "Repository URL" }, url,
                new TextBlock { Text = "Clone into folder" }, folderRow,
                bar, status, buttons
            }
        });

        CancellationTokenSource? cts = null;

        browse.Click += async (_, _) =>
        {
            var picked = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } p) parent.Text = p;
        };

        go.Click += async (_, _) =>
        {
            if (cts is not null) return;
            var address = url.Text?.Trim() ?? "";
            if (address.Length == 0 || string.IsNullOrWhiteSpace(parent.Text))
            {
                status.Text = "Enter a URL and a folder.";
                return;
            }

            var name = GitClone.SuggestFolderName(address);
            var destination = System.IO.Path.Combine(parent.Text.Trim(), name);
            cts = new CancellationTokenSource();
            go.IsEnabled = url.IsEnabled = parent.IsEnabled = browse.IsEnabled = false;
            bar.IsVisible = true;
            bar.IsIndeterminate = true;
            status.Text = "Cloning...";

            var progress = new Progress<GitProgress>(p =>
            {
                status.Text = p.Text;
                if (p.Percent is { } pct)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = pct;
                }
            });

            try
            {
                await clone(address, destination, progress, cts.Token);
                cts.Dispose();
                cts = null;
                dialog.Close(destination);
                return;
            }
            catch (OperationCanceledException)
            {
                status.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }

            cts?.Dispose();
            cts = null;
            go.IsEnabled = url.IsEnabled = parent.IsEnabled = browse.IsEnabled = true;
            bar.IsVisible = false;
        };

        cancel.Click += (_, _) =>
        {
            if (cts is not null) cts.Cancel();
            else dialog.Close(null);
        };
        dialog.Closing += (_, _) => cts?.Cancel();

        return await dialog.ShowDialog<string?>(this);
    }

    public async Task<RebaseRequest?> ShowInteractiveRebaseAsync(IReadOnlyList<RebaseEntry> entries, string description)
    {
        var dialog = new Dialogs.InteractiveRebaseDialog(entries, description);
        await dialog.ShowDialog(this);
        return dialog.Result;
    }

    public async Task ShowFileHistoryAsync(FileHistoryViewModel history, bool startOnBlame)
    {
        var window = new Views.FileHistoryWindow { DataContext = history };
        if (startOnBlame) window.ShowBlameTab();
        await window.ShowDialog(this);
    }

    public async Task ShowRepoToolsAsync(RepoToolsViewModel tools)
    {
        await tools.LoadAsync();
        await new Views.RepoToolsWindow { DataContext = tools }.ShowDialog(this);
    }

    private static Window Dialog(string title, Control content) => new()
    {
        Title = title,
        Content = content,
        SizeToContent = SizeToContent.WidthAndHeight,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false
    };
}
