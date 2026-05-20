namespace SmartFuture.Application.Users.Admin.Dtos;

public class AdminUserListItemDto
{
    public Guid Id { get; set; }
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

    // Customer-only fields. Null for staff users.
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? ActivePackageName { get; set; }
    public decimal? Outstanding { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
