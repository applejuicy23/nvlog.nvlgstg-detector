namespace NvlogDetector.Core;

public static class NvidiaVersion
{
    /// <summary>
    /// Windows stores the GeForce package as 32.0.16.2124 when the NVIDIA app shows 621.24.
    /// The last digit of the third component is prepended to the build, then split before the last two digits.
    /// </summary>
    public static string? FromWindowsDriver(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion))
            return null;

        var parts = windowsVersion.Split('.');
        if (parts.Length != 4)
            return null;
        if (!int.TryParse(parts[2], out var branch) || !int.TryParse(parts[3], out var build))
            return null;
        if (branch < 0 || build is < 0 or > 9999)
            return null;

        var packed = (branch % 10) * 10000 + build;
        return $"{packed / 100}.{packed % 100:00}";
    }
}
