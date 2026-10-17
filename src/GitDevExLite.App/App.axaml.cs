using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GitDevExLite.App.ViewModels;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(new GitService());
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // `GitDevExLite.App <path-to-repo>` opens a repo straight away.
            if (desktop.Args is { Length: > 0 }) vm.LoadRepository(desktop.Args[0]);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
