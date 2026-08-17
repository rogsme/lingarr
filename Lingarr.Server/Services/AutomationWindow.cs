namespace Lingarr.Server.Services;

/// <summary>
/// Pure time-window math for window-mode automation: determines whether the configured
/// daily window (start/end in a given IANA timezone) is currently open, and how long
/// until it next opens. Invalid configuration fails open so automation never wedges shut.
/// </summary>
public static class AutomationWindow
{
    /// <summary>
    /// Determines whether the automation window is currently open.
    /// A window with equal start and end times is considered always open.
    /// </summary>
    /// <param name="start">Window start time in "HH:mm" format.</param>
    /// <param name="end">Window end time in "HH:mm" format (exclusive).</param>
    /// <param name="timeZoneId">IANA timezone identifier the window is expressed in.</param>
    /// <param name="utcNow">Current UTC time; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public static bool IsOpenNow(string start, string end, string timeZoneId, DateTime? utcNow = null)
    {
        if (!TryParse(start, end, timeZoneId, out var startTime, out var endTime, out var timeZone))
        {
            return true;
        }

        if (startTime == endTime)
        {
            return true;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow ?? DateTime.UtcNow, timeZone);
        return TimeOnly.FromDateTime(local).IsBetween(startTime, endTime);
    }

    /// <summary>
    /// Calculates the delay until the window next opens (minimum one minute).
    /// </summary>
    /// <param name="start">Window start time in "HH:mm" format.</param>
    /// <param name="end">Window end time in "HH:mm" format.</param>
    /// <param name="timeZoneId">IANA timezone identifier the window is expressed in.</param>
    /// <param name="utcNow">Current UTC time; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public static TimeSpan UntilNextOpen(string start, string end, string timeZoneId, DateTime? utcNow = null)
    {
        if (!TryParse(start, end, timeZoneId, out var startTime, out _, out var timeZone))
        {
            return TimeSpan.FromMinutes(1);
        }

        var now = utcNow ?? DateTime.UtcNow;
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, timeZone);
        var candidate = local.Date.Add(startTime.ToTimeSpan());
        if (candidate <= local)
        {
            candidate = candidate.AddDays(1);
        }

        // DST spring-forward can make the start time nonexistent for one day
        if (timeZone.IsInvalidTime(candidate))
        {
            candidate = candidate.AddHours(1);
        }

        var delay = TimeZoneInfo.ConvertTimeToUtc(candidate, timeZone) - now;
        return delay < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay;
    }

    private static bool TryParse(
        string start,
        string end,
        string timeZoneId,
        out TimeOnly startTime,
        out TimeOnly endTime,
        out TimeZoneInfo timeZone)
    {
        startTime = default;
        endTime = default;
        timeZone = TimeZoneInfo.Utc;

        if (!TimeOnly.TryParse(start, out startTime) || !TimeOnly.TryParse(end, out endTime))
        {
            return false;
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return false;
        }

        return true;
    }
}
