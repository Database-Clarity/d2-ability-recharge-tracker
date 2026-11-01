using System.Globalization;

namespace D2AbilityTracker;

internal static class DurationDisplay
{
    public static string Format(TimeSpan? duration)
    {
        if (duration is not { } value)
        {
            return "--.-- s (--:--.--)";
        }

        var centiseconds = (long)Math.Round(value.TotalSeconds * 100, MidpointRounding.AwayFromZero);
        var seconds = centiseconds / 100m;
        return $"{seconds.ToString("F2", CultureInfo.InvariantCulture)} s ({centiseconds / 6000:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00})";
    }
}
