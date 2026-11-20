namespace NvlogDetector.Core;

// Keep persisted values 0/1 compatible with earlier history files.
public enum EpisodeSeverity { Spike = 0, Loop = 1, Activity = 2 }

public sealed class AnomalyEpisode
{
    public DateTimeOffset Start { get; internal set; }
    public DateTimeOffset? End { get; internal set; }
    public long Bytes { get; internal set; }
    public long PeakBytesPerSecond { get; internal set; }
    public int Seconds { get; internal set; }
    public EpisodeSeverity Severity { get; internal set; } = EpisodeSeverity.Activity;
    public bool IsOpen => End is null;
    public double AverageBytesPerSecond => Seconds <= 0 ? 0 : (double)Bytes / Seconds;
}

/// <summary>
/// Heuristic for excessive log writes, not proof of a specific driver defect.
/// Input is completed, timestamped seconds, never cumulative bytes since a UI tick.
/// </summary>
public sealed class AnomalyTracker
{
    public const long LoopBytesPerSecond = 128L * 1024;
    public const int LoopSeconds = 120;
    public const long ActivityBytesPerSecond = 32L * 1024;
    public const long SpikeBurstBytes = 2L * 1024 * 1024;
    public const int SpikeWindowSeconds = 5;
    public const int QuietHoldSeconds = 75;
    public const int ActivityQuietHoldSeconds = 5;

    readonly Queue<Sample> _window = new();
    readonly Queue<Sample> _quiet = new();
    readonly List<AnomalyEpisode> _closed = [];
    DateTimeOffset? _last;

    public AnomalyEpisode? Active { get; private set; }
    public IReadOnlyList<AnomalyEpisode> Closed => _closed;

    /// <summary>Submit each completed second once, in order. Missing seconds are quiet.</summary>
    public void AddSecond(DateTimeOffset timestamp, long bytes)
    {
        var second = DateTimeOffset.FromUnixTimeSeconds(timestamp.ToUnixTimeSeconds());
        if (_last is { } previous)
        {
            if (second <= previous)
                throw new ArgumentException("Submit each completed second once, in chronological order.", nameof(timestamp));
            var missing = (long)(second - previous).TotalSeconds - 1;
            for (var index = 1; index <= Math.Min(missing, LoopSeconds); index++)
                Observe(previous.AddSeconds(index), 0);
            if (missing >= LoopSeconds)
                _window.Clear();
        }
        Observe(second, Math.Max(0, bytes));
    }

    void Observe(DateTimeOffset second, long bytes)
    {
        _last = second;
        _window.Enqueue(new Sample(second, bytes));
        while (_window.Count > LoopSeconds)
            _window.Dequeue();

        if (Active is null)
        {
            if (bytes > 0)
            {
                Active = new AnomalyEpisode
                {
                    Start = second,
                    Bytes = bytes,
                    PeakBytesPerSecond = bytes,
                    Seconds = 1,
                };
                _quiet.Clear();
            }
        }
        else
        {
            Active.Bytes += bytes;
            Active.Seconds++;
            Active.PeakBytesPerSecond = Math.Max(Active.PeakBytesPerSecond, bytes);
        }

        if (Active is null) return;
        var recent = _window.TakeLast(SpikeWindowSeconds).ToArray();
        if (Active.Severity == EpisodeSeverity.Activity &&
            (bytes >= SpikeBurstBytes || (recent.Length == SpikeWindowSeconds &&
             recent.Sum(sample => sample.Bytes) >= LoopBytesPerSecond * SpikeWindowSeconds)))
            Active.Severity = EpisodeSeverity.Spike;

        // Every positive byte belongs to the journal, including low-rate tails of an alert.
        if (bytes == 0)
            _quiet.Enqueue(new Sample(second, bytes));
        else
            _quiet.Clear();

        var quietHold = Active.Severity == EpisodeSeverity.Activity ? ActivityQuietHoldSeconds : QuietHoldSeconds;
        if (_quiet.Count >= quietHold)
        {
            foreach (var sample in _quiet)
            {
                Active.Bytes -= sample.Bytes;
                Active.Seconds--;
            }
            Active.End = _quiet.Peek().Time.AddSeconds(-1);
            _closed.Add(Active);
            Active = null;
            _quiet.Clear();
            return;
        }

        if (_window.Count != LoopSeconds || _window.Sum(sample => sample.Bytes) < LoopBytesPerSecond * LoopSeconds)
            return;
        var activity = _window.Where(sample => sample.Bytes >= ActivityBytesPerSecond).ToArray();
        // A single large write followed by silence must never become a loop.
        if (activity.Length >= 3 &&
            (activity[^1].Time - activity[0].Time).TotalSeconds >= 60 &&
            activity.Select(sample => sample.Time.ToUnixTimeSeconds() / 10).Distinct().Count() >= 3)
            Active.Severity = EpisodeSeverity.Loop;
    }

    readonly record struct Sample(DateTimeOffset Time, long Bytes);
}
