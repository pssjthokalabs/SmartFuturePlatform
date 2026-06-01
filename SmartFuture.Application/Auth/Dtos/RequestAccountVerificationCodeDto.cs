namespace SmartFuture.Application.Auth.Dtos;

/// <summary>
/// Authenticated request to send an OTP for post-registration account
/// verification. Channel selects the delivery path:
///   - "email" — code is generated, stored in VerificationCodes
///     (Purpose=AccountVerification), and emailed via the existing
///     notification pipeline. Confirms EmailConfirmed on verify.
///   - "sms"   — code is sent via Twilio Verify. Confirms
///     PhoneNumberConfirmed on verify.
///   - "whatsapp" — not supported yet. Returns PROVIDER_NOT_CONFIGURED.
///
/// Identifier is sourced from the JWT — the caller does NOT pass an
/// email/phone. This prevents account enumeration via the request endpoint
/// (anyone could otherwise probe "is this email registered?").
/// </summary>
public class RequestAccountVerificationCodeDto
{
    public string? Channel { get; set; }
}
