using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using NvlogDetector.Core;

namespace NvlogDetector;

sealed class MonitorStatus
{
    public bool Running { get; init; }

    public long WriteEvents { get; init; }

    public long MatchedEvents { get; init; }

    public long LostEvents { get; init; }

    public long LateEvents { get; init; }

    public bool AttributionVerified { get; init; }

    public long CompletedWrites { get; init; }

    public long IncompleteWrites { get; init; }

    public int KnownObjects { get; init; }

    public string Payload { get; init; } = "";

    public string SeenEvents { get; init; } = "";

    public string? Error { get; init; }

    public string? HandleError { get; init; }

    public string? ResolvedPath { get; init; }

    public int[] Pids { get; init; } = [];
}

/// <summary>
/// Counts bytes issued to nvlog.nvlgstg. The file is rewritten in place, so its size barely moves.
/// Kernel write events report the size; the path is matched through the open-file object address.
/// </summary>
sealed class EtwFileWriteMonitor : IDisposable
{
    const string SessionName = "NvlogNvlgstgDetector";
    static readonly Guid ProviderGuid = new("EDD08927-9CC4-4E65-B970-C2560FB5C289");
    readonly string _sessionName;
    readonly Func<string?, bool> _pathMatches;

    readonly object _gate = new();
    readonly object _notes = new();
    readonly FileIdentityMap _identities = new();
    readonly WriteSecondBuffer _seconds = new();
    readonly WriteOperationBook _operations = new();
    readonly HashSet<string> _seenKeys = new();

    int[] _pids = [];
    TraceEventSession? _session;
    Thread? _thread;
    Timer? _handles;
    int _seenCount;
    long _writeEvents;
    long _matchedEvents;
    long _completedWrites;
    string _payload = "";
    string _seen = "";
    volatile string? _error;
    volatile string? _handleError;
    volatile string? _resolvedPath;
    int _lastPid;
    int _verified;
    int _refreshing;
    volatile bool _disposed;

    public EtwFileWriteMonitor(string? sessionName = null, Func<string?, bool>? pathMatches = null)
    {
        _sessionName = sessionName ?? SessionName;
        _pathMatches = pathMatches ?? NvlogPath.IsMatch;
    }

    public void Start()
    {
        if (_thread != null)
            return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "nvlog-etw",
        };
        _thread.Start();
        _handles = new Timer(_ => RefreshHandles(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }

    public void SetPids(int[] processIds)
    {
        processIds ??= [];
        lock (_gate)
        {
            if (_pids.SequenceEqual(processIds))
                return;
            _pids = processIds.ToArray();
            _identities.Clear();
            Volatile.Write(ref _verified, 0);
        }

        ApplyFilter();
    }

    public WriteSecond[] TakeSeconds(DateTimeOffset through) => _seconds.Drain(through);

    public void ClearPending(DateTimeOffset now) => _seconds.Reset(now);

    public int LastProcessId => Volatile.Read(ref _lastPid);

    public MonitorStatus Status()
    {
        _operations.Expire(DateTimeOffset.UtcNow.AddSeconds(-30));
        int[] pids;
        long lost = 0;
        string? error;
        lock (_gate)
        {
            pids = _pids.ToArray();
            error = _error;
            try
            {
                lost = _session?.EventsLost ?? 0;
            }
            catch (ObjectDisposedException)
            {
                lost = 0;
            }
        }

        string payload;
        string seen;
        lock (_notes)
        {
            payload = _payload;
            seen = _seen;
        }

        return new MonitorStatus
        {
            Running = _thread is { IsAlive: true } && error is null,
            WriteEvents = Interlocked.Read(ref _writeEvents),
            MatchedEvents = Interlocked.Read(ref _matchedEvents),
            LostEvents = lost,
            LateEvents = _seconds.LateEvents,
            AttributionVerified = Volatile.Read(ref _verified) != 0,
            CompletedWrites = Interlocked.Read(ref _completedWrites),
            IncompleteWrites = _operations.Incomplete,
            KnownObjects = _identities.Count,
            Payload = payload,
            SeenEvents = seen,
            Error = error,
            HandleError = _handleError,
            ResolvedPath = _resolvedPath,
            Pids = pids,
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _handles?.Dispose();
        TraceEventSession? session;
        lock (_gate)
            session = _session;

        try
        {
            session?.Stop();
        }
        catch (COMException)
        {
            // The session is already gone.
        }
        catch (InvalidOperationException)
        {
            // Stop raced with the trace thread.
        }

        _thread?.Join(TimeSpan.FromSeconds(3));
        lock (_gate)
        {
            try
            {
                _session?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by StopOnDispose.
            }

            _session = null;
        }
    }

    void Run()
    {
        try
        {
            StopStaleSession();
            var session = new TraceEventSession(_sessionName) { StopOnDispose = true };
            session.Source.Dynamic.All += OnEvent;
            lock (_gate)
            {
                if (_disposed)
                {
                    session.Dispose();
                    return;
                }
                _session = session;
            }
            ApplyFilter();
            session.Source.Process();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            _error = ex.Message;
        }
    }

    void StopStaleSession()
    {
        try
        {
            using var existing = TraceEventSession.GetActiveSession(_sessionName);
            existing?.Stop();
        }
        catch (COMException)
        {
            // Nothing to clean up.
        }
        catch (InvalidOperationException)
        {
            // Nothing to clean up.
        }
        catch (FileNotFoundException)
        {
            // GetActiveSession reports a missing session this way on some builds.
        }
    }

    void ApplyFilter()
    {
        TraceEventSession? session;
        int[] pids;
        lock (_gate)
        {
            session = _session;
            pids = _pids.ToArray();
        }

        if (session is null)
            return;

        try
        {
            if (pids.Length == 0)
            {
                session.DisableProvider(ProviderGuid);
                return;
            }

            session.EnableProvider(
                ProviderGuid,
                TraceEventLevel.Verbose,
                // Name, FileIO, OperationEnd, Create, Write, CreateNewFile.
                // Completions may run under System rather than the issuing process.
                matchAnyKeywords: 0x12F0);
            session.CaptureState(ProviderGuid);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _error = ex.Message;
        }
    }

    void RefreshHandles()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0)
            return;

        int[] pids;
        long revision;
        lock (_gate)
        {
            pids = _pids.ToArray();
            revision = _identities.Revision;
        }

        try
        {
            var found = NvlogHandleMap.Find(pids, out var path, out var error, _pathMatches);
            lock (_gate)
            {
                if (!_pids.SequenceEqual(pids)) return;
                _identities.SeedOpenObjects(found, revision);
                _resolvedPath = path;
                _handleError = error;
                if (found.Count > 0 && _identities.Revision == revision)
                    Volatile.Write(ref _verified, 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _handleError = ex.Message;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    void OnEvent(TraceEvent data)
    {
        try
        {
            if (!IsKernelFile(data))
                return;
            lock (_gate)
            {
                var id = (int)data.ID;
                var name = data.EventName ?? "";
                if (id == 24 || IsTask(name, "OperationEnd"))
                    OnOperationEnd(data);
                else if (id == 10 || IsTask(name, "NameCreate"))
                    OnName(data);
                else if (id == 11 || IsTask(name, "NameDelete"))
                    ForgetIdentity(data, "FileKey");
                else if (id == 14 || IsTask(name, "Close"))
                    ForgetIdentity(data, "FileObject");
                else if (_pids.Contains(data.ProcessID))
                {
                    NoteEvent(data);
                    if (id == 16 || IsTask(name, "Write"))
                        OnWrite(data);
                    else if (id is 12 or 30 || IsTask(name, "Create") || IsTask(name, "CreateNewFile"))
                        OnName(data);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A malformed event must not tear down the session.
            if (_error is null)
                _error = ex.Message;
        }
    }

    void OnWrite(TraceEvent data)
    {
        Interlocked.Increment(ref _writeEvents);
        RememberSchema(data);
        var size = ReadSize(data);
        if (size <= 0)
            return;

        var matched = false;
        var count = data.PayloadNames.Length;
        for (var index = 0; index < count; index++)
        {
            if (!IsIdentityName(data.PayloadNames[index]))
                continue;
            if (!TryKernelPointer(data.PayloadValue(index), out var pointer))
                continue;
            if (!_identities.Contains(data.PayloadNames[index].Equals("FileKey", StringComparison.OrdinalIgnoreCase), pointer))
                continue;
            matched = true;
            break;
        }

        if (!matched && PayloadHasPath(data))
        {
            RememberIdentities(data);
            matched = true;
        }

        if (!matched)
            return;

        if (!TryField(data, "Irp", out var irp))
        {
            _error = "The write-event schema has no usable Irp field; completion counting is unavailable.";
            return;
        }
        _operations.Begin(irp, size, new DateTimeOffset(data.TimeStamp.ToUniversalTime()));
        Volatile.Write(ref _verified, 1);
        Interlocked.Increment(ref _matchedEvents);
        Volatile.Write(ref _lastPid, data.ProcessID);
    }

    void OnOperationEnd(TraceEvent data)
    {
        if (!TryField(data, "Irp", out var irp) || !TryField(data, "Status", out var status) ||
            !TryField(data, "ExtraInformation", out var transferred))
        {
            _error = "The OperationEnd schema is unsupported; completion counting is unavailable.";
            return;
        }
        var bytes = _operations.Finish(irp, unchecked((uint)status), transferred);
        if (bytes <= 0) return;
        _seconds.Add(new DateTimeOffset(data.TimeStamp.ToUniversalTime()), bytes);
        Interlocked.Increment(ref _completedWrites);
    }

    static bool TryField(TraceEvent data, string field, out ulong value)
    {
        var index = Array.FindIndex(data.PayloadNames, name => name.Equals(field, StringComparison.OrdinalIgnoreCase));
        value = 0;
        if (index < 0) return false;
        switch (data.PayloadValue(index))
        {
            case ulong number: value = number; return true;
            case long number: value = unchecked((ulong)number); return true;
            case uint number: value = number; return true;
            case int number: value = unchecked((uint)number); return true;
            case IntPtr pointer: value = unchecked((ulong)pointer.ToInt64()); return true;
            case UIntPtr pointer: value = pointer.ToUInt64(); return true;
            default: return false;
        }
    }

    void OnName(TraceEvent data)
    {
        // A create for a different path invalidates a recycled address immediately.
        RememberIdentities(data, PayloadHasPath(data));
    }

    void RememberIdentities(TraceEvent data, bool matches = true)
    {
        var names = data.PayloadNames;
        for (var index = 0; index < names.Length; index++)
        {
            if (!IsIdentityName(names[index]))
                continue;
            if (TryKernelPointer(data.PayloadValue(index), out var pointer))
                _identities.Name(names[index].Equals("FileKey", StringComparison.OrdinalIgnoreCase), pointer, matches);
        }
    }

    void ForgetIdentity(TraceEvent data, string field)
    {
        for (var index = 0; index < data.PayloadNames.Length; index++)
            if (data.PayloadNames[index].Equals(field, StringComparison.OrdinalIgnoreCase) &&
                TryKernelPointer(data.PayloadValue(index), out var pointer))
                _identities.Name(field == "FileKey", pointer, false);
    }

    void RememberSchema(TraceEvent data)
    {
        var names = data.PayloadNames;
        lock (_notes)
            _payload = string.Join(", ", names);
    }

    long ReadSize(TraceEvent data)
    {
        // Event versions have different layouts; never reuse an index from another schema.
        var sizeIndex = Array.FindIndex(data.PayloadNames, name => name.Equals("IOSize", StringComparison.OrdinalIgnoreCase));
        if (sizeIndex < 0)
        {
            _error = "The write-event schema has no IOSize field; byte counting is unavailable.";
            return 0;
        }

        return data.PayloadValue(sizeIndex) switch
        {
            uint number => number,
            int number when number > 0 => number,
            long number when number is > 0 and <= uint.MaxValue => number,
            ulong number when number is > 0 and <= uint.MaxValue => (long)number,
            _ => 0,
        };
    }

    bool PayloadHasPath(TraceEvent data)
    {
        var count = data.PayloadNames.Length;
        for (var index = 0; index < count; index++)
        {
            if (data.PayloadValue(index) is string text && _pathMatches(text))
                return true;
        }

        return false;
    }

    void NoteEvent(TraceEvent data)
    {
        if (_seenCount >= 16)
            return;

        var key = $"{(int)data.ID}:{data.EventName}";
        lock (_notes)
        {
            if (!_seenKeys.Add(key))
                return;
            _seenCount = _seenKeys.Count;
            _seen = string.Join(", ", _seenKeys);
        }
    }

    static bool IsKernelFile(TraceEvent data)
    {
        if (data.ProviderGuid == ProviderGuid)
            return true;
        return string.Equals(data.ProviderName, "Microsoft-Windows-Kernel-File", StringComparison.Ordinal);
    }

    static bool IsTask(string eventName, string task) =>
        eventName.Equals(task, StringComparison.OrdinalIgnoreCase)
        || eventName.EndsWith("/" + task, StringComparison.OrdinalIgnoreCase);

    static bool IsIdentityName(string name) =>
        name.Equals("FileObject", StringComparison.OrdinalIgnoreCase)
        || name.Equals("FileKey", StringComparison.OrdinalIgnoreCase);

    static bool TryKernelPointer(object? value, out ulong pointer)
    {
        switch (value)
        {
            case ulong number when number >= 0xFFFF000000000000UL:
                pointer = number;
                return true;
            case long number when unchecked((ulong)number) >= 0xFFFF000000000000UL:
                pointer = unchecked((ulong)number);
                return true;
            case IntPtr address when unchecked((ulong)address.ToInt64()) >= 0xFFFF000000000000UL:
                pointer = unchecked((ulong)address.ToInt64());
                return true;
            case UIntPtr address when address.ToUInt64() >= 0xFFFF000000000000UL:
                pointer = address.ToUInt64();
                return true;
            default:
                pointer = 0;
                return false;
        }
    }
}
