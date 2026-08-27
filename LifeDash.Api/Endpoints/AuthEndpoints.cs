using System.Security.Claims;
using Google.Apis.Auth;
using LifeDash.Api.Data;
using LifeDash.Api.Models;
using LifeDash.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace LifeDash.Api.Endpoints;

public record GoogleLoginRequest(string IdToken);
public record AuthResponse(string Token, int UserId, string Email, string DisplayName);

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("auth");

        group.MapPost("/google", async (GoogleLoginRequest req, LifeDashContext db,
            TokenService tokens, IConfiguration cfg, CancellationToken ct) =>
        {
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

            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null)
            {
                user = new User
                {
                    Email = email,
                    DisplayName = string.IsNullOrWhiteSpace(payload.Name) ? email : payload.Name,
                    PasswordHash = "GOOGLE"
                };
                db.Users.Add(user);
                await db.SaveChangesAsync(ct);
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
