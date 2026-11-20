namespace NvlogDetector.Core;

/// <summary>Correlates write attempts with OperationEnd, excluding failed Fast I/O retries.</summary>
public sealed class WriteOperationBook
{
    readonly object _gate = new();
    readonly Dictionary<ulong, (long Bytes, DateTimeOffset Time)> _pending = [];
    long _incomplete;
    public long Incomplete { get { lock (_gate) return _incomplete; } }

    public void Begin(ulong irp, long bytes, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            if (irp == 0 || _pending.Count >= 8192) { _incomplete++; return; }
            if (_pending.ContainsKey(irp)) _incomplete++;
            _pending[irp] = (bytes, timestamp);
        }
    }

    public long Finish(ulong irp, uint status, ulong transferred)
    {
        lock (_gate)
        {
            if (!_pending.Remove(irp, out var request)) return 0;
            if (status == 0x103) { _incomplete++; return 0; } // STATUS_PENDING is not completion.
            if (unchecked((int)status) < 0) return 0;
            return (long)Math.Min((ulong)request.Bytes, transferred);
        }
    }

    public void Expire(DateTimeOffset before)
    {
        lock (_gate)
            foreach (var irp in _pending.Where(pair => pair.Value.Time < before).Select(pair => pair.Key).ToArray())
            {
                _pending.Remove(irp);
                _incomplete++;
            }
    }
}
