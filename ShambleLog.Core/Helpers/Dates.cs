namespace ShambleLog.Core.Helpers;

public static class Dates
{
    public static string AgeExpression(this DateTime utcTimestamp) => AgeExpression(utcTimestamp, DateTime.UtcNow);

    public static string AgeExpression(this DateTime from , DateTime to)
    {
        var diff = to - from;
        var duration = diff.Duration();
        var suffix = diff < TimeSpan.Zero ? "from now" : "ago";
        if (duration.TotalSeconds < 60) return $"{(int)duration.TotalSeconds} seconds {suffix}";
        if (duration.TotalMinutes < 60) return $"{(int)duration.TotalMinutes} minutes {suffix}";
        if (duration.TotalHours < 24) return $"{(int)duration.TotalHours} hours {suffix}";
        return $"{(int)duration.TotalDays} days {suffix}";
    }
}
