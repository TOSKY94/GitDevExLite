using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GitDevExLite.App.ViewModels;

namespace GitDevExLite.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open git repository",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is MainViewModel vm &&
            folders[0].TryGetLocalPath() is { } path)
            vm.LoadRepository(path);
    }
}
