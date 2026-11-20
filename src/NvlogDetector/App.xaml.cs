using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace NvlogDetector;

public partial class App : Application
{
    SingleInstance? _instance;

    void OnStartup(object sender, StartupEventArgs e)
    {
        ApplicationThemeManager.ApplySystemTheme();

        _instance = new SingleInstance();
        if (!_instance.Acquired)
        {
            MessageBox.Show(
                "The detector is already running.",
                "nvlog.nvlgstg",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var window = new MainWindow(_instance);
        MainWindow = window;
        window.Show();
    }

    void OnExit(object sender, ExitEventArgs e) => _instance?.Dispose();

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "nvlog.nvlgstg-detector");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"[{DateTimeOffset.Now:O}] {e.Exception}\n");
        }
        catch (IOException)
        {
            // The dialog still reports the failure.
        }

        MessageBox.Show(
            e.Exception.Message,
            "nvlog.nvlgstg",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
