namespace SmartFuture.Application.Auth.Dtos;

public class ForgotPasswordRequestDto
{
    // Either Email or PhoneNumber must be present depending on Channel.
    // Legacy callers (pre-SMS-channel) send only Email and no Channel;
    // the service defaults Channel to Email so those keep working
    // byte-identically.
    public string? Email { get; set; }

    // South African phone numbers, any common format: "0712345678",
    // "+27712345678", "27712345678". Normalised server-side via
    // PhoneNumberNormalizer before user lookup. Only used when
    // Channel == "Sms".
    public string? PhoneNumber { get; set; }

    // "client" or "admin" — echoed into audit + template context.
    public string? Portal { get; set; }

    // "Email" (default) or "Sms". Case-insensitive; anything unrecognised
    // falls back to Email so an old client never crashes the endpoint.
    public string? Channel { get; set; }
}
