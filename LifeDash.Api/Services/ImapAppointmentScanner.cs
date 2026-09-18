using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace LifeDash.Api.Services;

public record ScannedAppointmentDto(
    string Title,
    DateTime StartsAt,
    string[] MatchedNames
);

// Scans the same mailbox as ImapPackageScanner for Termin confirmations instead of shipments.
// Always looks back MailTracking:DefaultLookbackDays (7 by default) — unlike packages there is no
// full-history mode, since only recent mail should ever turn into an appointment.
public class ImapAppointmentScanner
{
    private readonly MailTrackingOptions _options;
    private readonly MicrosoftMailAuthService _msAuth;
    private readonly ILogger<ImapAppointmentScanner> _logger;

    public ImapAppointmentScanner(IOptions<MailTrackingOptions> options, MicrosoftMailAuthService msAuth, ILogger<ImapAppointmentScanner> logger)
    {
        _options = options.Value;
        _msAuth = msAuth;
        _logger = logger;
    }

    public async Task<List<ScannedAppointmentDto>> ScanMailboxAsync(IEnumerable<string> knownNames, CancellationToken ct = default)
    {
        var email = _options.Email?.Trim();
        var password = _options.AppPassword?.Replace(" ", "").Trim();
        var accessToken = await _msAuth.TryGetAccessTokenAsync(ct);

        if (string.IsNullOrWhiteSpace(email) || (accessToken is null && string.IsNullOrWhiteSpace(password)))
        {
            _logger.LogWarning("Neither a Microsoft OAuth connection nor an IMAP app password is configured for mailbox scanning.");
            return new List<ScannedAppointmentDto>();
        }

        var names = knownNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var results = new List<ScannedAppointmentDto>();
        var folderNames = _options.FoldersToScan is { Length: > 0 } configured ? configured : ["INBOX"];
        var maxPerFolder = Math.Max(1, _options.MaxMessagesPerFolder);
        var lookbackSince = DateTime.UtcNow.AddDays(-Math.Max(1, _options.DefaultLookbackDays));

        foreach (var folderName in folderNames)
        {
            try
            {
                results.AddRange(await ScanFolderAsync(email, accessToken, password, folderName, lookbackSince, maxPerFolder, names, ct));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scanning folder '{Folder}' for appointments failed; keeping results from other folders.", folderName);
            }
        }

        return results;
    }

    private async Task<List<ScannedAppointmentDto>> ScanFolderAsync(
        string email, string? accessToken, string? password, string folderName,
        DateTime lookbackSince, int maxPerFolder, List<string> knownNames, CancellationToken ct)
    {
        var found = new List<ScannedAppointmentDto>();

        using var client = new ImapClient();
        client.Timeout = 60000;
        await client.ConnectAsync(_options.ImapHost, _options.ImapPort, _options.UseSsl, ct);

        if (accessToken is not null)
            await client.AuthenticateAsync(new SaslMechanismOAuth2(email, accessToken), ct);
        else
            await client.AuthenticateAsync(email, password!, ct);

        var folder = string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(lookbackSince), ct);
        var targetUids = uids.OrderByDescending(u => u.Id).Take(maxPerFolder).ToList();

        foreach (var uid in targetUids)
        {
            try
            {
                var message = await folder.GetMessageAsync(uid, ct);
                if (message == null) continue;

                var parsed = ParseAppointmentFromMessage(message, knownNames);
                if (parsed != null) found.Add(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse message UID {Uid} in folder '{Folder}' for appointments", uid, folderName);
            }
        }

        try { await client.DisconnectAsync(true, ct); } catch { /* best-effort */ }

        return found;
    }

    // Requires both a Termin-related keyword AND a parseable date+time before treating a message as
    // an appointment — the same conservative two-signal approach ImapPackageScanner uses for
    // tracking numbers, to keep newsletters/unrelated mail from turning into junk appointments.
    // Keywords cover German, English and Bosnian, since the mailbox receives mail in all three.
    private static readonly Regex AppointmentKeywordRegex = new(
        @"\b(" +
        // German
        @"termin(?:bestätigung|vereinbarung)?|vorsorgetermin|arzttermin|zahnarzttermin|elterngespräch|elternabend|einladung|wir bestätigen|ihr termin|vereinbarter termin|" +
        // English
        @"appointment(?:\s+confirmation)?|confirmed appointment|we confirm|your appointment|scheduled|meeting|invitation|booking confirm(?:ation|ed)?|reservation confirmed|doctor'?s appointment|dentist appointment|parent(?:-|\s)teacher meeting|" +
        // Bosnian
        @"sastanak|zakazan(?:i|o)?\s*termin|potvrda termina|poziv na (?:sastanak|pregled)|ljekarski pregled|roditeljski sastanak" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "14:30 Uhr" / "14.30 Uhr" (German) / "14:30 sati" or "14:30h" (Bosnian) — the reliable
    // signal, checked first.
    private static readonly Regex TimeWithSuffixRegex = new(
        @"\b([01]?[0-9]|2[0-3])[:.]([0-5][0-9])\s*(uhr|sati|h)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "2:30 PM" / "2.30pm" (English 12-hour clock).
    private static readonly Regex TimeAmPmRegex = new(
        @"\b(0?[1-9]|1[0-2])[:.]([0-5][0-9])\s*(am|pm)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bare "14:30" — colon only (not dot), since a dot separator collides with DE/BA date formatting
    // (e.g. "12.09.2026") and would misread part of a date as a time.
    private static readonly Regex TimeColonRegex = new(
        @"\b([01]?[0-9]|2[0-3]):([0-5][0-9])\b", RegexOptions.Compiled);

    private ScannedAppointmentDto? ParseAppointmentFromMessage(MimeMessage message, List<string> knownNames)
    {
        var subject = message.Subject ?? "";
        var textBody = message.TextBody ?? "";
        var htmlBody = message.HtmlBody ?? "";
        var combined = $"{subject}\n{textBody}\n{htmlBody}";

        if (!AppointmentKeywordRegex.IsMatch(combined)) return null;

        var dateOnly = ExtractDate(combined);
        if (dateOnly is null) return null;

        var time = ExtractTime(combined);
        if (time is null) return null;

        var startsAt = dateOnly.Value.ToDateTime(new TimeOnly(time.Value.hour, time.Value.minute));

        var matchedNames = knownNames.Where(name => ContainsName(combined, name)).ToArray();

        var title = subject.Replace("Fwd:", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("Wg:", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("Re:", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(title)) title = "Termin (Postfach)";

        return new ScannedAppointmentDto(title, startsAt, matchedNames);
    }

    private static (int hour, int minute)? ExtractTime(string content)
    {
        var m = TimeWithSuffixRegex.Match(content);
        if (m.Success)
        {
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }

        m = TimeAmPmRegex.Match(content);
        if (m.Success)
        {
            var hour = int.Parse(m.Groups[1].Value);
            var isPm = string.Equals(m.Groups[3].Value, "pm", StringComparison.OrdinalIgnoreCase);
            if (isPm && hour != 12) hour += 12;
            if (!isPm && hour == 12) hour = 0;
            return (hour, int.Parse(m.Groups[2].Value));
        }

        m = TimeColonRegex.Match(content);
        if (!m.Success) return null;

        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    // Matches the full name, or just the given (first) name - deliberately NOT any individual
    // token, since matching on a shared family surname alone (e.g. "Familie Turkes") would
    // otherwise match every family member with that surname instead of just the one addressed.
    private static bool ContainsName(string content, string fullName)
    {
        if (Regex.IsMatch(content, $@"\b{Regex.Escape(fullName)}\b", RegexOptions.IgnoreCase))
            return true;

        var firstName = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return firstName is { Length: >= 2 } && Regex.IsMatch(content, $@"\b{Regex.Escape(firstName)}\b", RegexOptions.IgnoreCase);
    }

    private static DateOnly? ExtractDate(string content)
    {
        var isoMatch = Regex.Match(content, @"\b(202[4-9])-([01][0-9])-([0-3][0-9])\b");
        if (isoMatch.Success
            && int.TryParse(isoMatch.Groups[1].Value, out var y)
            && int.TryParse(isoMatch.Groups[2].Value, out var m)
            && int.TryParse(isoMatch.Groups[3].Value, out var d))
        {
            try { return new DateOnly(y, m, d); } catch { return null; }
        }

        // German and Bosnian both write dates as DD.MM.YYYY.
        var deMatch = Regex.Match(content, @"\b([0-3]?[0-9])\.([0-1]?[0-9])\.(202[4-9])\b");
        if (deMatch.Success
            && int.TryParse(deMatch.Groups[1].Value, out var day)
            && int.TryParse(deMatch.Groups[2].Value, out var month)
            && int.TryParse(deMatch.Groups[3].Value, out var year))
        {
            try { return new DateOnly(year, month, day); } catch { return null; }
        }

        // English writes dates as MM/DD/YYYY.
        var enMatch = Regex.Match(content, @"\b(0?[1-9]|1[0-2])/([0-3]?[0-9])/(202[4-9])\b");
        if (enMatch.Success
            && int.TryParse(enMatch.Groups[1].Value, out var enMonth)
            && int.TryParse(enMatch.Groups[2].Value, out var enDay)
            && int.TryParse(enMatch.Groups[3].Value, out var enYear))
        {
            try { return new DateOnly(enYear, enMonth, enDay); } catch { return null; }
        }

        return null;
    }
}
