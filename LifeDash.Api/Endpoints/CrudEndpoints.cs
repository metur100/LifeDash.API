using System.Security.Claims;
using LifeDash.Api.Data;
using LifeDash.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LifeDash.Api.Endpoints;

public static class CrudEndpoints
{
    public static int UserId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? user.FindFirstValue("sub")
                  ?? throw new UnauthorizedAccessException());

    /// Registers GET/POST/PUT/DELETE for any user-owned entity.
    public static RouteGroupBuilder MapOwned<T>(this IEndpointRouteBuilder app, string route)
        where T : OwnedEntity
    {
        var group = app.MapGroup(route).RequireAuthorization().WithTags(route.Trim('/'));

        group.MapGet("/", async (LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
            Results.Ok(await db.Set<T>().AsNoTracking()
                .Where(x => x.UserId == u.UserId()).ToListAsync(ct)));

        group.MapGet("/{id:int}", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var e = await db.Set<T>().AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            return e is null ? Results.NotFound() : Results.Ok(e);
        });

        group.MapPost("/", async (T input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            input.Id = 0;
            input.UserId = u.UserId();
            db.Set<T>().Add(input);
            await db.SaveChangesAsync(ct);
            return Results.Created($"{route}/{input.Id}", input);
        });

        group.MapPut("/{id:int}", async (int id, T input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var existing = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            if (existing is null) return Results.NotFound();
            input.Id = id;
            input.UserId = existing.UserId;
            db.Entry(existing).CurrentValues.SetValues(input);
            await db.SaveChangesAsync(ct);
            return Results.Ok(existing);
        });

        group.MapDelete("/{id:int}", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var existing = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            if (existing is null) return Results.NotFound();

            if (typeof(T) == typeof(FamilyMember))
            {
                var userId = u.UserId();

                await db.Documents
                    .Where(x => x.UserId == userId && x.FamilyMemberId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.FamilyMemberId, (int?)null), ct);

                await db.AppointmentAttendees
                    .Where(x => x.FamilyMemberId == id && x.Appointment!.UserId == userId)
                    .ExecuteDeleteAsync(ct);

                await db.ImportantDates
                    .Where(x => x.UserId == userId && x.FamilyMemberId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.FamilyMemberId, (int?)null), ct);

                await db.AuthorityCases
                    .Where(x => x.UserId == userId && x.FamilyMemberId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.FamilyMemberId, (int?)null), ct);

                await db.Subscriptions
                    .Where(x => x.UserId == userId && x.FamilyMemberId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.FamilyMemberId, (int?)null), ct);
            }

            db.Remove(existing);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return group;
    }
}
