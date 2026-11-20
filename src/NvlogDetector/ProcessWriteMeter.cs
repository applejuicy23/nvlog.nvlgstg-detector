using System.Runtime.InteropServices;

namespace NvlogDetector;

/// <summary>
/// Process-wide write bytes for NVDisplay.Container. This includes every file, so it is only a cross-check.
/// </summary>
sealed class ProcessWriteMeter
{
    readonly Dictionary<int, ulong> _baseline = [];
    long _total;

    public long TotalBytes => Interlocked.Read(ref _total);

    public void Sample(IReadOnlyList<int> processIds)
    {
        WindowsPrivilege.EnableDebug();
        var alive = processIds.Count == 0 ? [] : processIds.ToHashSet();
        foreach (var processId in processIds)
        {
            if (!TryRead(processId, out var written))
                continue;

            if (!_baseline.TryGetValue(processId, out var baseline))
            {
                _baseline[processId] = written;
                continue;
            }

            if (written >= baseline)
                Interlocked.Add(ref _total, (long)(written - baseline));
            _baseline[processId] = written;
        }

        if (_baseline.Count == 0)
            return;

        foreach (var processId in _baseline.Keys.ToArray())
        {
            if (!alive.Contains(processId))
                _baseline.Remove(processId);
        }
    }

    public void Reset()
    {
        _baseline.Clear();
        Interlocked.Exchange(ref _total, 0);
    }

    static bool TryRead(int processId, out ulong written)
    {
        written = 0;
        var process = OpenProcess(0x1400, false, processId);
        if (process == IntPtr.Zero)
            return false;

        try
        {
            if (!GetProcessIoCounters(process, out var counters))
                return false;
            written = counters.WriteTransferCount;
            return true;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
