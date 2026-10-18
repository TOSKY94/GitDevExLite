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

    public async Task<PullRequestCreationFormResult?> ShowPullRequestCreationAsync(
        HostedRepositoryId repository,
        IReadOnlyList<string> sourceBranches,
        IReadOnlyList<string> targetBranches,
        CreatePullRequestDraft draft,
        IReadOnlyList<PullRequestTemplateOption> templates)
    {
        var repoText = new TextBox
        {
            Text = repository.Provider == HostingProviderKind.AzureDevOps
                ? $"{repository.Owner}/{repository.Project}/{repository.Name}"
                : $"{repository.Owner}/{repository.Name}",
            IsReadOnly = true
        };
        var sourceOptions = sourceBranches.Contains(draft.SourceBranch, StringComparer.OrdinalIgnoreCase)
            ? sourceBranches
            : sourceBranches.Append(draft.SourceBranch).ToArray();
        var sourceChoice = new ComboBox
        {
            ItemsSource = sourceOptions,
            SelectedItem = draft.SourceBranch,
            MinWidth = 420
        };
        var targetOptions = targetBranches.Contains(draft.TargetBranch, StringComparer.OrdinalIgnoreCase)
            ? targetBranches
            : targetBranches.Append(draft.TargetBranch).ToArray();
        var targetChoice = new ComboBox
        {
            ItemsSource = targetOptions,
            SelectedItem = draft.TargetBranch,
            MinWidth = 420
        };
        var title = new TextBox { Text = draft.Title, Watermark = "Pull request title" };
        var description = new TextBox
        {
            Text = draft.Description,
            Watermark = "Describe the changes and context for reviewers",
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top,
            MinHeight = 230
        };
        var templateOptions = new[] { new PullRequestTemplateOption("No template", string.Empty) }
            .Concat(templates).ToArray();
        var templateChoice = new ComboBox
        {
            ItemsSource = templateOptions,
            SelectedItem = templates.FirstOrDefault(template => template.IsDefault) ?? templateOptions[0],
            MinWidth = 280
        };
        var submitAsDraft = new CheckBox { Content = "Submit as draft", IsChecked = draft.IsDraft };
        var helper = new TextBlock { Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var generate = new Button { Content = "[Generate title and description ]" };
        generate.Click += (_, _) => helper.Text = "Title and description generation will be available later.";
        templateChoice.SelectionChanged += (_, _) =>
        {
            if (templateChoice.SelectedItem is not PullRequestTemplateOption selected || string.IsNullOrWhiteSpace(selected.Content))
                return;
            description.Text = selected.IsDefault || string.IsNullOrWhiteSpace(description.Text)
                ? selected.Content
                : $"{description.Text.TrimEnd()}\n\n{selected.Content}";
            helper.Text = $"Loaded template: {selected.Name}";
        };
        if (templateChoice.SelectedItem is PullRequestTemplateOption { IsDefault: true } defaultTemplate)
        {
            description.Text = defaultTemplate.Content;
            helper.Text = $"Loaded default template: {defaultTemplate.Name}";
        }

        var validation = new TextBlock { Foreground = Avalonia.Media.Brushes.OrangeRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var create = new Button { Content = "Create pull request", IsDefault = true };
        var contentGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var actionPanel = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(20, 8, 20, 16),
            Children =
            {
                validation,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, create }
                }
            }
        };
        Grid.SetRow(actionPanel, 1);
        contentGrid.Children.Add(new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = "Create pull request", FontSize = 22, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = "Review the repository and branch details, then write a title and description for reviewers.", Opacity = 0.75, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    FormField("Repository", repoText),
                    FormField("Source branch", sourceChoice),
                    FormField("Target branch", targetChoice),
                    FormField("Title", title),
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Template", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                            templateChoice
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Description", FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                            generate
                        }
                    },
                    description,
                    helper,
                    submitAsDraft
                }
            }
        });
        contentGrid.Children.Add(actionPanel);
        var dialog = new Window
        {
            Title = "Create pull request",
            Width = 820,
            Height = 760,
            MinWidth = 680,
            MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = contentGrid
        };
        cancel.Click += (_, _) => dialog.Close(null);
        create.Click += (_, _) =>
        {
            var selectedTarget = (targetChoice.SelectedItem as string)?.Trim();
            var selectedSource = (sourceChoice.SelectedItem as string)?.Trim();
            var selectedTitle = title.Text?.Trim();
            if (string.IsNullOrWhiteSpace(selectedSource) || string.IsNullOrWhiteSpace(selectedTarget) || string.IsNullOrWhiteSpace(selectedTitle))
            {
                validation.Text = "Choose a target branch and enter a title to continue.";
                return;
            }
            dialog.Close(new PullRequestCreationFormResult(
                selectedSource,
                selectedTarget,
                selectedTitle,
                description.Text ?? string.Empty,
                submitAsDraft.IsChecked == true));
        };

        return await dialog.ShowDialog<PullRequestCreationFormResult?>(this);
    }

    private static StackPanel FormField(string label, Control control) => new()
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = label, FontWeight = Avalonia.Media.FontWeight.SemiBold },
            control
        }
    };

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
        var isAzureDevOps = GitCredentials.IsAzureDevOpsUrl(request.Url);
        var organization = new TextBox
        {
            Watermark = "Organization name",
            Text = GitCredentials.GetAzureDevOpsOrganization(request.Url) ?? "",
            MinWidth = 320
        };

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
        if (isAzureDevOps)
        {
            panel.Children.Add(new TextBlock { Text = "Azure DevOps organization" });
            panel.Children.Add(organization);
        }
        panel.Children.Add(user);
        panel.Children.Add(secret);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var dialog = Dialog("Authentication required", panel);
        ok.Click += (_, _) =>
        {
            if (gcm.IsChecked == true)
            {
                dialog.Close(new CredentialChoice(true, null, null, isAzureDevOps ? organization.Text?.Trim() : null));
                return;
            }
            if (string.IsNullOrWhiteSpace(user.Text) || string.IsNullOrEmpty(secret.Text)) return;
            dialog.Close(new CredentialChoice(false, user.Text.Trim(), secret.Text, isAzureDevOps ? organization.Text?.Trim() : null));
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
