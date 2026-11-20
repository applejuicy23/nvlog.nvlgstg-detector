namespace NvlogDetector.Core;

public static class NvlogPath
{
    public const string FileName = "nvlog.nvlgstg";

    public static string ProgramDataRoot => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static string DefaultPath => Path.Combine(ProgramDataRoot, @"NVIDIA Corporation\nvtopps\nct\nvlog.nvlgstg");

    public static bool IsMatch(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Replace('/', '\\').Trim();
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
            normalized = normalized[4..];
        if (normalized.StartsWith(@"\??\", StringComparison.Ordinal))
            normalized = normalized[4..];

        var separator = normalized.LastIndexOf('\\');
        var name = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        if (!name.Equals(FileName, StringComparison.OrdinalIgnoreCase))
            return false;

        return new[] { "NVIDIA Corporation", "NVIDIA" }.Any(folder =>
            normalized.StartsWith(Path.Combine(ProgramDataRoot, folder) + "\\", StringComparison.OrdinalIgnoreCase)
            || (normalized.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains(@"\ProgramData\" + folder + "\\", StringComparison.OrdinalIgnoreCase)));
    }
}
