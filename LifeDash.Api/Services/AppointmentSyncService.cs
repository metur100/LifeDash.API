using LifeDash.Api.Data;
using LifeDash.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LifeDash.Api.Services;

public record AppointmentSyncResult(int Scanned, int Added, int SkippedDuplicate);

// Shared by the authenticated manual trigger (POST /api/appointments/scan-mailbox) and the
// anonymous cron endpoint (POST /api/jobs/mail-scan)
public class AppointmentSyncService
{
    private readonly ImapAppointmentScanner _scanner;

    public AppointmentSyncService(ImapAppointmentScanner scanner)
    {
        _scanner = scanner;
    }

    public async Task<AppointmentSyncResult> SyncAsync(int userId, LifeDashContext db, CancellationToken ct)
    {
        var members = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Id, m.FullName })
            .ToListAsync(ct);

        var scanned = await _scanner.ScanMailboxAsync(members.Select(m => m.FullName), ct);
        if (scanned.Count == 0) return new AppointmentSyncResult(0, 0, 0);

        var existing = await db.Appointments
            .Include(a => a.Attendees)
            .Where(a => a.UserId == userId)
            .ToListAsync(ct);

        var added = 0;
        var skipped = 0;

        foreach (var s in scanned)
        {
            var attendeeIds = members
                .Where(m => s.MatchedNames.Contains(m.FullName, StringComparer.OrdinalIgnoreCase))
                .Select(m => m.Id)
                .Distinct()
                .ToList();

            // Dedup exactly per the requirement: same date+time and same set of people already
            // recorded means skip, regardless of title/wording differences between emails.
            var isDuplicate = existing.Any(a =>
                a.StartsAt == s.StartsAt &&
                SameAttendees(a.Attendees.Select(x => x.FamilyMemberId), attendeeIds));
            if (isDuplicate) { skipped++; continue; }

            var appointment = new Appointment
            {
                UserId = userId,
                Title = s.Title,
                Category = attendeeIds.Count > 0 ? "family" : "other",
                StartsAt = s.StartsAt,
                Notes = attendeeIds.Count > 0
                    ? "[mail-scan] Automatisch aus dem Postfach erkannt."
                    : "[mail-scan] Automatisch aus dem Postfach erkannt - Person nicht sicher zugeordnet, bitte prüfen.",
                Attendees = attendeeIds.Select(id => new AppointmentAttendee { FamilyMemberId = id }).ToList(),
            };
            db.Appointments.Add(appointment);
            existing.Add(appointment); // guards against the same candidate appearing twice in one run
            added++;
        }

        await db.SaveChangesAsync(ct);
        return new AppointmentSyncResult(scanned.Count, added, skipped);
    }

    private static bool SameAttendees(IEnumerable<int> a, IEnumerable<int> b) =>
        new HashSet<int>(a).SetEquals(new HashSet<int>(b));
}
