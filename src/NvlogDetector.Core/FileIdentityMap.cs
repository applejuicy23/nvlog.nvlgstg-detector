namespace NvlogDetector.Core;

/// <summary>FileObject and FileKey are different namespaces, with create/close/delete lifetimes.</summary>
public sealed class FileIdentityMap
{
    readonly object _gate = new();
    readonly HashSet<(bool IsKey, ulong Address)> _known = [];
    HashSet<ulong> _seeded = [];
    long _revision;
    public long Revision { get { lock (_gate) return _revision; } }
    public int Count { get { lock (_gate) return _known.Count + _seeded.Count; } }

    public void Name(bool isKey, ulong address, bool matches)
    {
        lock (_gate)
        {
            _revision++;
            _known.Remove((isKey, address));
            if (!isKey) _seeded.Remove(address);
            if (matches) _known.Add((isKey, address));
        }
    }

    public bool Contains(bool isKey, ulong address)
    {
        lock (_gate)
            return _known.Contains((isKey, address)) || (!isKey && _seeded.Contains(address));
    }

    public void SeedOpenObjects(HashSet<ulong> objects, long expectedRevision)
    {
        lock (_gate)
        {
            // A snapshot predating a close/create must not resurrect a recycled object.
            if (_revision == expectedRevision) _seeded = new HashSet<ulong>(objects);
        }
    }

    public void Clear()
    {
        lock (_gate) { _revision++; _known.Clear(); _seeded.Clear(); }
    }
}
