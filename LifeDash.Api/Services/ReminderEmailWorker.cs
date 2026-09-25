using System.Net;
using System.Net.Mail;
using LifeDash.Api.Data;
using LifeDash.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LifeDash.Api.Services;

public class ReminderEmailWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReminderEmailOptions _options;
    private readonly ILogger<ReminderEmailWorker> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    // Every reminder except the time-based flight and appointment reminders only actually sends once
    // local time reaches this hour - otherwise a date match right after
    // midnight would go out at 00:05 instead of a predictable time each day.
    // Time-based flight and appointment reminders are the exception: waiting
    // until 08:00 could mean sending them after the event already happened.
    private const int SendHour = 8;

    // "1 day before" markers
    private const string AppointmentSentTag = "[appt-reminder-sent]";
    private const string FlightSentTag = "[flight-checkin-reminder-sent]";
    private const string PaymentSentTag = "[payment-reminder-sent]";
    private const string FixedCostSentTag = "[fixedcost-reminder-sent]";
    private const string IncomeSentTag = "[income-reminder-sent]";
    private const string ContractCancelSentTag = "[contract-cancel-reminder-sent]";
    private const string AuthorityCaseSentTag = "[authority-case-reminder-sent]";

    // "on the due day itself" markers - Wichtige Daten (birthdays) and
    // Aufgaben already only ever send on the day, so they have no separate
    // "day before" tag/pass.
    private const string BirthdaySentTag = "[birthday-reminder-sent]";
    private const string TaskSentTag = "[task-due-today-sent]";
    private const string AppointmentOneHourSentTag = "[appt-one-hour-reminder-sent]";
    private const string FlightDueTodaySentTag = "[flight-due-today-sent]";
    private const string PaymentDueTodaySentTag = "[payment-due-today-sent]";
    private const string FixedCostDueTodaySentTag = "[fixedcost-due-today-sent]";
    private const string IncomeDueTodaySentTag = "[income-due-today-sent]";
    private const string ContractCancelDueTodaySentTag = "[contract-cancel-due-today-sent]";
    private const string AuthorityCaseDueTodaySentTag = "[authority-case-due-today-sent]";

    private static readonly string[] ClosedAuthorityStatuses = { "closed", "approved", "rejected" };

    // These mirror the (English-valued) select options the UI shows as German
    // labels - e.g. Family.tsx's APPOINTMENT_CATEGORIES. The stored value is
    // the English code; reminder emails need the German label, not the raw code.
    private static readonly Dictionary<string, string> AppointmentCategoryLabels = new()
    {
        ["family"] = "Familie", ["birthday"] = "Geburtstag", ["anniversary"] = "Jahrestag",
        ["authority"] = "Behörde", ["health"] = "Gesundheit", ["school"] = "Schule",
        ["work"] = "Arbeit", ["finance"] = "Finanzen", ["travel"] = "Reise",
        ["home"] = "Haushalt", ["other"] = "Sonstiges",
    };

    private static readonly Dictionary<string, string> CadenceLabels = new()
    {
        ["monthly"] = "monatlich", ["quarterly"] = "quartalsweise",
        ["yearly"] = "jährlich", ["onetime"] = "einmalig",
    };

    private static readonly Dictionary<string, string> TaskPriorityLabels = new()
    {
        ["low"] = "niedrig", ["normal"] = "normal", ["high"] = "hoch",
    };

    private static readonly Dictionary<string, string> TaskModuleLabels = new()
    {
        ["general"] = "Allgemein", ["family"] = "Familie", ["authority"] = "Behörden",
        ["finance"] = "Finanzen", ["home"] = "Allgemein", ["travel"] = "Reisen",
    };

    private static readonly Dictionary<string, string> AuthorityStatusLabels = new()
    {
        ["open"] = "Offen", ["waiting"] = "Wartend", ["submitted"] = "Eingereicht",
        ["approved"] = "Bewilligt", ["rejected"] = "Abgelehnt", ["closed"] = "Abgeschlossen",
    };

    private static readonly Dictionary<string, string> CostCategoryLabels = new()
    {
        ["miete"] = "Miete", ["nebenkosten"] = "Nebenkosten", ["strom"] = "Strom",
        ["dsl"] = "DSL/Internet", ["abo"] = "Abo", ["vertrag"] = "Vertrag",
        ["versicherung"] = "Versicherung", ["steuer"] = "Steuer", ["auto"] = "Auto",
        ["gesundheit"] = "Gesundheit", ["lebensmittel"] = "Lebensmittel",
        ["online-kaeufe"] = "Online-Käufe", ["sonstiges"] = "Sonstiges",
    };

    private static string Label(Dictionary<string, string> map, string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : map.GetValueOrDefault(value, value);

    public ReminderEmailWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ReminderEmailOptions> options,
        ILogger<ReminderEmailWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reminder worker run failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    public async Task<ReminderRunResult> RunOnceAsync(CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct))
        {
            _logger.LogInformation("Reminder run skipped because another run is still in progress.");
            return ReminderRunResult.AlreadyRunning;
        }

        try
        {
            await SendDueRemindersAsync(ct);
            return ReminderRunResult.Completed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reminder worker run failed");
            return ReminderRunResult.Failed;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task SendDueRemindersAsync(CancellationToken ct)
    {
        if (!IsConfigured())
        {
            _logger.LogWarning("Reminder email is disabled because SMTP or recipient settings are incomplete.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LifeDashContext>();

        var now = GetLocalNow();
        var today = DateOnly.FromDateTime(now.Date);
        var afterSendHour = now.Hour >= SendHour;

        var memberNames = await db.FamilyMembers.AsNoTracking()
            .ToDictionaryAsync(m => m.Id, m => m.FullName, ct);

        await SendAppointmentRemindersAsync(db, memberNames, now, today, afterSendHour, ct);
        await SendFlightRemindersAsync(db, now, today, ct);
        if (afterSendHour)
        {
            await SendBirthdayRemindersAsync(db, memberNames, today, ct);
            await SendTaskRemindersAsync(db, today, ct);
            await SendPaymentRemindersAsync(db, today, ct);
            await SendFixedCostRemindersAsync(db, today, ct);
            await SendIncomeRemindersAsync(db, today, ct);
            await SendContractCancelRemindersAsync(db, memberNames, today, ct);
            await SendAuthorityCaseRemindersAsync(db, memberNames, today, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SendAppointmentRemindersAsync(LifeDashContext db, Dictionary<int, string> memberNames, DateTime now, DateOnly today, bool afterSendHour, CancellationToken ct)
    {
        var tomorrow = today.AddDays(1);
        var appointments = await db.Appointments
            .Include(a => a.Attendees)
            .Where(a => !a.IsDone)
            .ToListAsync(ct);

        foreach (var a in appointments)
        {
            var recurring = AppointmentRecurrence.IsRecurring(a);
            var attendees = a.Attendees.Select(x => memberNames.GetValueOrDefault(x.FamilyMemberId, "-")).ToList();
            (string, string)[] RowsFor(DateTime startsAt) => new[]
            {
                ("Datum & Uhrzeit", startsAt.ToString("dd.MM.yyyy HH:mm")),
                ("Ort", a.Location ?? ""),
                ("Kategorie", Label(AppointmentCategoryLabels, a.Category)),
                ("Teilnehmer", attendees.Count > 0 ? string.Join(", ", attendees) : ""),
            };

            // A series sends one pair of markers per occurrence; drop the ones for past
            // occurrences so Notes (max 2000 chars) doesn't fill up over the months.
            if (recurring)
            {
                var pruned = PruneAppointmentMarkers(a.Notes, today);
                if (pruned != a.Notes) a.Notes = pruned;
            }

            var tomorrowOccurrence = recurring
                ? AppointmentRecurrence.OccurrenceOn(a, tomorrow)
                : DateOnly.FromDateTime(a.StartsAt) == tomorrow ? a.StartsAt : null;
            if (afterSendHour && tomorrowOccurrence is { } dayBefore)
            {
                var body = EmailTemplate.Render(
                    "#4f6df5", "📅", "Termin", a.Title, "Morgen fällig",
                    "Dieser Termin steht morgen an.", RowsFor(dayBefore),
                    AppUrl("/termine"), "Termin ansehen",
                    "LifeDash erinnert dich automatisch einen Tag vor jedem Termin.");
                await SendReminderAsync(a.Notes, MarkerFor(AppointmentSentTag, tomorrow),
                    $"Erinnerung morgen: {a.Title}", body, ct, n => a.Notes = n);
            }

            var startsAt = recurring ? AppointmentRecurrence.NextOccurrence(a, now) : a.StartsAt;
            if (startsAt is not { } next) continue;
            var timeUntilAppointment = next - now;
            if (timeUntilAppointment > TimeSpan.Zero && timeUntilAppointment <= TimeSpan.FromHours(1))
            {
                var body = EmailTemplate.Render(
                    "#4f6df5", "📅", "Termin", a.Title, "In einer Stunde",
                    "Dieser Termin beginnt in weniger als einer Stunde.", RowsFor(next),
                    AppUrl("/termine"), "Termin ansehen",
                    "LifeDash erinnert dich automatisch ungefähr eine Stunde vor dem Termin.");
                await SendReminderAsync(a.Notes, MarkerFor(AppointmentOneHourSentTag, next),
                    $"Termin in einer Stunde: {a.Title}", body, ct, n => a.Notes = n);
            }
        }
    }

    private async Task SendFlightRemindersAsync(LifeDashContext db, DateTime now, DateOnly today, CancellationToken ct)
    {
        var flights = await db.Bookings
            .Include(b => b.Trip)
            .Where(b => b.Kind == "flight" && b.StartsAt != null)
            .ToListAsync(ct);

        foreach (var b in flights)
        {
            if (b.StartsAt is not { } startsAt) continue;
            var eventDate = DateOnly.FromDateTime(startsAt);
            var rows = new[]
            {
                ("Reise", b.Trip?.Title ?? ""),
                ("Abflug", startsAt.ToString("dd.MM.yyyy HH:mm")),
                ("Referenznummer", b.ReferenceNo ?? ""),
                ("Betrag", b.Amount is { } amt ? $"{amt:0.00} {b.Currency}" : ""),
            };

            var hoursUntilDeparture = startsAt - now;
            if (hoursUntilDeparture > TimeSpan.Zero && hoursUntilDeparture <= TimeSpan.FromHours(24))
            {
                var body = EmailTemplate.Render(
                    "#0ea5a3", "✈️", "Reise · Check-in", b.Title, "In 24 Stunden",
                    "Dieser Flug startet in weniger als 24 Stunden.", rows,
                    AppUrl($"/travel/{b.TripId}"), "Reise ansehen",
                    "LifeDash erinnert dich automatisch ungefähr 24 Stunden vor Abflug.");
                await SendReminderAsync(b.Notes, MarkerFor(FlightSentTag, startsAt),
                    $"Flug in 24 Stunden: {b.Title}", body, ct, n => b.Notes = n);
            }
        }
    }

    private async Task SendBirthdayRemindersAsync(LifeDashContext db, Dictionary<int, string> memberNames, DateOnly today, CancellationToken ct)
    {
        var dates = await db.ImportantDates.ToListAsync(ct);

        foreach (var i in dates)
        {
            var occurrence = DateOccurrence.NextYearlyOrOnce(i.DateValue, i.RepeatsYearly, today);
            if (occurrence != today) continue;

            var person = i.FamilyMemberId is { } mid ? memberNames.GetValueOrDefault(mid, "") : "";
            var body = EmailTemplate.Render(
                "#ec4899", "🎂", "Wichtiger Tag", i.Title, "Heute",
                "Dieser Tag ist heute.",
                new[]
                {
                    ("Datum", occurrence.Value.ToString("dd.MM.yyyy")),
                    ("Person", person),
                },
                AppUrl("/termine"), "In den Terminen ansehen",
                "LifeDash erinnert dich automatisch am Tag selbst an wichtigen Anlässen.");

            await SendReminderAsync(i.Notes, MarkerFor(BirthdaySentTag, occurrence.Value),
                $"Heute: {i.Title}", body, ct, n => i.Notes = n);
        }
    }

    private async Task SendTaskRemindersAsync(LifeDashContext db, DateOnly today, CancellationToken ct)
    {
        var tasks = await db.Tasks
            .Where(t => !t.IsDone && t.DueOn == today)
            .ToListAsync(ct);

        foreach (var t in tasks)
        {
            var body = EmailTemplate.Render(
                "#0891b2", "✅", "Aufgabe", t.Title, "Heute fällig",
                "Diese Aufgabe ist heute fällig.",
                new[]
                {
                    ("Fällig am", t.DueOn!.Value.ToString("dd.MM.yyyy")),
                    ("Priorität", Label(TaskPriorityLabels, t.Priority)),
                    ("Bereich", Label(TaskModuleLabels, t.Module)),
                },
                AppUrl("/tasks"), "Aufgabe ansehen",
                "LifeDash erinnert dich automatisch am Fälligkeitstag jeder Aufgabe.");

            await SendReminderAsync(t.Notes, MarkerFor(TaskSentTag, t.DueOn.Value),
                $"Heute fällig: {t.Title}", body, ct, n => t.Notes = n);
        }
    }

    private async Task SendPaymentRemindersAsync(LifeDashContext db, DateOnly today, CancellationToken ct)
    {
        var tomorrow = today.AddDays(1);
        var payments = await db.Payments
            .Where(p => !p.IsPaid && (p.DueOn == tomorrow || p.DueOn == today))
            .ToListAsync(ct);

        foreach (var p in payments)
        {
            var rows = new[]
            {
                ("Betrag", $"{p.Amount:0.00} {p.Currency}"),
                ("Fällig am", p.DueOn.ToString("dd.MM.yyyy")),
                ("Kategorie", Label(CostCategoryLabels, p.Category)),
            };

            if (p.DueOn == tomorrow)
            {
                var body = EmailTemplate.Render(
                    "#ef4444", "💳", "Finanzen · Zahlung", p.Title, "Morgen fällig",
                    "Diese Zahlung ist morgen fällig.", rows,
                    AppUrl("/finance"), "In Finanzen öffnen",
                    "LifeDash erinnert dich automatisch einen Tag vor jeder fälligen Zahlung.");
                await SendReminderAsync(p.Notes, MarkerFor(PaymentSentTag, p.DueOn),
                    $"Zahlung morgen fällig: {p.Title}", body, ct, n => p.Notes = n);
            }

            if (p.DueOn == today)
            {
                var body = EmailTemplate.Render(
                    "#ef4444", "💳", "Finanzen · Zahlung", p.Title, "Heute fällig",
                    "Diese Zahlung ist heute fällig.", rows,
                    AppUrl("/finance"), "In Finanzen öffnen",
                    "LifeDash erinnert dich automatisch am Fälligkeitstag jeder Zahlung.");
                await SendReminderAsync(p.Notes, MarkerFor(PaymentDueTodaySentTag, p.DueOn),
                    $"Zahlung heute fällig: {p.Title}", body, ct, n => p.Notes = n);
            }
        }
    }

    private async Task SendFixedCostRemindersAsync(LifeDashContext db, DateOnly today, CancellationToken ct)
    {
        var tomorrow = today.AddDays(1);
        var costs = await db.FixedCosts
            .Where(c => c.IsActive && c.DayOfMonth != null)
            .ToListAsync(ct);

        foreach (var c in costs)
        {
            var (billingDate, isVariable) = ParseFixedCostMeta(c.Notes);
            if (isVariable) continue; // no predictable due date to remind about

            var anchor = billingDate ?? DateOccurrence.NextMonthly(c.DayOfMonth, today);
            if (anchor is null) continue;
            var nextDue = DateOccurrence.NextFromAnchor(anchor.Value, c.Cadence, today);
            if (nextDue != tomorrow && nextDue != today) continue;

            // Once a projected occurrence has been marked paid once, the app
            // materializes a real Payment row for the next one (so "paid" can
            // be tracked) - the underlying FixedCost still projects that same
            // date too. If a matching unpaid Payment already exists for this
            // exact occurrence, SendPaymentRemindersAsync will handle it -
            // skip here so it's one email, not two.
            var duplicatePayment = await db.Payments.AnyAsync(p =>
                !p.IsPaid && p.DueOn == nextDue && p.Title == c.Name &&
                (p.Category ?? "") == (c.Category ?? "") &&
                Math.Abs(p.Amount - c.Amount) < 0.005m, ct);
            if (duplicatePayment) continue;

            var rows = new[]
            {
                ("Betrag", $"{c.Amount:0.00} {c.Currency}"),
                ("Fällig am", nextDue.ToString("dd.MM.yyyy")),
                ("Turnus", Label(CadenceLabels, c.Cadence)),
                ("Kategorie", Label(CostCategoryLabels, c.Category)),
            };

            if (nextDue == tomorrow)
            {
                var body = EmailTemplate.Render(
                    "#ef4444", "💳", "Finanzen · Fixkosten", c.Name, "Morgen fällig",
                    "Diese wiederkehrenden Kosten sind morgen fällig.", rows,
                    AppUrl("/finance"), "In Finanzen öffnen",
                    "LifeDash erinnert dich automatisch einen Tag vor jeder fälligen Fixkosten-Zahlung.");
                await SendReminderAsync(c.Notes, MarkerFor(FixedCostSentTag, nextDue),
                    $"Fixkosten morgen fällig: {c.Name}", body, ct, n => c.Notes = n);
            }

            if (nextDue == today)
            {
                var body = EmailTemplate.Render(
                    "#ef4444", "💳", "Finanzen · Fixkosten", c.Name, "Heute fällig",
                    "Diese wiederkehrenden Kosten sind heute fällig.", rows,
                    AppUrl("/finance"), "In Finanzen öffnen",
                    "LifeDash erinnert dich automatisch am Fälligkeitstag jeder Fixkosten-Zahlung.");
                await SendReminderAsync(c.Notes, MarkerFor(FixedCostDueTodaySentTag, nextDue),
                    $"Fixkosten heute fällig: {c.Name}", body, ct, n => c.Notes = n);
            }
        }
    }

    private static (DateOnly? BillingDate, bool IsVariable) ParseFixedCostMeta(string? notes)
    {
        const string startTag = "[finance-meta]";
        const string endTag = "[/finance-meta]";
        if (string.IsNullOrWhiteSpace(notes)) return (null, false);

        var start = notes.IndexOf(startTag, StringComparison.Ordinal);
        var end = notes.IndexOf(endTag, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end <= start) return (null, false);

        var block = notes[(start + startTag.Length)..end];
        DateOnly? billingDate = null;
        var isVariable = false;

        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.Trim();
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key == "billingDate" && DateOnly.TryParse(value, out var d)) billingDate = d;
            if (key == "costType" && value == "variable") isVariable = true;
        }

        return (billingDate, isVariable);
    }

    private async Task SendIncomeRemindersAsync(LifeDashContext db, DateOnly today, CancellationToken ct)
    {
        var tomorrow = today.AddDays(1);
        var incomes = await db.Incomes
            .Where(i => i.IsActive && i.Cadence == "monthly" && i.DayOfMonth != null)
            .ToListAsync(ct);

        foreach (var i in incomes)
        {
            var next = DateOccurrence.NextMonthly(i.DayOfMonth, today);
            if (next != tomorrow && next != today) continue;

            var rows = new[]
            {
                ("Betrag", $"{i.Amount:0.00} {i.Currency}"),
                ("Erwartet am", next.Value.ToString("dd.MM.yyyy")),
                ("Turnus", Label(CadenceLabels, i.Cadence)),
            };

            if (next == tomorrow)
            {
                var body = EmailTemplate.Render(
                    "#10b981", "💰", "Finanzen · Einnahme", i.Source, "Morgen erwartet",
                    "Diese Einnahme wird morgen erwartet.", rows,
                    AppUrl("/finance"), "In Finanzen öffnen",
                    "LifeDash erinnert dich automatisch einen Tag vor jeder erwarteten Einnahme (monatlicher Turnus).");
                await SendReminderAsync(i.Notes, MarkerFor(IncomeSentTag, next.Value),
                    $"Einnahme morgen erwartet: {i.Source}", body, ct, n => i.Notes = n);
            }

            if (next == today)
            {
                var body = EmailTemplate.Render(
                    "#10b981", "💰", "Finanzen · Einnahme", i.Source, "Heute erwartet",
                    "Diese Einnahme wird heute erwartet.", rows,
                    AppUrl("/finance"), "In Finanzen öffnen",
                    "LifeDash erinnert dich automatisch am Tag jeder erwarteten Einnahme (monatlicher Turnus).");
                await SendReminderAsync(i.Notes, MarkerFor(IncomeDueTodaySentTag, next.Value),
                    $"Einnahme heute erwartet: {i.Source}", body, ct, n => i.Notes = n);
            }
        }
    }

    private async Task SendContractCancelRemindersAsync(LifeDashContext db, Dictionary<int, string> memberNames, DateOnly today, CancellationToken ct)
    {
        var tomorrow = today.AddDays(1);
        var contracts = await db.Subscriptions
            .Where(s => s.IsActive && (s.CancelByOn == tomorrow || s.CancelByOn == today))
            .ToListAsync(ct);

        foreach (var s in contracts)
        {
            var person = s.FamilyMemberId is { } mid ? memberNames.GetValueOrDefault(mid, "") : "";
            var rows = new[]
            {
                ("Kündigen bis", s.CancelByOn!.Value.ToString("dd.MM.yyyy")),
                ("Betrag", s.FlowType != "none" && s.Amount is { } amt ? $"{amt:0.00} {s.Currency}" : ""),
                ("Person", person),
                ("Hinweis", s.NoticeText ?? ""),
            };

            if (s.CancelByOn == tomorrow)
            {
                var body = EmailTemplate.Render(
                    "#d97706", "📄", "Vertrag · Kündigungsfrist", s.Name, "Frist endet morgen",
                    "Die Kündigungsfrist für diesen Vertrag endet morgen.", rows,
                    AppUrl("/contracts"), "Vertrag ansehen",
                    "LifeDash erinnert dich automatisch einen Tag vor jeder Kündigungsfrist.");
                await SendReminderAsync(s.Notes, MarkerFor(ContractCancelSentTag, s.CancelByOn.Value),
                    $"Kündigungsfrist morgen: {s.Name}", body, ct, n => s.Notes = n);
            }

            if (s.CancelByOn == today)
            {
                var body = EmailTemplate.Render(
                    "#d97706", "📄", "Vertrag · Kündigungsfrist", s.Name, "Frist endet heute",
                    "Die Kündigungsfrist für diesen Vertrag endet heute.", rows,
                    AppUrl("/contracts"), "Vertrag ansehen",
                    "LifeDash erinnert dich automatisch am Tag jeder Kündigungsfrist.");
                await SendReminderAsync(s.Notes, MarkerFor(ContractCancelDueTodaySentTag, s.CancelByOn.Value),
                    $"Kündigungsfrist heute: {s.Name}", body, ct, n => s.Notes = n);
            }
        }
    }

    private async Task SendAuthorityCaseRemindersAsync(LifeDashContext db, Dictionary<int, string> memberNames, DateOnly today, CancellationToken ct)
    {
        var tomorrow = today.AddDays(1);
        var cases = await db.AuthorityCases
            .Where(c => !ClosedAuthorityStatuses.Contains(c.Status) && (c.DeadlineOn == tomorrow || c.DeadlineOn == today))
            .ToListAsync(ct);

        foreach (var c in cases)
        {
            var person = c.FamilyMemberId is { } mid ? memberNames.GetValueOrDefault(mid, "") : "";
            var rows = new[]
            {
                ("Frist", c.DeadlineOn!.Value.ToString("dd.MM.yyyy")),
                ("Behörde", c.Authority ?? ""),
                ("Aktenzeichen", c.ReferenceNo ?? ""),
                ("Status", Label(AuthorityStatusLabels, c.Status)),
                ("Person", person),
            };

            if (c.DeadlineOn == tomorrow)
            {
                var body = EmailTemplate.Render(
                    "#7c3aed", "🏛️", "Behörden · Frist", c.Title, "Frist morgen",
                    "Die Frist für diesen Behördenvorgang endet morgen.", rows,
                    AppUrl($"/authorities/{c.Id}"), "Vorgang ansehen",
                    "LifeDash erinnert dich automatisch einen Tag vor jeder Behördenfrist.");
                await SendReminderAsync(c.Notes, MarkerFor(AuthorityCaseSentTag, c.DeadlineOn.Value),
                    $"Frist morgen: {c.Title}", body, ct, n => c.Notes = n);
            }

            if (c.DeadlineOn == today)
            {
                var body = EmailTemplate.Render(
                    "#7c3aed", "🏛️", "Behörden · Frist", c.Title, "Frist heute",
                    "Die Frist für diesen Behördenvorgang endet heute.", rows,
                    AppUrl($"/authorities/{c.Id}"), "Vorgang ansehen",
                    "LifeDash erinnert dich automatisch am Tag jeder Behördenfrist.");
                await SendReminderAsync(c.Notes, MarkerFor(AuthorityCaseDueTodaySentTag, c.DeadlineOn.Value),
                    $"Frist heute: {c.Title}", body, ct, n => c.Notes = n);
            }
        }
    }

    private string? AppUrl(string path) =>
        string.IsNullOrWhiteSpace(_options.AppBaseUrl) ? null : _options.AppBaseUrl!.TrimEnd('/') + path;

    private DateTime GetLocalNow()
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning("Reminder timezone {TimeZoneId} was not found; server local time will be used.", _options.TimeZoneId);
            return DateTime.Now;
        }
        catch (InvalidTimeZoneException)
        {
            _logger.LogWarning("Reminder timezone {TimeZoneId} is invalid; server local time will be used.", _options.TimeZoneId);
            return DateTime.Now;
        }
    }

    private bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(_options.SmtpHost)
            && _options.SmtpPort > 0
            && !string.IsNullOrWhiteSpace(_options.SmtpUser)
            && !string.IsNullOrWhiteSpace(_options.SmtpPassword)
            && !string.IsNullOrWhiteSpace(_options.FromEmail)
            && !string.IsNullOrWhiteSpace(_options.ToEmail);
    }

    private static string MarkerFor(string tag, DateTime dateTime) => $"{tag}:{dateTime:yyyyMMddHHmm}";
    private static string MarkerFor(string tag, DateOnly date) => $"{tag}:{date:yyyyMMdd}";

    /// <summary>Removes appointment reminder markers whose occurrence date lies before `today`.</summary>
    private static string? PruneAppointmentMarkers(string? notes, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(notes)) return notes;
        var lines = notes.Split('\n');
        var kept = lines.Where(line =>
        {
            var l = line.Trim();
            string? stamp = null;
            if (l.StartsWith(AppointmentSentTag + ":", StringComparison.Ordinal)) stamp = l[(AppointmentSentTag.Length + 1)..];
            else if (l.StartsWith(AppointmentOneHourSentTag + ":", StringComparison.Ordinal)) stamp = l[(AppointmentOneHourSentTag.Length + 1)..];
            if (stamp is null || stamp.Length < 8) return true;
            return !DateOnly.TryParseExact(stamp[..8], "yyyyMMdd", out var d) || d >= today;
        }).ToArray();
        return kept.Length == lines.Length ? notes : string.Join('\n', kept).Trim();
    }

    private static bool HasMarker(string? notes, string marker) =>
        !string.IsNullOrWhiteSpace(notes) && notes.Contains(marker, StringComparison.Ordinal);

    private static string AppendMarker(string? notes, string marker)
    {
        var existing = (notes ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(existing)) return marker;
        return $"{existing}\n{marker}";
    }

    /// <summary>Sends once per marker (dedup across polls), then records the marker via `setNotes`.</summary>
    private async Task<bool> SendReminderAsync(string? notes, string marker, string subject, string body, CancellationToken ct, Action<string> setNotes)
    {
        if (HasMarker(notes, marker)) return false;
        if (!await TrySendEmailAsync(subject, body, ct)) return false;
        setNotes(AppendMarker(notes, marker));
        return true;
    }

    private async Task<bool> TrySendEmailAsync(string subject, string body, CancellationToken ct)
    {
        try
        {
            using var mail = new MailMessage
            {
                Subject = subject,
                Body = body,
                IsBodyHtml = true,
                From = new MailAddress(_options.FromEmail!, _options.FromName)
            };
            mail.To.Add(new MailAddress(_options.ToEmail!));

            using var smtp = new SmtpClient(_options.SmtpHost!, _options.SmtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(_options.SmtpUser!, _options.SmtpPassword!),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
            };

            await smtp.SendMailAsync(mail, ct);
            _logger.LogInformation("Reminder email sent to {Recipient}: {Subject}", _options.ToEmail, subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send reminder email: {Subject}", subject);
            return false;
        }
    }
}

public enum ReminderRunResult
{
    Completed,
    AlreadyRunning,
    Failed
}
