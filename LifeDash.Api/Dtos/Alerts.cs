namespace LifeDash.Api.Dtos;

public enum AlertSeverity { Info = 0, Soon = 1, Urgent = 2, Overdue = 3 }

/// One row in the "don't forget important stuff" feed.
public record Alert(
    string Id,
    string Module,        // family | authority | finance | home | travel
    string Kind,          // expiry | missing-document | renewal | payment | appointment | warranty | task | birthday | trip
    AlertSeverity Severity,
    string Title,
    string Message,       // human sentence, already formatted
    DateOnly? DueOn,
    int? DaysLeft,        
    string? ActionLabel,
    string? ActionPath,   // frontend route
    string? RelatedType,
    int? RelatedId
);

public record Insight(string Id, string Icon, string Message, string? ActionPath);

public record DashboardSummary(
    int AlertsTotal,
    int Overdue,
    int Urgent,
    int Soon,
    decimal MonthlyIncome,
    decimal MonthlyFixedCosts,
    decimal MonthlyBalance,
    int OpenTasks,
    int MissingDocuments,
    string? NextTripTitle,
    int? NextTripInDays
);

public record DashboardResponse(
    DashboardSummary Summary,
    List<Alert> Alerts,
    List<Insight> Insights
);
