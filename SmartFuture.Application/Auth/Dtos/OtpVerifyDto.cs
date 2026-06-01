namespace SmartFuture.Application.Auth.Dtos;

public class OtpVerifyDto
{
    public string Identifier { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;

    // Optional. When supplied, picks the verification path explicitly:
    //   "sms"   → Twilio Verify, flips PhoneNumberConfirmed
    //   "email" → VerificationCodes table, flips EmailConfirmed
    // When omitted, the backend infers the channel from the identifier
    // shape (contains "@" → email, otherwise sms). Older mobile builds
    // that don't yet send this field still work.
    public string? Channel { get; set; }
}
