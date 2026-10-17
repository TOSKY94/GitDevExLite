using Avalonia.Controls;
using GitDevExLite.App.ViewModels;

namespace GitDevExLite.App.Views;

public partial class RepoToolsWindow : Window
{
    private RepoToolsViewModel? _vm;

    public RepoToolsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.CloseRequested -= Close;
            _vm = DataContext as RepoToolsViewModel;
            if (_vm is not null) _vm.CloseRequested += Close;
        };
    }
}
