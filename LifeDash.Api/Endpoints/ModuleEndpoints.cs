using System.Security.Claims;
using System.Text.Json;
using LifeDash.Api.Data;
using LifeDash.Api.Dtos;
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
        app.MapOwned<ImportantDate>("/api/important-dates");
        app.MapOwned<Income>("/api/incomes");
        app.MapOwned<FixedCost>("/api/fixed-costs");
        app.MapOwned<Subscription>("/api/subscriptions");
        app.MapOwned<Payment>("/api/payments");
        app.MapOwned<HomeItem>("/api/home-items");
        app.MapOwned<TaskItem>("/api/tasks");
        app.MapOwned<Document>("/api/documents");
        app.MapOwned<Package>("/api/packages");

        // ---- dashboard ----
        app.MapGet("/api/dashboard", async (int? horizonDays, DeadlineEngine engine,
                ClaimsPrincipal u, CancellationToken ct) =>
                Results.Ok(await engine.BuildAsync(u.UserId(), horizonDays ?? 120, ct)))
            .RequireAuthorization().WithTags("dashboard");

        // ---- appointments with multiple attendees ----
        var appointments = app.MapGroup("/api/appointments").RequireAuthorization().WithTags("appointments");

        appointments.MapGet("/", async (LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var list = await db.Appointments.AsNoTracking()
                .Include(a => a.Attendees)
                .Where(a => a.UserId == u.UserId())
                .ToListAsync(ct);
            return Results.Ok(list.Select(ToDto));
        });

        appointments.MapGet("/{id:int}", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var a = await db.Appointments.AsNoTracking().Include(x => x.Attendees)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            return a is null ? Results.NotFound() : Results.Ok(ToDto(a));
        });

        appointments.MapPost("/", async (AppointmentInput input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var a = new Appointment
            {
                UserId = u.UserId(),
                Title = input.Title,
                Category = input.Category,
                StartsAt = input.StartsAt,
                EndsAt = input.EndsAt,
                Location = input.Location,
                ReminderDays = input.ReminderDays,
                Notes = input.Notes,
                IsDone = input.IsDone,
                Attendees = (input.AttendeeIds ?? new()).Distinct()
                    .Select(mid => new AppointmentAttendee { FamilyMemberId = mid }).ToList(),
            };
            db.Appointments.Add(a);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/appointments/{a.Id}", ToDto(a));
        });

        appointments.MapPut("/{id:int}", async (int id, AppointmentInput input, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var a = await db.Appointments.Include(x => x.Attendees)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            if (a is null) return Results.NotFound();

            a.Title = input.Title;
            a.Category = input.Category;
            a.StartsAt = input.StartsAt;
            a.EndsAt = input.EndsAt;
            a.Location = input.Location;
            a.ReminderDays = input.ReminderDays;
            a.Notes = input.Notes;
            a.IsDone = input.IsDone;

            db.AppointmentAttendees.RemoveRange(a.Attendees);
            a.Attendees = (input.AttendeeIds ?? new()).Distinct()
                .Select(mid => new AppointmentAttendee { AppointmentId = id, FamilyMemberId = mid }).ToList();

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(a));
        });

        appointments.MapDelete("/{id:int}", async (int id, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            var a = await db.Appointments.FirstOrDefaultAsync(x => x.Id == id && x.UserId == u.UserId(), ct);
            if (a is null) return Results.NotFound();
            db.Remove(a);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

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
            t.Title = input.Title; t.StartPlace = input.StartPlace; t.Destination = input.Destination;
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

        // ---- package tracking & IMAP email sync ----
        var deliveries = app.MapGroup("/api/deliveries").RequireAuthorization().WithTags("deliveries");

        deliveries.MapPost("/scan", async (bool? full, ImapPackageScanner scanner, LifeDashContext db, ClaimsPrincipal u, CancellationToken ct) =>
        {
            try
            {
                var scanned = await scanner.ScanMailboxAsync(full ?? false, ct);
                var userId = u.UserId();
                var existing = await db.Packages.Where(p => p.UserId == userId).ToListAsync(ct);
                var byTracking = existing.ToDictionary(p => p.TrackingNumber.ToUpperInvariant(), p => p);

                var added = 0;
                var updated = 0;

                foreach (var s in scanned)
                {
                    var key = s.TrackingNumber.ToUpperInvariant();
                    var newEntry = new PackageHistoryEntry(s.Status, s.LatestEvent, s.LatestEventTime, s.UpdatedAt);

                    if (byTracking.TryGetValue(key, out var existingPkg))
                    {
                        var history = DeserializeHistory(existingPkg.HistoryJson);
                        if (history.Any(h => h.text == newEntry.text && h.time == newEntry.time)) continue;

                        history.Add(newEntry);
                        existingPkg.Status = s.Status;
                        existingPkg.Sender ??= s.Sender;
                        existingPkg.ExpectedDelivery = ParseDateOnly(s.ExpectedDelivery) ?? existingPkg.ExpectedDelivery;
                        existingPkg.LatestEvent = s.LatestEvent;
                        existingPkg.LatestEventTime = s.LatestEventTime;
                        existingPkg.HistoryJson = JsonSerializer.Serialize(history);
                        existingPkg.UpdatedAt = DateTime.UtcNow;
                        updated++;
                    }
                    else
                    {
                        var pkg = new Package
                        {
                            UserId = userId,
                            Title = s.Title,
                            Carrier = s.Carrier,
                            TrackingNumber = s.TrackingNumber,
                            Sender = s.Sender,
                            Status = s.Status,
                            ExpectedDelivery = ParseDateOnly(s.ExpectedDelivery),
                            LatestEvent = s.LatestEvent,
                            LatestEventTime = s.LatestEventTime,
                            Source = "email_scan",
                            UpdatedAt = DateTime.UtcNow,
                            HistoryJson = JsonSerializer.Serialize(new[] { newEntry }),
                        };
                        db.Packages.Add(pkg);
                        byTracking[key] = pkg;
                        added++;
                    }
                }

                await db.SaveChangesAsync(ct);
                return Results.Ok(new { success = true, scanned = scanned.Count, added, updated });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "E-Mail-Postfach konnte nicht gescannt werden.",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // Microsoft OAuth (XOAUTH2) sign-in, needed since Microsoft retired IMAP app passwords
        // for Outlook.com/Live/Hotmail mailboxes. Device-code flow: the user opens the returned
        // verification URL and enters the code, then GET /connect/status confirms completion.
        deliveries.MapPost("/microsoft/connect/start", async (MicrosoftMailAuthService auth, CancellationToken ct) =>
        {
            try
            {
                var info = await auth.StartDeviceCodeSignInAsync(ct);
                return Results.Ok(new { userCode = info.UserCode, verificationUrl = info.VerificationUrl, message = info.Message, expiresAtUtc = info.ExpiresAtUtc });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Microsoft-Anmeldung konnte nicht gestartet werden.",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        deliveries.MapGet("/microsoft/connect/status", async (MicrosoftMailAuthService auth, CancellationToken ct) =>
        {
            var connected = await auth.TryGetAccessTokenAsync(ct) is not null;
            var (status, error) = auth.GetPendingStatus();
            return Results.Ok(new { connected, status = status.ToString().ToLowerInvariant(), error });
        });

        deliveries.MapPost("/microsoft/disconnect", async (MicrosoftMailAuthService auth) =>
        {
            await auth.DisconnectAsync();
            return Results.NoContent();
        });
    }

    private record PackageHistoryEntry(string status, string text, string time, string at);

    private static List<PackageHistoryEntry> DeserializeHistory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<PackageHistoryEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<PackageHistoryEntry>>(json) ?? new List<PackageHistoryEntry>();
        }
        catch
        {
            return new List<PackageHistoryEntry>();
        }
    }

    private static DateOnly? ParseDateOnly(string? iso) => DateOnly.TryParse(iso, out var d) ? d : null;

    private static AppointmentDto ToDto(Appointment a) => new(
        a.Id, a.UserId, a.Title, a.Category, a.StartsAt, a.EndsAt, a.Location,
        a.ReminderDays, a.Notes, a.IsDone, a.Attendees.Select(x => x.FamilyMemberId).ToList());

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
