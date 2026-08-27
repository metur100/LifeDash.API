using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace LifeDash.Api.Models;

public class User
{
    public int Id { get; set; }
    [MaxLength(256)] public string Email { get; set; } = "";
    [MaxLength(128)] public string DisplayName { get; set; } = "";
    [MaxLength(512)] public string PasswordHash { get; set; } = "";
    [MaxLength(10)]  public string Locale { get; set; } = "de-DE";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public abstract class OwnedEntity
{
    public int Id { get; set; }
    public int UserId { get; set; }
}

public class FamilyMember : OwnedEntity
{
    [MaxLength(160)] public string FullName { get; set; } = "";
    [MaxLength(60)]  public string? Relation { get; set; }
    public DateOnly? BirthDate { get; set; }
    [MaxLength(80)]  public string? Nationality { get; set; }
    [MaxLength(160)] public string? SchoolName { get; set; }
    [MaxLength(40)]  public string? SchoolGrade { get; set; }
    [MaxLength(1000)]public string? SchoolNote { get; set; }
    [MaxLength(2000)]public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Document : OwnedEntity
{
    public int? FamilyMemberId { get; set; }
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(40)]  public string Category { get; set; } = "other";
    [MaxLength(80)]  public string? DocumentType { get; set; }
    public DateOnly? IssuedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public int ReminderDays { get; set; } = 30;
    [MaxLength(500)] public string? StoragePath { get; set; }
    [MaxLength(300)] public string? OriginalName { get; set; }
    [MaxLength(120)] public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
    [MaxLength(2000)]public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Appointment : OwnedEntity
{
    public int? FamilyMemberId { get; set; }
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(40)]  public string Category { get; set; } = "family";
    public DateTime StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    [MaxLength(240)] public string? Location { get; set; }
    public int ReminderDays { get; set; } = 3;
    [MaxLength(2000)]public string? Notes { get; set; }
    public bool IsDone { get; set; }
}

public class ImportantDate : OwnedEntity
{
    public int? FamilyMemberId { get; set; }
    [MaxLength(200)] public string Title { get; set; } = "";
    public DateOnly DateValue { get; set; }
    public bool RepeatsYearly { get; set; } = true;
    public int ReminderDays { get; set; } = 14;
    [MaxLength(1000)]public string? Notes { get; set; }
}

public class AuthorityCase : OwnedEntity
{
    public int? FamilyMemberId { get; set; }
    [MaxLength(40)]  public string CaseType { get; set; } = "Aufenthalt";
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(200)] public string? Authority { get; set; }
    [MaxLength(120)] public string? ReferenceNo { get; set; }
    [MaxLength(40)]  public string Status { get; set; } = "open";
    public DateOnly? SubmittedOn { get; set; }
    public DateOnly? DeadlineOn { get; set; }
    public DateOnly? NextActionOn { get; set; }
    public int ReminderDays { get; set; } = 21;
    [MaxLength(2000)]public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<RequiredDocument> RequiredDocuments { get; set; } = new();
}

public class RequiredDocument
{
    public int Id { get; set; }
    public int AuthorityCaseId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    public bool IsMandatory { get; set; } = true;
    public int? DocumentId { get; set; }
    public DateOnly? DueOn { get; set; }
    [MaxLength(1000)]public string? Notes { get; set; }
    [JsonIgnore]
    public AuthorityCase? AuthorityCase { get; set; }
}

public class Income : OwnedEntity
{
    [MaxLength(200)] public string Source { get; set; } = "";
    [Column(TypeName="decimal(12,2)")] public decimal Amount { get; set; }
    [MaxLength(3)]   public string Currency { get; set; } = "EUR";
    [MaxLength(20)]  public string Cadence { get; set; } = "monthly";
    public int? DayOfMonth { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)]public string? Notes { get; set; }
}

public class FixedCost : OwnedEntity
{
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(60)]  public string? Category { get; set; }
    [Column(TypeName="decimal(12,2)")] public decimal Amount { get; set; }
    [MaxLength(3)]   public string Currency { get; set; } = "EUR";
    [MaxLength(20)]  public string Cadence { get; set; } = "monthly";
    public int? DayOfMonth { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)]public string? Notes { get; set; }
}

public class Subscription : OwnedEntity
{
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? Provider { get; set; }
    [Column(TypeName="decimal(12,2)")] public decimal Amount { get; set; }
    [MaxLength(3)]   public string Currency { get; set; } = "EUR";
    [MaxLength(20)]  public string Cadence { get; set; } = "monthly";
    public DateOnly RenewsOn { get; set; }
    public DateOnly? CancelByOn { get; set; }
    public int? NoticePeriodDays { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)]public string? Notes { get; set; }
}

public class Payment : OwnedEntity
{
    [MaxLength(200)] public string Title { get; set; } = "";
    [Column(TypeName="decimal(12,2)")] public decimal Amount { get; set; }
    [MaxLength(3)]   public string Currency { get; set; } = "EUR";
    public DateOnly DueOn { get; set; }
    [MaxLength(60)]  public string? Category { get; set; }
    public bool IsPaid { get; set; }
    public DateOnly? PaidOn { get; set; }
    [MaxLength(1000)]public string? Notes { get; set; }
}

public class HomeItem : OwnedEntity
{
    [MaxLength(20)]  public string Kind { get; set; } = "repair";
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(80)]  public string? Room { get; set; }
    [MaxLength(160)] public string? Vendor { get; set; }
    [Column(TypeName="decimal(12,2)")] public decimal? Cost { get; set; }
    [MaxLength(3)]   public string Currency { get; set; } = "EUR";
    public DateOnly? PurchasedOn { get; set; }
    public DateOnly? WarrantyUntil { get; set; }
    [MaxLength(30)]  public string Status { get; set; } = "open";
    public DateOnly? ScheduledOn { get; set; }
    public int? DocumentId { get; set; }
    [MaxLength(2000)]public string? Notes { get; set; }
}

public class Trip : OwnedEntity
{
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(200)] public string? Destination { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    [MaxLength(30)]  public string Status { get; set; } = "planned";
    [Column(TypeName="decimal(12,2)")] public decimal? Budget { get; set; }
    [MaxLength(2000)]public string? Notes { get; set; }
    public List<Booking> Bookings { get; set; } = new();
    public List<PackingItem> PackingItems { get; set; } = new();
}

public class Booking
{
    public int Id { get; set; }
    public int TripId { get; set; }
    [MaxLength(30)]  public string Kind { get; set; } = "flight";
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(120)] public string? ReferenceNo { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    [Column(TypeName="decimal(12,2)")] public decimal? Amount { get; set; }
    [MaxLength(3)]   public string Currency { get; set; } = "EUR";
    public int? DocumentId { get; set; }
    [MaxLength(1000)]public string? Notes { get; set; }
    [JsonIgnore]
    public Trip? Trip { get; set; }
}

public class PackingItem
{
    public int Id { get; set; }
    public int TripId { get; set; }
    [MaxLength(160)] public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    [MaxLength(60)]  public string? Category { get; set; }
    public bool IsPacked { get; set; }
    [JsonIgnore]
    public Trip? Trip { get; set; }
}

public class TaskItem : OwnedEntity
{
    [MaxLength(240)] public string Title { get; set; } = "";
    [MaxLength(30)]  public string Module { get; set; } = "general";
    public DateOnly? DueOn { get; set; }
    [MaxLength(20)]  public string Priority { get; set; } = "normal";
    public bool IsDone { get; set; }
    [MaxLength(40)]  public string? RelatedType { get; set; }
    public int? RelatedId { get; set; }
    [MaxLength(2000)]public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
