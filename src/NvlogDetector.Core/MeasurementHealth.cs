namespace NvlogDetector.Core;

public static class MeasurementHealth
{
    public static string? Problem(bool running, bool hasProcesses, bool fileFound, bool attributionVerified,
        long lostEvents, long lateEvents, string? handleError, long incompleteWrites = 0)
    {
        if (!running) return "The write monitor is not running; absence of writes has not been established.";
        if (lostEvents > 0) return "Windows dropped trace events. Recorded volume is incomplete; restart monitoring for a fresh check.";
        if (lateEvents > 0) return "Some trace events arrived after their seconds were displayed. Volume is retained, but timing is uncertain; restart for a fresh check.";
        if (incompleteWrites > 0) return "Some write attempts have no reliable completion. Recorded volume may be incomplete; restart for a fresh check.";
        if (!hasProcesses || !fileFound) return null;
        if (handleError != null) return "File attribution may be incomplete: " + handleError;
        if (!attributionVerified) return "No file identity has been verified yet. Zero observed writes does not rule out activity.";
        return null;
    }
}
