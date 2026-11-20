namespace NvlogDetector.Core;

public readonly record struct WriteSecond(DateTimeOffset Timestamp, long Bytes);

/// <summary>Preserves event times through UI stalls. Completed seconds are drained after an ETW grace period.</summary>
public sealed class WriteSecondBuffer
{
    readonly object _gate = new();
    readonly SortedDictionary<long, long> _seconds = new();
    long _through = long.MinValue;
    long _lateEvents;
    DateTimeOffset _ignoreBefore = DateTimeOffset.MinValue;
    public long LateEvents { get { lock (_gate) return _lateEvents; } }

    public void Add(DateTimeOffset timestamp, long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            if (timestamp < _ignoreBefore) return;
            var second = timestamp.ToUnixTimeSeconds();
            if (second <= _through)
            {
                _lateEvents++;
                second = _through + 1; // Preserve volume, but expose timing uncertainty.
            }
            _seconds.TryGetValue(second, out var value);
            _seconds[second] = value + bytes;
        }
    }

    public void Reset(DateTimeOffset now)
    {
        lock (_gate)
        {
            _seconds.Clear();
            _lateEvents = 0;
            _ignoreBefore = now;
            _through = now.ToUnixTimeSeconds() - 1;
        }
    }

    public WriteSecond[] Drain(DateTimeOffset through)
    {
        lock (_gate)
        {
            var end = through.ToUnixTimeSeconds();
            if (end <= _through) return [];
            var result = _seconds.TakeWhile(pair => pair.Key <= end)
                .Select(pair => new WriteSecond(DateTimeOffset.FromUnixTimeSeconds(pair.Key), pair.Value)).ToList();
            foreach (var sample in result) _seconds.Remove(sample.Timestamp.ToUnixTimeSeconds());
            if (result.Count == 0 || result[^1].Timestamp.ToUnixTimeSeconds() < end)
                result.Add(new WriteSecond(DateTimeOffset.FromUnixTimeSeconds(end), 0));
            _through = end;
            return result.ToArray();
        }
    }
}
