using NvlogDetector.Core;
using Xunit;

namespace NvlogDetector.Tests;

public class AnomalyTrackerTests
{
    static readonly DateTimeOffset Start = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    const long MiB = 1024L * 1024;

    [Fact]
    public void Records_low_rate_writes_in_blue_without_an_alert()
    {
        var tracker = Feed(600, _ => 14 * 1024);
        Assert.Equal(EpisodeSeverity.Activity, tracker.Active!.Severity);
        Assert.Equal(600 * 14 * 1024, tracker.Active.Bytes);
        Assert.Empty(tracker.Closed);
    }

    [Fact]
    public void Records_a_single_byte_and_closes_at_the_last_write()
    {
        var tracker = Feed(6, second => second == 0 ? 1 : 0);
        Assert.Null(tracker.Active);
        var episode = Assert.Single(tracker.Closed);
        Assert.Equal(EpisodeSeverity.Activity, episode.Severity);
        Assert.Equal(1, episode.Bytes);
        Assert.Equal(1, episode.Seconds);
        Assert.Equal(Start, episode.End);
    }

    [Fact]
    public void Nearby_small_writes_share_a_blue_interval_and_separated_writes_do_not()
    {
        var tracker = Feed(20, second => second is 0 or 2 or 10 ? 1024 : 0);
        Assert.Null(tracker.Active);
        Assert.Equal(2, tracker.Closed.Count);
        Assert.All(tracker.Closed, episode => Assert.Equal(EpisodeSeverity.Activity, episode.Severity));
        Assert.Equal(2048, tracker.Closed[0].Bytes);
        Assert.Equal(3, tracker.Closed[0].Seconds);
        Assert.Equal(1024, tracker.Closed[1].Bytes);
    }

    [Fact]
    public void Blue_promotes_to_yellow_then_red_without_losing_or_duplicating_bytes()
    {
        var tracker = Feed(4, _ => 833333);
        Assert.Equal(EpisodeSeverity.Activity, tracker.Active!.Severity);
        tracker.AddSecond(Start.AddSeconds(4), 833333);
        Assert.Equal(EpisodeSeverity.Spike, tracker.Active.Severity);
        for (var second = 5; second < 120; second++)
            tracker.AddSecond(Start.AddSeconds(second), 833333);
        Assert.Equal(EpisodeSeverity.Loop, tracker.Active.Severity);
        Assert.Equal(120 * 833333L, tracker.Active.Bytes);
        Assert.Empty(tracker.Closed);
    }

    [Fact]
    public void Low_rate_tail_of_an_alert_is_retained()
    {
        var tracker = Feed(180, second => second == 0 ? 3 * MiB : 7);
        Assert.Equal(3 * MiB + 179 * 7, tracker.Active!.Bytes);
        tracker.AddSecond(Start.AddSeconds(300), 0);
        Assert.Null(tracker.Active);
        Assert.Equal(3 * MiB + 179 * 7, Assert.Single(tracker.Closed).Bytes);
    }

    [Fact]
    public void All_recorded_interval_bytes_equal_all_input_bytes()
    {
        var random = new Random(42);
        var tracker = new AnomalyTracker();
        long total = 0;
        for (var second = 0; second < 1800; second++)
        {
            var bytes = second % 120 < 8 ? random.Next(1, 3_000_000) : second % 10 == 0 ? 23 : 0;
            total += bytes;
            tracker.AddSecond(Start.AddSeconds(second), bytes);
        }
        Assert.Equal(total, tracker.Closed.Sum(episode => episode.Bytes) + (tracker.Active?.Bytes ?? 0));
    }

    [Theory]
    [InlineData(833333)] // About 3 GB/hour.
    [InlineData(5242880)]
    [InlineData(8333333)] // About 30 GB/hour.
    [InlineData(20971520)]
    public void Continuous_reported_rates_become_sustained(long bytes)
    {
        var tracker = Feed(119, _ => bytes);
        Assert.Equal(EpisodeSeverity.Spike, tracker.Active!.Severity);
        tracker.AddSecond(Start.AddSeconds(119), bytes);
        Assert.Equal(EpisodeSeverity.Loop, tracker.Active.Severity);
        Assert.Equal(120 * bytes, tracker.Active.Bytes);
        Assert.Equal(120, tracker.Active.Seconds);
    }

    [Fact]
    public void Rewrites_every_thirty_seconds_remain_one_sustained_episode()
    {
        var tracker = Feed(600, second => second % 30 == 0 ? 10 * MiB : 0);
        Assert.Equal(EpisodeSeverity.Loop, tracker.Active!.Severity);
        Assert.Empty(tracker.Closed);
        Assert.Equal(200 * MiB, tracker.Active.Bytes);
    }

    [Fact]
    public void One_huge_burst_never_becomes_sustained()
    {
        var tracker = Feed(600, second => second == 0 ? 4_000 * MiB : 0);
        Assert.Null(tracker.Active);
        var episode = Assert.Single(tracker.Closed);
        Assert.Equal(EpisodeSeverity.Spike, episode.Severity);
        Assert.Equal(4_000 * MiB, episode.Bytes);
        Assert.Equal(1, episode.Seconds);
        Assert.Equal(Start, episode.End);
    }

    [Fact]
    public void Small_burst_trims_all_trailing_quiet_seconds()
    {
        var tracker = Feed(120, second => second == 0 ? MiB : 0);
        var episode = Assert.Single(tracker.Closed);
        Assert.Equal(Start, episode.End);
        Assert.Equal(1, episode.Seconds);
        Assert.Equal(MiB, episode.Bytes);
    }

    [Fact]
    public void A_short_burst_across_multiple_buckets_is_not_a_loop()
    {
        var tracker = Feed(120, second => second < 25 ? 20 * MiB : 0);
        Assert.All(tracker.Closed, episode => Assert.Equal(EpisodeSeverity.Spike, episode.Severity));
        Assert.Null(tracker.Active);
    }

    [Fact]
    public void Missing_seconds_contribute_time_not_extra_speed()
    {
        var tracker = new AnomalyTracker();
        for (var second = 0; second < 120; second += 2)
            tracker.AddSecond(Start.AddSeconds(second), 20 * MiB);
        tracker.AddSecond(Start.AddSeconds(119), 0);
        Assert.Equal(120, tracker.Active!.Seconds);
        Assert.Equal(10 * MiB, tracker.Active.AverageBytesPerSecond);
        Assert.Equal(20 * MiB, tracker.Active.PeakBytesPerSecond);
    }

    [Fact]
    public void Long_idle_gap_closes_the_previous_episode()
    {
        var tracker = Feed(120, _ => MiB);
        tracker.AddSecond(Start.AddDays(1), 0);
        Assert.Null(tracker.Active);
        Assert.Single(tracker.Closed);
        Assert.Equal(Start.AddSeconds(119), tracker.Closed[0].End);
    }

    [Fact]
    public void Duplicate_or_reversed_seconds_are_rejected()
    {
        var tracker = Feed(1, _ => MiB);
        Assert.Throws<ArgumentException>(() => tracker.AddSecond(Start, MiB));
        Assert.Throws<ArgumentException>(() => tracker.AddSecond(Start.AddSeconds(-1), MiB));
    }

    static AnomalyTracker Feed(int seconds, Func<int, long> bytes)
    {
        var tracker = new AnomalyTracker();
        for (var second = 0; second < seconds; second++)
            tracker.AddSecond(Start.AddSeconds(second), bytes(second));
        return tracker;
    }
}

public class FileIdentityMapTests
{
    [Fact]
    public void Close_and_reuse_for_another_path_invalidate_the_object()
    {
        var map = new FileIdentityMap();
        map.Name(false, 123, true);
        Assert.True(map.Contains(false, 123));
        map.Name(false, 123, false);
        Assert.False(map.Contains(false, 123));
        map.Name(false, 123, true);
        map.Name(false, 123, false); // Create with another filename, even if Close was missed.
        Assert.False(map.Contains(false, 123));
    }

    [Fact]
    public void File_key_does_not_alias_a_file_object()
    {
        var map = new FileIdentityMap();
        map.Name(true, 123, true);
        Assert.False(map.Contains(false, 123));
        map.Name(false, 456, true);
        map.Name(false, 456, false);
        Assert.True(map.Contains(true, 123)); // Closing one handle does not remove the shared key.
        map.Name(true, 123, false);
        Assert.False(map.Contains(true, 123));
    }

    [Fact]
    public void Stale_handle_snapshot_cannot_resurrect_a_closed_object()
    {
        var map = new FileIdentityMap();
        var revision = map.Revision;
        map.Name(false, 123, false);
        map.SeedOpenObjects([123], revision);
        Assert.False(map.Contains(false, 123));
        map.SeedOpenObjects([456], map.Revision);
        Assert.True(map.Contains(false, 456));
        map.SeedOpenObjects([], map.Revision);
        Assert.False(map.Contains(false, 456));
    }
}

public class WriteSecondBufferTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(10000);

    [Fact]
    public void Delayed_ui_drain_preserves_event_seconds_and_exact_volume()
    {
        var buffer = new WriteSecondBuffer();
        for (var second = 0; second < 120; second++)
        {
            buffer.Add(Start.AddSeconds(second).AddMilliseconds(100), 400000);
            buffer.Add(Start.AddSeconds(second).AddMilliseconds(900), 433333);
        }
        var samples = buffer.Drain(Start.AddSeconds(119));
        Assert.Equal(120, samples.Length);
        Assert.All(samples, sample => Assert.Equal(833333, sample.Bytes));
        var tracker = new AnomalyTracker();
        foreach (var sample in samples) tracker.AddSecond(sample.Timestamp, sample.Bytes);
        Assert.Equal(EpisodeSeverity.Loop, tracker.Active!.Severity);
        Assert.Equal(833333, tracker.Active.PeakBytesPerSecond);
        Assert.Empty(buffer.Drain(Start.AddSeconds(119)));
        Assert.Equal(0, buffer.LateEvents);
    }

    [Fact]
    public void Future_seconds_are_retained_and_quiet_time_advances()
    {
        var buffer = new WriteSecondBuffer();
        buffer.Add(Start.AddSeconds(10), 123);
        Assert.Equal(0, Assert.Single(buffer.Drain(Start)).Bytes);
        var samples = buffer.Drain(Start.AddSeconds(11));
        Assert.Equal(123, samples.Sum(sample => sample.Bytes));
        Assert.Equal(Start.AddSeconds(11), samples[^1].Timestamp);
        Assert.Equal(0, samples[^1].Bytes);
    }

    [Fact]
    public void Late_events_preserve_volume_and_expose_uncertain_timing()
    {
        var buffer = new WriteSecondBuffer();
        buffer.Drain(Start);
        buffer.Add(Start.AddSeconds(-5), 456);
        Assert.Equal(1, buffer.LateEvents);
        Assert.Equal(456, Assert.Single(buffer.Drain(Start.AddSeconds(1))).Bytes);
    }

    [Fact]
    public void Clear_drops_pending_and_in_flight_old_events_without_losing_new_writes()
    {
        var buffer = new WriteSecondBuffer();
        buffer.Add(Start, 123);
        buffer.Reset(Start.AddMilliseconds(500));
        buffer.Add(Start.AddMilliseconds(100), 456);
        buffer.Add(Start.AddMilliseconds(700), 789);
        Assert.Equal(789, Assert.Single(buffer.Drain(Start)).Bytes);
        Assert.Equal(0, buffer.LateEvents);
    }
}

public class MeasurementHealthTests
{
    [Fact]
    public void Missing_identity_and_failed_monitor_are_not_a_clean_result()
    {
        Assert.NotNull(MeasurementHealth.Problem(false, true, true, true, 0, 0, null));
        Assert.NotNull(MeasurementHealth.Problem(true, true, true, false, 0, 0, null));
        Assert.NotNull(MeasurementHealth.Problem(true, true, true, true, 0, 0, "access denied"));
        Assert.Null(MeasurementHealth.Problem(true, true, true, true, 0, 0, null));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Lost_or_late_events_are_not_a_clean_result(long lost, long late) =>
        Assert.NotNull(MeasurementHealth.Problem(true, true, true, true, lost, late, null));
}
public class WriteOperationBookTests
{
    [Fact]
    public void Failed_fast_io_retry_is_not_counted_twice()
    {
        var book = new WriteOperationBook();
        var now = DateTimeOffset.UtcNow;
        book.Begin(123, 65536, now);
        Assert.Equal(0, book.Finish(123, 0xC00000D8, 0));
        book.Begin(123, 65536, now);
        Assert.Equal(65536, book.Finish(123, 0, 65536));
        Assert.Equal(0, book.Finish(123, 0, 65536));
        Assert.Equal(0, book.Incomplete);
    }

    [Fact]
    public void Partial_and_failed_writes_use_completion_status_and_transferred_bytes()
    {
        var book = new WriteOperationBook();
        book.Begin(123, 65536, DateTimeOffset.UtcNow);
        Assert.Equal(4096, book.Finish(123, 0, 4096));
        book.Begin(456, 65536, DateTimeOffset.UtcNow);
        Assert.Equal(0, book.Finish(456, 0xC0000022, 65536));
    }

    [Fact]
    public void Missing_completions_are_reported_and_bounded()
    {
        var book = new WriteOperationBook();
        var now = DateTimeOffset.UtcNow;
        book.Begin(123, 4096, now);
        book.Expire(now.AddSeconds(31));
        Assert.Equal(1, book.Incomplete);
        Assert.Equal(0, book.Finish(123, 0, 4096));
        Assert.NotNull(MeasurementHealth.Problem(true, true, true, true, 0, 0, null, book.Incomplete));
    }
}

public class FormatAndPathTests
{
    [Theory]
    [InlineData(@"C:\ProgramData\NVIDIA Corporation\nvtopps\nct\nvlog.nvlgstg", true)]
    [InlineData(@"\\?\C:\ProgramData\NVIDIA Corporation\nvtopps\nct\nvlog.nvlgstg", true)]
    [InlineData(@"\??\C:\ProgramData\NVIDIA Corporation\nvtopps\nct\nvlog.nvlgstg", true)]
    [InlineData(@"\Device\HarddiskVolume3\ProgramData\NVIDIA Corporation\nvtopps\nct\NVLOG.NVLGSTG", true)]
    [InlineData("nvlog.nvlgstg", false)]
    [InlineData(@"C:\temp\nvlog.nvlgstg", false)]
    [InlineData(@"C:\ProgramData\NVIDIA CorporationX\nvlog.nvlgstg", false)]
    [InlineData(@"C:\temp\nvlog.nvlgstg.bak", false)]
    [InlineData(@"C:\temp\nvlog.nvlgstg.txt", false)]
    [InlineData(null, false)]
    public void Matches_only_the_log_file(string? path, bool expected) =>
        Assert.Equal(expected, NvlogPath.IsMatch(path));

    [Fact]
    public void Decodes_the_windows_driver_version()
    {
        Assert.Equal("621.24", NvidiaVersion.FromWindowsDriver("32.0.16.2124"));
        Assert.Equal("561.09", NvidiaVersion.FromWindowsDriver("32.0.15.6109"));
        Assert.Equal("551.23", NvidiaVersion.FromWindowsDriver("31.0.15.5123"));
        Assert.Null(NvidiaVersion.FromWindowsDriver("not-a-version"));
    }

    [Fact]
    public void Formats_sizes_in_english()
    {
        Assert.Equal("0 B", ByteFormat.Bytes(0));
        Assert.Equal("1.5 KB", ByteFormat.Bytes(1536));
        Assert.Equal("1.00 MB", ByteFormat.Bytes(1024 * 1024));
        Assert.Equal("1.50 GB", ByteFormat.Bytes((long)(1.5 * 1024 * 1024 * 1024)));
        Assert.Equal("-1.00 MB", ByteFormat.Bytes(-1024 * 1024));
    }

    [Fact]
    public void Tracks_growth_from_the_first_size()
    {
        var book = new FileGrowthBook();
        book.Observe([new KeyValuePair<string, long>(@"C:\log", 100)]);
        book.Observe([new KeyValuePair<string, long>(@"C:\log", 150)]);

        Assert.Equal(150, book.TotalCurrent);
        Assert.Equal(50, book.TotalGrowth);
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void Blue_history_round_trips_without_changing_legacy_yellow_and_red_values()
    {
        Assert.Equal(0, (int)EpisodeSeverity.Spike);
        Assert.Equal(1, (int)EpisodeSeverity.Loop);
        var path = Path.Combine(Path.GetTempPath(), "nvlog-blue-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = HistoryStore.Load(path);
            foreach (var level in new[] { EpisodeSeverity.Activity, EpisodeSeverity.Spike, EpisodeSeverity.Loop })
                store.AddEpisode(new StoredEpisode { Start = DateTimeOffset.UtcNow, End = DateTimeOffset.UtcNow,
                    Bytes = 1, Seconds = 1, Severity = level });
            store.Save();
            Assert.Equal(new[] { EpisodeSeverity.Activity, EpisodeSeverity.Spike, EpisodeSeverity.Loop },
                HistoryStore.Load(path).Episodes.Select(episode => episode.Severity));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Remembers_bytes_and_a_closed_episode()
    {
        var path = Path.Combine(Path.GetTempPath(), "nvlog-history-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = HistoryStore.Load(path);
            var minute = new DateTimeOffset(2026, 11, 20, 1, 0, 30, TimeSpan.Zero);
            store.AddSecond(minute, 5_000_000);
            store.AddEpisode(new StoredEpisode
            {
                Start = minute,
                End = minute.AddMinutes(2),
                Bytes = 5_000_000,
                PeakBytesPerSecond = 2_000_000,
                Seconds = 120,
                Severity = EpisodeSeverity.Spike,
            });
            store.Save();

            var loaded = HistoryStore.Load(path);
            Assert.Equal(5_000_000, loaded.TotalBytes);
            Assert.Equal(5_000_000, loaded.GetMinute(minute));
            var episode = Assert.Single(loaded.Episodes);
            Assert.Equal(5_000_000, episode.Bytes);
            Assert.Equal(EpisodeSeverity.Spike, episode.Severity);
            Assert.False(episode.Open);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Closes_an_episode_left_open_by_a_crash()
    {
        var path = Path.Combine(Path.GetTempPath(), "nvlog-history-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = HistoryStore.Load(path);
            var start = new DateTimeOffset(2026, 11, 20, 2, 0, 0, TimeSpan.Zero);
            store.SetOpenEpisode(new StoredEpisode
            {
                Start = start,
                Bytes = 4096,
                PeakBytesPerSecond = 4096,
                Seconds = 3,
                Open = true,
            });
            store.Save();

            var loaded = HistoryStore.Load(path);
            var episode = Assert.Single(loaded.Episodes);
            Assert.False(episode.Open);
            Assert.NotNull(episode.End);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
