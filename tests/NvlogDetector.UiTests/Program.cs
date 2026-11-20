using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NvlogDetector;
using NvlogDetector.Core;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "nvlog-ui-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = HistoryStore.Load(path);
            var now = DateTimeOffset.UtcNow.AddMinutes(-10);
            var index = 0;
            foreach (var level in new[] { EpisodeSeverity.Activity, EpisodeSeverity.Spike, EpisodeSeverity.Loop })
                for (var count = 0; count < 2; count++)
                {
                    var bytes = 1024L << index;
                    store.AddEpisode(new StoredEpisode { Start = now.AddSeconds(index * 30),
                        End = now.AddSeconds(index * 30 + 1), Bytes = bytes,
                        Seconds = 2, PeakBytesPerSecond = bytes, Severity = level });
                    index++;
                }
            store.Save();

            var application = new App();
            application.InitializeComponent();
            // App schedules Startup on the dispatcher; this test must never start a real monitor/window.
            var startup = typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
            application.Startup -= (StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler), application);
            var unhandled = typeof(App).GetMethod("OnDispatcherUnhandledException", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
            application.DispatcherUnhandledException -= (DispatcherUnhandledExceptionEventHandler)unhandled.CreateDelegate(typeof(DispatcherUnhandledExceptionEventHandler), application);
            Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
            using var instance = new SingleInstance();
            using (var view = new MainViewModel(instance, path))
            {
                Check(view.EpisodesView.Cast<EpisodeRow>().Count() == 6, "All colors initially visible");
                view.ShowYellow = false;
                view.ShowRed = false;
                Check(view.EpisodesView.Cast<EpisodeRow>().All(row => row.Severity == EpisodeSeverity.Activity), "Blue filter");
                Check(view.EpisodesView.Cast<EpisodeRow>().Count() == 2, "Blue row count");
                var promoted = view.Episodes.First(row => row.Severity == EpisodeSeverity.Activity);
                promoted.Refresh(new StoredEpisode { Start = promoted.Start, Bytes = 1234567, Seconds = 5,
                    Severity = EpisodeSeverity.Spike, Open = true }, true, DateTimeOffset.UtcNow);
                typeof(MainViewModel).GetMethod("RefreshActivityView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
                Check(view.EpisodesView.Cast<EpisodeRow>().Count() == 1, "Live color promotion updates the filter");
                promoted.Refresh(new StoredEpisode { Start = promoted.Start, End = promoted.Start.AddSeconds(1), Bytes = 1024,
                    Seconds = 2, Severity = EpisodeSeverity.Activity }, false, DateTimeOffset.UtcNow);
                Check(promoted.DurationText == ByteFormat.Duration(TimeSpan.FromSeconds(2)), "Duration matches sampled seconds");
                view.ShowBlue = false;
                view.ShowBlue = true;
                view.ShowBlue = false;
                Check(view.EpisodesView.IsEmpty && !view.HasEpisodes, "Empty-filter state");
                view.ShowYellow = true;
                Check(view.EpisodesView.Cast<EpisodeRow>().All(row => row.Severity == EpisodeSeverity.Spike), "Yellow filter");
                view.ShowYellow = false;
                view.ShowRed = true;
                Check(view.EpisodesView.Cast<EpisodeRow>().All(row => row.Severity == EpisodeSeverity.Loop), "Red filter");
                view.ShowBlue = view.ShowYellow = true;
                view.SelectedOrder = 1;
                Check(view.EpisodesView.Cast<EpisodeRow>().Select(row => row.SeverityRank).SequenceEqual(new[] { 0, 0, 1, 1, 2, 2 }), "Blue-first order");
                view.SelectedOrder = 2;
                Check(view.EpisodesView.Cast<EpisodeRow>().Select(row => row.SeverityRank).SequenceEqual(new[] { 2, 2, 1, 1, 0, 0 }), "Red-first order");
                view.SelectedOrder = 3;
                Check(view.EpisodesView.Cast<EpisodeRow>().Select(row => row.Bytes).SequenceEqual(view.Episodes.Select(row => row.Bytes).OrderDescending()), "Numeric volume order");
                view.SelectedOrder = 0;
                Check(view.EpisodesView.Cast<EpisodeRow>().Select(row => row.Start).SequenceEqual(view.Episodes.Select(row => row.Start).OrderDescending()), "Newest-first order");
            }

            if (args.Length == 1)
            {
                // Render a detached visual using isolated test history. Never open or control a desktop window.
                using var renderInstance = new SingleInstance();
                var window = new MainWindow(renderInstance, path);
                var root = (FrameworkElement)window.Content;
                var view = window.DataContext;
                window.Content = null;
                ((System.Windows.Controls.Grid)root).Background = new SolidColorBrush(Color.FromRgb(27, 31, 45));
                System.Windows.Documents.TextElement.SetForeground(root, (Brush)application.FindResource("TextFillColorPrimaryBrush"));
                root.DataContext = view;
                root.Width = 1120;
                root.Height = 850;
                root.Measure(new Size(1120, 850));
                root.Arrange(new Rect(0, 0, 1120, 850));
                root.UpdateLayout();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                root.UpdateLayout();
                var grid = (DataGrid)window.FindName("ActivityGrid");
                Check(grid.Items.Count == 6, "Rendered table has all rows");
                Check(grid.ActualHeight >= 140, "Table has visible layout space");
                Check(grid.Columns[0].SortMemberPath == nameof(EpisodeRow.SeverityRank), "Level column sorts by rank");
                Check(grid.Columns[4].SortMemberPath == nameof(EpisodeRow.Bytes), "Volume column sorts numerically");
                var bitmap = new RenderTargetBitmap(1120, 850, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(args[0]);
                encoder.Save(output);
            }
            Console.WriteLine("PASS: color filters, empty state, all four sort orders, numeric column sorting, and table layout.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { File.Delete(path); }
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
