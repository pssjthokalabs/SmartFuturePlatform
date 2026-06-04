namespace SmartFuture.Application.Users.Admin.Dtos;

public class AdminUserListItemDto
{
    public Guid Id { get; set; }

    // Phase 41 — short, human-friendly identifier for the admin portal.
    // Rendered as `USR-1000` etc. Nullable in case a row hasn't been
    // backfilled yet (the startup seeder fills any missing values).
    public int? UserNumber { get; set; }
    public string? UserCode => UserNumber.HasValue ? $"USR-{UserNumber.Value:D4}" : null;

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName => $"{FirstName} {LastName}".Trim();

    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    // Canonical user-type bucket (Customer / Admin / Agent / Technician /
    // Support). Derived from the user's Identity role set.
    public string UserType { get; set; } = string.Empty;
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    public string AccountStatus { get; set; } = "Active";

    // Controlled QA test account (customer{1000-1999}@gmail.com). Drives the
    // "Test" badge + the SuperAdmin-only super-delete affordance in the portal.
    public bool IsTestAccount { get; set; }

    // Customer-only fields. Null for staff users.
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? ActivePackageName { get; set; }
    public decimal? Outstanding { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
