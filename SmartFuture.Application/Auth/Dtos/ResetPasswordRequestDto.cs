namespace SmartFuture.Application.Auth.Dtos;

// Phase 35C — switched from reset-token-link flow to 6-digit OTP.
// `Code` is the new primary field; `Token` is retained on the wire so
// that older deployed portals can still POST without breaking, but the
// service uses `Code` and ignores `Token` if both are present.
public class ResetPasswordRequestDto
{
    // Populated when Channel == "Email" (the default / legacy flow).
    public string? Email { get; set; }

    // Populated when Channel == "Sms". Same normalisation as the
    // forgot-password entry: PhoneNumberNormalizer.Normalize(). The
    // verification-code lookup queries by user + channel, so an SMS
    // reset must supply the same channel string it used on
    // /forgot-password.
    public string? PhoneNumber { get; set; }

    // "Email" (default) or "Sms". Anything unrecognised falls back to
    // Email so old clients that don't send this field keep working.
    public string? Channel { get; set; }

    /// <summary>6-digit OTP delivered by email or SMS. Phase 35C.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Legacy reset-token field — kept compiled for older clients; unused server-side.</summary>
    public string? Token { get; set; }

    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}
