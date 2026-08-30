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

    // "1 day before" markers
    private const string AppointmentSentTag = "[appt-reminder-sent]";
    private const string FlightSentTag = "[flight-checkin-reminder-sent]";
    private const string PaymentSentTag = "[payment-reminder-sent]";
    private const string FixedCostSentTag = "[fixedcost-reminder-sent]";
    private const string IncomeSentTag = "[income-reminder-sent]";
    private const string ContractCancelSentTag = "[contract-cancel-reminder-sent]";
    private const string AuthorityCaseSentTag = "[authority-case-reminder-sent]";

    // "on the due day itself" markers - Wichtige Daten (birthdays) already only
    // ever sends on the day, so it has no separate "day before" tag/pass.
    private const string BirthdaySentTag = "[birthday-reminder-sent]";
    private const string AppointmentDueTodaySentTag = "[appt-due-today-sent]";
    private const string FlightDueTodaySentTag = "[flight-due-today-sent]";
    private const string PaymentDueTodaySentTag = "[payment-due-today-sent]";
    private const string FixedCostDueTodaySentTag = "[fixedcost-due-today-sent]";
    private const string IncomeDueTodaySentTag = "[income-due-today-sent]";
    private const string ContractCancelDueTodaySentTag = "[contract-cancel-due-today-sent]";
    private const string AuthorityCaseDueTodaySentTag = "[authority-case-due-today-sent]";

    private static readonly string[] ClosedAuthorityStatuses = { "closed", "approved", "rejected" };

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
                await SendDueRemindersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reminder worker run failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    private async Task SendDueRemindersAsync(CancellationToken ct)
    {
        if (!IsConfigured()) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LifeDashContext>();

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now.Date);

        var memberNames = await db.FamilyMembers.AsNoTracking()
            .ToDictionaryAsync(m => m.Id, m => m.FullName, ct);

        await SendAppointmentRemindersAsync(db, memberNames, now, ct);
        await SendFlightRemindersAsync(db, now, ct);
        await SendBirthdayRemindersAsync(db, memberNames, today, ct);
        await SendPaymentRemindersAsync(db, today, ct);
        await SendFixedCostRemindersAsync(db, today, ct);
        await SendIncomeRemindersAsync(db, today, ct);
        await SendContractCancelRemindersAsync(db, memberNames, today, ct);
        await SendAuthorityCaseRemindersAsync(db, memberNames, today, ct);

        await db.SaveChangesAsync(ct);
    }

    private async Task SendAppointmentRemindersAsync(LifeDashContext db, Dictionary<int, string> memberNames, DateTime now, CancellationToken ct)
    {
        var appointments = await db.Appointments
            .Include(a => a.Attendees)
            .Where(a => !a.IsDone)
            .ToListAsync(ct);

        foreach (var a in appointments)
        {
            var attendees = a.Attendees.Select(x => memberNames.GetValueOrDefault(x.FamilyMemberId, "-")).ToList();
            var rows = new[]
            {
                ("Datum & Uhrzeit", a.StartsAt.ToString("dd.MM.yyyy HH:mm")),
                ("Ort", a.Location ?? ""),
                ("Kategorie", a.Category),
                ("Teilnehmer", attendees.Count > 0 ? string.Join(", ", attendees) : ""),
            };

            if (ShouldSendOneDayBefore(a.StartsAt, now))
            {
                var body = EmailTemplate.Render(
                    "#4f6df5", "📅", "Termin", a.Title, "Morgen fällig",
                    "Dieser Termin steht morgen an.", rows,
                    AppUrl("/family"), "Termin ansehen",
                    "LifeDash erinnert dich automatisch einen Tag vor jedem Termin.");
                await SendReminderAsync(a.Notes, MarkerFor(AppointmentSentTag, a.StartsAt),
                    $"Erinnerung morgen: {a.Title}", body, ct, n => a.Notes = n);
            }

            if (ShouldSendOnDueDay(a.StartsAt, now))
            {
                var body = EmailTemplate.Render(
                    "#4f6df5", "📅", "Termin", a.Title, "Heute",
                    "Dieser Termin steht heute an.", rows,
                    AppUrl("/family"), "Termin ansehen",
                    "LifeDash erinnert dich automatisch am Tag des Termins.");
                await SendReminderAsync(a.Notes, MarkerFor(AppointmentDueTodaySentTag, a.StartsAt),
                    $"Heute: {a.Title}", body, ct, n => a.Notes = n);
            }
        }
    }

    private async Task SendFlightRemindersAsync(LifeDashContext db, DateTime now, CancellationToken ct)
    {
        var flights = await db.Bookings
            .Include(b => b.Trip)
            .Where(b => b.Kind == "flight" && b.StartsAt != null)
            .ToListAsync(ct);

        foreach (var b in flights)
        {
            if (b.StartsAt is not { } startsAt) continue;
            var rows = new[]
            {
                ("Reise", b.Trip?.Title ?? ""),
                ("Abflug", startsAt.ToString("dd.MM.yyyy HH:mm")),
                ("Referenznummer", b.ReferenceNo ?? ""),
                ("Betrag", b.Amount is { } amt ? $"{amt:0.00} {b.Currency}" : ""),
            };

            if (ShouldSendOneDayBefore(startsAt, now))
            {
                var body = EmailTemplate.Render(
                    "#0ea5a3", "✈️", "Reise · Check-in", b.Title, "In 24 Std.",
                    "Der Check-in für diesen Flug öffnet in den nächsten 24 Stunden.", rows,
                    AppUrl($"/travel/{b.TripId}"), "Reise ansehen",
                    "LifeDash erinnert dich automatisch 24 Stunden vor jedem Flug.");
                await SendReminderAsync(b.Notes, MarkerFor(FlightSentTag, startsAt),
                    $"Check-in Erinnerung morgen: {b.Title}", body, ct, n => b.Notes = n);
            }

            if (ShouldSendOnDueDay(startsAt, now))
            {
                var body = EmailTemplate.Render(
                    "#0ea5a3", "✈️", "Reise · Heute", b.Title, "Heute",
                    "Dieser Flug ist heute.", rows,
                    AppUrl($"/travel/{b.TripId}"), "Reise ansehen",
                    "LifeDash erinnert dich automatisch am Tag des Flugs.");
                await SendReminderAsync(b.Notes, MarkerFor(FlightDueTodaySentTag, startsAt),
                    $"Heute: {b.Title}", body, ct, n => b.Notes = n);
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
                AppUrl("/family"), "Im Familienbereich ansehen",
                "LifeDash erinnert dich automatisch am Tag selbst an wiederkehrende wichtige Daten.");

            await SendReminderAsync(i.Notes, MarkerFor(BirthdaySentTag, occurrence.Value),
                $"Heute: {i.Title}", body, ct, n => i.Notes = n);
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
                ("Kategorie", p.Category ?? ""),
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
                ("Turnus", c.Cadence),
                ("Kategorie", c.Category ?? ""),
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
                ("Turnus", i.Cadence),
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
                ("Status", c.Status),
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

    private bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(_options.SmtpHost)
            && _options.SmtpPort > 0
            && !string.IsNullOrWhiteSpace(_options.SmtpUser)
            && !string.IsNullOrWhiteSpace(_options.SmtpPassword)
            && !string.IsNullOrWhiteSpace(_options.FromEmail)
            && !string.IsNullOrWhiteSpace(_options.ToEmail);
    }

    private static bool ShouldSendOneDayBefore(DateTime eventAt, DateTime now)
    {
        if (eventAt <= now) return false;
        if (eventAt.Date == now.Date) return false; // that's "due day", handled separately
        var remindAt = eventAt.AddDays(-1);
        return now >= remindAt;
    }

    private static bool ShouldSendOnDueDay(DateTime eventAt, DateTime now) =>
        eventAt.Date == now.Date && eventAt > now;

    private static string MarkerFor(string tag, DateTime dateTime) => $"{tag}:{dateTime:yyyyMMddHHmm}";
    private static string MarkerFor(string tag, DateOnly date) => $"{tag}:{date:yyyyMMdd}";

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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send reminder email: {Subject}", subject);
            return false;
        }
    }
}
