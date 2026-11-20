using System.Globalization;

namespace NvlogDetector.Core;

/// <summary>
/// Formats byte counts the way Windows does, in English.
/// </summary>
public static class ByteFormat
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static string Bytes(long value)
    {
        var negative = value < 0;
        var magnitude = Math.Abs((double)value);
        string text;
        if (magnitude < 1024)
            text = magnitude.ToString("0", English) + " B";
        else if (magnitude < 1024d * 1024)
            text = (magnitude / 1024).ToString("0.0", English) + " KB";
        else if (magnitude < 1024d * 1024 * 1024)
            text = (magnitude / 1024 / 1024).ToString("0.00", English) + " MB";
        else
            text = (magnitude / 1024 / 1024 / 1024).ToString("0.00", English) + " GB";

        return negative ? "-" + text : text;
    }

    public static string PerSecond(double bytesPerSecond) =>
        Bytes((long)Math.Round(bytesPerSecond)) + "/s";

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes} min {span.Seconds:00} s";
        return $"{(int)span.TotalSeconds} s";
    }

    public static string Time(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("MMM d, yyyy, HH:mm:ss", English);
}
