namespace LifeDash.Api.Services;

public class MailTrackingOptions
{
    public string Email { get; set; } = "medin_93@live.com";

    // Legacy Basic Auth (username + app password). Microsoft retired this for Outlook.com/
    // Live/Hotmail consumer mailboxes in September 2024 — IMAP now only accepts XOAUTH2, so this
    // field only still works for providers that still support plain LOGIN (e.g. Gmail app passwords).
    public string AppPassword { get; set; } = "";

    public string ImapHost { get; set; } = "outlook.office365.com";
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;

    // Mail folders to scan for package tracking emails. "INBOX" is filtered by subject keyword
    // (it mixes everything), any other folder (e.g. a mail rule that files DHL mail into "DHL")
    // is assumed already-curated and scanned in full.
    public string[] FoldersToScan { get; set; } = ["INBOX", "DHL"];

    // Safety cap per folder so a huge mailbox can't turn a scan into a multi-thousand-message fetch.
    public int MaxMessagesPerFolder { get; set; } = 300;

    // Everyday scans (ScanMailboxAsync(fullHistory: false)) only look this far back.
    public int DefaultLookbackDays { get; set; } = 7;

    // Which account owns the mailbox-scanned packages/appointments when the scan is triggered by
    // the anonymous cron endpoint (POST /api/jobs/mail-scan) instead of a logged-in user. Find your
    // own id via GET /api/auth/me while signed in. 0 = not configured.
    public int OwnerUserId { get; set; } = 0;

    public MicrosoftOAuthOptions MicrosoftOAuth { get; set; } = new();
}

public class MicrosoftOAuthOptions
{
    // Azure App registration "Application (client) ID". Required for XOAUTH2 against Outlook.com.
    public string ClientId { get; set; } = "";

    // "consumers" restricts sign-in to personal Microsoft accounts (outlook.com/live.com/hotmail.com).
    public string TenantId { get; set; } = "consumers";

    public string[] Scopes { get; set; } = ["https://outlook.office.com/IMAP.AccessAsUser.All", "offline_access"];

    // Where the persisted MSAL token cache (refresh token) is stored, relative to the app base directory.
    public string TokenCachePath { get; set; } = "App_Data/ms-mail-token-cache.bin";
}
