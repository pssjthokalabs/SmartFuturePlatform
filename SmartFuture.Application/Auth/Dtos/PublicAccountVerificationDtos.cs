namespace SmartFuture.Application.Auth.Dtos;

// Pre-login / no-session counterparts to the authenticated verify-account
// endpoints. Used when a customer's login attempt returned
// ACCOUNT_VERIFICATION_REQUIRED — the portal has no JWT yet, so it can't
// call the authenticated /api/auth/verify-account/* endpoints.
//
// Anonymous BUT rate-limited via the controller-level AuthPolicy. The
// user is resolved by identifier (email or normalised phone) the same
// way LoginAsync does.

/// <summary>Request a verification OTP using only the identifier the user just typed at login.</summary>
public class PublicRequestAccountVerificationCodeDto
{
    public string? Identifier { get; set; }
    public string? Channel { get; set; }
}

/// <summary>Confirm a verification OTP using only the identifier + channel + code.</summary>
public class PublicConfirmAccountVerificationDto
{
    public string? Identifier { get; set; }
    public string? Channel { get; set; }
    public string? Code { get; set; }
}
