using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using NvlogDetector;
using NvlogDetector.Core;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "nvlog-ui-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            CheckRegistryWorkaround();
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
            using (var view = new MainViewModel(instance, path, new ReadOnlyWorkaroundPlatform(false)))
            {
                Check(view.EpisodesView.Cast<EpisodeRow>().Count() == 6, "All colors initially visible");
                Check(view.WorkaroundActivityText.Contains("OFF (registry)"), "Startup detects OFF registry state");
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

            using (var secondInstance = new SingleInstance())
            using (var restarted = new MainViewModel(secondInstance, path, new ReadOnlyWorkaroundPlatform(true)))
            {
                Check(restarted.WorkaroundConfigured && restarted.WorkaroundActivityText.Contains("ON (registry)"), "Restart rereads actual registry state");
                Check(restarted.WorkaroundHistory.Count == 2, "Changed state recorded after restart");
                Check(restarted.WorkaroundHistory[0].Contains("actual change time and actor unknown"), "External change has no invented actor");
            }
            using (var thirdInstance = new SingleInstance())
            using (var unchanged = new MainViewModel(thirdInstance, path, new ReadOnlyWorkaroundPlatform(true)))
                Check(unchanged.WorkaroundHistory.Count == 2, "Unchanged startup preserves history without duplicates");
            using (var fourthInstance = new SingleInstance())
            using (var unknown = new MainViewModel(fourthInstance, path, new ReadOnlyWorkaroundPlatform(true, fail: true)))
            {
                Check(!unknown.WorkaroundConfigurationKnown && unknown.WorkaroundActivityText.Contains("UNKNOWN"), "Unreadable registry is UNKNOWN, not OFF");
                Check(unknown.WorkaroundHistory.Count == 2, "Read failure preserves prior observations");
            }

            // Layout and scrolling are checked in CI too; the PNG is optional.
            {
                var renderHistory = HistoryStore.Load(path);
                for (var count = 0; count < 40; count++)
                    renderHistory.AddEpisode(new StoredEpisode { Start = now.AddHours(-2).AddMinutes(count),
                        End = now.AddHours(-2).AddMinutes(count).AddSeconds(20), Bytes = 1048576,
                        Seconds = 21, PeakBytesPerSecond = 524288,
                        Severity = count % 3 == 0 ? EpisodeSeverity.Loop : count % 3 == 1 ? EpisodeSeverity.Spike : EpisodeSeverity.Activity });
                renderHistory.Save();
                // Render a detached visual using isolated test history. Never open or control a desktop window.
                using var renderInstance = new SingleInstance();
                var window = new MainWindow(renderInstance, path, new ReadOnlyWorkaroundPlatform(true));
                var root = (FrameworkElement)window.Content;
                var view = window.DataContext;
                window.Content = null;
                ((System.Windows.Controls.Grid)root).Background = new SolidColorBrush(Color.FromRgb(27, 31, 45));
                System.Windows.Documents.TextElement.SetForeground(root, (Brush)application.FindResource("TextFillColorPrimaryBrush"));
                root.DataContext = view;
                root.Width = 1120;
                root.Height = 1200;
                root.Measure(new Size(1120, 1200));
                root.Arrange(new Rect(0, 0, 1120, 1200));
                root.UpdateLayout();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                root.UpdateLayout();
                var grid = (DataGrid)window.FindName("ActivityGrid");
                Check(grid.Items.Count == 46, "Rendered table retains all rows");
                Check(grid.ActualHeight >= 350, "Table has a taller viewport");
                var tableScroll = Descendants<ScrollViewer>(grid).First();
                Check(tableScroll.ScrollableHeight > 0, "Table has its own scrollable overflow");
                tableScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = Mouse.MouseWheelEvent });
                root.UpdateLayout();
                Check(tableScroll.VerticalOffset > 0, "Mouse wheel scrolls table rows");
                tableScroll.ScrollToBottom();
                root.UpdateLayout();
                var last = grid.Items[grid.Items.Count - 1];
                Check(grid.ItemContainerGenerator.ContainerFromItem(last) is DataGridRow lastRow &&
                    lastRow.TranslatePoint(new Point(0, 0), grid).Y < grid.ActualHeight,
                    "Last event can be reached inside the table");
                tableScroll.ScrollToTop();
                root.UpdateLayout();
                Check(grid.Columns[0].SortMemberPath == nameof(EpisodeRow.SeverityRank), "Level column sorts by rank");
                Check(grid.Columns[4].SortMemberPath == nameof(EpisodeRow.Bytes), "Volume column sorts numerically");
                Check(window.FindName("ApplyWorkaroundButton") is Button, "Apply workaround button exists");
                Check(window.FindName("RestoreWorkaroundButton") is Button, "Restore workaround button exists");
                Check(window.FindName("WorkaroundActivityBanner") is Border banner && banner.ActualHeight > 50, "Workaround state strip has visible layout space");
                var workaroundBanner = (Border)window.FindName("WorkaroundActivityBanner");
                var applyButton = (Button)window.FindName("ApplyWorkaroundButton");
                Check(workaroundBanner.TranslatePoint(new Point(0, 0), root).Y > applyButton.TranslatePoint(new Point(0, 0), root).Y + applyButton.ActualHeight,
                    "Workaround status is below its controls");
                var historyExpander = (Expander)window.FindName("WorkaroundHistoryExpander");
                historyExpander.IsExpanded = true;
                var expandFrame = new DispatcherFrame();
                var expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                expandTimer.Tick += (_, _) => { expandTimer.Stop(); expandFrame.Continue = false; };
                expandTimer.Start();
                Dispatcher.PushFrame(expandFrame);
                root.UpdateLayout();
                Check(historyExpander.Content is ScrollViewer historyScroll && historyScroll.ActualHeight > 20, "Expanded workaround history has visible layout space");
                if (args.Length == 1)
                {
                    var pageScroll = Descendants<ScrollViewer>(root).First();
                    pageScroll.ScrollToBottom();
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1120, 1200, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(args[0]);
                    encoder.Save(output);
                }
            }
            Console.WriteLine("PASS: isolated registry backup/restore, startup state and history, filters, sorting, table mouse-wheel scrolling and last-row access, and workaround placement. NVIDIA settings/services were not changed.");
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

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    static void CheckRegistryWorkaround()
    {
        var id = Guid.NewGuid();
        var root = @"Software\NvlogDetector.Tests\" + id.ToString("N");
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            var platform = WindowsNvtoppsPlatform.ForRegistryTest(id);
            var original = platform.ReadSetting();
            Check(!original.KeyExisted && platform.ReadBackup() == null, "Initially absent setting and backup");
            var backup = new NvtoppsBackup(original, true, "test");
            platform.SaveBackup(backup);
            Check(WindowsNvtoppsPlatform.ForRegistryTest(id).ReadBackup() == backup, "Durable backup roundtrip across instances");
            platform.DisablePlugin();
            Check(platform.ReadSetting().Disabled, "Registry DWORD set to one");
            using (var key = hive.CreateSubKey(root + @"\Plugin"))
            {
                key.SetValue("Unrelated", "keep");
                using var child = key.CreateSubKey("OtherPluginData");
                child.SetValue("Keep", 17);
            }
            platform.RestoreSetting(original);
            Check(!platform.ReadSetting().ValueExisted, "Only originally absent value is removed");
            using (var key = hive.OpenSubKey(root + @"\Plugin", writable: true))
            {
                Check((string?)key!.GetValue("Unrelated") == "keep", "Unrelated registry value preserved");
                using var child = key.OpenSubKey("OtherPluginData");
                Check((int?)child!.GetValue("Keep") == 17, "Unrelated subkey preserved");
                key.SetValue("DisableLoad", -1, RegistryValueKind.DWord);
            }
            var previous = platform.ReadSetting();
            platform.DisablePlugin();
            platform.RestoreSetting(previous);
            Check(platform.ReadSetting().Value == -1, "Exact original DWORD restored");
            platform.ClearBackup();
            Check(platform.ReadBackup() == null, "Backup cleared");
        }
        finally { hive.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }

    sealed class ReadOnlyWorkaroundPlatform(bool configured, bool fail = false) : INvtoppsPlatform
    {
        public bool ServiceExists => true;
        public bool ServiceRunning => true;
        public NvtoppsSetting ReadSetting() => fail ? throw new UnauthorizedAccessException("Test read failure") : new(true, configured, true, configured ? 1 : 0);
        public NvtoppsBackup? ReadBackup() => null;
        public void SaveBackup(NvtoppsBackup backup) => throw new InvalidOperationException("Unexpected mutation");
        public void ClearBackup() => throw new InvalidOperationException("Unexpected mutation");
        public void DisablePlugin() => throw new InvalidOperationException("Unexpected mutation");
        public void RestoreSetting(NvtoppsSetting original) => throw new InvalidOperationException("Unexpected mutation");
        public void RestartService() => throw new InvalidOperationException("Unexpected mutation");
    }
}
