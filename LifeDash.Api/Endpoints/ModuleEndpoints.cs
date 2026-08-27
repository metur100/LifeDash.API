using System.Security.Claims;
using LifeDash.Api.Data;
using LifeDash.Api.Models;
using LifeDash.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace LifeDash.Api.Endpoints;

public static class ModuleEndpoints
{
    public static void MapLifeDash(this IEndpointRouteBuilder app)
    {
        // ---- plain CRUD modules ----
        app.MapOwned<FamilyMember>("/api/family-members");
        app.MapOwned<Appointment>("/api/appointments");
        app.MapOwned<ImportantDate>("/api/important-dates");
        app.MapOwned<Income>("/api/incomes");
        app.MapOwned<FixedCost>("/api/fixed-costs");
        app.MapOwned<Subscription>("/api/subscriptions");
        app.MapOwned<Payment>("/api/payments");
        app.MapOwned<HomeItem>("/api/home-items");
        app.MapOwned<TaskItem>("/api/tasks");
        app.MapOwned<Document>("/api/documents");

        // ---- dashboard ----
        app.MapGet("/api/dashboard", async (int? horizonDays, DeadlineEngine engine,
                ClaimsPrincipal u, CancellationToken ct) =>
                Results.Ok(await engine.BuildAsync(u.UserId(), horizonDays ?? 120, ct)))
            .RequireAuthorization().WithTags("dashboard");

        // ---- authority cases with nested checklist ----
        var cases = app.MapGroup("/api/authority-cases").RequireAuthorization().WithTags("authorities");

        cases.MapGet("/", async (LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
            Results.Ok(await db.AuthorityCases.AsNoTracking()
                .Include(c => c.RequiredDocuments)
                .Where(c => c.UserId == u.UserId())
                .OrderBy(c => c.DeadlineOn ?? DateOnly.MaxValue)
                .ToListAsync(ct)));

        cases.MapGet("/{id:int}", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var c = await db.AuthorityCases.AsNoTracking().Include(x => x.RequiredDocuments)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            return c is null ? Results.NotFound() : Results.Ok(c);
        });

        cases.MapPost("/", async (AuthorityCase input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            input.Id = 0;
            input.UserId = u.UserId();
            foreach (var r in input.RequiredDocuments) { r.Id = 0; r.AuthorityCaseId = 0; }
            db.AuthorityCases.Add(input);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/authority-cases/{input.Id}", input);
        });

        cases.MapPut("/{id:int}", async (int id, AuthorityCase input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var existing = await db.AuthorityCases.Include(c => c.RequiredDocuments)
                .FirstOrDefaultAsync(c => c.Id == id && c.UserId == u.UserId(), ct);
            if (existing is null) return Results.NotFound();

            existing.CaseType = input.CaseType;
            existing.Title = input.Title;
            existing.Authority = input.Authority;
            existing.ReferenceNo = input.ReferenceNo;
            existing.Status = input.Status;
            existing.SubmittedOn = input.SubmittedOn;
            existing.DeadlineOn = input.DeadlineOn;
            existing.NextActionOn = input.NextActionOn;
            existing.ReminderDays = input.ReminderDays;
            existing.FamilyMemberId = input.FamilyMemberId;
            existing.Notes = input.Notes;

            db.RequiredDocuments.RemoveRange(existing.RequiredDocuments);
            foreach (var r in input.RequiredDocuments)
                db.RequiredDocuments.Add(new RequiredDocument
                {
                    AuthorityCaseId = id, Name = r.Name, IsMandatory = r.IsMandatory,
                    DocumentId = r.DocumentId, DueOn = r.DueOn, Notes = r.Notes
                });

            await db.SaveChangesAsync(ct);
            return Results.Ok(existing);
        });

        cases.MapDelete("/{id:int}", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var existing = await db.AuthorityCases.FirstOrDefaultAsync(c => c.Id == id && c.UserId == u.UserId(), ct);
            if (existing is null) return Results.NotFound();
            db.Remove(existing);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // link an uploaded document to a checklist row
        cases.MapPost("/{caseId:int}/required-documents/{reqId:int}/link/{documentId:int}",
            async (int caseId, int reqId, int documentId, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var owns = await db.AuthorityCases.AnyAsync(c => c.Id == caseId && c.UserId == u.UserId(), ct);
            if (!owns) return Results.NotFound();
            var req = await db.RequiredDocuments.FirstOrDefaultAsync(r => r.Id == reqId && r.AuthorityCaseId == caseId, ct);
            if (req is null) return Results.NotFound();
            req.DocumentId = documentId;
            await db.SaveChangesAsync(ct);
            return Results.Ok(req);
        });

        // ---- trips with bookings + packing ----
        var trips = app.MapGroup("/api/trips").RequireAuthorization().WithTags("travel");

        trips.MapGet("/", async (LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
            Results.Ok(await db.Trips.AsNoTracking()
                .Include(t => t.Bookings).Include(t => t.PackingItems)
                .Where(t => t.UserId == u.UserId())
                .OrderBy(t => t.StartsOn).ToListAsync(ct)));

        trips.MapGet("/{id:int}", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var t = await db.Trips.AsNoTracking()
                .Include(x => x.Bookings).Include(x => x.PackingItems)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            return t is null ? Results.NotFound() : Results.Ok(t);
        });

        trips.MapPost("/", async (Trip input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            input.Id = 0;
            input.UserId = u.UserId();
            foreach (var b in input.Bookings) { b.Id = 0; b.TripId = 0; }
            foreach (var p in input.PackingItems) { p.Id = 0; p.TripId = 0; }
            db.Trips.Add(input);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/trips/{input.Id}", input);
        });

        trips.MapPut("/{id:int}", async (int id, Trip input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var t = await db.Trips.FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            if (t is null) return Results.NotFound();
            t.Title = input.Title; t.Destination = input.Destination;
            t.StartsOn = input.StartsOn; t.EndsOn = input.EndsOn;
            t.Status = input.Status; t.Budget = input.Budget; t.Notes = input.Notes;
            await db.SaveChangesAsync(ct);
            return Results.Ok(t);
        });

        trips.MapDelete("/{id:int}", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var t = await db.Trips.FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            if (t is null) return Results.NotFound();
            db.Remove(t);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        trips.MapPost("/{tripId:int}/bookings", async (int tripId, Booking b, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (!await db.Trips.AnyAsync(t => t.Id == tripId && t.UserId == u.UserId(), ct)) return Results.NotFound();
            b.Id = 0; b.TripId = tripId;
            db.Bookings.Add(b);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/trips/{tripId}/bookings/{b.Id}", b);
        });

        trips.MapDelete("/{tripId:int}/bookings/{id:int}", async (int tripId, int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (!await db.Trips.AnyAsync(t => t.Id == tripId && t.UserId == u.UserId(), ct)) return Results.NotFound();
            var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == id && x.TripId == tripId, ct);
            if (b is null) return Results.NotFound();
            db.Remove(b);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        trips.MapPut("/{tripId:int}/bookings/{id:int}", async (int tripId, int id, Booking input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (!await db.Trips.AnyAsync(t => t.Id == tripId && t.UserId == u.UserId(), ct)) return Results.NotFound();
            var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == id && x.TripId == tripId, ct);
            if (b is null) return Results.NotFound();
            b.Kind = input.Kind;
            b.Title = input.Title;
            b.ReferenceNo = input.ReferenceNo;
            b.StartsAt = input.StartsAt;
            b.EndsAt = input.EndsAt;
            b.Amount = input.Amount;
            b.Currency = input.Currency;
            b.DocumentId = input.DocumentId;
            b.Notes = input.Notes;
            await db.SaveChangesAsync(ct);
            return Results.Ok(b);
        });

        trips.MapPost("/{tripId:int}/packing", async (int tripId, PackingItem p, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (!await db.Trips.AnyAsync(t => t.Id == tripId && t.UserId == u.UserId(), ct)) return Results.NotFound();
            p.Id = 0; p.TripId = tripId;
            db.PackingItems.Add(p);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/trips/{tripId}/packing/{p.Id}", p);
        });

        trips.MapPut("/{tripId:int}/packing/{id:int}", async (int tripId, int id, PackingItem input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (!await db.Trips.AnyAsync(t => t.Id == tripId && t.UserId == u.UserId(), ct)) return Results.NotFound();
            var p = await db.PackingItems.FirstOrDefaultAsync(x => x.Id == id && x.TripId == tripId, ct);
            if (p is null) return Results.NotFound();
            p.Name = input.Name; p.Quantity = input.Quantity;
            p.Category = input.Category; p.IsPacked = input.IsPacked;
            await db.SaveChangesAsync(ct);
            return Results.Ok(p);
        });

        trips.MapDelete("/{tripId:int}/packing/{id:int}", async (int tripId, int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (!await db.Trips.AnyAsync(t => t.Id == tripId && t.UserId == u.UserId(), ct)) return Results.NotFound();
            var p = await db.PackingItems.FirstOrDefaultAsync(x => x.Id == id && x.TripId == tripId, ct);
            if (p is null) return Results.NotFound();
            db.Remove(p);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- file upload / download for documents ----
        var files = app.MapGroup("/api/documents").RequireAuthorization().WithTags("documents");

        files.MapPost("/{id:int}/file", async (int id, IFormFile file, LifeDashContext db,
            IConfiguration cfg, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.UserId == u.UserId(), ct);
            if (doc is null) return Results.NotFound();
            if (file.Length == 0) return Results.BadRequest(new { error = "Die Datei ist leer." });

            var maxMb = cfg.GetValue("Storage:MaxUploadMb", 20);
            if (file.Length > maxMb * 1024L * 1024L)
                return Results.BadRequest(new { error = $"Die Datei ist größer als {maxMb} MB." });

            var root = cfg["Storage:UploadPath"] ?? "App_Data/uploads";
            var absoluteRoot = Path.IsPathRooted(root)
                ? root
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
            var dir = Path.Combine(absoluteRoot, u.UserId().ToString());
            Directory.CreateDirectory(dir);

            var ext = Path.GetExtension(file.FileName);
            var name = $"{Guid.NewGuid():N}{ext}";
            var path = Path.Combine(dir, name);
            await using (var stream = File.Create(path))
                await file.CopyToAsync(stream, ct);

            doc.StoragePath = path;
            doc.OriginalName = file.FileName;
            doc.ContentType = file.ContentType;
            doc.SizeBytes = file.Length;
            await db.SaveChangesAsync(ct);
            return Results.Ok(doc);
        }).DisableAntiforgery();

        files.MapGet("/{id:int}/file", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var doc = await db.Documents.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id && d.UserId == u.UserId(), ct);

            if (doc is null) return Results.NotFound();
            var resolvedPath = ResolveStoragePath(doc.StoragePath);
            if (resolvedPath is null) return Results.NotFound();

            var contentType = string.IsNullOrWhiteSpace(doc.ContentType)
                ? "application/octet-stream"
                : doc.ContentType;
            var safeName = SafeDownloadName(doc.OriginalName);

            try
            {
                return string.IsNullOrWhiteSpace(safeName)
                    ? Results.File(resolvedPath, contentType)
                    : Results.File(resolvedPath, contentType, safeName);
            }
            catch (IOException)
            {
                return Results.NotFound();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
        });
    }

    private static string? ResolveStoragePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;

        try
        {
            if (Path.IsPathRooted(rawPath))
            {
                var full = Path.GetFullPath(rawPath);
                return File.Exists(full) ? full : null;
            }

            var localFull = Path.GetFullPath(rawPath);
            if (File.Exists(localFull)) return localFull;

            var appBaseFull = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rawPath));
            return File.Exists(appBaseFull) ? appBaseFull : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeDownloadName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var cleaned = fileName.Replace("\r", "").Replace("\n", "").Trim();
        cleaned = Path.GetFileName(cleaned);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }
}
