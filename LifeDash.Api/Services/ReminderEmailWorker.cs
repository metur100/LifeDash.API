using System.Net;
using System.Net.Mail;
using LifeDash.Api.Data;
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

        var appointments = await db.Appointments
            .Where(a => !a.IsDone)
            .ToListAsync(ct);

        foreach (var a in appointments)
        {
            if (!ShouldSendOneDayBefore(a.StartsAt, now)) continue;

            var marker = MarkerFor(AppointmentSentTag, a.StartsAt);
            if (HasMarker(a.Notes, marker)) continue;

            var subject = $"Erinnerung morgen: {a.Title}";
            var body = BuildAppointmentBody(a);
            if (await TrySendEmailAsync(subject, body, ct))
            {
                a.Notes = AppendMarker(a.Notes, marker);
            }
        }

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
            var body = BuildFlightBody(b);
            if (await TrySendEmailAsync(subject, body, ct))
            {
                b.Notes = AppendMarker(b.Notes, marker);
            }
        }

        await db.SaveChangesAsync(ct);
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

    private static string MarkerFor(string tag, DateTime dateTime)
    {
        return $"{tag}:{dateTime:yyyyMMddHHmm}";
    }

    private static bool HasMarker(string? notes, string marker)
    {
        return !string.IsNullOrWhiteSpace(notes) && notes.Contains(marker, StringComparison.Ordinal);
    }

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
                IsBodyHtml = false,
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

    private static string BuildAppointmentBody(Models.Appointment a)
    {
        return $"Termin Erinnerung\n\nTitel: {a.Title}\nKategorie: {a.Category}\nZeit: {a.StartsAt:dd.MM.yyyy HH:mm}\nOrt: {a.Location ?? "-"}\n\nLifeDash erinnert dich einen Tag vorher.";
    }

    private static string BuildFlightBody(Models.Booking b)
    {
        return $"Check-in Erinnerung\n\nFlug: {b.Title}\nZeit: {b.StartsAt:dd.MM.yyyy HH:mm}\nReise: {b.Trip?.Title ?? "-"}\n\nBitte Check-in heute erledigen.";
    }
}
