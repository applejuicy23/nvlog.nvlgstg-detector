using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NvlogDetector;

public partial class MainWindow : FluentWindow
{
    readonly MainViewModel _viewModel;

    public MainWindow(SingleInstance instance, string? historyPath = null)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _viewModel = new MainViewModel(instance, historyPath);
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Start();
        Closed += (_, _) => _viewModel.Dispose();
    }
}
