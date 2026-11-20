using System.Text.Json;

namespace NvlogDetector.Core;

public sealed record WorkaroundEvent(DateTimeOffset Timestamp, bool Configured, string Action, string? Actor);

public sealed class StoredEpisode
{
    public DateTimeOffset Start { get; set; }

    public DateTimeOffset? End { get; set; }

    public long Bytes { get; set; }

    public long PeakBytesPerSecond { get; set; }

    public int Seconds { get; set; }

    public EpisodeSeverity Severity { get; set; } = EpisodeSeverity.Spike;

    public bool Open { get; set; }
}

public sealed class HistoryStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    readonly object _gate = new();
    readonly string _path;
    readonly List<StoredEpisode> _episodes = [];
    readonly List<WorkaroundEvent> _workaroundEvents = [];
    readonly Dictionary<long, long> _minutes = [];

    HistoryStore(string path) => _path = path;

    public long TotalBytes { get; private set; }

    public double ThresholdBytesPerSecond { get; set; } = 100 * 1024;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "nvlog.nvlgstg-detector",
        "history.json");

    public static HistoryStore Load(string? path = null)
    {
        path ??= DefaultPath;
        var store = new HistoryStore(path);
        if (!File.Exists(path))
            return store;

        try
        {
            var document = JsonSerializer.Deserialize<HistoryDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null)
                return store;

            store.TotalBytes = Math.Max(0, document.TotalBytes);
            store._workaroundEvents.AddRange(document.WorkaroundEvents.OrderBy(item => item.Timestamp));
            if (document.ThresholdBytesPerSecond > 0)
                store.ThresholdBytesPerSecond = document.ThresholdBytesPerSecond;

            var cutoff = DateTimeOffset.UtcNow.AddDays(-14);
            foreach (var minute in document.Minutes)
            {
                if (minute.Start < cutoff || minute.Bytes <= 0)
                    continue;
                var key = MinuteKey(minute.Start);
                store._minutes[key] = minute.Bytes;
            }

            foreach (var episode in document.Episodes)
                store._episodes.Add(episode);

            if (document.OpenEpisode is { } open)
            {
                open.Open = false;
                open.End ??= open.Start.AddSeconds(Math.Max(0, open.Seconds - 1));
                if (!store._episodes.Any(episode => episode.Start == open.Start && episode.Bytes == open.Bytes))
                    store._episodes.Add(open);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            try
            {
                File.Copy(path, path + ".bad", overwrite: true);
            }
            catch (IOException)
            {
                // The next save replaces the unreadable file.
            }
        }

        return store;
    }

    public StoredEpisode[] Episodes
    {
        get
        {
            lock (_gate)
                return _episodes.ToArray();
        }
    }

    public void AddSecond(DateTimeOffset timestamp, long bytes)
    {
        if (bytes <= 0)
            return;

        lock (_gate)
        {
            TotalBytes += bytes;
            var key = MinuteKey(timestamp);
            _minutes.TryGetValue(key, out var current);
            _minutes[key] = current + bytes;
        }
    }

    public WorkaroundEvent[] WorkaroundEvents
    {
        get { lock (_gate) return _workaroundEvents.ToArray(); }
    }

    // Observations give the discovery time, never the unknown time/actor of an external change.
    // Applied/Restored events are recorded only after a successful operation in this app.
    public bool RecordWorkaround(DateTimeOffset timestamp, bool configured, string action = "Detected", string? actor = null)
    {
        lock (_gate)
        {
            if (action == "Detected" && _workaroundEvents.LastOrDefault()?.Configured == configured)
                return false;
            var item = new WorkaroundEvent(timestamp, configured, action, actor);
            _workaroundEvents.Add(item);
            try { WriteUnlocked(); }
            catch { _workaroundEvents.Remove(item); throw; }
            return true;
        }
    }

    public long GetMinute(DateTimeOffset timestamp)
    {
        lock (_gate)
            return _minutes.TryGetValue(MinuteKey(timestamp), out var bytes) ? bytes : 0;
    }

    public void AddEpisode(StoredEpisode episode)
    {
        lock (_gate)
        {
            episode.Open = false;
            _episodes.Add(episode);
        }
    }

    public void SetOpenEpisode(StoredEpisode? episode)
    {
        lock (_gate)
            OpenEpisode = episode;
    }

    public StoredEpisode? OpenEpisode { get; private set; }

    public void Clear()
    {
        lock (_gate)
        {
            TotalBytes = 0;
            _episodes.Clear();
            _workaroundEvents.Clear();
            _minutes.Clear();
            OpenEpisode = null;
            WriteUnlocked();
        }
    }

    public void Save()
    {
        lock (_gate)
            WriteUnlocked();
    }

    void WriteUnlocked()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-14);
        var stale = _minutes.Where(pair => DateTimeOffset.FromUnixTimeSeconds(pair.Key) < cutoff).Select(pair => pair.Key).ToArray();
        foreach (var key in stale)
            _minutes.Remove(key);

        if (_episodes.Count > 1000)
            _episodes.RemoveRange(0, _episodes.Count - 1000);
        if (_workaroundEvents.Count > 1000)
            _workaroundEvents.RemoveRange(0, _workaroundEvents.Count - 1000);

        var document = new HistoryDocument
        {
            TotalBytes = TotalBytes,
            ThresholdBytesPerSecond = ThresholdBytesPerSecond,
            Episodes = _episodes.ToList(),
            WorkaroundEvents = _workaroundEvents.ToList(),
            Minutes = _minutes
                .OrderBy(pair => pair.Key)
                .Select(pair => new MinuteRow
                {
                    Start = DateTimeOffset.FromUnixTimeSeconds(pair.Key),
                    Bytes = pair.Value,
                })
                .ToList(),
            OpenEpisode = OpenEpisode,
        };

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(document, JsonOptions);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }

    static long MinuteKey(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        var minute = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
        return minute.ToUnixTimeSeconds();
    }

    sealed class HistoryDocument
    {
        public long TotalBytes { get; set; }

        public double ThresholdBytesPerSecond { get; set; }

        public List<StoredEpisode> Episodes { get; set; } = [];
        public List<WorkaroundEvent> WorkaroundEvents { get; set; } = [];

        public List<MinuteRow> Minutes { get; set; } = [];

        public StoredEpisode? OpenEpisode { get; set; }
    }

    sealed class MinuteRow
    {
        public DateTimeOffset Start { get; set; }

        public long Bytes { get; set; }
    }
}
