namespace SmartFuture.Application.Auth.Dtos;

// Phase 35C — switched from reset-token-link flow to 6-digit OTP.
// `Code` is the new primary field; `Token` is retained on the wire so
// that older deployed portals can still POST without breaking, but the
// service uses `Code` and ignores `Token` if both are present.
public class ResetPasswordRequestDto
{
    public string Email { get; set; } = string.Empty;

    /// <summary>6-digit OTP delivered by email. New in Phase 35C.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Legacy reset-token field — kept compiled for older clients; unused server-side.</summary>
    public string? Token { get; set; }

    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}
