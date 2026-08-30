using LifeDash.Api.Data;
using LifeDash.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace LifeDash.Api.Services;

/// <summary>
/// Turns raw records into the alert feed and the insight cards.
/// Everything here is pure date arithmetic - no side effects, easy to test.
/// </summary>
public class DeadlineEngine(LifeDashContext db)
{
    public static AlertSeverity Grade(int daysLeft, int reminderDays)
    {
        if (daysLeft < 0) return AlertSeverity.Overdue;
        if (daysLeft <= 7) return AlertSeverity.Urgent;
        if (daysLeft <= Math.Max(reminderDays, 14)) return AlertSeverity.Soon;
        return AlertSeverity.Info;
    }

    private static string Countdown(int d) => d switch
    {
        < -1 => $"{Math.Abs(d)} Tage überfällig",
        -1   => "seit gestern überfällig",
        0    => "heute fällig",
        1    => "morgen fällig",
        _    => $"in {d} Tagen"
    };

    public async Task<DashboardResponse> BuildAsync(int userId, int horizonDays, CancellationToken ct = default)
    {
        // Local time, not UTC: Germany is UTC+1/+2, so UtcNow would report
        // "yesterday" for the first 1-2 hours after local midnight - the same
        // class of bug already fixed on the frontend and avoided in
        // ReminderEmailWorker (which uses DateTime.Now for the same reason).
        var today = DateOnly.FromDateTime(DateTime.Now);
        var horizon = today.AddDays(horizonDays);
        var alerts = new List<Alert>();

        // ---------- 1. documents about to expire ----------
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.UserId == userId && d.ExpiresOn != null && d.ExpiresOn <= horizon)
            .ToListAsync(ct);

        foreach (var d in docs)
        {
            var days = d.ExpiresOn!.Value.DayNumber - today.DayNumber;
            alerts.Add(new Alert(
                $"doc-{d.Id}", MapModule(d.Category), "expiry",
                Grade(days, d.ReminderDays),
                d.Title,
                $"{d.DocumentType ?? "Dokument"} läuft ab — {Countdown(days)}.",
                d.ExpiresOn, days, "Dokument öffnen", $"/documents/{d.Id}", "Document", d.Id));
        }

        // ---------- 2. authority cases: deadlines + next actions ----------
        var cases = await db.AuthorityCases.AsNoTracking()
            .Include(c => c.RequiredDocuments)
            .Where(c => c.UserId == userId && !new[] { "closed", "approved", "rejected" }.Contains(c.Status))
            .ToListAsync(ct);

        foreach (var c in cases)
        {
            if (c.DeadlineOn is { } dl && dl <= horizon)
            {
                var days = dl.DayNumber - today.DayNumber;
                alerts.Add(new Alert(
                    $"case-{c.Id}", "authority", "expiry", Grade(days, c.ReminderDays),
                    c.Title,
                    $"Frist bei {c.Authority ?? "der Behörde"} — {Countdown(days)}.",
                    dl, days, "Vorgang öffnen", $"/authorities/{c.Id}", "AuthorityCase", c.Id));
            }

            if (c.NextActionOn is { } na && na <= horizon)
            {
                var days = na.DayNumber - today.DayNumber;
                alerts.Add(new Alert(
                    $"case-next-{c.Id}", "authority", "task", Grade(days, 14),
                    c.Title,
                    $"Nächster Schritt fällig — {Countdown(days)}.",
                    na, days, "Vorgang öffnen", $"/authorities/{c.Id}", "AuthorityCase", c.Id));
            }

            // ---------- 3. missing mandatory documents ----------
            foreach (var r in c.RequiredDocuments.Where(r => r.IsMandatory && r.DocumentId == null))
            {
                var days = r.DueOn is { } due ? due.DayNumber - today.DayNumber : (int?)null;
                var severity = days is { } dd ? Grade(dd, 14) : AlertSeverity.Soon;
                var tail = days is { } d2 ? $" — {Countdown(d2)}" : "";
                alerts.Add(new Alert(
                    $"reqdoc-{r.Id}", "authority", "missing-document", severity,
                    r.Name,
                    $"Pflichtdokument für „{c.Title}\" fehlt noch{tail}.",
                    r.DueOn, days, "Hochladen", $"/authorities/{c.Id}", "RequiredDocument", r.Id));
            }
        }

        // ---------- 4. unpaid payments ----------
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.UserId == userId && !p.IsPaid && p.DueOn <= horizon)
            .ToListAsync(ct);

        foreach (var p in payments)
        {
            var days = p.DueOn.DayNumber - today.DayNumber;
            alerts.Add(new Alert(
                $"pay-{p.Id}", "finance", "payment", Grade(days, 10),
                p.Title,
                $"{p.Amount:0.00} {p.Currency} — {Countdown(days)}.",
                p.DueOn, days, "Als bezahlt markieren", "/finance", "Payment", p.Id));
        }

        // ---------- 5. subscriptions: cancellation deadline only ----------
        // Subscription.RenewsOn is not a rolling "next renewal" date - it's set
        // to StartOn on every save (see Contracts.tsx) and never advances, so
        // comparing it directly to `today` used to produce a permanently
        // "überfällig" alert for any contract older than a few weeks. Rather
        // than reconstruct a synthetic renewal date, only the real, fixed
        // Kündigungsfrist deadline is surfaced here - that's the one date a
        // contract actually needs to act on, and it's what the reminder
        // emails already cover.
        var subs = await db.Subscriptions.AsNoTracking()
            .Where(s => s.UserId == userId && s.IsActive)
            .ToListAsync(ct);

        foreach (var s in subs)
        {
            if (s.CancelByOn is { } cancel && cancel <= horizon)
            {
                var days = cancel.DayNumber - today.DayNumber;
                var tail = s.FlowType != "none"
                    ? " — danach verlängert sich der Vertrag automatisch."
                    : ".";
                alerts.Add(new Alert(
                    $"sub-cancel-{s.Id}", "finance", "renewal", Grade(days, 21),
                    s.Name,
                    $"Kündigungsfrist endet {Countdown(days)}{tail}",
                    cancel, days, "Vertrag prüfen", "/contracts", "Subscription", s.Id));
            }
        }

        // ---------- 6. warranties running out ----------
        var home = await db.HomeItems.AsNoTracking()
            .Where(h => h.UserId == userId && h.WarrantyUntil != null && h.WarrantyUntil <= horizon)
            .ToListAsync(ct);

        foreach (var h in home)
        {
            var days = h.WarrantyUntil!.Value.DayNumber - today.DayNumber;
            alerts.Add(new Alert(
                $"warranty-{h.Id}", "home", "warranty", Grade(days, 30),
                h.Title,
                $"Garantie endet {Countdown(days)}. Defekte jetzt noch kostenlos reklamieren.",
                h.WarrantyUntil, days, "Im Haushalt öffnen", "/home-items", "HomeItem", h.Id));
        }

        // ---------- 7. appointments ----------
        var appts = await db.Appointments.AsNoTracking()
            .Where(a => a.UserId == userId && !a.IsDone
                        && a.StartsAt <= horizon.ToDateTime(TimeOnly.MaxValue))
            .ToListAsync(ct);

        foreach (var a in appts)
        {
            var date = DateOnly.FromDateTime(a.StartsAt);
            var days = date.DayNumber - today.DayNumber;
            if (days < -1) continue;
            alerts.Add(new Alert(
                $"appt-{a.Id}", MapModule(a.Category), "appointment", Grade(days, a.ReminderDays),
                a.Title,
                $"{a.StartsAt:dd.MM.yyyy HH:mm}{(string.IsNullOrWhiteSpace(a.Location) ? "" : $", {a.Location}")} — {Countdown(days)}.",
                date, days, "Termin öffnen", "/family", "Appointment", a.Id));
        }

        // ---------- 8. important dates (yearly roll-forward) ----------
        var dates = await db.ImportantDates.AsNoTracking()
            .Where(i => i.UserId == userId).ToListAsync(ct);

        foreach (var i in dates)
        {
            var next = NextOccurrence(i.DateValue, i.RepeatsYearly, today);
            if (next is null || next > horizon) continue;
            var days = next.Value.DayNumber - today.DayNumber;
            alerts.Add(new Alert(
                $"date-{i.Id}", "family", "birthday", days <= 7 ? AlertSeverity.Soon : AlertSeverity.Info,
                i.Title, $"{next:dd.MM.yyyy} — {Countdown(days)}.",
                next, days, "Im Familienbereich ansehen", "/family", "ImportantDate", i.Id));
        }

        // ---------- 9. open tasks ----------
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.UserId == userId && !t.IsDone && t.DueOn != null && t.DueOn <= horizon)
            .ToListAsync(ct);

        foreach (var t in tasks)
        {
            var days = t.DueOn!.Value.DayNumber - today.DayNumber;
            alerts.Add(new Alert(
                $"task-{t.Id}", t.Module, "task",
                Grade(days, t.Priority == "high" ? 21 : 7),
                t.Title, $"Aufgabe {Countdown(days)}.",
                t.DueOn, days, "Erledigen", "/tasks", "TaskItem", t.Id));
        }

        // ---------- 10. upcoming trip ----------
        var trips = await db.Trips.AsNoTracking()
            .Include(t => t.PackingItems)
            .Where(t => t.UserId == userId && t.StartsOn >= today)
            .OrderBy(t => t.StartsOn).ToListAsync(ct);

        var nextTrip = trips.FirstOrDefault();
        if (nextTrip is not null && nextTrip.StartsOn <= horizon)
        {
            var days = nextTrip.StartsOn.DayNumber - today.DayNumber;
            var unpacked = nextTrip.PackingItems.Count(p => !p.IsPacked);
            alerts.Add(new Alert(
                $"trip-{nextTrip.Id}", "travel", "trip",
                days <= 14 ? AlertSeverity.Soon : AlertSeverity.Info,
                nextTrip.Title,
                $"Abreise {Countdown(days)}" + (unpacked > 0 ? $", {unpacked} Positionen noch nicht gepackt." : "."),
                nextTrip.StartsOn, days, "Reise öffnen", $"/travel/{nextTrip.Id}", "Trip", nextTrip.Id));
        }

        alerts = alerts
            .OrderByDescending(a => a.Severity)
            .ThenBy(a => a.DaysLeft ?? int.MaxValue)
            .ThenBy(a => a.Title)
            .ToList();

        var insights = await BuildInsightsAsync(userId, today, cases, ct);
        var summary = await BuildSummaryAsync(userId, today, alerts, cases, nextTrip, ct);

        return new DashboardResponse(summary, alerts, insights);
    }

    private async Task<List<Insight>> BuildInsightsAsync(
        int userId, DateOnly today,
        List<Models.AuthorityCase> cases,
        CancellationToken ct)
    {
        var list = new List<Insight>();

        // missing mandatory documents
        var missing = cases.SelectMany(c => c.RequiredDocuments)
                           .Count(r => r.IsMandatory && r.DocumentId == null);
        if (missing > 0)
            list.Add(new Insight("missing-docs", "⚠️",
                $"{missing} Pflichtdokumente fehlen noch in deinen Behördenvorgängen.", "/authorities"));

        // budget check - Einnahmen/Fixkosten only, Verträge are deliberately
        // excluded from all Finanzen-facing math (see Finance.tsx/DeadlineEngine).
        var income = await MonthlyIncomeAsync(userId, ct);
        var costs = await MonthlyFixedCostsAsync(userId, ct);
        var balance = income - costs;
        if (income > 0)
        {
            list.Add(balance < 0
                ? new Insight("budget", "📉",
                    $"Deine Fixkosten übersteigen dein Einkommen um {Math.Abs(balance):0.00} € pro Monat.", "/finance")
                : new Insight("budget", "💰",
                    $"Nach Fixkosten bleiben dir {balance:0.00} € pro Monat.", "/finance"));
        }

        // documents expiring within 90 days
        var soon = await db.Documents.CountAsync(d =>
            d.UserId == userId && d.ExpiresOn != null &&
            d.ExpiresOn >= today && d.ExpiresOn <= today.AddDays(90), ct);
        if (soon > 0)
            list.Add(new Insight("docs-90", "🗂️",
                $"{soon} Dokumente laufen in den nächsten 90 Tagen ab.", "/documents"));

        return list;
    }

    private async Task<DashboardSummary> BuildSummaryAsync(
        int userId, DateOnly today, List<Alert> alerts,
        List<Models.AuthorityCase> cases,
        Models.Trip? nextTrip, CancellationToken ct)
    {
        // Einnahmen/Fixkosten only - Verträge are deliberately excluded from
        // all Finanzen-facing math (see Finance.tsx/DeadlineEngine).
        var income = await MonthlyIncomeAsync(userId, ct);
        var costs = await MonthlyFixedCostsAsync(userId, ct);
        var openTasks = await db.Tasks.CountAsync(t => t.UserId == userId && !t.IsDone, ct);
        var missing = cases.SelectMany(c => c.RequiredDocuments)
                           .Count(r => r.IsMandatory && r.DocumentId == null);

        return new DashboardSummary(
            alerts.Count,
            alerts.Count(a => a.Severity == AlertSeverity.Overdue),
            alerts.Count(a => a.Severity == AlertSeverity.Urgent),
            alerts.Count(a => a.Severity == AlertSeverity.Soon),
            Math.Round(income, 2),
            Math.Round(costs, 2),
            Math.Round(income - costs, 2),
            openTasks,
            missing,
            nextTrip?.Title,
            nextTrip is null ? null : nextTrip.StartsOn.DayNumber - today.DayNumber);
    }

    private async Task<decimal> MonthlyIncomeAsync(int userId, CancellationToken ct)
    {
        var rows = await db.Incomes.AsNoTracking()
            .Where(i => i.UserId == userId && i.IsActive).ToListAsync(ct);
        return rows.Sum(i => i.Cadence switch
        {
            "yearly" => i.Amount / 12m,
            "quarterly" => i.Amount / 3m,
            "onetime" => 0m,
            _ => i.Amount
        });
    }

    private async Task<decimal> MonthlyFixedCostsAsync(int userId, CancellationToken ct)
    {
        var rows = await db.FixedCosts.AsNoTracking()
            .Where(f => f.UserId == userId && f.IsActive).ToListAsync(ct);
        return rows.Sum(f => f.Cadence switch
        {
            "yearly" => f.Amount / 12m,
            "quarterly" => f.Amount / 3m,
            "onetime" => 0m,
            _ => f.Amount
        });
    }

    private static DateOnly? NextOccurrence(DateOnly value, bool yearly, DateOnly today) =>
        DateOccurrence.NextYearlyOrOnce(value, yearly, today);

    private static string MapModule(string category) => category switch
    {
        "authority" or "tax" or "insurance" => "authority",
        "home" => "home",
        "travel" => "travel",
        "school" or "health" or "family" => "family",
        _ => "family"
    };
}
