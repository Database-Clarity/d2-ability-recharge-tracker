namespace D2AbilityTracker;

internal static class CaptureSchedule
{
    public static long NextFrame(long started, long finished, int targetRate, long frequency)
    {
        var targetInterval = frequency / targetRate;
        var restInterval = Math.Max(frequency / 1000, (finished - started) * 3);
        return Math.Max(started + targetInterval, finished + restInterval);
    }
}
