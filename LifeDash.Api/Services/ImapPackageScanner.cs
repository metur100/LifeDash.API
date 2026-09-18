using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace LifeDash.Api.Services;

public record ScannedPackageDto(
    string Id,
    string Title,
    string Carrier,
    string TrackingNumber,
    string? Sender,
    string Status,
    string? ExpectedDelivery,
    string LatestEvent,
    string LatestEventTime,
    string RecipientEmail,
    string Source,
    string UpdatedAt
);

public class ImapPackageScanner
{
    private readonly MailTrackingOptions _options;
    private readonly MicrosoftMailAuthService _msAuth;
    private readonly ILogger<ImapPackageScanner> _logger;

    public ImapPackageScanner(IOptions<MailTrackingOptions> options, MicrosoftMailAuthService msAuth, ILogger<ImapPackageScanner> logger)
    {
        _options = options.Value;
        _msAuth = msAuth;
        _logger = logger;
    }

    // fullHistory: true does a one-off deep scan of each folder's full history (slow, meant for
    // the initial backfill right after connecting). false (the normal, everyday case) only looks
    // at the last MailTracking:DefaultLookbackDays days, which is fast and avoids Exchange's IMAP
    // throttling on repeated scans.
    public async Task<List<ScannedPackageDto>> ScanMailboxAsync(bool fullHistory = false, CancellationToken ct = default)
    {
        var email = _options.Email?.Trim();
        var password = _options.AppPassword?.Replace(" ", "").Trim();

        // Microsoft retired Basic Auth (app passwords) for Outlook.com/Live/Hotmail IMAP in
        // September 2024 — prefer the OAuth (XOAUTH2) token when a Microsoft sign-in is connected,
        // and only fall back to the legacy password login for providers that still support it.
        var accessToken = await _msAuth.TryGetAccessTokenAsync(ct);

        if (string.IsNullOrWhiteSpace(email) || (accessToken is null && string.IsNullOrWhiteSpace(password)))
        {
            _logger.LogWarning("Neither a Microsoft OAuth connection nor an IMAP app password is configured for package tracking.");
            return new List<ScannedPackageDto>();
        }

        var results = new Dictionary<string, ScannedPackageDto>(StringComparer.OrdinalIgnoreCase);
        var folderNames = _options.FoldersToScan is { Length: > 0 } configured ? configured : ["INBOX"];
        var maxPerFolder = Math.Max(1, _options.MaxMessagesPerFolder);
        var lookbackSince = DateTime.UtcNow.AddDays(-Math.Max(1, _options.DefaultLookbackDays));

        // Exchange Online enforces some per-session cap on total IMAP commands/duration — scanning
        // several folders back-to-back on one connection can get disconnected mid-way (observed even
        // with a single SEARCH + a few hundred FETCHes per folder). Using one fresh connection per
        // folder keeps each session short, and a failure in one folder never discards results already
        // found in another.
        foreach (var folderName in folderNames)
        {
            try
            {
                var folderResults = await ScanFolderAsync(email, accessToken, password, folderName, fullHistory, lookbackSince, maxPerFolder, ct);
                foreach (var parsed in folderResults)
                {
                    var key = parsed.TrackingNumber.ToUpperInvariant();
                    if (!results.ContainsKey(key) || parsed.Status == "delivered" || parsed.Status == "out_for_delivery")
                        results[key] = parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scanning folder '{Folder}' failed; keeping results from other folders.", folderName);
            }
        }

        return results.Values.OrderByDescending(p => p.ExpectedDelivery ?? "").ToList();
    }

    private async Task<List<ScannedPackageDto>> ScanFolderAsync(
        string email, string? accessToken, string? password, string folderName,
        bool fullHistory, DateTime lookbackSince, int maxPerFolder, CancellationToken ct)
    {
        var found = new List<ScannedPackageDto>();

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

        var uids = fullHistory
            ? await folder.SearchAsync(SearchQuery.All, ct)
            : await folder.SearchAsync(SearchQuery.DeliveredAfter(lookbackSince), ct);
        var targetUids = uids.OrderByDescending(u => u.Id).Take(maxPerFolder).ToList();

        foreach (var uid in targetUids)
        {
            try
            {
                var message = await folder.GetMessageAsync(uid, ct);
                if (message == null) continue;

                var parsed = ParsePackageFromMessage(message, email);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.TrackingNumber))
                    found.Add(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse message UID {Uid} in folder '{Folder}'", uid, folderName);
            }
        }

        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch
        {
            // best-effort — we already have what we came for
        }

        return found;
    }

    private ScannedPackageDto? ParsePackageFromMessage(MimeMessage message, string recipientEmail)
    {
        var from = message.From.ToString();
        var subject = message.Subject ?? "";
        var textBody = message.TextBody ?? "";
        var htmlBody = message.HtmlBody ?? "";
        var combined = $"{from} {subject} {textBody} {htmlBody}";

        // 1. Detect carrier
        string carrier = DetectCarrier(from, subject, combined);

        // 2. Extract tracking number
        string? trackingNumber = ExtractTrackingNumber(carrier, combined);
        if (string.IsNullOrWhiteSpace(trackingNumber))
        {
            return null;
        }

        // 3. Detect merchant/sender
        string? senderName = DetectSender(from, subject);

        // 4. Derive clean title
        string title = subject.Replace("Fwd:", "", StringComparison.OrdinalIgnoreCase)
                              .Replace("Wg:", "", StringComparison.OrdinalIgnoreCase)
                              .Replace("Re:", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!string.IsNullOrWhiteSpace(senderName))
        {
            title = $"{senderName} Lieferung";
        }
        else if (title.Length > 45)
        {
            title = $"Sendung {carrier.ToUpper()} ({trackingNumber[^Math.Min(6, trackingNumber.Length)..]})";
        }

        // 5. Detect status
        string status = DetectStatus(combined);

        // 6. Detect expected delivery date
        string? expectedDate = ExtractDeliveryDate(combined, message.Date.DateTime);

        var dateStr = message.Date.LocalDateTime.ToString("dd.MM., HH:mm") + " Uhr";

        return new ScannedPackageDto(
            Id: $"pkg-{trackingNumber}",
            Title: title,
            Carrier: carrier,
            TrackingNumber: trackingNumber,
            Sender: senderName,
            Status: status,
            ExpectedDelivery: expectedDate,
            LatestEvent: subject,
            LatestEventTime: dateStr,
            RecipientEmail: recipientEmail,
            Source: "email_scan",
            UpdatedAt: DateTime.UtcNow.ToString("o")
        );
    }

    private static string DetectCarrier(string from, string subject, string content)
    {
        // Deutsche Post letter mail ("Brief"/"Einschreiben") — checked before the DHL/Deutsche
        // Post Paket check below, since deutschepost.de also sends DHL parcel notifications.
        var mentionsBrief = Regex.IsMatch(subject, @"\b(brief|einschreiben)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(content, @"\b(brief|einschreiben)\b", RegexOptions.IgnoreCase);
        var mentionsPaket = Regex.IsMatch(content, @"\bpaket\b", RegexOptions.IgnoreCase);
        if (mentionsBrief && !mentionsPaket) return "deutschepost";

        if (Regex.IsMatch(from, @"@(dhl|deutschepost|paket)\.", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(subject, @"\bdhl\b", RegexOptions.IgnoreCase))
            return "dhl";

        if (Regex.IsMatch(from, @"@dpd\.", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(subject, @"\bdpd\b", RegexOptions.IgnoreCase))
            return "dpd";

        if (Regex.IsMatch(from, @"@(myhermes|hermesworld)\.", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(subject, @"\bhermes\b", RegexOptions.IgnoreCase))
            return "hermes";

        if (Regex.IsMatch(from, @"@gls-", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(subject, @"\bgls\b", RegexOptions.IgnoreCase))
            return "gls";

        if (Regex.IsMatch(content, @"\bdhl\b", RegexOptions.IgnoreCase)) return "dhl";
        if (Regex.IsMatch(content, @"\bdpd\b", RegexOptions.IgnoreCase)) return "dpd";
        if (Regex.IsMatch(content, @"\bhermes\b", RegexOptions.IgnoreCase)) return "hermes";
        if (Regex.IsMatch(content, @"\bgls\b", RegexOptions.IgnoreCase)) return "gls";

        return "dhl";
    }

    private static string? ExtractTrackingNumber(string carrier, string content)
    {
        Match match;
        switch (carrier.ToLowerInvariant())
        {
            case "dhl":
                match = Regex.Match(content, @"(?:sendungsnummer|sendungscode|tracking(?:-?nr|\.?| number)?|sendung)[:\s#]*([0-9]{12,20}|JJD[0-9A-Z]{12,20}|JD[0-9]{18})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(0034[0-9]{16})\b");
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(JJD[0-9]{16,20})\b", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                break;

            case "dpd":
                match = Regex.Match(content, @"(?:paketnummer|sendungsnummer|tracking(?:-?nr|\.?| number)?|dpd)[:\s#]*([0-9]{14})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(0142[0-9]{10}|0144[0-9]{10}|0134[0-9]{10}|0154[0-9]{10})\b");
                if (match.Success) return match.Groups[1].Value.Trim();
                break;

            case "hermes":
                match = Regex.Match(content, @"(?:sendungsnummer|quittungsnummer|auftragsnummer|tracking)[:\s#]*([0-9]{11,14}|H100[0-9]{16})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(H100[0-9]{16})\b", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(01[0-9]{12})\b");
                if (match.Success) return match.Groups[1].Value.Trim();
                break;

            case "gls":
                match = Regex.Match(content, @"(?:paketnummer|sendungsnummer|tracking(?:-?nr|\.?| number)?|gls)[:\s#]*([0-9A-Z]{8,12})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(GLS[0-9A-Z]{8,10})\b", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                break;

            case "deutschepost":
                // Einschreiben/registered-letter reference numbers follow the UPU S10 format
                // (2 letters + 9 digits + 2 letters, e.g. "RR123456785DE").
                match = Regex.Match(content, @"(?:sendungsnummer|einschreiben-?nr\.?|referenznummer)[:\s#]*([A-Z]{2}[0-9]{9}[A-Z]{2})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim().ToUpperInvariant();
                match = Regex.Match(content, @"\b([A-Z]{2}[0-9]{9}[A-Z]{2})\b", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim().ToUpperInvariant();
                break;
        }

        // Generic fallback — require a handful of digits so it doesn't latch onto a stray word
        // near "tracking"/"Sendung" in unrelated boilerplate (e.g. a legal footer).
        match = Regex.Match(content, @"(?:sendungsnummer|tracking(?:-?nr|\.?| number)?|tracking code)[:\s#]*([0-9A-Za-z]{10,24})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var candidate = match.Groups[1].Value.Trim();
            if (candidate.Count(char.IsDigit) >= 3) return candidate;
        }

        return null;
    }

    private static string? DetectSender(string from, string subject)
    {
        var text = $"{from} {subject}";
        if (Regex.IsMatch(text, @"amazon", RegexOptions.IgnoreCase)) return "Amazon";
        if (Regex.IsMatch(text, @"zalando", RegexOptions.IgnoreCase)) return "Zalando";
        if (Regex.IsMatch(text, @"otto", RegexOptions.IgnoreCase)) return "OTTO";
        if (Regex.IsMatch(text, @"mediamarkt", RegexOptions.IgnoreCase)) return "MediaMarkt";
        if (Regex.IsMatch(text, @"saturn", RegexOptions.IgnoreCase)) return "Saturn";
        if (Regex.IsMatch(text, @"ikea", RegexOptions.IgnoreCase)) return "IKEA";
        if (Regex.IsMatch(text, @"notebooksbilliger", RegexOptions.IgnoreCase)) return "Notebooksbilliger";
        if (Regex.IsMatch(text, @"ebay", RegexOptions.IgnoreCase)) return "eBay";
        if (Regex.IsMatch(text, @"asos", RegexOptions.IgnoreCase)) return "ASOS";
        if (Regex.IsMatch(text, @"about\s*you", RegexOptions.IgnoreCase)) return "About You";
        return null;
    }

    private static string DetectStatus(string content)
    {
        if (Regex.IsMatch(content, @"zugestellt|erfolgreich übergeben|an nachbar|abholbereit", RegexOptions.IgnoreCase))
            return "delivered";

        if (Regex.IsMatch(content, @"in zustellung|zustellfahrzeug|wird heute zugestellt|heute geliefert", RegexOptions.IgnoreCase))
            return "out_for_delivery";

        if (Regex.IsMatch(content, @"im paketzentrum|weitertransport|logistikzentrum|sortiert|auf dem weg", RegexOptions.IgnoreCase))
            return "in_transit";

        if (Regex.IsMatch(content, @"angekündigt|elektronisch übermittelt|auftragsdaten erhalten", RegexOptions.IgnoreCase))
            return "announced";

        return "in_transit";
    }

    private static string? ExtractDeliveryDate(string content, DateTime fallbackDate)
    {
        var isoMatch = Regex.Match(content, @"\b(202[4-9]-[01][0-9]-[0-3][0-9])\b");
        if (isoMatch.Success) return isoMatch.Groups[1].Value;

        var deMatch = Regex.Match(content, @"\b([0-3]?[0-9])\.([0-1]?[0-9])\.(202[4-9])?\b");
        if (deMatch.Success)
        {
            var day = deMatch.Groups[1].Value.PadLeft(2, '0');
            var month = deMatch.Groups[2].Value.PadLeft(2, '0');
            var year = !string.IsNullOrEmpty(deMatch.Groups[3].Value) ? deMatch.Groups[3].Value : DateTime.UtcNow.Year.ToString();
            return $"{year}-{month}-{day}";
        }

        if (Regex.IsMatch(content, @"heute|voraussichtlich heute", RegexOptions.IgnoreCase))
            return DateTime.UtcNow.ToString("yyyy-MM-dd");

        if (Regex.IsMatch(content, @"morgen|voraussichtlich morgen", RegexOptions.IgnoreCase))
            return DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        return fallbackDate.ToString("yyyy-MM-dd");
    }
}
