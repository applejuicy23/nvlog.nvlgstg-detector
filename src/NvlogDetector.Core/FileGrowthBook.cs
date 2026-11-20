namespace NvlogDetector.Core;

public sealed class FileGrowthBook
{
    readonly Dictionary<string, long> _start = new(StringComparer.OrdinalIgnoreCase);

    public long TotalCurrent { get; private set; }

    public long TotalGrowth { get; private set; }

    public void Observe(IEnumerable<KeyValuePair<string, long>> files)
    {
        long current = 0;
        long growth = 0;
        foreach (var file in files)
        {
            current += file.Value;
            if (!_start.TryGetValue(file.Key, out var start))
            {
                start = file.Value;
                _start[file.Key] = start;
            }

            growth += file.Value - start;
        }

        TotalCurrent = current;
        TotalGrowth = growth;
    }

    public void Reset()
    {
        _start.Clear();
        TotalCurrent = 0;
        TotalGrowth = 0;
    }
}
