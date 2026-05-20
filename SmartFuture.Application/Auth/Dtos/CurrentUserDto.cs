namespace SmartFuture.Application.Auth.Dtos;

public class CurrentUserDto
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string AccountStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    // Phase 42 — portal eligibility flags. The frontend reads these to
    // decide whether a freshly-authenticated session is allowed into
    // the Admin Portal or the Client Zone, so it doesn't have to
    // re-parse role strings or guess from the User row.
    public bool IsSuperAdmin { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsCustomer { get; set; }
    public bool HasCustomerProfile { get; set; }
}
