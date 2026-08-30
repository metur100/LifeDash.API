namespace LifeDash.Api.Services;

/// <summary>
/// Shared "next occurrence of a date" math, used by the dashboard alert feed
/// and the reminder email worker so both agree on when a yearly date (e.g. a
/// birthday) or a monthly cadence (e.g. income/subscription) next falls due.
/// </summary>
public static class DateOccurrence
{
    public static DateOnly? NextYearlyOrOnce(DateOnly value, bool yearly, DateOnly today)
    {
        if (!yearly) return value >= today ? value : null;
        var day = Math.Min(value.Day, DateTime.DaysInMonth(today.Year, value.Month));
        var candidate = new DateOnly(today.Year, value.Month, day);
        if (candidate < today)
        {
            day = Math.Min(value.Day, DateTime.DaysInMonth(today.Year + 1, value.Month));
            candidate = new DateOnly(today.Year + 1, value.Month, day);
        }
        return candidate;
    }

    /// <summary>Next monthly occurrence for a given day-of-month, rolling into next month if needed.</summary>
    public static DateOnly? NextMonthly(int? dayOfMonth, DateOnly today)
    {
        if (dayOfMonth is not { } dom || dom < 1) return null;

        var day = Math.Min(dom, DateTime.DaysInMonth(today.Year, today.Month));
        var candidate = new DateOnly(today.Year, today.Month, day);
        if (candidate < today)
        {
            var nextMonth = today.AddMonths(1);
            day = Math.Min(dom, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
            candidate = new DateOnly(nextMonth.Year, nextMonth.Month, day);
        }
        return candidate;
    }
}
