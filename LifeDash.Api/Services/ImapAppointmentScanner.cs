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
    string[] MatchedNames,
    string? Category,
    string? Location
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

        var rawSubject = message.Subject ?? "";
        var subject = ForwardPrefixRegex.Replace(rawSubject, "").Trim();

        // A mail forwarded "as attachment" carries the real confirmation as an embedded message —
        // its body and sender are what matter, not the forwarding wrapper's.
        var attached = message.BodyParts.OfType<MessagePart>().Select(p => p.Message).FirstOrDefault(m => m != null);
        var bodyText = GetPlainText(message);
        if (attached != null) bodyText += "\n" + GetPlainText(attached);
        var combined = $"{subject}\n{bodyText}";

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

        var (senderName, senderAddress) = ResolveOriginalSender(message, attached, rawSubject, bodyText);
        var senderDomain = senderAddress?.Split('@').ElementAtOrDefault(1) ?? "";
        var category = DetectCategory($"{combined}\n{senderDomain.Replace('-', ' ')}", senderDomain);
        var location = DetectLocation(combined);

        // The subject is deliberately NOT used as the title — it's usually a sentence like
        // "Wir freuen uns auf Ihren Termin". The title names who the appointment is with instead.
        var title = DetectOrganizer(combined, knownNames)
                    ?? CleanSenderName(senderName, senderAddress, knownNames)
                    ?? OrganizerFromDomain(senderDomain)
                    ?? FallbackTitle(category);

        return new ScannedAppointmentDto(title, startsAt, matchedNames, category, location);
    }

    // ---- Plain text ----------------------------------------------------------------------------

    private static readonly Regex ForwardPrefixRegex = new(
        @"^\s*(?:(?:fwd?|wg|aw|re|sv|odg|proslijeđeno)\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string GetPlainText(MimeMessage message)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.TextBody)) parts.Add(NormalizeText(message.TextBody));
        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) parts.Add(HtmlToText(message.HtmlBody));
        return string.Join("\n", parts);
    }

    // Drops <style>/<script>/<head> content entirely (otherwise CSS rules leak into the text and end
    // up as "addresses"), turns block-level tags into line breaks so lines keep their meaning, then
    // strips the remaining tags and decodes entities.
    private static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|head|title|noscript)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<!--.*?-->", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<(?:br|/p|/div|/tr|/li|/h[1-6]|/table|/ul|/ol|/blockquote)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        return NormalizeText(s);
    }

    private static string NormalizeText(string text)
    {
        var lines = text.Replace("\r", "").Replace(' ', ' ').Replace('​', ' ').Split('\n')
            .Select(l => Regex.Replace(l, @"[ \t]+", " ").Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    // ---- Sender --------------------------------------------------------------------------------

    // "Von: Praxis Dr. Müller <info@praxis-mueller.de>" / "From: x@y.de" / "Von: Name [mailto:x@y.de]"
    // lines that mail clients put above an inline-forwarded message.
    private static readonly Regex InlineForwardHeaderRegex = new(
        @"^[ \t>*]*(?:Von|From|Od|Absender)[ \t]*:[ \t]*(?<name>[^<\[\n@]*?)[ \t]*[<\[(]?(?:mailto:)?(?<addr>[^\s<>\[\]()@]+@[^\s<>\[\]()]+?)[>\])]?[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // For mail the user forwarded to themselves the outer sender is the user — the original sender
    // (the practice, school, ...) sits in the attached message or the inline "Von:" header block.
    private (string? Name, string? Address) ResolveOriginalSender(MimeMessage message, MimeMessage? attached, string rawSubject, string bodyText)
    {
        if (attached?.From.Mailboxes.FirstOrDefault() is { } inner)
            return (inner.Name, inner.Address);

        var outer = message.From.Mailboxes.FirstOrDefault();
        var ownAddress = _options.Email?.Trim();
        var isFromSelf = outer != null && !string.IsNullOrEmpty(ownAddress)
                         && string.Equals(outer.Address, ownAddress, StringComparison.OrdinalIgnoreCase);

        if (isFromSelf || ForwardPrefixRegex.IsMatch(rawSubject))
        {
            foreach (Match m in InlineForwardHeaderRegex.Matches(bodyText))
            {
                var addr = m.Groups["addr"].Value.Trim();
                if (!string.IsNullOrEmpty(ownAddress) && string.Equals(addr, ownAddress, StringComparison.OrdinalIgnoreCase)) continue;
                return (m.Groups["name"].Value.Trim(' ', '"', '\''), addr);
            }
        }

        // Still the user's own address -> the sender says nothing about who the appointment is with.
        return isFromSelf ? (null, null) : (outer?.Name, outer?.Address);
    }

    private static readonly Regex GenericSenderRegex = new(
        @"^(?:no-?reply|do-?not-?reply|noreply|info|service|support|team|newsletter|notifications?|benachrichtigung(?:en)?|termine?|terminservice|kalender|calendar|mailer|system|admin|kontakt|contact|office|mail)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Booking platforms / mail infrastructure — their name is not who the appointment is with.
    private static readonly string[] PlatformNames =
    [
        "doctolib", "jameda", "samedi", "clickdoc", "calendly", "google", "microsoft", "outlook", "teams", "zoom",
        "eventbrite", "sendgrid", "mailchimp", "amazonses", "office365",
    ];

    private static string? CleanSenderName(string? name, string? address, List<string> knownNames)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cleaned = Regex.Replace(name, @"\s+(?:via|über|ueber|by)\s+.*$", "", RegexOptions.IgnoreCase).Trim(' ', '"', '\'');
        if (cleaned.Contains('@')) return null;
        if (GenericSenderRegex.IsMatch(cleaned)) return null;
        if (PlatformNames.Any(p => cleaned.Contains(p, StringComparison.OrdinalIgnoreCase))) return null;
        return IsSensibleName(cleaned, knownNames) ? cleaned : null;
    }

    private static readonly HashSet<string> GenericMailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail", "googlemail", "gmx", "web", "outlook", "hotmail", "live", "yahoo", "icloud", "me", "t-online",
        "aol", "freenet", "posteo", "mail", "protonmail", "proton", "arcor", "mailbox",
    };

    // Booking/notification services ("termine-online.net") — not the organizer's own domain.
    private static readonly HashSet<string> GenericDomainWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "termin", "termine", "online", "portal", "booking", "buchung", "service", "services", "app", "mail", "mailer",
        "newsletter", "news", "noreply", "notify", "notification", "kalender", "calendar", "reminder", "system",
    };

    // "praxis-dr-mueller.de" -> "Praxis Dr Mueller", "kita-sonnenschein.de" -> "Kita Sonnenschein".
    private static string? OrganizerFromDomain(string domain)
    {
        var labels = domain.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return null;
        var label = labels[^2];
        if (label.Length < 3 || label.Any(char.IsDigit)) return null;
        if (GenericMailDomains.Contains(label) || PlatformNames.Any(p => label.Contains(p))) return null;

        var parts = label.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => GenericDomainWords.Contains(p))) return null;
        var words = parts.Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(" ", words);
    }

    private static string FallbackTitle(string? category) => category switch
    {
        "health" => "Arzttermin",
        "school" => "Schultermin",
        "authority" => "Behördentermin",
        "finance" => "Banktermin",
        "travel" => "Reise",
        "home" => "Handwerkertermin",
        _ => "Termin",
    };

    // ---- Organizer (practice / doctor / institution) -------------------------------------------

    // "Ihr Termin bei Dr. Müller", "appointment with Smile Dental", "pregled kod dr. Hodžić".
    private static readonly Regex AppointmentWithRegex = new(
        @"\b(?:termin|behandlung|untersuchung|sprechstunde|vorsorge\w*|impfung|appointment|visit|pregled)[ ]+(?:bei|in der|im|with|at|kod|u)[ ]+(?<org>[^\n]{2,100})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Zahnarztpraxis Dr. med. dent. Anna Schmidt", "Dr. Müller", "Prof. Dr. Weber".
    private static readonly Regex DoctorRegex = new(
        @"(?<![\p{L}])(?<pre>(?:\p{Lu}[\p{L}\-]*praxis|Praxis|Ordination|Ordinacija)[ ]+)?(?<title>(?:Dr|Prof|Dipl\.-Med|Doc)\.(?:[ ]?(?:Dr|med|dent|vet|rer\.[ ]?nat|univ|habil|sci|phil)\.)*)[ ]+(?<name>[^\n]{2,80})",
        RegexOptions.Compiled);

    // "Grundschule Am Park", "Kinderarztpraxis Sonnenberg", "Bürgeramt Mitte", "Dom zdravlja Centar".
    private static readonly Regex InstitutionRegex = new(
        @"(?<![\p{L}\-])(?=\p{Lu})(?<head>[\p{L}\-]*?(?i:praxis|klinikum|kliniken|klinik|krankenhaus|zentrum|center|centre|mvz|schule|gymnasium|kita|kindergarten|kindertagesstätte|apotheke|amt|behörde|rathaus|jobcenter|sparkasse|bank|versicherung|therapie|institut|hotel|kanzlei|ordination|ordinacija|klinika|bolnica|ambulanta|poliklinika|škola|vrtić|clinic|hospital|practice|school|dental|dom zdravlja))(?![\p{L}])(?<tail>[^\n]{0,80})",
        RegexOptions.Compiled);

    private static string? DetectOrganizer(string text, List<string> knownNames)
    {
        foreach (Match m in AppointmentWithRegex.Matches(text))
        {
            var org = TakeNameTokens(m.Groups["org"].Value, knownNames, maxTokens: 6);
            // Prefer the full doctor/institution form when the phrase points at one.
            if (org != null && IsSensibleName(org, knownNames)) return ExpandDoctor(org, knownNames) ?? org;
        }

        foreach (Match m in DoctorRegex.Matches(text))
        {
            var name = TakeNameTokens(m.Groups["name"].Value, knownNames, maxTokens: 3, connectorsAllowed: false);
            if (name == null) continue;
            var full = $"{m.Groups["pre"].Value}{m.Groups["title"].Value} {name}".Trim();
            if (IsSensibleName(full, knownNames)) return full;
        }

        foreach (Match m in InstitutionRegex.Matches(text))
        {
            var head = m.Groups["head"].Value;
            var tail = TakeNameTokens(m.Groups["tail"].Value, knownNames, maxTokens: 4, mustStartWithSpace: true);
            // A bare "Praxis"/"Schule" says nothing — needs a name after it or a compound like "Zahnarztpraxis".
            if (tail == null && GenericInstitutionWords.Contains(head)) continue;
            var full = tail == null ? head : $"{head} {tail}";
            if (IsSensibleName(full, knownNames)) return full;
        }

        return null;
    }

    private static string? ExpandDoctor(string org, List<string> knownNames)
    {
        var m = DoctorRegex.Match(org);
        if (!m.Success || m.Index != 0) return null;
        var name = TakeNameTokens(m.Groups["name"].Value, knownNames, maxTokens: 3, connectorsAllowed: false);
        return name == null ? null : $"{m.Groups["pre"].Value}{m.Groups["title"].Value} {name}".Trim();
    }

    private static readonly HashSet<string> GenericInstitutionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Praxis", "Arztpraxis", "Klinik", "Klinikum", "Kliniken", "Krankenhaus", "Zentrum", "Center", "Centre", "Schule",
        "Kita", "Kindergarten", "Apotheke", "Amt", "Behörde", "Bank", "Versicherung", "Therapie", "Institut", "Hotel",
        "Kanzlei", "Clinic", "Hospital", "Practice", "School", "Dental", "Ordination", "Ordinacija", "Klinika",
        "Bolnica", "Ambulanta", "Škola", "Vrtić", "Online-Praxis",
    };

    // Words that end a name: sentence words ("Wir freuen uns"), greetings, labels and the like.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "wir", "sie", "ihr", "ihre", "ihnen", "ihren", "ihrem", "ihrer", "uns", "bitte", "vielen", "dank", "danke", "liebe", "lieber",
        "sehr", "geehrte", "geehrter", "hallo", "guten", "tag", "freundliche", "freundlichen", "grüße", "grüßen", "gruß",
        "mit", "ist", "wurde", "wird", "hat", "haben", "findet", "statt", "bestätigt", "bestätigen", "freuen", "termin",
        "termine", "terminbestätigung", "terminerinnerung", "datum", "uhrzeit", "uhr", "zeit", "ort", "adresse",
        "anschrift", "telefon", "tel", "fax", "mobil", "e-mail", "email", "web", "www", "website", "hiermit", "heute",
        "morgen", "als", "wegen", "from", "is", "has", "was", "your", "we", "dear", "hello", "hi", "thank", "thanks",
        "please", "date", "time", "phone", "address", "confirmed", "reminder", "erinnerung", "patient", "patientin",
        "herr", "frau", "mr", "mrs", "ms", "vaš", "poštovani", "hvala", "vrijeme", "adresa", "insgesamt", "gesamt",
        "kalender", "calendar", "anhang", "voraus", "rahmen", "zusammenhang", "folgenden", "folgende",
        "montag", "dienstag", "mittwoch", "donnerstag", "freitag", "samstag", "sonntag", "monday", "tuesday",
        "wednesday", "thursday", "friday", "saturday", "sunday", "ponedjeljak", "utorak", "srijeda", "četvrtak",
        "petak", "subota", "nedjelja", "online", "video", "abgesagt", "storniert", "verschoben", "gmbh",
    };

    // Lower-case words allowed inside a name ("Praxis für Allgemeinmedizin", "Grundschule am Park").
    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
    {
        "für", "der", "des", "die", "am", "an", "im", "zur", "zum", "von", "und", "&", "i", "u", "za", "de", "of", "for", "the",
    };

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dr.", "Prof.", "med.", "dent.", "vet.", "St.", "univ.", "Dipl.", "rer.", "nat.", "habil.", "Dipl.-Med.",
    };

    private static readonly Regex StreetSuffixRegex = new(
        @"(?:straße|strasse|str\.|weg|allee|platz|gasse|ring|damm|ufer|chaussee|steig|pfad|hof|markt|kamp|graben|stieg|zeile|ulica)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Reads name-like tokens from the start of `raw` and stops at the first word that can't belong
    // to a name (sentence words, dates, digits, punctuation, street names, the family's own names).
    private static string? TakeNameTokens(string raw, List<string> knownNames, int maxTokens,
        bool connectorsAllowed = true, bool mustStartWithSpace = false)
    {
        if (mustStartWithSpace && !raw.StartsWith(' ')) return null;

        var knownTokens = knownNames
            .SelectMany(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var taken = new List<string>();

        for (var i = 0; i < tokens.Length && taken.Count < maxTokens; i++)
        {
            var token = tokens[i];
            var endsSentence = false;

            if (Abbreviations.Contains(token)) { taken.Add(token); continue; }
            if (Regex.IsMatch(token, @"[,;:!?|()\[\]""„“”»«/]$|\.$")) { endsSentence = true; token = token.TrimEnd(',', ';', ':', '!', '?', '|', ')', ']', '"', '“', '”', '»', '«', '/', '.'); }
            token = token.TrimStart('(', '[', '"', '„', '“', '«', '»');
            if (token.Length == 0) break;

            if (token == "-" || token == "–" || token == "|") break;
            if (char.IsDigit(token[0]) || token.Contains('@') || token.Contains("http", StringComparison.OrdinalIgnoreCase)) break;
            if (StopWords.Contains(token) || MonthNames.ContainsKey(token) || knownTokens.Contains(token)) break;
            if (StreetSuffixRegex.IsMatch(token)) break;

            if (char.IsLower(token[0]))
            {
                if (!connectorsAllowed || !Connectors.Contains(token)) break;
            }
            else if (!char.IsLetter(token[0]) && token != "&") break;

            taken.Add(token);
            if (endsSentence) break;
        }

        // Never end on a connector ("Praxis für").
        while (taken.Count > 0 && Connectors.Contains(taken[^1])) taken.RemoveAt(taken.Count - 1);
        return taken.Count == 0 ? null : string.Join(" ", taken);
    }

    private static bool IsSensibleName(string name, List<string> knownNames)
    {
        name = name.Trim();
        if (name.Length < 3 || name.Length > 70) return false;
        if (!char.IsUpper(name[0])) return false;
        if (Regex.IsMatch(name, @"[{}<>=;@\\#*_]|https?:|www\.", RegexOptions.IgnoreCase)) return false;
        if (name.Count(char.IsLetter) < 3) return false;

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.All(w => StopWords.Contains(w.Trim('.', ',')) || MonthNames.ContainsKey(w.Trim('.')))) return false;
        if (words.Length == 1 && GenericInstitutionWords.Contains(words[0])) return false;
        if (words.Length <= 2 && words.All(w => Abbreviations.Contains(w))) return false;
        if (PlatformNames.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase))) return false;

        // The family member the appointment is for is not the organizer.
        return !knownNames.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(k.Split(' ')[0], name, StringComparison.OrdinalIgnoreCase));
    }

    // Sender-domain rules take priority (e.g. any Doctolib confirmation is health regardless of
    // wording), then keyword groups over the subject+body. Categories match the app's own
    // Kategorie dropdown values (Termine.tsx APPOINTMENT_CATEGORIES) so scanned appointments land
    // in the same buckets a user would pick by hand. Returns null when nothing matches, so the
    // caller can fall back to its own attendee-based default.
    private static readonly (string Domain, string Category)[] SenderDomainCategories =
    [
        ("doctolib", "health"),
    ];

    private static readonly (string Category, Regex Keywords)[] CategoryKeywordGroups =
    [
        ("health", new Regex(@"\b(arzt|zahnarzt|kinderarzt|hausarzt|impf|vorsorge|sprechstunde|apotheke|klinik|praxis|doctor|dentist|physician|ljekar|doktor|pregled|zdravlj|bolnic|ordinacij|ambulant)\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("school", new Regex(@"\b(schule|kindergarten|kita|elternabend|elterngespräch|lehrer|school|teacher|škol\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("authority", new Regex(@"\b(finanzamt|behörde|bürgeramt|ausländerbehörde|amt für|rathaus)\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("finance", new Regex(@"\b(bank|sparkasse|versicherung|rechnung|invoice|zahlung)\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("travel", new Regex(@"\b(flug|hotel|buchungsbestätigung|booking\.com|airbnb|reise|flight|reservation)\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("home", new Regex(@"\b(handwerker|reparatur|lieferung|installateur|elektriker)\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    private static string? DetectCategory(string combined, string senderDomain)
    {
        foreach (var (domain, category) in SenderDomainCategories)
        {
            if (senderDomain.Contains(domain, StringComparison.OrdinalIgnoreCase)) return category;
        }

        foreach (var (category, keywords) in CategoryKeywordGroups)
        {
            if (keywords.IsMatch(combined)) return category;
        }

        return null;
    }

    // Only a real postal address counts as a location: "<Street> <No>, <PLZ> <City>" with street and
    // PLZ on the same line or on two consecutive lines. Anything else ("Ort: siehe Anhang", leaked
    // markup, ...) is ignored and the field stays empty. Failing an address, video-call keywords
    // mean the appointment has no physical location at all.
    private static readonly Regex PostalAddressRegex = new(
        @"(?<street>\p{Lu}[\p{L}.\-]*(?:[ ][\p{L}.\-]+){0,4})[ ]+(?<no>\d{1,4}[ ]?[a-zA-Z]?(?:[ ]?[-–/][ ]?\d{1,4}[a-zA-Z]?)?|bb)[ ]*(?:,[ ]*|\n|[ ]+)(?:D-|DE-|BA-)?(?<plz>\d{5})[ ]+(?<city>\p{Lu}[\p{L}\-]+(?:[ ](?:am|an der|ob der|im|in|\(\p{L}+\)|\p{Lu}[\p{L}\-]+)){0,2})",
        RegexOptions.Compiled);

    private static readonly HashSet<string> StreetPrefixWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "am", "an", "der", "den", "dem", "im", "in", "auf", "zum", "zur", "alte", "alter", "neue", "neuer", "große",
        "großer", "kleine", "kleiner", "st.", "sankt", "unter", "obere", "untere", "hinter", "vor", "bei", "ulica",
    };

    private static readonly HashSet<string> NonStreetWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "telefon", "tel", "tel.", "fax", "nr", "nr.", "nummer", "kundennummer", "rechnung", "uhr", "termin", "postfach",
        "plz", "iban", "bic", "hrb", "ust", "steuernummer", "betrag", "eur", "euro", "mobil", "handy",
    };

    private static readonly Regex OnlineKeywordRegex = new(
        @"\b(zoom|teams|videosprechstunde|videokonferenz|video call|online meeting|webinar)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? DetectLocation(string combined)
    {
        foreach (Match m in PostalAddressRegex.Matches(combined))
        {
            var street = TrimStreet(m.Groups["street"].Value);
            if (street == null) continue;

            var city = m.Groups["city"].Value.Trim();
            var cityWords = city.Split(' ');
            if (cityWords.Any(w => StopWords.Contains(w) || NonStreetWords.Contains(w))) city = cityWords[0];
            if (StopWords.Contains(city) || NonStreetWords.Contains(city)) continue;

            var no = Regex.Replace(m.Groups["no"].Value, @"\s+", "");
            return $"{street} {no}, {m.Groups["plz"].Value} {city}";
        }

        return OnlineKeywordRegex.IsMatch(combined) ? "Online" : null;
    }

    // The street group can swallow words in front of the street ("Praxis Dr. Müller Hauptstraße").
    // Keep the street-name word plus only typical leading words ("Am", "Alte", ...); without a
    // recognizable street suffix keep the last words that aren't titles or labels.
    private static string? TrimStreet(string raw)
    {
        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Any(w => NonStreetWords.Contains(w) || char.IsDigit(w[0]))) return null;

        var suffixIndex = words.FindLastIndex(w => StreetSuffixRegex.IsMatch(w));
        int start;
        if (suffixIndex >= 0)
        {
            start = suffixIndex;
            while (start > 0 && StreetPrefixWords.Contains(words[start - 1])) start--;
        }
        else
        {
            start = words.Count - 1;
            while (start > 0 && words.Count - start < 4
                   && !Abbreviations.Contains(words[start - 1]) && !StopWords.Contains(words[start - 1])
                   && !GenericInstitutionWords.Contains(words[start - 1])) start--;
        }

        var street = string.Join(" ", words.Skip(start));
        if (street.Length < 3 || !char.IsUpper(street[0])) return null;
        if (StopWords.Contains(street) || Abbreviations.Contains(street) || MonthNames.ContainsKey(street.TrimEnd('.'))) return null;
        return street;
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
