using Avalonia.Controls;
using GitDevExLite.App.ViewModels;

namespace GitDevExLite.App.Views;

public partial class FileHistoryWindow : Window
{
    private FileHistoryViewModel? _vm;

    public FileHistoryWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.CloseRequested -= Close;
            _vm = DataContext as FileHistoryViewModel;
            if (_vm is not null) _vm.CloseRequested += Close;
        };
    }

    public void ShowBlameTab() => Tabs.SelectedIndex = 1;
}
