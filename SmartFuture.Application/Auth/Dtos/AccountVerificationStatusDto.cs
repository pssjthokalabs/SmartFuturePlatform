namespace SmartFuture.Application.Auth.Dtos;

/// <summary>
/// Returned by the confirm endpoint on success so the caller can refresh
/// its session-level verification flags without an extra /me round-trip.
/// </summary>
public class AccountVerificationStatusDto
{
    public bool EmailConfirmed { get; set; }
    public bool PhoneNumberConfirmed { get; set; }
    /// <summary>
    /// True when the super-OTP path was used (both flags confirmed at once).
    /// Always false in Production responses.
    /// </summary>
    public bool SuperOtpUsed { get; set; }
}
