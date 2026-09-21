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
    private readonly ReminderEmailOptions _reminderOptions;
    private readonly MicrosoftMailAuthService _msAuth;
    private readonly ILogger<ImapAppointmentScanner> _logger;

    public ImapAppointmentScanner(
        IOptions<MailTrackingOptions> options,
        IOptions<ReminderEmailOptions> reminderOptions,
        MicrosoftMailAuthService msAuth,
        ILogger<ImapAppointmentScanner> logger)
    {
        _options = options.Value;
        _reminderOptions = reminderOptions.Value;
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
        @"termin(?:bestätigung|vereinbarung|erinnerung)?|vorsorgetermin|arzttermin|zahnarzttermin|impftermin|prüfungstermin|beratungstermin|besichtigungstermin|liefertermin|abholtermin|elterngespräch|elternabend|einladung|wir bestätigen|ihr termin|vereinbarter termin|sprechstunde|videosprechstunde|videokonferenz|findet statt am|bestätigt für|" +
        // English
        @"appointment(?:\s*:|\s+confirmation)?|confirmed appointment|we confirm|your appointment|scheduled|meeting|invitation|booking confirm(?:ation|ed)?|reservation confirmed|doctor'?s appointment|dentist appointment|parent(?:-|\s)teacher meeting|exam(?:ination)?(?:\s+confirmation)?|test date|interview scheduled|video call|webinar|consultation|confirmed for|save the date|" +
        // Bosnian / Serbian / Croatian
        @"sastanak|zakazan(?:i|o)?\s*termin|potvrda termina|poziv na (?:sastanak|pregled)|ljekarski pregled|roditeljski sastanak|ispit|zakazano za|potvrđeno za|konsultacija|video poziv" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Month names/abbreviations in English, German and Bosnian, so dates spelled out in words
    // (e.g. "September 30, 2026" or "30. septembar 2026.") can be recognized, not just numeric
    // DD.MM.YYYY / MM/DD/YYYY dates.
    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        ["january"] = 1, ["jan"] = 1,
        ["february"] = 2, ["feb"] = 2,
        ["march"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4, ["aprila"] = 4,
        ["may"] = 5,
        ["june"] = 6, ["jun"] = 6,
        ["july"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sept"] = 9, ["sep"] = 9,
        ["october"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["december"] = 12, ["dec"] = 12,
        // German
        ["januar"] = 1, ["januara"] = 1,
        ["februar"] = 2, ["februara"] = 2,
        ["märz"] = 3, ["marz"] = 3, ["mrz"] = 3, ["mart"] = 3, ["marta"] = 3,
        ["mai"] = 5, ["maj"] = 5, ["maja"] = 5,
        ["juni"] = 6, ["juna"] = 6,
        ["juli"] = 7, ["jula"] = 7,
        ["oktober"] = 10, ["okt"] = 10, ["oktobar"] = 10, ["oktobra"] = 10,
        ["dezember"] = 12, ["dez"] = 12, ["decembar"] = 12, ["decembra"] = 12,
        // Bosnian / Serbian / Croatian
        ["avgust"] = 8, ["avgusta"] = 8,
        ["septembar"] = 9, ["septembra"] = 9,
        ["novembar"] = 11, ["novembra"] = 11,
    };

    private static readonly string MonthNamesPattern = string.Join("|", MonthNames.Keys.Select(Regex.Escape));

    // "September 30, 2026" / "Sep 30 2026".
    private static readonly Regex MonthDayYearRegex = new(
        $@"\b(?<month>{MonthNamesPattern})\.?\s+(?<day>[0-3]?[0-9])(?:st|nd|rd|th)?,?\s+(?<year>202[4-9])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "30. September 2026" (German) / "30. septembar 2026." (Bosnian).
    private static readonly Regex DayMonthYearRegex = new(
        $@"\b(?<day>[0-3]?[0-9])\.?\s+(?<month>{MonthNamesPattern})\.?,?\s+(?<year>202[4-9])\.?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "14:30 Uhr" / "14.30 Uhr" (German) / "14:30 sati" or "14:30h" (Bosnian) — the reliable
    // signal, checked first. Seconds (":00") are optional and ignored.
    private static readonly Regex TimeWithSuffixRegex = new(
        @"\b([01]?[0-9]|2[0-3])[:.]([0-5][0-9])(?::[0-5][0-9])?\s*(uhr|sati|h)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "2:30 PM" / "2.30pm" / "5:30:00 PM" (English 12-hour clock, seconds optional).
    private static readonly Regex TimeAmPmRegex = new(
        @"\b(0?[1-9]|1[0-2])[:.]([0-5][0-9])(?::[0-5][0-9])?\s*(am|pm)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bare "14:30" — colon only (not dot), since a dot separator collides with DE/BA date formatting
    // (e.g. "12.09.2026") and would misread part of a date as a time. Seconds are optional.
    private static readonly Regex TimeColonRegex = new(
        @"\b([01]?[0-9]|2[0-3]):([0-5][0-9])(?::[0-5][0-9])?\b", RegexOptions.Compiled);

    private ScannedAppointmentDto? ParseAppointmentFromMessage(MimeMessage message, List<string> knownNames)
    {
        // LifeDash's own reminder emails (ReminderEmailWorker) land in this same mailbox - since
        // they mention "Termin" and include the appointment's date+time, they'd otherwise match the
        // keyword+date/time heuristic below and get re-ingested as a duplicate "new" appointment.
        if (IsOwnReminderEmail(message)) return null;

        var subject = message.Subject ?? "";
        var textBody = message.TextBody ?? "";
        // Strip tags/decode entities so wording split across HTML elements (e.g. a table cell)
        // still reads as plain text for the keyword/date/time regexes below.
        var htmlBody = string.IsNullOrEmpty(message.HtmlBody)
            ? ""
            : System.Net.WebUtility.HtmlDecode(Regex.Replace(message.HtmlBody, "<[^>]+>", " "));
        var combined = $"{subject}\n{textBody}\n{htmlBody}";

        var keywordMatches = AppointmentKeywordRegex.Matches(combined);
        if (keywordMatches.Count == 0) return null;

        // A message can contain several dates (e.g. an invoice's "Transaction Date" alongside an
        // "Appointment" line). Prefer the date+time found near an appointment keyword over the
        // first date/time anywhere in the message, so unrelated dates aren't picked by accident.
        (DateOnly date, (int hour, int minute) time)? best = null;
        foreach (Match keywordMatch in keywordMatches)
        {
            // Forward-only: a date/time preceding the keyword usually belongs to something else
            // entirely (e.g. an invoice's "Order date" ahead of a later "Appointment:" line), while
            // "<keyword>: <date> at <time>" / "<keyword> am <date> um <time>" is the common phrasing.
            var windowEnd = Math.Min(combined.Length, keywordMatch.Index + keywordMatch.Length + 250);
            var window = combined[keywordMatch.Index..windowEnd];

            var windowDate = ExtractDate(window);
            if (windowDate is null) continue;
            var windowTime = ExtractTime(window);
            if (windowTime is null) continue;

            best = (windowDate.Value, windowTime.Value);
            break;
        }

        if (best is null)
        {
            // Fall back to searching the whole message, e.g. when the keyword is only in the
            // subject and the date/time is further away in the body.
            var dateOnly = ExtractDate(combined);
            if (dateOnly is null) return null;
            var time = ExtractTime(combined);
            if (time is null) return null;
            best = (dateOnly.Value, time.Value);
        }

        var startsAt = best.Value.date.ToDateTime(new TimeOnly(best.Value.time.hour, best.Value.time.minute));

        var matchedNames = knownNames.Where(name => ContainsName(combined, name)).ToArray();

        var title = subject.Replace("Fwd:", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("Wg:", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("Re:", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(title)) title = "Termin (Postfach)";

        return new ScannedAppointmentDto(title, startsAt, matchedNames);
    }

    private bool IsOwnReminderEmail(MimeMessage message)
    {
        var reminderFrom = _reminderOptions.FromEmail?.Trim();
        if (string.IsNullOrWhiteSpace(reminderFrom)) return false;

        return message.From.Mailboxes.Any(m => string.Equals(m.Address, reminderFrom, StringComparison.OrdinalIgnoreCase));
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

        // Spelled-out months, e.g. "September 30, 2026" or "30. septembar 2026.".
        var monthDayYear = MonthDayYearRegex.Match(content);
        if (monthDayYear.Success
            && MonthNames.TryGetValue(monthDayYear.Groups["month"].Value, out var mdyMonth)
            && int.TryParse(monthDayYear.Groups["day"].Value, out var mdyDay)
            && int.TryParse(monthDayYear.Groups["year"].Value, out var mdyYear))
        {
            try { return new DateOnly(mdyYear, mdyMonth, mdyDay); } catch { return null; }
        }

        var dayMonthYear = DayMonthYearRegex.Match(content);
        if (dayMonthYear.Success
            && MonthNames.TryGetValue(dayMonthYear.Groups["month"].Value, out var dmyMonth)
            && int.TryParse(dayMonthYear.Groups["day"].Value, out var dmyDay)
            && int.TryParse(dayMonthYear.Groups["year"].Value, out var dmyYear))
        {
            try { return new DateOnly(dmyYear, dmyMonth, dmyDay); } catch { return null; }
        }

        return null;
    }
}
