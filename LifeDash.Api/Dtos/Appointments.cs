namespace LifeDash.Api.Dtos;

/// Appointment as sent to clients: attendees flattened to a plain id list.
public record AppointmentDto(
    int Id,
    int UserId,
    string Title,
    string Category,
    DateTime StartsAt,
    DateTime? EndsAt,
    string? Location,
    int ReminderDays,
    string? Notes,
    bool IsDone,
    List<int> AttendeeIds
);

/// Appointment as received from clients on create/update.
public record AppointmentInput(
    string Title,
    string Category,
    DateTime StartsAt,
    DateTime? EndsAt,
    string? Location,
    int ReminderDays,
    string? Notes,
    bool IsDone,
    List<int>? AttendeeIds
);
