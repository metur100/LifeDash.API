using LifeDash.Api.Models;
using LifeDash.Api.Services;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace LifeDash.Api.Data;

public class LifeDashContext : DbContext
{
    private readonly IHttpContextAccessor _http;
    private readonly IAuditLogWriter _audit;

    public LifeDashContext(
        DbContextOptions<LifeDashContext> options,
        IHttpContextAccessor http,
        IAuditLogWriter audit) : base(options)
    {
        _http = http;
        _audit = audit;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<ImportantDate> ImportantDates => Set<ImportantDate>();
    public DbSet<AuthorityCase> AuthorityCases => Set<AuthorityCase>();
    public DbSet<RequiredDocument> RequiredDocuments => Set<RequiredDocument>();
    public DbSet<Income> Incomes => Set<Income>();
    public DbSet<FixedCost> FixedCosts => Set<FixedCost>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<HomeItem> HomeItems => Set<HomeItem>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<PackingItem> PackingItems => Set<PackingItem>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(x => x.Email).IsUnique();
        b.Entity<TaskItem>().ToTable("Tasks");
        b.Entity<Document>()
            .HasOne<FamilyMember>()
            .WithMany()
            .HasForeignKey(x => x.FamilyMemberId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<Appointment>()
            .HasOne<FamilyMember>()
            .WithMany()
            .HasForeignKey(x => x.FamilyMemberId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<ImportantDate>()
            .HasOne<FamilyMember>()
            .WithMany()
            .HasForeignKey(x => x.FamilyMemberId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<AuthorityCase>()
            .HasOne<FamilyMember>()
            .WithMany()
            .HasForeignKey(x => x.FamilyMemberId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<AuthorityCase>()
            .HasMany(c => c.RequiredDocuments)
            .WithOne(r => r.AuthorityCase!)
            .HasForeignKey(r => r.AuthorityCaseId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<Trip>()
            .HasMany(t => t.Bookings).WithOne(x => x.Trip!)
            .HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Trip>()
            .HasMany(t => t.PackingItems).WithOne(x => x.Trip!)
            .HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
    }

    public override int SaveChanges()
    {
        var audits = CollectAudits();
        var result = base.SaveChanges();
        WriteAudits(audits);
        return result;
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var audits = CollectAudits();
        var result = await base.SaveChangesAsync(cancellationToken);
        WriteAudits(audits);
        return result;
    }

    private List<(object Entity, string State, string Type)> CollectAudits()
    {
        return ChangeTracker.Entries()
            .Where(e => e.Entity is not User)
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => (e.Entity, e.State.ToString().ToLowerInvariant(), e.Entity.GetType().Name))
            .ToList();
    }

    private void WriteAudits(IEnumerable<(object Entity, string State, string Type)> audits)
    {
        var ctx = _http.HttpContext;
        var userId = TryUserId(ctx?.User);
        var path = ctx?.Request.Path.Value;

        foreach (var (entity, state, type) in audits)
        {
            var idProp = entity.GetType().GetProperty("Id");
            var id = idProp?.GetValue(entity);
            _audit.Write(new
            {
                ts = DateTimeOffset.UtcNow,
                eventType = "db-change",
                userId,
                path,
                entity = type,
                state,
                entityId = id
            });
        }
    }

    private static int? TryUserId(ClaimsPrincipal? user)
    {
        var raw = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
        return int.TryParse(raw, out var id) ? id : null;
    }
}
