using System.Runtime.InteropServices;
using System.Text;
using NvlogDetector.Core;

namespace NvlogDetector;

/// <summary>
/// Maps the kernel FILE_OBJECT address of an open nvlog.nvlgstg to the path.
/// ETW write events carry that address and a byte count, but not the path.
/// </summary>
static class NvlogHandleMap
{
    public static HashSet<ulong> Find(IReadOnlyList<int> processIds, out string? path, out string? error,
        Func<string?, bool>? pathMatches = null)
    {
        path = null;
        error = null;
        pathMatches ??= NvlogPath.IsMatch;
        if (processIds.Count == 0)
            return [];

        WindowsPrivilege.EnableDebug();
        var wanted = processIds.ToHashSet();
        var result = new HashSet<ulong>();
        var temporary = Path.GetTempFileName();
        FileStream? stream = null;
        try
        {
            stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entries = QueryHandles(out var queryError);
            if (entries is null)
            {
                error = queryError;
                return result;
            }

            var typeIndex = FileTypeIndex(entries, stream.SafeFileHandle.DangerousGetHandle().ToInt64());
            if (typeIndex is null)
            {
                error = "This process's own file handle was not in the system table.";
                return result;
            }

        foreach (var processId in wanted)
        {
            var process = OpenProcess(0x0040, false, processId);
            if (process == IntPtr.Zero)
            {
                error ??= "Could not open the NVIDIA process, code " + Marshal.GetLastWin32Error() + ".";
                continue;
            }

            try
            {
                var inspected = 0;
                foreach (var entry in entries)
                {
                    if (entry.ProcessId != processId || entry.TypeIndex != typeIndex.Value || entry.Object < 0xFFFF000000000000UL)
                        continue;
                    if (++inspected > 400)
                    {
                        error ??= "The NVIDIA process has more than 400 file handles; attribution is incomplete.";
                        break;
                    }

                    if (!DuplicateHandle(process, entry.Handle, new IntPtr(-1), out var copy, 0, false, 2))
                        continue;

                    try
                    {
                        var name = PathOf(copy);
                        if (!pathMatches(name))
                            continue;

                        result.Add(entry.Object);
                        path = name;
                    }
                    finally
                    {
                        CloseHandle(copy);
                    }
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        return result;
        }
        finally
        {
            stream?.Dispose();
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // A locked temp file is harmless.
            }
        }
    }

    static ushort? FileTypeIndex(List<HandleRow> entries, long handleValue)
    {
        var pid = Environment.ProcessId;
        foreach (var entry in entries)
        {
            if (entry.ProcessId == pid && entry.Handle.ToInt64() == handleValue)
                return entry.TypeIndex;
        }

        return null;
    }

    static List<HandleRow>? QueryHandles(out string? error)
    {
        error = null;
        var size = 1024 * 1024;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(64, buffer, size, out var needed);
                if (status == unchecked((int)0xC0000004))
                {
                    size = Math.Max(size * 2, needed + (64 * 1024));
                    continue;
                }

                if (status < 0)
                {
                    error = "Handle table is unavailable, code 0x" + status.ToString("X8") + ".";
                    return null;
                }

                var count = Marshal.ReadInt64(buffer);
                if (count < 0)
                    count = 0;
                var entrySize = Marshal.SizeOf<SystemHandle>();
                var header = IntPtr.Size * 2;
                var max = (size - header) / entrySize;
                if (count > max)
                    count = max;
                if (count > 2_000_000)
                    count = 2_000_000;

                var rows = new List<HandleRow>((int)Math.Min(count, 4096));
                for (long index = 0; index < count; index++)
                {
                    var entry = Marshal.PtrToStructure<SystemHandle>(IntPtr.Add(buffer, header + (int)(index * entrySize)));
                    if (entry.Object == IntPtr.Zero)
                        continue;
                    rows.Add(new HandleRow(
                        entry.UniqueProcessId.ToInt64(),
                        entry.HandleValue,
                        unchecked((ulong)entry.Object.ToInt64()),
                        entry.ObjectTypeIndex));
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        error = "Handle table did not fit in the buffer.";
        return null;
    }

    static string? PathOf(IntPtr handle)
    {
        var builder = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, builder, builder.Capacity, 0);
        if (length <= 0)
            return null;
        if (length >= builder.Capacity)
        {
            builder.EnsureCapacity(length + 1);
            length = GetFinalPathNameByHandle(handle, builder, builder.Capacity, 0);
            if (length <= 0)
                return null;
        }

        return builder.ToString(0, length);
    }

    readonly record struct HandleRow(long ProcessId, IntPtr Handle, ulong Object, ushort TypeIndex);

    [StructLayout(LayoutKind.Sequential)]
    struct SystemHandle
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int @class, IntPtr buffer, int length, out int needed);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out IntPtr targetHandle,
        uint desiredAccess,
        bool inherit,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetFinalPathNameByHandle(IntPtr handle, StringBuilder path, int capacity, uint flags);
}
