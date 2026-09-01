namespace LifeDash.Api.Services;

public class ReminderEmailOptions
{
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
    public string? ToEmail { get; set; }
    public string? AppBaseUrl { get; set; }
    public string TimeZoneId { get; set; } = "Europe/Berlin";
}
