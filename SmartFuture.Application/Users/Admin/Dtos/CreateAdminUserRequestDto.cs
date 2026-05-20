namespace SmartFuture.Application.Users.Admin.Dtos;

public class CreateAdminUserRequestDto
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }

    // Canonical user type ("Customer" / "Admin" / "Agent" / "Technician"
    // / "Support"). Maps to the matching Identity role.
    public string UserType { get; set; } = string.Empty;

    // Admin sets a temporary password for now. The user can change it
    // through the normal Forgot-Password flow after first login. Email
    // invite delivery is intentionally deferred until the notification
    // pipeline is GA — sending invites today would silently fail in
    // tenants where SMTP isn't fully configured.
    public string? TemporaryPassword { get; set; }

    // Optional initial account status — defaults to "Active" when blank.
    public string? AccountStatus { get; set; }
}
