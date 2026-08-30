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

    private const string AppointmentSentTag = "[appt-reminder-sent]";
    private const string FlightSentTag = "[flight-checkin-reminder-sent]";
    private const string BirthdaySentTag = "[birthday-reminder-sent]";
    private const string PaymentSentTag = "[payment-reminder-sent]";
    private const string IncomeSentTag = "[income-reminder-sent]";
    private const string ContractCancelSentTag = "[contract-cancel-reminder-sent]";

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
        await SendIncomeRemindersAsync(db, today, ct);
        await SendContractCancelRemindersAsync(db, memberNames, today, ct);

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
            if (!ShouldSendOneDayBefore(a.StartsAt, now)) continue;

            var marker = MarkerFor(AppointmentSentTag, a.StartsAt);
            if (HasMarker(a.Notes, marker)) continue;

            var attendees = a.Attendees.Select(x => memberNames.GetValueOrDefault(x.FamilyMemberId, "-")).ToList();
            var subject = $"Erinnerung morgen: {a.Title}";
            var body = EmailTemplate.Render(
                "#2f6fed", "Termin morgen", a.Title,
                "Dieser Termin steht morgen an.",
                new[]
                {
                    ("Datum & Uhrzeit", a.StartsAt.ToString("dd.MM.yyyy HH:mm")),
                    ("Ort", a.Location ?? ""),
                    ("Kategorie", a.Category),
                    ("Teilnehmer", attendees.Count > 0 ? string.Join(", ", attendees) : ""),
                },
                "LifeDash erinnert dich automatisch einen Tag vor jedem Termin.");

            if (await TrySendEmailAsync(subject, body, ct))
                a.Notes = AppendMarker(a.Notes, marker);
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
            if (b.StartsAt is null) continue;
            if (!ShouldSendOneDayBefore(b.StartsAt.Value, now)) continue;

            var marker = MarkerFor(FlightSentTag, b.StartsAt.Value);
            if (HasMarker(b.Notes, marker)) continue;

            var subject = $"Check-in Erinnerung morgen: {b.Title}";
            var body = EmailTemplate.Render(
                "#0f9d78", "Reise · Check-in in 24 Std.", b.Title,
                "Der Check-in für diesen Flug öffnet in den nächsten 24 Stunden.",
                new[]
                {
                    ("Reise", b.Trip?.Title ?? ""),
                    ("Abflug", b.StartsAt.Value.ToString("dd.MM.yyyy HH:mm")),
                    ("Referenznummer", b.ReferenceNo ?? ""),
                    ("Betrag", b.Amount is { } amt ? $"{amt:0.00} {b.Currency}" : ""),
                },
                "LifeDash erinnert dich automatisch 24 Stunden vor jedem Flug.");

            if (await TrySendEmailAsync(subject, body, ct))
                b.Notes = AppendMarker(b.Notes, marker);
        }
    }

    private async Task SendBirthdayRemindersAsync(LifeDashContext db, Dictionary<int, string> memberNames, DateOnly today, CancellationToken ct)
    {
        var dates = await db.ImportantDates.ToListAsync(ct);

        foreach (var i in dates)
        {
            var occurrence = DateOccurrence.NextYearlyOrOnce(i.DateValue, i.RepeatsYearly, today);
            if (occurrence != today) continue;

            var marker = MarkerFor(BirthdaySentTag, occurrence.Value);
            if (HasMarker(i.Notes, marker)) continue;

            var person = i.FamilyMemberId is { } mid ? memberNames.GetValueOrDefault(mid, "") : "";
            var subject = $"Heute: {i.Title}";
            var body = EmailTemplate.Render(
                "#c2417a", "Heute", i.Title,
                "Dieser Tag ist heute.",
                new[]
                {
                    ("Datum", occurrence.Value.ToString("dd.MM.yyyy")),
                    ("Person", person),
                },
                "LifeDash erinnert dich automatisch am Tag selbst an wiederkehrende wichtige Daten.");

            if (await TrySendEmailAsync(subject, body, ct))
                i.Notes = AppendMarker(i.Notes, marker);
        }
    }

    private async Task SendPaymentRemindersAsync(LifeDashContext db, DateOnly today, CancellationToken ct)
    {
        var due = today.AddDays(1);
        var payments = await db.Payments
            .Where(p => !p.IsPaid && p.DueOn == due)
            .ToListAsync(ct);

        foreach (var p in payments)
        {
            var marker = MarkerFor(PaymentSentTag, p.DueOn);
            if (HasMarker(p.Notes, marker)) continue;

            var subject = $"Zahlung morgen fällig: {p.Title}";
            var body = EmailTemplate.Render(
                "#d9534f", "Finanzen · Zahlung morgen", p.Title,
                "Diese Zahlung ist morgen fällig.",
                new[]
                {
                    ("Betrag", $"{p.Amount:0.00} {p.Currency}"),
                    ("Fällig am", p.DueOn.ToString("dd.MM.yyyy")),
                    ("Kategorie", p.Category ?? ""),
                },
                "LifeDash erinnert dich automatisch einen Tag vor jeder fälligen Zahlung.");

            if (await TrySendEmailAsync(subject, body, ct))
                p.Notes = AppendMarker(p.Notes, marker);
        }
    }

    private async Task SendIncomeRemindersAsync(LifeDashContext db, DateOnly today, CancellationToken ct)
    {
        var incomes = await db.Incomes
            .Where(i => i.IsActive && i.Cadence == "monthly" && i.DayOfMonth != null)
            .ToListAsync(ct);

        foreach (var i in incomes)
        {
            var next = DateOccurrence.NextMonthly(i.DayOfMonth, today);
            if (next != today.AddDays(1)) continue;

            var marker = MarkerFor(IncomeSentTag, next.Value);
            if (HasMarker(i.Notes, marker)) continue;

            var subject = $"Einnahme morgen erwartet: {i.Source}";
            var body = EmailTemplate.Render(
                "#1f9d55", "Finanzen · Einnahme morgen", i.Source,
                "Diese Einnahme wird morgen erwartet.",
                new[]
                {
                    ("Betrag", $"{i.Amount:0.00} {i.Currency}"),
                    ("Erwartet am", next.Value.ToString("dd.MM.yyyy")),
                    ("Turnus", i.Cadence),
                },
                "LifeDash erinnert dich automatisch einen Tag vor jeder erwarteten Einnahme (monatlicher Turnus).");

            if (await TrySendEmailAsync(subject, body, ct))
                i.Notes = AppendMarker(i.Notes, marker);
        }
    }

    private async Task SendContractCancelRemindersAsync(LifeDashContext db, Dictionary<int, string> memberNames, DateOnly today, CancellationToken ct)
    {
        var due = today.AddDays(1);
        var contracts = await db.Subscriptions
            .Where(s => s.IsActive && s.CancelByOn == due)
            .ToListAsync(ct);

        foreach (var s in contracts)
        {
            var marker = MarkerFor(ContractCancelSentTag, s.CancelByOn!.Value);
            if (HasMarker(s.Notes, marker)) continue;

            var person = s.FamilyMemberId is { } mid ? memberNames.GetValueOrDefault(mid, "") : "";
            var subject = $"Kündigungsfrist morgen: {s.Name}";
            var body = EmailTemplate.Render(
                "#b8860b", "Vertrag · Kündigungsfrist morgen", s.Name,
                "Die Kündigungsfrist für diesen Vertrag endet morgen.",
                new[]
                {
                    ("Kündigen bis", s.CancelByOn.Value.ToString("dd.MM.yyyy")),
                    ("Betrag", s.FlowType != "none" && s.Amount is { } amt ? $"{amt:0.00} {s.Currency}" : ""),
                    ("Person", person),
                    ("Hinweis", s.NoticeText ?? ""),
                },
                "LifeDash erinnert dich automatisch einen Tag vor jeder Kündigungsfrist.");

            if (await TrySendEmailAsync(subject, body, ct))
                s.Notes = AppendMarker(s.Notes, marker);
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

    private static bool ShouldSendOneDayBefore(DateTime eventAt, DateTime now)
    {
        if (eventAt <= now) return false;
        var remindAt = eventAt.AddDays(-1);
        return now >= remindAt;
    }

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
