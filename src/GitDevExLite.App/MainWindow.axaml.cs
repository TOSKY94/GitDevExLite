using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using GitDevExLite.App.ViewModels;

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

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
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
