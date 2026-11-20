using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NvlogDetector;

public partial class MainWindow : FluentWindow
{
    readonly MainViewModel _viewModel;

    public MainWindow(SingleInstance instance, string? historyPath = null, NvlogDetector.Core.INvtoppsPlatform? workaroundPlatform = null)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _viewModel = new MainViewModel(instance, historyPath, workaroundPlatform);
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Start();
        Closing += (_, args) =>
        {
            if (!_viewModel.IsWorkaroundBusy) return;
            args.Cancel = true;
            System.Windows.MessageBox.Show("Please wait for the NVIDIA settings operation to finish before closing.",
                "Settings operation in progress", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        };
        Closed += (_, _) => _viewModel.Dispose();
    }
}
