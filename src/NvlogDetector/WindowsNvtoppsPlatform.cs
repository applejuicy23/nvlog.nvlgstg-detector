using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NvlogDetector.Core;

namespace NvlogDetector;

sealed class WindowsNvtoppsPlatform : INvtoppsPlatform
{
    public const string PluginPath = @"SYSTEM\CurrentControlSet\Services\NVDisplay.ContainerLocalSystem\Parameters\LocalSystem\_nvtopps";
    const string BackupPath = @"SOFTWARE\NvlogDetector\NvtoppsWorkaround";
    const string BackupName = "OriginalSettingV1";
    const string ServiceName = "NVDisplay.ContainerLocalSystem";
    readonly RegistryHive _hive;
    readonly string _pluginPath, _backupPath;
    readonly bool _registryTestOnly;

    public WindowsNvtoppsPlatform() : this(RegistryHive.LocalMachine, PluginPath, BackupPath, false) { }

    WindowsNvtoppsPlatform(RegistryHive hive, string pluginPath, string backupPath, bool registryTestOnly)
        => (_hive, _pluginPath, _backupPath, _registryTestOnly) = (hive, pluginPath, backupPath, registryTestOnly);

    internal static WindowsNvtoppsPlatform ForRegistryTest(Guid id)
    {
        var root = @"Software\NvlogDetector.Tests\" + id.ToString("N");
        return new(RegistryHive.CurrentUser, root + @"\Plugin", root + @"\Backup", true);
    }

    void RequireProductionService()
    {
        if (_registryTestOnly) throw new InvalidOperationException("Service access is forbidden in registry tests.");
    }

    public bool ServiceExists
    {
        get
        {
            RequireProductionService();
            using var scm = Manager();
            using var service = OpenService(scm, ServiceName, 4);
            if (!service.IsInvalid) return true;
            var error = Marshal.GetLastWin32Error();
            if (error == 1060) return false;
            throw new Win32Exception(error);
        }
    }

    public bool ServiceRunning
    {
        get { RequireProductionService(); using var scm = Manager(); using var service = Service(scm, 4); return State(service) == 4; }
    }

    RegistryKey Machine() => RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);

    public NvtoppsSetting ReadSetting()
    {
        using var machine = Machine();
        using var key = machine.OpenSubKey(_pluginPath);
        if (key == null) return new(false, false, true, 0);
        var exists = key.GetValueNames().Contains("DisableLoad", StringComparer.OrdinalIgnoreCase);
        if (!exists) return new(true, false, true, 0);
        var isDword = key.GetValueKind("DisableLoad") == RegistryValueKind.DWord;
        return new(true, true, isDword, isDword ? (int)key.GetValue("DisableLoad")! : 0);
    }

    public NvtoppsBackup? ReadBackup()
    {
        using var machine = Machine();
        using var key = machine.OpenSubKey(_backupPath);
        var value = key?.GetValue(BackupName);
        if (value == null) return null;
        if (value is not string json) throw new InvalidOperationException("The workaround backup has an unexpected type.");
        return JsonSerializer.Deserialize<NvtoppsBackup>(json)
            ?? throw new InvalidOperationException("The workaround backup is empty or invalid.");
    }

    public void SaveBackup(NvtoppsBackup backup)
    {
        using var machine = Machine();
        using var key = machine.CreateSubKey(_backupPath, writable: true);
        if (key.GetValue(BackupName) != null) throw new InvalidOperationException("An original backup already exists.");
        key.SetValue(BackupName, JsonSerializer.Serialize(backup), RegistryValueKind.String);
        key.Flush();
    }

    public void ClearBackup()
    {
        using var machine = Machine();
        using var key = machine.OpenSubKey(_backupPath, writable: true);
        key?.DeleteValue(BackupName, throwOnMissingValue: false);
        key?.Flush();
    }

    public void DisablePlugin()
    {
        using var machine = Machine();
        using var key = machine.CreateSubKey(_pluginPath, writable: true);
        key.SetValue("DisableLoad", 1, RegistryValueKind.DWord);
        key.Flush();
    }

    public void RestoreSetting(NvtoppsSetting original)
    {
        using var machine = Machine();
        using (var key = original.ValueExisted ? machine.CreateSubKey(_pluginPath, writable: true) : machine.OpenSubKey(_pluginPath, writable: true))
        {
            if (original.ValueExisted) key!.SetValue("DisableLoad", original.Value, RegistryValueKind.DWord);
            else key?.DeleteValue("DisableLoad", throwOnMissingValue: false);
            key?.Flush();
        }
        // Leave even a newly-created empty key in place: deleting it could race with another writer.
        // Only DisableLoad is restored; unrelated NVIDIA values/subkeys are never deleted.
    }

    public static void RunExclusive(Action operation)
    {
        using var mutex = new Mutex(false, @"Global\NvlogDetectorNvtoppsWorkaround");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("Another workaround operation is already in progress.");
        try { operation(); }
        finally { mutex.ReleaseMutex(); }
    }

    public void RestartService()
    {
        RequireProductionService();
        using var scm = Manager();
        using var service = Service(scm, 4 | 16 | 32);
        var state = State(service);
        if (state == 2) { Wait(service, 4); state = 4; } // Start pending.
        if (state == 4)
        {
            if (!ControlService(service, 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Wait(service, 1);
        }
        else if (state == 3) Wait(service, 1); // Stop pending.
        else if (state != 1) throw new InvalidOperationException("The NVIDIA service is in an unsupported state.");
        if (!StartService(service, 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Wait(service, 4);
    }

    static ServiceHandle Manager()
    {
        var handle = OpenSCManager(null, null, 1);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }

    static ServiceHandle Service(ServiceHandle manager, uint access)
    {
        var handle = OpenService(manager, ServiceName, access);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }

    static uint State(ServiceHandle service)
    {
        if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return status.CurrentState;
    }

    static void Wait(ServiceHandle service, uint expected)
    {
        var timer = Stopwatch.StartNew();
        while (State(service) != expected)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("The NVIDIA service did not reach the requested state within 15 seconds.");
            Thread.Sleep(100);
        }
    }

    sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool StartService(ServiceHandle service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseServiceHandle(IntPtr service);
}
