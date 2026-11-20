using System.Diagnostics;
using Microsoft.Win32;
using NvlogDetector.Core;

namespace NvlogDetector;

static class NvidiaInfo
{
    public static int[] ContainerPids()
    {
        try
        {
            return Process.GetProcessesByName("NVDisplay.Container")
                .Select(process =>
                {
                    try
                    {
                        return process.Id;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                })
                .Distinct()
                .Order()
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    public static string? WindowsDriverVersion()
    {
        const string adapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(adapters);
            if (root is null)
                return null;

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var description = key?.GetValue("DriverDesc") as string;
                var version = key?.GetValue("DriverVersion") as string;
                if (!string.IsNullOrWhiteSpace(version)
                    && description?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true)
                    return version;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            // Restricted hosts may deny display-adapter registry reads. Monitoring can still start.
            return null;
        }

        return null;
    }
}

static class NvlogFiles
{
    public static string[] Discover()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(NvlogPath.DefaultPath))
            found.Add(Path.GetFullPath(NvlogPath.DefaultPath));

        foreach (var root in new[] { Path.Combine(NvlogPath.ProgramDataRoot, "NVIDIA Corporation"), Path.Combine(NvlogPath.ProgramDataRoot, "NVIDIA") })
        {
            if (!Directory.Exists(root))
                continue;

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, NvlogPath.FileName, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }))
                {
                    if (NvlogPath.IsMatch(path))
                        found.Add(path);
                }
            }
            catch (IOException)
            {
                // Skip a root we cannot walk.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip a root we cannot walk.
            }
        }

        return found.ToArray();
    }
}
