using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GitDevExLite.App.ViewModels;
using GitDevExLite.Core.Models;

namespace GitDevExLite.App.Views;

public partial class RepoView : UserControl
{
    private PullRequestDetailsWindow? _pullRequestDetailsWindow;

    public RepoView() => InitializeComponent();

    private void OnPullRequestDoubleTapped(object? sender, TappedEventArgs e) => OpenPullRequestDetails();

    private void OnOpenPullRequestDetails(object? sender, RoutedEventArgs e) => OpenPullRequestDetails();

    private void OnBranchPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || !e.GetCurrentPoint(list).Properties.IsRightButtonPressed)
            return;

        if (e.Source is Control { DataContext: RefInfo branch })
        {
            list.SelectedItem = branch;
            if (DataContext is RepoTabViewModel viewModel)
                viewModel.SelectedBranch = branch;
        }
    }

    private void OpenPullRequestDetails()
    {
        if (DataContext is not RepoTabViewModel { SelectedPullRequest: not null } viewModel)
            return;

        if (_pullRequestDetailsWindow is { IsVisible: true } existingWindow)
        {
            existingWindow.Activate();
            return;
        }

        var window = new PullRequestDetailsWindow { DataContext = viewModel };
        _pullRequestDetailsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pullRequestDetailsWindow, window))
                _pullRequestDetailsWindow = null;
        };

        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }
}
