namespace NvlogDetector.Core;

public sealed record NvtoppsSetting(bool KeyExisted, bool ValueExisted, bool IsDword, int Value)
{
    public bool Supported => !ValueExisted || IsDword;
    public bool Disabled => ValueExisted && IsDword && Value == 1;
    public bool SameValue(NvtoppsSetting other) => ValueExisted == other.ValueExisted &&
        (!ValueExisted || (IsDword == other.IsDword && Value == other.Value));
}

public sealed record NvtoppsBackup(NvtoppsSetting Original, bool ServiceWasRunning,
    string? DriverVersion, int FormatVersion = 1);

public sealed record WorkaroundState(bool CanApply, bool CanRestore, string Message);

/// <summary>Implementations must store the backup durably before changing DisableLoad.</summary>
public interface INvtoppsPlatform
{
    bool ServiceExists { get; }
    bool ServiceRunning { get; }
    NvtoppsSetting ReadSetting();
    NvtoppsBackup? ReadBackup();
    void SaveBackup(NvtoppsBackup backup);
    void ClearBackup();
    void DisablePlugin();
    void RestoreSetting(NvtoppsSetting original);
    void RestartService();
}

/// <summary>Community workaround. No system change is made by constructing or inspecting this class.</summary>
public sealed class NvtoppsWorkaround(INvtoppsPlatform platform)
{
    public WorkaroundState Inspect()
    {
        var backup = platform.ReadBackup();
        if (backup != null) Validate(backup);
        if (!platform.ServiceExists)
            return new(false, false, "NVIDIA Display Container service was not found. No settings changed.");
        var current = platform.ReadSetting();
        if (!current.Supported)
            return new(false, false, "DisableLoad has an unsupported registry type. No settings changed.");
        if (backup != null)
        {
            if (!current.Disabled && !current.SameValue(backup.Original))
                return new(false, false, "The setting changed outside this app. The original backup is retained; automatic restore is blocked.");
            return new(false, true, current.Disabled
                ? "Workaround configured. Original setting saved. Low writes now reflect the disabled plugin, not a repaired driver."
                : "Original setting is saved; restoration may still need to complete.");
        }
        if (current.Disabled)
            return new(false, false, "The plugin is already disabled outside this app. No original backup is available to restore.");
        if (!platform.ServiceRunning)
            return new(false, false, "NVIDIA Display Container is not running. Start it normally before applying this workaround.");
        return new(true, false, "Not applied. Optional community workaround; effectiveness and side effects may vary.");
    }

    public void Apply(string? driverVersion)
    {
        if (platform.ReadBackup() != null)
            throw new InvalidOperationException("An original backup already exists. Restore it before applying again.");
        if (!platform.ServiceExists || !platform.ServiceRunning)
            throw new InvalidOperationException("NVIDIA Display Container must be running before applying the workaround.");
        var original = platform.ReadSetting();
        if (!original.Supported || original.Disabled)
            throw new InvalidOperationException("The setting is unsupported or was already disabled outside this app.");
        var backup = new NvtoppsBackup(original, true, driverVersion);
        platform.SaveBackup(backup);
        try
        {
            if (!platform.ReadSetting().SameValue(original))
                throw new InvalidOperationException("The setting changed while preparing the workaround.");
            platform.DisablePlugin();
            if (!platform.ReadSetting().Disabled)
                throw new InvalidOperationException("The requested registry value could not be verified.");
            platform.RestartService();
        }
        catch (Exception error)
        {
            try
            {
                RestoreOriginal(backup);
                platform.RestartService();
                platform.ClearBackup();
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException("Apply failed and recovery did not complete. The original backup is retained. " +
                    error.Message + " Recovery: " + rollbackError.Message, error);
            }
            throw new InvalidOperationException("Apply failed. The original setting and service were restored. " + error.Message, error);
        }
    }

    public void Restore()
    {
        var backup = platform.ReadBackup() ?? throw new InvalidOperationException("No original backup from this app exists.");
        Validate(backup);
        if (!platform.ServiceExists)
            throw new InvalidOperationException("The NVIDIA service is missing. The backup is retained.");
        try
        {
            RestoreOriginal(backup);
            if (backup.ServiceWasRunning) platform.RestartService();
            platform.ClearBackup();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Restore did not complete. The original backup is retained; resolve the error and retry Restore. " + error.Message, error);
        }
    }

    void RestoreOriginal(NvtoppsBackup backup)
    {
        var current = platform.ReadSetting();
        if (!current.Supported || (!current.Disabled && !current.SameValue(backup.Original)))
            throw new InvalidOperationException("The setting changed outside this app; refusing to overwrite it.");
        platform.RestoreSetting(backup.Original);
        if (!platform.ReadSetting().SameValue(backup.Original))
            throw new InvalidOperationException("The restored value could not be verified.");
    }

    static void Validate(NvtoppsBackup backup)
    {
        if (backup.FormatVersion != 1 || backup.Original == null || !backup.Original.Supported ||
            (backup.Original.ValueExisted && !backup.Original.KeyExisted) || backup.Original.Disabled)
            throw new InvalidOperationException("The saved backup is invalid or unsupported. No settings changed.");
    }
}
