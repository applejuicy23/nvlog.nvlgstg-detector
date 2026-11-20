using System.Security.Principal;
using NvlogDetector;

using var report = args.Length == 1 ? new StreamWriter(args[0], append: false) { AutoFlush = true } : null;
if (report != null)
{
    Console.SetOut(report);
    Console.SetError(report);
}

using var identity = WindowsIdentity.GetCurrent();
if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.Error.WriteLine("NOT RUN: administrator rights are required for the real ETW test.");
    return 2;
}

var directory = Path.Combine(Path.GetTempPath(), "NvlogDetector-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var target = Path.Combine(directory, "nvlog.nvlgstg");
var other = Path.Combine(directory, "other.bin");
bool Matches(string? path) => string.Equals(path?.Replace(@"\\?\", ""), target, StringComparison.OrdinalIgnoreCase)
    || (path?.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase) == true
        && path.EndsWith(target[2..], StringComparison.OrdinalIgnoreCase));

try
{
    // Opening the target before monitoring verifies the handle-snapshot fallback too.
    using var preopened = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    using var monitor = new EtwFileWriteMonitor("NvlogDetector-test-" + Guid.NewGuid().ToString("N"), Matches);
    monitor.SetPids([Environment.ProcessId]);
    monitor.Start();
    await Task.Delay(2500);
    CheckHealth();

    var block = new byte[65536];
    const long expected = 16L * 65536;
    Rewrite(preopened);
    await Task.Delay(4000);
    var counted = Drain();
    CheckHealth();
    if (counted != expected)
        throw new InvalidOperationException($"Preopened target: expected {expected} bytes, got {counted}.");
    preopened.Dispose();

    // Fast create/close cycles exercise object reuse and rejection of unrelated writes.
    for (var index = 0; index < 64; index++)
    {
        using var file = new FileStream(other, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        file.Write(block);
    }
    await Task.Delay(4000);
    var unrelated = Drain();
    CheckHealth();
    if (unrelated != 0)
        throw new InvalidOperationException($"Unrelated file was counted: {unrelated} bytes.");

    using (var reopened = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        Rewrite(reopened);
    await Task.Delay(4000);
    var reopenedCount = Drain();
    CheckHealth();
    if (reopenedCount != expected)
        throw new InvalidOperationException($"Reopened target: expected {expected}, got {reopenedCount}.");

    using (var asynchronous = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
        bufferSize: 4096, options: FileOptions.Asynchronous))
    {
        for (var index = 0; index < 16; index++)
        {
            asynchronous.Position = 0;
            await asynchronous.WriteAsync(block);
        }
        await asynchronous.FlushAsync();
    }
    await Task.Delay(4000);
    var asynchronousCount = Drain();
    CheckHealth();
    if (asynchronousCount != expected)
        throw new InvalidOperationException($"Asynchronous target: expected {expected}, got {asynchronousCount}.");

    Console.WriteLine($"PASS: preopened target {counted}, unrelated file {unrelated}, reopened target {reopenedCount}, asynchronous target {asynchronousCount} bytes. No lost, late, or unresolved events.");
    return 0;

    void Rewrite(FileStream file)
    {
        for (var index = 0; index < 16; index++)
        {
            file.Position = 0;
            file.Write(block);
        }
        file.Flush();
    }

    long Drain() => monitor.TakeSeconds(DateTimeOffset.UtcNow.AddSeconds(-3)).Sum(sample => sample.Bytes);

    void CheckHealth()
    {
        var status = monitor.Status();
        if (!status.Running || status.Error != null || status.LostEvents > 0 || status.LateEvents > 0 || status.IncompleteWrites > 0)
            throw new InvalidOperationException($"ETW unhealthy: {status.Error}; running={status.Running}; lost={status.LostEvents}; late={status.LateEvents}; incomplete={status.IncompleteWrites}.");
    }
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL: " + error.Message);
    return 1;
}
finally
{
    // Only the two exact files created in this unique temporary directory are removed.
    File.Delete(target);
    File.Delete(other);
    Directory.Delete(directory);
}
