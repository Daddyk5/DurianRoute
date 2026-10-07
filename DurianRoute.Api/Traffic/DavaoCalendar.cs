namespace DurianRoute.Api.Traffic;

/// <summary>Philippine time (UTC+8, no DST) and the calendar effects that drive traffic demand.</summary>
public static class DavaoCalendar
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    // Fixed-date national holidays plus Kadayawan Festival week (approximated as Aug 15–21).
    private static readonly HashSet<(int Month, int Day)> FixedHolidays =
    [
        (1, 1), (2, 25), (4, 9), (5, 1), (6, 12), (8, 21), (11, 1), (11, 2), (11, 30),
        (12, 8), (12, 24), (12, 25), (12, 30), (12, 31)
    ];

    public static DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) + Offset;

    public static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local - Offset, DateTimeKind.Utc);

    public static DateTime CurrentHourUtc()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
    }

    public static bool IsHoliday(DateTime local) =>
        FixedHolidays.Contains((local.Month, local.Day)) || IsKadayawan(local);

    public static bool IsKadayawan(DateTime local) => local.Month == 8 && local.Day is >= 15 and <= 21;

    public static bool IsPayday(DateTime local) =>
        local.Day == 15 || local.Day == DateTime.DaysInMonth(local.Year, local.Month);

    /// <summary>School days: weekdays outside April–May break and holidays.</summary>
    public static bool IsSchoolDay(DateTime local) =>
        local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && local.Month is not (4 or 5)
        && !IsHoliday(local);
}
