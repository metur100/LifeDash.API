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

            if (input is FamilyMember fm)
                await SyncBirthdayAsync(fm, db, ct);

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

            if (existing is FamilyMember fm)
                await SyncBirthdayAsync(fm, db, ct);

            return Results.Ok(existing);
        });

        group.MapDelete("/{id:int}", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var existing = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            if (existing is null) return Results.NotFound();

            if (typeof(T) == typeof(FamilyMember))
            {
                var userId = u.UserId();

                await db.FamilyMembers
                    .Where(x => x.UserId == userId && x.RelatedToFamilyMemberId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.RelatedToFamilyMemberId, (int?)null), ct);

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

    // Keeps a FamilyMember's birthday mirrored into Wichtige Anlässe (ImportantDates) so adding a
    // person's birth date automatically gives them a yearly reminder, without the user having to
    // create it by hand. Only creates/updates - never deletes - so clearing a birth date later
    // doesn't destroy a reminder the user may have since customized, matching the existing
    // "null the FK, don't cascade-delete" convention used elsewhere for FamilyMember links.
    private static async Task SyncBirthdayAsync(FamilyMember fm, LifeDashContext db, CancellationToken ct)
    {
        if (fm.BirthDate is not { } birthDate) return;

        var existing = await db.ImportantDates.FirstOrDefaultAsync(
            d => d.UserId == fm.UserId && d.FamilyMemberId == fm.Id && d.Category == "birthday", ct);

        if (existing is null)
        {
            db.ImportantDates.Add(new ImportantDate
            {
                UserId = fm.UserId,
                FamilyMemberId = fm.Id,
                Title = $"Geburtstag {fm.FullName}".Trim(),
                Category = "birthday",
                DateValue = birthDate,
                RepeatsYearly = true,
            });
        }
        else
        {
            existing.Title = $"Geburtstag {fm.FullName}".Trim();
            existing.DateValue = birthDate;
        }

        await db.SaveChangesAsync(ct);
    }
}
