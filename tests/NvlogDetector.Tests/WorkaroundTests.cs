using NvlogDetector.Core;
using Xunit;

namespace NvlogDetector.Tests;

public class WorkaroundTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 7)]
    [InlineData(true, true, -1)]
    public void Apply_and_restore_preserve_the_exact_original_value(bool key, bool value, int originalValue)
    {
        var original = new NvtoppsSetting(key, value, true, originalValue);
        var platform = new FakePlatform { Setting = original };
        var workaround = new NvtoppsWorkaround(platform);
        Assert.True(workaround.Inspect().CanApply);
        Assert.Equal(0, platform.Mutations);
        workaround.Apply("621.24");
        Assert.True(platform.Setting.Disabled);
        Assert.Equal(original, platform.Backup!.Original);
        Assert.Equal("621.24", platform.Backup.DriverVersion);
        Assert.True(workaround.Inspect().CanRestore);
        Assert.False(workaround.Inspect().CanApply);
        workaround.Restore();
        Assert.True(platform.Setting.SameValue(original));
        Assert.Null(platform.Backup);
        Assert.Equal(2, platform.Restarts);
        Assert.Equal(new[] { "backup", "disable", "restart", "restore", "restart", "clear" }, platform.Events);
    }

    [Fact]
    public void Backup_failure_never_changes_the_target_or_restarts_the_service()
    {
        var platform = new FakePlatform { BackupFails = true };
        Assert.Throws<IOException>(() => new NvtoppsWorkaround(platform).Apply("621.24"));
        Assert.False(platform.Setting.Disabled);
        Assert.Equal(0, platform.Restarts);
        Assert.Equal(new[] { "backup" }, platform.Events);
    }

    [Fact]
    public void Restart_failure_rolls_back_registry_and_recovers_service()
    {
        var platform = new FakePlatform { RestartFailures = 1 };
        var original = platform.Setting;
        var error = Assert.Throws<InvalidOperationException>(() => new NvtoppsWorkaround(platform).Apply("621.24"));
        Assert.Contains("were restored", error.Message);
        Assert.True(platform.Setting.SameValue(original));
        Assert.True(platform.ServiceRunning);
        Assert.Null(platform.Backup);
        Assert.Equal(2, platform.Restarts);
    }

    [Fact]
    public void Failed_recovery_retains_backup_and_restore_can_be_retried()
    {
        var platform = new FakePlatform { RestartFailures = 2 };
        var workaround = new NvtoppsWorkaround(platform);
        var original = platform.Setting;
        var error = Assert.Throws<InvalidOperationException>(() => workaround.Apply("621.24"));
        Assert.Contains("backup is retained", error.Message);
        Assert.NotNull(platform.Backup);
        Assert.False(platform.ServiceRunning);
        Assert.True(workaround.Inspect().CanRestore);
        workaround.Restore();
        Assert.True(platform.Setting.SameValue(original));
        Assert.True(platform.ServiceRunning);
        Assert.Null(platform.Backup);
    }

    [Fact]
    public void Restore_failure_does_not_delete_the_original_backup()
    {
        var platform = new FakePlatform();
        var workaround = new NvtoppsWorkaround(platform);
        workaround.Apply(null);
        platform.RestartFailures = 1;
        Assert.Throws<InvalidOperationException>(() => workaround.Restore());
        Assert.NotNull(platform.Backup);
        workaround.Restore();
        Assert.Null(platform.Backup);
    }

    [Fact]
    public void Repeated_apply_cannot_overwrite_the_original_backup()
    {
        var platform = new FakePlatform();
        var workaround = new NvtoppsWorkaround(platform);
        workaround.Apply("621.24");
        var backup = platform.Backup;
        var mutations = platform.Mutations;
        Assert.Throws<InvalidOperationException>(() => workaround.Apply("other"));
        Assert.Equal(backup, platform.Backup);
        Assert.Equal(mutations, platform.Mutations);
    }

    [Fact]
    public void Externally_disabled_plugin_is_not_adopted_or_restored()
    {
        var platform = new FakePlatform { Setting = new(true, true, true, 1) };
        var workaround = new NvtoppsWorkaround(platform);
        Assert.False(workaround.Inspect().CanApply);
        Assert.False(workaround.Inspect().CanRestore);
        Assert.Throws<InvalidOperationException>(() => workaround.Apply(null));
        Assert.Throws<InvalidOperationException>(() => workaround.Restore());
        Assert.Equal(0, platform.Mutations);
    }

    [Fact]
    public void Unsupported_value_type_and_invalid_backup_are_never_modified()
    {
        var platform = new FakePlatform { Setting = new(true, true, false, 0) };
        var workaround = new NvtoppsWorkaround(platform);
        Assert.False(workaround.Inspect().CanApply);
        Assert.Throws<InvalidOperationException>(() => workaround.Apply(null));
        platform.Backup = new(new(false, true, true, 0), true, null);
        Assert.Throws<InvalidOperationException>(() => workaround.Restore());
        Assert.Equal(0, platform.Mutations);
    }

    [Fact]
    public void Outside_changes_are_not_overwritten_by_restore()
    {
        var platform = new FakePlatform();
        var workaround = new NvtoppsWorkaround(platform);
        workaround.Apply(null);
        platform.Setting = new(true, true, true, 9);
        var mutations = platform.Mutations;
        Assert.False(workaround.Inspect().CanRestore);
        Assert.Throws<InvalidOperationException>(() => workaround.Restore());
        Assert.Equal(9, platform.Setting.Value);
        Assert.NotNull(platform.Backup);
        Assert.Equal(mutations, platform.Mutations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Missing_or_stopped_service_prevents_apply(bool exists, bool running)
    {
        var platform = new FakePlatform { ServiceExists = exists, ServiceRunning = running };
        var workaround = new NvtoppsWorkaround(platform);
        Assert.False(workaround.Inspect().CanApply);
        Assert.Throws<InvalidOperationException>(() => workaround.Apply(null));
        Assert.Equal(0, platform.Mutations);
    }

    [Fact]
    public void Configured_state_is_read_from_registry_even_without_backup_or_service()
    {
        var platform = new FakePlatform { Setting = new(true, true, true, 1), ServiceExists = false };
        var workaround = new NvtoppsWorkaround(platform);
        Assert.True(workaround.Inspect().Configured);
        platform.Setting = new(true, true, true, 0);
        Assert.False(workaround.Inspect().Configured);
        platform.Setting = new(true, true, false, 0);
        Assert.Null(workaround.Inspect().Configured);
        Assert.Equal(0, platform.Mutations);
    }

    sealed class FakePlatform : INvtoppsPlatform
    {
        public bool ServiceExists { get; set; } = true;
        public bool ServiceRunning { get; set; } = true;
        public NvtoppsSetting Setting { get; set; } = new(true, false, true, 0);
        public NvtoppsBackup? Backup { get; set; }
        public List<string> Events { get; } = [];
        public int Mutations => Events.Count;
        public bool BackupFails { get; set; }
        public int RestartFailures { get; set; }
        public int Restarts { get; private set; }
        public NvtoppsSetting ReadSetting() => Setting;
        public NvtoppsBackup? ReadBackup() => Backup;
        public void SaveBackup(NvtoppsBackup backup)
        {
            Events.Add("backup");
            if (BackupFails) throw new IOException("Backup write failed.");
            Backup = backup;
        }
        public void ClearBackup() { Events.Add("clear"); Backup = null; }
        public void DisablePlugin() { Events.Add("disable"); Assert.NotNull(Backup); Setting = new(true, true, true, 1); }
        public void RestoreSetting(NvtoppsSetting original) { Events.Add("restore"); Setting = original; }
        public void RestartService()
        {
            Events.Add("restart"); Restarts++;
            if (RestartFailures-- > 0) { ServiceRunning = false; throw new TimeoutException("Service restart failed."); }
            ServiceRunning = true;
        }
    }
}
