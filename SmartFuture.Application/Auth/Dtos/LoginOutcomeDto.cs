namespace SmartFuture.Application.Auth.Dtos;

// Wraps every login response — success AND failure — so the mobile / portal
// can always read the verification context the API actually observed, even
// when no token is minted. Without this, an ACCOUNT_VERIFICATION_REQUIRED
// failure returns only `{ code, message }` and the caller is left guessing
// whether the API saw EmailConfirmed=true or not.
public class LoginOutcomeDto
{
    // Non-null only on a fully-successful sign-in. When the gate refuses
    // the login (or any other failure path) this is null and the caller
    // routes on the diagnostic fields below.
    public AuthTokenDto? Token { get; set; }

    // Set when the gate decided the user must verify before signing in.
    // Always true when Code == ACCOUNT_VERIFICATION_REQUIRED; mirrored on
    // the success path as false so the mobile can read a single boolean.
    public bool RequiresVerification { get; set; }

    // Always populated with the value the API observed for THIS request.
    // Never null on the wire — the mobile log can use these to confirm
    // what the database actually held at login time. If a customer reports
    // "I confirmed my email but login still wants OTP", these two fields
    // tell us at a glance whether the API saw the DB update.
    public bool EmailConfirmed { get; set; }
    public bool PhoneNumberConfirmed { get; set; }

    // String form of UserAccountStatus, always present (defaults to "" on
    // INVALID_CREDENTIALS / unknown identifier paths so the mobile never
    // has to guard against null).
    public string AccountStatus { get; set; } = string.Empty;

    // Masked identifiers for the verification banner copy. Safe to ship
    // even on failure because the caller already typed the identifier
    // they're trying to verify.
    public string? MaskedEmail { get; set; }
    public string? MaskedPhone { get; set; }

    // Channels the user can pick from on the verification screen.
    // Currently ["sms", "email"]; WhatsApp is excluded until the provider
    // is wired. Surfaced from the backend so the mobile doesn't hard-code
    // a list that drifts when delivery options change.
    public IReadOnlyList<string> AvailableOtpChannels { get; set; } = Array.Empty<string>();

    // Short machine-readable label for the reason the gate fired.
    // Examples: "email_and_phone_unconfirmed", "account_inactive",
    // "account_suspended". Null on success or for failures that have no
    // verification interpretation (e.g. invalid_credentials).
    public string? VerificationReason { get; set; }

    // Returned ONLY when the password was correct. Lets the verification
    // screen pre-fetch /me-style data on success paths without leaking
    // user ids on failed password attempts.
    public Guid? UserId { get; set; }
}
