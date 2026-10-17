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
            var window = new MainWindow();
            var vm = new MainViewModel(() => new GitService(), new PullRequestService(), new SecretScanner(), window, new RepoSettingsStore());
            window.DataContext = vm;
            desktop.MainWindow = window;

            vm.RestoreTabs();
            // `GitDevExLite.App <path-to-repo>` opens a repo straight away.
            if (desktop.Args is { Length: > 0 }) vm.OpenRepository(desktop.Args[0]);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
