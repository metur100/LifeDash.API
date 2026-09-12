using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace LifeDash.Api.Services;

public enum MicrosoftMailConnectionStatus { Disconnected, Pending, Connected, Failed }

public record DeviceCodeInfo(string UserCode, string VerificationUrl, string Message, DateTime ExpiresAtUtc);

// Handles the Microsoft OAuth (XOAUTH2) sign-in used to read the configured mailbox over IMAP,
// now that Microsoft has retired Basic Auth (app passwords) for Outlook.com/Live/Hotmail accounts.
// A single owner mailbox is assumed, so the device-code flow state and token cache are process-wide.
public class MicrosoftMailAuthService
{
    private readonly MailTrackingOptions _options;
    private readonly ILogger<MicrosoftMailAuthService> _logger;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _appLock = new(1, 1);
    private IPublicClientApplication? _app;

    private volatile MicrosoftMailConnectionStatus _pendingStatus = MicrosoftMailConnectionStatus.Disconnected;
    private string? _pendingError;
    private CancellationTokenSource? _deviceCodeCts;

    public MicrosoftMailAuthService(IOptions<MailTrackingOptions> options, ILogger<MicrosoftMailAuthService> logger)
    {
        _options = options.Value;
        _logger = logger;

        var configuredPath = string.IsNullOrWhiteSpace(_options.MicrosoftOAuth.TokenCachePath)
            ? "App_Data/ms-mail-token-cache.bin"
            : _options.MicrosoftOAuth.TokenCachePath;
        _cachePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    private string[] Scopes => _options.MicrosoftOAuth.Scopes is { Length: > 0 } s
        ? s
        : ["https://outlook.office.com/IMAP.AccessAsUser.All", "offline_access"];

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        if (_app != null) return _app;
        await _appLock.WaitAsync();
        try
        {
            if (_app != null) return _app;

            var clientId = _options.MicrosoftOAuth.ClientId?.Trim();
            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("MailTracking:MicrosoftOAuth:ClientId ist nicht konfiguriert.");

            var tenant = string.IsNullOrWhiteSpace(_options.MicrosoftOAuth.TenantId) ? "consumers" : _options.MicrosoftOAuth.TenantId;
            var pca = PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority($"https://login.microsoftonline.com/{tenant}/")
                .Build();

            var cacheDir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(cacheDir)) Directory.CreateDirectory(cacheDir);

            pca.UserTokenCache.SetBeforeAccessAsync(async args =>
            {
                if (File.Exists(_cachePath))
                    args.TokenCache.DeserializeMsalV3(await File.ReadAllBytesAsync(_cachePath));
            });
            pca.UserTokenCache.SetAfterAccessAsync(async args =>
            {
                if (args.HasStateChanged)
                    await File.WriteAllBytesAsync(_cachePath, args.TokenCache.SerializeMsalV3());
            });

            _app = pca;
            return _app;
        }
        finally
        {
            _appLock.Release();
        }
    }

    // Starts the device-code sign-in flow and returns the code the user must enter at
    // microsoft.com/devicelogin. The actual token acquisition keeps running in the background
    // until the user completes it (or it times out) — poll GetPendingStatus() for the outcome.
    public async Task<DeviceCodeInfo> StartDeviceCodeSignInAsync(CancellationToken ct)
    {
        var app = await GetAppAsync();

        _deviceCodeCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _deviceCodeCts = cts;

        var deviceCodeTcs = new TaskCompletionSource<DeviceCodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStatus = MicrosoftMailConnectionStatus.Pending;
        _pendingError = null;

        var executeTask = app.AcquireTokenWithDeviceCode(Scopes, result =>
        {
            deviceCodeTcs.TrySetResult(result);
            return Task.CompletedTask;
        }).ExecuteAsync(cts.Token);

        _ = executeTask.ContinueWith(t =>
        {
            if (!ReferenceEquals(cts, _deviceCodeCts)) return; // superseded by a newer sign-in attempt

            if (t.IsCompletedSuccessfully)
            {
                _pendingStatus = MicrosoftMailConnectionStatus.Connected;
            }
            else
            {
                _pendingStatus = MicrosoftMailConnectionStatus.Failed;
                _pendingError = t.Exception?.GetBaseException().Message ?? "Anmeldung abgebrochen oder abgelaufen.";
                _logger.LogWarning(t.Exception, "Microsoft device code sign-in failed.");
            }
        }, TaskScheduler.Default);

        try
        {
            var deviceCode = await deviceCodeTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            return new DeviceCodeInfo(deviceCode.UserCode, deviceCode.VerificationUrl, deviceCode.Message, deviceCode.ExpiresOn.UtcDateTime);
        }
        catch (TimeoutException)
        {
            _pendingStatus = MicrosoftMailConnectionStatus.Failed;
            _pendingError = "Microsoft hat keinen Gerätecode zurückgegeben.";
            throw new InvalidOperationException(_pendingError);
        }
    }

    public (MicrosoftMailConnectionStatus Status, string? Error) GetPendingStatus() => (_pendingStatus, _pendingError);

    public async Task<string?> TryGetAccessTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.MicrosoftOAuth.ClientId)) return null;

        try
        {
            var app = await GetAppAsync();
            var accounts = await app.GetAccountsAsync();
            var account = accounts.FirstOrDefault();
            if (account is null) return null;

            var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            _pendingStatus = MicrosoftMailConnectionStatus.Connected;
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task DisconnectAsync()
    {
        var app = await GetAppAsync();
        foreach (var account in await app.GetAccountsAsync())
            await app.RemoveAsync(account);
        _pendingStatus = MicrosoftMailConnectionStatus.Disconnected;
        _pendingError = null;
    }
}
