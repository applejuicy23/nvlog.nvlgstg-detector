using NvlogDetector.Core;

namespace NvlogDetector;

public sealed class EpisodeRow : Observable
{
    public DateTimeOffset Start { get; private set; }

    public DateTimeOffset? End { get; private set; }
    public EpisodeSeverity Severity { get; private set; }
    public int SeverityRank => Severity switch { EpisodeSeverity.Loop => 2, EpisodeSeverity.Spike => 1, _ => 0 };
    public long Bytes { get; private set; }
    public long PeakBytesPerSecond { get; private set; }
    public double AverageBytesPerSecond { get; private set; }
    public int Seconds { get; private set; }

    public string StartText { get; private set; } = "";

    public string EndText { get; private set; } = "";

    public string DurationText { get; private set; } = "";

    public string VolumeText { get; private set; } = "";

    public string PeakText { get; private set; } = "";

    public string AverageText { get; private set; } = "";

    public string KindText { get; private set; } = "";

    public static EpisodeRow Create(StoredEpisode episode, bool open, DateTimeOffset now)
    {
        var row = new EpisodeRow();
        row.Refresh(episode, open, now);
        return row;
    }

    public void Refresh(StoredEpisode episode, bool open, DateTimeOffset now)
    {
        Start = episode.Start;
        End = open ? null : episode.End;
        Severity = episode.Severity;
        Bytes = episode.Bytes;
        PeakBytesPerSecond = episode.PeakBytesPerSecond;
        Seconds = Math.Max(1, episode.Seconds);
        AverageBytesPerSecond = Bytes / (double)Seconds;
        StartText = ByteFormat.Time(episode.Start);
        EndText = open || episode.End is null ? "now" : ByteFormat.Time(episode.End.Value);
        DurationText = ByteFormat.Duration(TimeSpan.FromSeconds(Seconds));
        VolumeText = ByteFormat.Bytes(episode.Bytes);
        PeakText = ByteFormat.PerSecond(episode.PeakBytesPerSecond);
        var seconds = Math.Max(1, episode.Seconds);
        AverageText = ByteFormat.PerSecond(episode.Bytes / (double)seconds);
        KindText = episode.Severity switch
        {
            EpisodeSeverity.Loop => "Sustained",
            EpisodeSeverity.Spike => "Elevated",
            _ => "Activity",
        };
        Raise(nameof(Start));
        Raise(nameof(End));
        Raise(nameof(Severity));
        Raise(nameof(SeverityRank));
        Raise(nameof(Bytes));
        Raise(nameof(PeakBytesPerSecond));
        Raise(nameof(Seconds));
        Raise(nameof(AverageBytesPerSecond));
        Raise(nameof(StartText));
        Raise(nameof(EndText));
        Raise(nameof(DurationText));
        Raise(nameof(VolumeText));
        Raise(nameof(PeakText));
        Raise(nameof(AverageText));
        Raise(nameof(KindText));
    }
}
