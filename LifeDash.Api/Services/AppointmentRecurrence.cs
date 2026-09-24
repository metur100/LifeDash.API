using LifeDash.Api.Models;

namespace LifeDash.Api.Services;

/// <summary>
/// Occurrence math for repeating appointments, shared by the dashboard alert
/// feed and the reminder email worker. Appointment.StartsAt is the series
/// anchor; occurrence n is always computed from the anchor (not from the
/// previous occurrence), so a monthly series on the 31st doesn't get dragged
/// down to the 28th after February. Mirrors the UI's lib/recurrence.ts.
/// </summary>
public static class AppointmentRecurrence
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        { "daily", "weekly", "biweekly", "monthly", "yearly" };

    // Guard against runaway loops, e.g. a daily series anchored decades ago.
    private const int MaxSteps = 20000;

    /// <summary>Maps client input onto a stored value; anything unknown/"none" means one-off (null).</summary>
    public static string? Normalize(string? recurrence)
    {
        var key = recurrence?.Trim().ToLowerInvariant();
        return key is not null && Known.Contains(key) ? key : null;
    }

    public static bool IsRecurring(Appointment a) => Normalize(a.Recurrence) is not null;

    private static DateTime Nth(DateTime anchor, string recurrence, int n) => recurrence switch
    {
        "daily" => anchor.AddDays(n),
        "weekly" => anchor.AddDays(7 * n),
        "biweekly" => anchor.AddDays(14 * n),
        "monthly" => anchor.AddMonths(n),
        "yearly" => anchor.AddYears(n),
        _ => anchor,
    };

    private static bool WithinUntil(Appointment a, DateTime occurrence) =>
        a.RecurrenceUntil is not { } until || DateOnly.FromDateTime(occurrence) <= until;

    /// <summary>
    /// First occurrence starting at or after `from`, or null once the series
    /// has ended. A one-off appointment just returns its own StartsAt if it
    /// hasn't passed yet.
    /// </summary>
    public static DateTime? NextOccurrence(Appointment a, DateTime from)
    {
        var rule = Normalize(a.Recurrence);
        if (rule is null) return a.StartsAt >= from ? a.StartsAt : null;

        // Jump close to `from` for the fixed-length rules instead of stepping day by day.
        var n = 0;
        var stepDays = rule switch { "daily" => 1, "weekly" => 7, "biweekly" => 14, _ => 0 };
        if (stepDays > 0 && from > a.StartsAt)
            n = Math.Max(0, (int)((from - a.StartsAt).TotalDays / stepDays) - 1);

        for (var guard = 0; guard < MaxSteps; guard++, n++)
        {
            var occ = Nth(a.StartsAt, rule, n);
            if (!WithinUntil(a, occ)) return null;
            if (occ >= from) return occ;
        }
        return null;
    }

    /// <summary>The occurrence falling on `date`, if the series has one that day.</summary>
    public static DateTime? OccurrenceOn(Appointment a, DateOnly date)
    {
        var next = NextOccurrence(a, date.ToDateTime(TimeOnly.MinValue));
        return next is { } occ && DateOnly.FromDateTime(occ) == date ? occ : null;
    }
}
