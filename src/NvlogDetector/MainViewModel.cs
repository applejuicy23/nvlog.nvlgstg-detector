using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using NvlogDetector.Core;

namespace NvlogDetector;

/// <summary>
/// Live view of writes to nvlog.nvlgstg. Other NVIDIA processes are not the signal.
/// </summary>
public sealed class MainViewModel : Observable, IDisposable
{
    readonly SingleInstance _instance;
    readonly HistoryStore _history;
    readonly FileGrowthBook _growth = new();
    readonly ProcessWriteMeter _processMeter = new();
    readonly Queue<WriteSecond> _rate = new();
    readonly Queue<WriteSecond> _minute = new();
    readonly bool _admin;
    readonly string? _windowsVersion;
    readonly string? _marketingVersion;
    readonly NvtoppsWorkaround _workaround;
    WorkaroundState _workaroundState = new(false, false, "Checking workaround availability...");
    bool _workaroundBusy;
    int _workaroundRefresh;
    string _workaroundResult = "";
    DateTimeOffset? _workaroundChecked;
    string _workaroundHistoryError = "";

    AnomalyTracker _tracker;
    EtwFileWriteMonitor? _monitor;
    DispatcherTimer? _timer;
    string[] _knownPaths;
    List<WatchedFile> _latestFiles = [];
    int[] _pids = [];
    long _sessionBytes;
    long _peakSecond;
    long _minuteSum;
    int _savedClosed;
    int _scanCooldown;
    int _pidRefresh;
    int _checkpointTick;
    bool _scanning;
    bool _disposed;
    DateTimeOffset _sessionStarted;

    public MainViewModel(SingleInstance instance, string? historyPath = null, INvtoppsPlatform? workaroundPlatform = null)
    {
        _instance = instance;
        _admin = IsAdministrator();
        _history = HistoryStore.Load(historyPath);
        _windowsVersion = NvidiaInfo.WindowsDriverVersion();
        _marketingVersion = NvidiaVersion.FromWindowsDriver(_windowsVersion);
        _workaround = new NvtoppsWorkaround(workaroundPlatform ?? new WindowsNvtoppsPlatform());
        _tracker = new AnomalyTracker();
        _knownPaths = File.Exists(NvlogPath.DefaultPath) ? [NvlogPath.DefaultPath] : [];
        _latestFiles = ReadFiles();
        _growth.Observe(_latestFiles.Select(file => new KeyValuePair<string, long>(file.Path, file.Length)));

        ElevateCommand = new RelayCommand(Elevate);
        ExportCommand = new RelayCommand(Export);
        ClearCommand = new RelayCommand(Clear);
        ApplyWorkaroundCommand = new RelayCommand(() => _ = ChangeWorkaroundAsync(restore: false));
        RestoreWorkaroundCommand = new RelayCommand(() => _ = ChangeWorkaroundAsync(restore: true));
        RefreshWorkaround();
        EpisodesView = CollectionViewSource.GetDefaultView(Episodes);
        EpisodesView.Filter = MatchesActivityFilter;
        ApplyEpisodeSort();
        UpdatePresentation(DateTimeOffset.UtcNow);
    }

    public ObservableCollection<EpisodeRow> Episodes { get; } = [];

    public ICollectionView EpisodesView { get; }
    bool _showBlue = true;
    bool _showYellow = true;
    bool _showRed = true;
    int _selectedOrder;

    public bool ShowBlue { get => _showBlue; set { if (Set(ref _showBlue, value)) RefreshActivityView(); } }
    public bool ShowYellow { get => _showYellow; set { if (Set(ref _showYellow, value)) RefreshActivityView(); } }
    public bool ShowRed { get => _showRed; set { if (Set(ref _showRed, value)) RefreshActivityView(); } }
    public int SelectedOrder { get => _selectedOrder; set { if (Set(ref _selectedOrder, value)) ApplyEpisodeSort(); } }
    public string ActivityFilterSummary { get; private set; } = "";
    public string EmptyLogText => Episodes.Count == 0 ? "No writes recorded yet. Even small, brief writes will appear here in blue." : "No activities match the selected colors.";

    bool MatchesActivityFilter(object item) => item is EpisodeRow row && (row.Severity switch
    {
        EpisodeSeverity.Activity => ShowBlue,
        EpisodeSeverity.Spike => ShowYellow,
        EpisodeSeverity.Loop => ShowRed,
        _ => false,
    });

    void ApplyEpisodeSort()
    {
        using (EpisodesView.DeferRefresh())
        {
            EpisodesView.SortDescriptions.Clear();
            if (SelectedOrder is 1 or 2)
                EpisodesView.SortDescriptions.Add(new SortDescription(nameof(EpisodeRow.SeverityRank),
                    SelectedOrder == 1 ? ListSortDirection.Ascending : ListSortDirection.Descending));
            else if (SelectedOrder == 3)
                EpisodesView.SortDescriptions.Add(new SortDescription(nameof(EpisodeRow.Bytes), ListSortDirection.Descending));
            EpisodesView.SortDescriptions.Add(new SortDescription(nameof(EpisodeRow.Start), ListSortDirection.Descending));
        }
        RefreshActivityView();
    }

    void RefreshActivityView()
    {
        // Include promotions of an open interval while a color filter is active.
        EpisodesView.Refresh();
        Set(ref HasEpisodesField, !EpisodesView.IsEmpty, nameof(HasEpisodes));
        ActivityFilterSummary = $"Blue {Episodes.Count(row => row.Severity == EpisodeSeverity.Activity)}  ·  Yellow {Episodes.Count(row => row.Severity == EpisodeSeverity.Spike)}  ·  Red {Episodes.Count(row => row.Severity == EpisodeSeverity.Loop)}  ·  showing {EpisodesView.Cast<EpisodeRow>().Count()} of {Episodes.Count}";
        Raise(nameof(ActivityFilterSummary));
        Raise(nameof(EmptyLogText));
    }

    public RelayCommand ElevateCommand { get; }

    public RelayCommand ExportCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand ApplyWorkaroundCommand { get; }
    public RelayCommand RestoreWorkaroundCommand { get; }
    public bool CanApplyWorkaround => _admin && !_workaroundBusy && _workaroundState.CanApply;
    public bool CanRestoreWorkaround => _admin && !_workaroundBusy && _workaroundState.CanRestore;
    public bool IsWorkaroundBusy => _workaroundBusy;
    public string WorkaroundStatusText => _workaroundBusy ? "Updating NVIDIA settings; please wait..." :
        (_admin ? _workaroundState.Message : "Administrator rights are required. " + _workaroundState.Message);
    public string WorkaroundResultText => _workaroundResult;
    public bool WorkaroundConfigured => _workaroundState.Configured == true;
    public bool WorkaroundConfigurationKnown => _workaroundState.Configured.HasValue;
    public string WorkaroundActivityText => "Community workaround: " + (_workaroundState.Configured switch
    {
        true => "ON (registry)", false => "OFF (registry)", _ => "UNKNOWN",
    }) + (_workaroundChecked is { } time ? " · checked " + ByteFormat.Time(time) : "");
    public string WorkaroundActivityHint => (_workaroundState.Configured switch
    {
        true => "DisableLoad=1 persists across restarts. Registry state does not verify whether the plugin is currently loaded or the driver is fixed.",
        false => "DisableLoad=1 is not configured. This does not prove that a previously disabled plugin has already reloaded.",
        _ => "The current setting could not be verified. Do not interpret this as OFF.",
    }) + _workaroundHistoryError;
    public string WorkaroundHistoryHeader => $"Workaround history ({WorkaroundHistory.Count})";
    public ObservableCollection<string> WorkaroundHistory { get; } = [];

    void UpdateWorkaroundHistory()
    {
        WorkaroundHistory.Clear();
        foreach (var item in _history.WorkaroundEvents.Reverse())
        {
            var action = item.Action == "Detected"
                ? "Detected " + (item.Configured ? "ON" : "OFF") + " · actual change time and actor unknown"
                : item.Action + " by " + item.Actor + " · " + (item.Configured ? "ON" : "OFF");
            WorkaroundHistory.Add(ByteFormat.Time(item.Timestamp) + " · " + action);
        }
        Raise(nameof(WorkaroundHistoryHeader));
    }

    void RecordWorkaround(bool configured, string action = "Detected", string? actor = null)
    {
        try
        {
            var added = _history.RecordWorkaround(DateTimeOffset.UtcNow, configured, action, actor);
            _workaroundHistoryError = "";
            if (added || WorkaroundHistory.Count == 0) UpdateWorkaroundHistory();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _workaroundHistoryError = " History could not be saved: " + error.Message;
            UpdateWorkaroundHistory();
        }
    }

    void RefreshWorkaround()
    {
        try
        {
            _workaroundState = _workaround.Inspect();
            _workaroundChecked = DateTimeOffset.UtcNow;
            if (_workaroundState.Configured is { } configured) RecordWorkaround(configured);
            else UpdateWorkaroundHistory();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _workaroundState = new(false, false, "Workaround status could not be read: " + error.Message);
            _workaroundChecked = null;
            UpdateWorkaroundHistory();
        }
        NotifyWorkaround();
    }

    void NotifyWorkaround()
    {
        Raise(nameof(CanApplyWorkaround));
        Raise(nameof(CanRestoreWorkaround));
        Raise(nameof(IsWorkaroundBusy));
        Raise(nameof(WorkaroundStatusText));
        Raise(nameof(WorkaroundResultText));
        Raise(nameof(WorkaroundConfigured));
        Raise(nameof(WorkaroundConfigurationKnown));
        Raise(nameof(WorkaroundActivityText));
        Raise(nameof(WorkaroundActivityHint));
    }

    async Task ChangeWorkaroundAsync(bool restore)
    {
        if (_workaroundBusy || !_admin || (restore ? !CanRestoreWorkaround : !CanApplyWorkaround)) return;
        var description = restore
            ? "Restore this app's saved original DisableLoad setting and reload NVIDIA Display Container? The service may be started if it was running before the workaround."
            : "Apply an unofficial community workaround by setting DisableLoad (DWORD) to 1 for the _nvtopps plugin? The original value is saved first. NVIDIA Display Container will be restarted; its apps may briefly disconnect and the display may flicker. Other plugin functions may be affected.";
        description += "\n\nHKLM\\" + WindowsNvtoppsPlatform.PluginPath + "\\DisableLoad";
        description += "\n\nClose any driver installer before continuing. No driver downgrade or log-file deletion is performed.";
        if (MessageBox.Show(description, restore ? "Restore original NVIDIA setting" : "Apply community workaround",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        _workaroundBusy = true;
        _workaroundResult = "";
        NotifyWorkaround();
        try
        {
            await Task.Run(() => WindowsNvtoppsPlatform.RunExclusive(() =>
            {
                if (restore) _workaround.Restore();
                else _workaround.Apply(_marketingVersion ?? _windowsVersion);
            }));
            RecordWorkaround(!restore, restore ? "Restored" : "Applied", Environment.UserName);
            _workaroundResult = restore
                ? "Original setting restored and service reloaded. Observe new writes; previous history is retained."
                : "Workaround applied and original setting saved. Observe new writes; a quiet log now reflects the disabled plugin, not proof of a fixed driver. Recheck settings after driver updates.";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _workaroundResult = error.Message;
            MessageBox.Show(error.Message, "Workaround could not complete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _workaroundBusy = false;
            RefreshWorkaround();
        }
    }

    public string StatusText => StatusField;

    public string State => StateField;

    public bool BannerOpen => BannerOpenField;

    public string BannerTitle => BannerTitleField;

    public string BannerMessage => BannerMessageField;

    public Wpf.Ui.Controls.InfoBarSeverity BannerSeverity => BannerSeverityField;

    public bool ShowElevate => ShowElevateField;

    public string RateText => RateTextField;

    public string RateHint => RateHintField;

    public string SessionText => SessionTextField;

    public string SessionHint => SessionHintField;

    public string LifetimeText => LifetimeTextField;

    public string LifetimeHint { get; } = "across launches";

    public string FileSizeText => FileSizeTextField;

    public string FileHint => FileHintField;

    public string ChartCaption => ChartCaptionField;

    public IReadOnlyList<ChartBar> ChartBars { get; private set; } = [];

    public string TodayText => TodayTextField;

    public bool HasEpisodes => HasEpisodesField;

    public string FilePathText => FilePathTextField;

    public string ProcessText => ProcessTextField;

    public string VersionNote => VersionNoteField;

    public bool HasVersionNote => HasVersionNoteField;

    public string Footnote { get; } =
        "Counts every observed write to nvlog.nvlgstg under NVIDIA's ProgramData folders. Blue: small or brief activity. Yellow: elevated writes under observation. Red: at least 128 KiB/s on average over 2 minutes, with repeated activity spread across at least a minute. Pauses between rewrites are allowed. This is a heuristic, not proof of a driver defect or SSD wear. Counts successful file writes, excluding failed retries; SSD writes may differ due to caching. Display trails events by about 3 seconds.";

    public string DiagnosticsText => DiagnosticsTextField;

    public bool IsLoop => IsLoopField;

    public bool IsSpike => IsSpikeField;

    public void Start()
    {
        if (_timer != null || _disposed)
            return;

        _sessionStarted = DateTimeOffset.UtcNow;
        if (_admin)
        {
            try
            {
                _monitor = new EtwFileWriteMonitor();
                _monitor.Start();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                Set(ref _startError, ex.Message);
            }
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    string? _startError;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer?.Stop();
        _monitor?.Dispose();
        try
        {
            if (_monitor != null)
                foreach (var sample in _monitor.TakeSeconds(DateTimeOffset.UtcNow.AddSeconds(1)))
                {
                    _history.AddSecond(sample.Timestamp, sample.Bytes);
                    _tracker.AddSecond(sample.Timestamp, sample.Bytes);
                }
            CollectClosedEpisodes();
            _history.SetOpenEpisode(_tracker.Active is { } active ? Copy(active) : null);
            _history.Save();
        }
        catch (IOException)
        {
            // The window is closing; a missed checkpoint is kept in the previous save.
        }

        _monitor = null;

        _instance.Dispose();
    }

    void Tick()
    {
        if (_disposed)
            return;

        var now = DateTimeOffset.UtcNow;
        if (!_workaroundBusy && _workaroundRefresh-- <= 0)
        {
            _workaroundRefresh = 5;
            RefreshWorkaround();
        }
        ScheduleScan();
        _latestFiles = ReadFiles();
        _growth.Observe(_latestFiles.Select(file => new KeyValuePair<string, long>(file.Path, file.Length)));

        if (_monitor != null)
        {
            if (_pidRefresh-- <= 0)
            {
                _pidRefresh = 2;
                _pids = NvidiaInfo.ContainerPids();
                _monitor.SetPids(_pids);
            }

            _processMeter.Sample(_pids);
            // Use ETW event timestamps, not the amount accumulated since the previous UI tick.
            foreach (var sample in _monitor.TakeSeconds(now.AddSeconds(-3)))
            {
                PushRate(sample);
                _history.AddSecond(sample.Timestamp, sample.Bytes);
                _tracker.AddSecond(sample.Timestamp, sample.Bytes);
            }
        }

        CollectClosedEpisodes();
        Checkpoint();
        UpdatePresentation(now);
    }

    void ScheduleScan()
    {
        if (_scanning)
            return;
        if (_scanCooldown-- > 0)
            return;

        _scanCooldown = 20;
        _scanning = true;
        Task.Run(() =>
        {
            string[] found;
            try
            {
                found = NvlogFiles.Discover();
            }
            catch (IOException)
            {
                found = [];
            }

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (found.Length > 0)
                    _knownPaths = found;
                _scanning = false;
            });
        });
    }

    List<WatchedFile> ReadFiles()
    {
        var files = new List<WatchedFile>();
        foreach (var path in _knownPaths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    continue;
                files.Add(new WatchedFile(info.FullName, info.Length, new DateTimeOffset(info.LastWriteTimeUtc)));
            }
            catch (IOException)
            {
                // The directory can disappear between the scan and the read.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip a file we cannot stat.
            }
        }

        return files;
    }

    void PushRate(WriteSecond sample)
    {
        var bytes = sample.Bytes;
        _sessionBytes += bytes;
        if (bytes > _peakSecond)
            _peakSecond = bytes;

        _rate.Enqueue(sample);
        while (_rate.Count > 0 && _rate.Peek().Timestamp <= sample.Timestamp.AddSeconds(-3))
            _rate.Dequeue();

        _minute.Enqueue(sample);
        _minuteSum += bytes;
        while (_minute.Count > 0 && _minute.Peek().Timestamp <= sample.Timestamp.AddSeconds(-60))
            _minuteSum -= _minute.Dequeue().Bytes;
    }

    void CollectClosedEpisodes()
    {
        var closed = _tracker.Closed;
        if (closed.Count == _savedClosed)
            return;

        for (var index = _savedClosed; index < closed.Count; index++)
            _history.AddEpisode(Copy(closed[index]));
        _savedClosed = closed.Count;
        _history.SetOpenEpisode(null);
        _history.Save();
    }

    void Checkpoint()
    {
        _checkpointTick++;
        if (_tracker.Active is { } active)
            _history.SetOpenEpisode(Copy(active));
        else if (_checkpointTick % 10 == 0)
            _history.SetOpenEpisode(null);

        if (_checkpointTick % 10 == 0)
            _history.Save();
    }

    void UpdatePresentation(DateTimeOffset now)
    {
        var active = _tracker.Active;
        var status = _monitor?.Status();
        var touchedRecently = _latestFiles.Any(file => now - file.LastWrite < TimeSpan.FromSeconds(10));
        var measurementProblem = MeasurementHealth.Problem(
            status?.Running == true, _pids.Length > 0, _latestFiles.Count > 0,
            status?.AttributionVerified == true, status?.LostEvents ?? 0, status?.LateEvents ?? 0,
            status?.HandleError, status?.IncompleteWrites ?? 0);

        var looping = active?.Severity == EpisodeSeverity.Loop;
        var state = "Ok";
        var statusText = _minuteSum > 0 ? "Writes observed, below alert criteria" : "No writes observed in the last minute";
        if (!_admin)
        {
            state = "Warning";
            statusText = "Administrator rights required";
        }
        else if (_startError != null || status?.Error != null)
        {
            state = "Warning";
            statusText = "Watching did not start";
        }
        else if (looping)
        {
            state = "Loop";
            statusText = "Sustained log writes";
        }
        else if (active?.Severity == EpisodeSeverity.Spike)
        {
            state = "Spike";
            statusText = "Elevated writes, observing pattern";
        }
        else if (active != null && measurementProblem == null)
        {
            state = "Activity";
            statusText = "Log activity recorded";
        }
        else if (_monitor != null && _pids.Length == 0)
        {
            state = "Idle";
            statusText = "Waiting for NVIDIA Display Container";
        }
        else if (_latestFiles.Count == 0)
        {
            state = "Idle";
            statusText = "File not found, still watching";
        }
        else if (measurementProblem != null)
        {
            state = "Warning";
            statusText = "Measurement incomplete";
        }

        if (active != null && measurementProblem != null)
            statusText += " (measurement incomplete)";

        Set(ref StateField, state, nameof(State));
        Set(ref StatusField, statusText, nameof(StatusText));

        var showBanner = !_admin || active?.Severity is EpisodeSeverity.Spike or EpisodeSeverity.Loop || _startError != null || status?.Error != null || measurementProblem != null;
        Set(ref BannerOpenField, showBanner, nameof(BannerOpen));
        Set(ref ShowElevateField, !_admin, nameof(ShowElevate));
        if (!_admin)
        {
            Set(ref BannerSeverityField, Wpf.Ui.Controls.InfoBarSeverity.Warning, nameof(BannerSeverity));
            Set(ref BannerTitleField, "Administrator rights are required to count the bytes", nameof(BannerTitle));
            var message = "This log is rewritten in place, so its size barely moves. Counting the writes needs an elevated process.";
            if (touchedRecently)
                message += " The file was just touched.";
            Set(ref BannerMessageField, message, nameof(BannerMessage));
        }
        else if (_startError != null || status?.Error != null)
        {
            Set(ref BannerSeverityField, Wpf.Ui.Controls.InfoBarSeverity.Error, nameof(BannerSeverity));
            Set(ref BannerTitleField, "Watching did not start", nameof(BannerTitle));
            Set(ref BannerMessageField, _startError ?? status?.Error ?? "", nameof(BannerMessage));
        }
        else if (active?.Severity is EpisodeSeverity.Spike or EpisodeSeverity.Loop)
        {
            var live = $"Since {ByteFormat.Time(active.Start)}. {ByteFormat.Bytes(active.Bytes)} in {ByteFormat.Duration(now - active.Start)}. Peak {ByteFormat.PerSecond(active.PeakBytesPerSecond)}.";
            if (looping)
            {
                Set(ref BannerSeverityField, Wpf.Ui.Controls.InfoBarSeverity.Error, nameof(BannerSeverity));
                Set(ref BannerTitleField, "Sustained writes to nvlog.nvlgstg", nameof(BannerTitle));
            }
            else
            {
                Set(ref BannerSeverityField, Wpf.Ui.Controls.InfoBarSeverity.Warning, nameof(BannerSeverity));
                Set(ref BannerTitleField, "Elevated writes, observing pattern", nameof(BannerTitle));
                live += " Repeated writes are evaluated over a two-minute window, including pauses.";
            }

            if (measurementProblem != null) live += " Measurement incomplete: " + measurementProblem;

            Set(ref BannerMessageField, live, nameof(BannerMessage));
        }
        else if (measurementProblem != null)
        {
            Set(ref BannerSeverityField, Wpf.Ui.Controls.InfoBarSeverity.Warning, nameof(BannerSeverity));
            Set(ref BannerTitleField, "Measurement incomplete", nameof(BannerTitle));
            Set(ref BannerMessageField, measurementProblem, nameof(BannerMessage));
        }

        var rate = _rate.Sum(sample => sample.Bytes) / 3.0;
        Set(ref RateTextField, ByteFormat.PerSecond(rate), nameof(RateText));
        Set(ref RateHintField, $"this minute {ByteFormat.Bytes(_minuteSum)}  ·  peak {ByteFormat.PerSecond(_peakSecond)}", nameof(RateHint));
        Set(ref SessionTextField, ByteFormat.Bytes(_sessionBytes), nameof(SessionText));
        var elapsed = _sessionStarted == default ? TimeSpan.Zero : now - _sessionStarted;
        Set(ref SessionHintField, "running " + ByteFormat.Duration(elapsed), nameof(SessionHint));
        Set(ref LifetimeTextField, ByteFormat.Bytes(_history.TotalBytes), nameof(LifetimeText));

        var size = _latestFiles.Sum(file => file.Length);
        Set(ref FileSizeTextField, _latestFiles.Count == 0 ? "no file" : ByteFormat.Bytes(size), nameof(FileSizeText));
        Set(ref FileHintField, DescribeFile(now), nameof(FileHint));

        var bars = BuildChart(now, out var peakMinute);
        ChartBars = bars;
        Raise(nameof(ChartBars));
        Set(ref ChartCaptionField,
            peakMinute <= 0 ? "Nothing recorded in the last hour" : "Peak this hour: " + ByteFormat.Bytes(peakMinute) + " in one minute",
            nameof(ChartCaption));

        SyncEpisodes(now);
        RefreshActivityView();
        Set(ref TodayTextField, TodaySummary(now), nameof(TodayText));
        Set(ref IsLoopField, looping, nameof(IsLoop));
        Set(ref IsSpikeField, active?.Severity == EpisodeSeverity.Spike, nameof(IsSpike));

        Set(ref FilePathTextField,
            _latestFiles.Count == 0
                ? @"Looking for nvlog.nvlgstg under C:\ProgramData\NVIDIA Corporation"
                : string.Join(Environment.NewLine, _latestFiles.Select(file => file.Path)),
            nameof(FilePathText));

        var version = _marketingVersion is null
            ? _windowsVersion ?? "version unknown"
            : $"{_marketingVersion}   ·   Windows {_windowsVersion}";
        var process = _pids.Length == 0
            ? "NVIDIA Display Container is not running"
            : "Watching this file only";
        Set(ref ProcessTextField, $"{process}  ·  driver {version}", nameof(ProcessText));

        var note = DescribeVersion();
        Set(ref VersionNoteField, note, nameof(VersionNote));
        Set(ref HasVersionNoteField, note.Length > 0, nameof(HasVersionNote));
        Set(ref DiagnosticsTextField, Diagnostics(status), nameof(DiagnosticsText));
    }

    string DescribeFile(DateTimeOffset now)
    {
        if (_latestFiles.Count == 0)
            return "size shows up here once the file exists";

        var growth = _growth.TotalGrowth;
        var rewriting = _sessionBytes > 1024 * 1024 && Math.Abs(growth) <= _sessionBytes / 10;
        var growthText = rewriting
            ? "size is flat, the file is being rewritten"
            : growth switch
            {
                > 0 => "grew " + ByteFormat.Bytes(growth),
                < 0 => "shrank " + ByteFormat.Bytes(-growth),
                _ => "size unchanged",
            };

        var latest = _latestFiles.Max(file => file.LastWrite);
        var age = now - latest;
        var when = age < TimeSpan.FromSeconds(5)
            ? "changed just now"
            : "changed " + ByteFormat.Time(latest);
        return growthText + "  ·  " + when;
    }

    string DescribeVersion() => _marketingVersion switch
    {
        "621.14" or "621.24" => "Drivers 621.14 and 621.24 are the ones people saw rewriting this log while the PC sat idle, often a few MB/s and sometimes tens of GB an hour. In those reports, 621.01 was quiet.",
        "621.01" => "Some reports found 621.01 quiet. A driver version alone cannot confirm or rule out excessive log writes.",
        _ => "",
    };

    string Diagnostics(MonitorStatus? status)
    {
        if (status is null)
            return _admin
                ? "Watching has not started."
                : "Without administrator rights the write events are hidden. File size and last-changed time still show.";

        return string.Join(Environment.NewLine,
        [
            "rights: administrator",
            "debug privilege: " + (WindowsPrivilege.EnableDebug() ? "yes" : "no"),
            "container pids: " + (status.Pids.Length == 0 ? "none" : string.Join(", ", status.Pids)),
            $"write events seen: {status.WriteEvents}, matched this file: {status.MatchedEvents}",
            $"completed writes: {status.CompletedWrites}, unresolved completions: {status.IncompleteWrites}",
            "file objects: " + status.KnownObjects,
            "file attribution verified: " + status.AttributionVerified,
            "events lost: " + status.LostEvents,
            "late events (timing uncertain): " + status.LateEvents,
            "path from handle: " + (status.ResolvedPath ?? "not yet"),
            "handle error: " + (status.HandleError ?? "none"),
            "write fields: " + (status.Payload.Length == 0 ? "no write event yet" : status.Payload),
            "events seen: " + (status.SeenEvents.Length == 0 ? "none yet" : status.SeenEvents),
            "all container I/O writes (includes this file): " + ByteFormat.Bytes(_processMeter.TotalBytes),
            "error: " + (status.Error ?? _startError ?? "none"),
        ]);
    }

    ChartBar[] BuildChart(DateTimeOffset now, out long peak)
    {
        var end = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        var values = new long[60];
        peak = 0;
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = _history.GetMinute(end.AddMinutes(index - 59));
            if (values[index] > peak)
                peak = values[index];
        }

        var scale = Math.Max(1, peak);
        var episodes = _history.Episodes;
        var active = _tracker.Active is { } current ? Copy(current) : null;
        var bars = new ChartBar[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var minute = end.AddMinutes(index - 59);
            bars[index] = new ChartBar(values[index] / (double)scale, values[index] > 0 ? LevelForMinute(minute, episodes, active) : null);
        }

        return bars;
    }

    void SyncEpisodes(DateTimeOffset now)
    {
        var desired = new List<StoredEpisode>();
        var open = false;
        if (_tracker.Active is { } active)
        {
            desired.Add(Copy(active));
            open = true;
        }

        var history = _history.Episodes;
        for (var index = history.Length - 1; index >= 0; index--)
            desired.Add(history[index]);

        var sameShape = desired.Count == Episodes.Count;
        if (sameShape)
        {
            for (var index = 0; index < desired.Count; index++)
            {
                if (Episodes[index].Start != desired[index].Start)
                {
                    sameShape = false;
                    break;
                }
            }
        }

        if (sameShape)
        {
            if (Episodes.Count > 0)
                Episodes[0].Refresh(desired[0], open, now);
            return;
        }

        Episodes.Clear();
        for (var index = 0; index < desired.Count; index++)
            Episodes.Add(EpisodeRow.Create(desired[index], open && index == 0, now));
    }

    string TodaySummary(DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        long bytes = 0;
        var count = 0;
        foreach (var episode in EnumerateStored(now))
        {
            var start = episode.Start.ToLocalTime().Date;
            var end = (episode.End ?? now).ToLocalTime().Date;
            if (start != today && end != today)
                continue;
            bytes += episode.Bytes;
            count++;
        }

        return count == 0
            ? "No intervals today"
            : $"Today: {count} {(count == 1 ? "interval" : "intervals")}  ·  {ByteFormat.Bytes(bytes)}";
    }

    IEnumerable<StoredEpisode> EnumerateStored(DateTimeOffset now)
    {
        if (_tracker.Active is { } active)
            yield return Copy(active);
        foreach (var episode in _history.Episodes)
            yield return episode;
    }

    void Elevate()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "Open NvlogDetector.exe and accept the administrator prompt.",
                "nvlog.nvlgstg",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception)
        {
            return;
        }

        _disposed = true;
        _timer?.Stop();
        _monitor?.Dispose();
        _monitor = null;
        try
        {
            _history.Save();
        }
        catch (IOException)
        {
            // Elevation still proceeds.
        }

        _instance.Dispose();
        Application.Current.Shutdown();
    }

    void Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"nvlog-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog() != true)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("kind;start;end;seconds;bytes;peak_bytes_per_s;average_bytes_per_s");
        foreach (var episode in EpisodesView.Cast<EpisodeRow>())
        {
            var seconds = Math.Max(1, episode.Seconds);
            builder.Append(episode.Severity switch { EpisodeSeverity.Loop => "loop", EpisodeSeverity.Spike => "spike", _ => "activity" }).Append(';');
            builder.Append(ByteFormat.Time(episode.Start)).Append(';');
            builder.Append(episode.End is null ? "now" : ByteFormat.Time(episode.End.Value)).Append(';');
            builder.Append(seconds).Append(';');
            builder.Append(episode.Bytes).Append(';');
            builder.Append(episode.PeakBytesPerSecond).Append(';');
            builder.Append((long)Math.Round(episode.Bytes / (double)seconds));
            builder.AppendLine();
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    void Clear()
    {
        if (MessageBox.Show(
                "Clear the interval log and the all-time counter? This session starts over too.",
                "nvlog.nvlgstg",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _history.Clear();
        RefreshWorkaround();
        _tracker = new AnomalyTracker();
        _savedClosed = 0;
        _sessionBytes = 0;
        _peakSecond = 0;
        _minuteSum = 0;
        _rate.Clear();
        _minute.Clear();
        _growth.Reset();
        _processMeter.Reset();
        _sessionStarted = DateTimeOffset.UtcNow;
        _monitor?.ClearPending(_sessionStarted);
        _latestFiles = ReadFiles();
        _growth.Observe(_latestFiles.Select(file => new KeyValuePair<string, long>(file.Path, file.Length)));
        Episodes.Clear();
        UpdatePresentation(DateTimeOffset.UtcNow);
    }

    static StoredEpisode Copy(AnomalyEpisode episode) => new()
    {
        Start = episode.Start,
        End = episode.End,
        Bytes = episode.Bytes,
        PeakBytesPerSecond = episode.PeakBytesPerSecond,
        Seconds = episode.Seconds,
        Severity = episode.Severity,
    };

    static EpisodeSeverity? LevelForMinute(DateTimeOffset minute, IEnumerable<StoredEpisode> episodes, StoredEpisode? active)
    {
        EpisodeSeverity? level = null;
        void Consider(StoredEpisode episode)
        {
            var end = minute.AddMinutes(1);
            var episodeEnd = episode.End?.AddSeconds(1) ?? DateTimeOffset.UtcNow;
            if (episode.Start >= end || episodeEnd <= minute)
                return;
            var rank = episode.Severity switch { EpisodeSeverity.Loop => 2, EpisodeSeverity.Spike => 1, _ => 0 };
            var currentRank = level switch { EpisodeSeverity.Loop => 2, EpisodeSeverity.Spike => 1, EpisodeSeverity.Activity => 0, _ => -1 };
            if (rank > currentRank) level = episode.Severity;
        }

        if (active != null)
            Consider(active);
        foreach (var episode in episodes)
            Consider(episode);
        return level;
    }

    static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    string StateField = "Idle";
    string StatusField = "";
    bool BannerOpenField;
    string BannerTitleField = "";
    string BannerMessageField = "";
    Wpf.Ui.Controls.InfoBarSeverity BannerSeverityField;
    bool ShowElevateField;
    string RateTextField = "";
    string RateHintField = "";
    string SessionTextField = "";
    string SessionHintField = "";
    string LifetimeTextField = "";
    string FileSizeTextField = "";
    string FileHintField = "";
    string ChartCaptionField = "";
    string TodayTextField = "";
    bool HasEpisodesField;
    string FilePathTextField = "";
    string ProcessTextField = "";
    string VersionNoteField = "";
    bool HasVersionNoteField;
    string DiagnosticsTextField = "";
    bool IsLoopField;
    bool IsSpikeField;

    sealed record WatchedFile(string Path, long Length, DateTimeOffset LastWrite);
}
