using System.Security.Claims;
using Google.Apis.Auth;
using LifeDash.Api.Data;
using LifeDash.Api.Models;
using LifeDash.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LifeDash.Api.Endpoints;

public record GoogleLoginRequest(string IdToken);
public record AuthResponse(string Token, int UserId, string Email, string DisplayName);

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("auth");

        group.MapPost("/google", async (GoogleLoginRequest req, LifeDashContext db,
            TokenService tokens, IConfiguration cfg, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AuthEndpoints");
            var clientId = cfg["GoogleAuth:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
                return Results.BadRequest(new { error = "GoogleAuth:ClientId fehlt in der API-Konfiguration." });
            if (string.IsNullOrWhiteSpace(req.IdToken))
                return Results.BadRequest(new { error = "IdToken fehlt." });

            GoogleJsonWebSignature.Payload payload;
            try
            {
                payload = await GoogleJsonWebSignature.ValidateAsync(req.IdToken,
                    new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { clientId } });
            }
            catch
            {
                return Results.Json(new { error = "Google-Anmeldung konnte nicht verifiziert werden." }, statusCode: 401);
            }

            var email = payload.Email?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email))
                return Results.Json(new { error = "Google-Konto liefert keine E-Mail." }, statusCode: 401);
            if (!payload.EmailVerified)
                return Results.Json(new { error = "Google-Konto hat keine verifizierte E-Mail." }, statusCode: 401);

            var displayName = string.IsNullOrWhiteSpace(payload.Name) ? email : payload.Name.Trim();
            if (displayName.Length > 128)
                displayName = displayName[..128];

            User? user;
            try
            {
                user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Google login failed while reading user {Email}.", email);
                return Results.Json(new { error = "Datenbank nicht erreichbar. Bitte versuche es gleich erneut." }, statusCode: 503);
            }

            if (user is null)
            {
                user = new User
                {
                    Email = email,
                    DisplayName = displayName,
                    PasswordHash = "GOOGLE"
                };
                db.Users.Add(user);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex)
                {
                    logger.LogWarning(ex, "Google login user create conflict for {Email}. Retrying load.", email);
                    db.Entry(user).State = EntityState.Detached;
                    user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
                    if (user is null)
                        return Results.Json(new { error = "Benutzer konnte nicht erstellt werden. Bitte versuche es erneut." }, statusCode: 500);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Google login failed while creating user {Email}.", email);
                    return Results.Json(new { error = "Datenbank nicht erreichbar. Bitte versuche es gleich erneut." }, statusCode: 503);
                }
            }
            else if (!string.Equals(user.DisplayName, displayName, StringComparison.Ordinal))
            {
                user.DisplayName = displayName;
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not update display name for {Email}.", email);
                }
            }

            return Results.Ok(new AuthResponse(tokens.Create(user), user.Id, user.Email, user.DisplayName));
        });

        group.MapGet("/me", (ClaimsPrincipal u) => Results.Ok(new
        {
            userId = u.UserId(),
            email = u.FindFirstValue(ClaimTypes.Email),
            displayName = u.FindFirstValue("name")
        })).RequireAuthorization();
    }
}
