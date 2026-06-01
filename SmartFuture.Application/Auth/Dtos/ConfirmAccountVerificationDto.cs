namespace SmartFuture.Application.Auth.Dtos;

/// <summary>
/// Authenticated confirm step for post-registration account verification.
///
/// Channel determines which flag is flipped on success:
///   - "email" → EmailConfirmed = true
///   - "sms"   → PhoneNumberConfirmed = true
///
/// UAT super OTP path: when the supplied code matches the configured
/// <c>Otp:UatSuperOtpCode</c> AND the host environment is NOT Production
/// AND <c>Otp:UatSuperOtpEnabled</c> is true, BOTH EmailConfirmed and
/// PhoneNumberConfirmed are flipped at once. Production is hard-blocked
/// regardless of config.
/// </summary>
public class ConfirmAccountVerificationDto
{
    public string? Channel { get; set; }
    public string? Code { get; set; }
}
