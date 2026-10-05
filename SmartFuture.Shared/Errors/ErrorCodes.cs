namespace SmartFuture.Shared.Errors;

public static class ErrorCodes
{
    public const string BAD_REQUEST = "BAD_REQUEST";
    public const string VALIDATION_ERROR = "VALIDATION_ERROR";
    public const string UNAUTHORIZED = "UNAUTHORIZED";
    public const string INVALID_CREDENTIALS = "INVALID_CREDENTIALS";
    public const string INVALID_REFRESH_TOKEN = "INVALID_REFRESH_TOKEN";
    public const string REFRESH_TOKEN_EXPIRED = "REFRESH_TOKEN_EXPIRED";
    public const string FORBIDDEN = "FORBIDDEN";
    public const string NOT_FOUND = "NOT_FOUND";
    public const string CONFLICT = "CONFLICT";
    public const string EMAIL_TAKEN = "EMAIL_TAKEN";
    public const string PHONE_TAKEN = "PHONE_TAKEN";
    public const string WEAK_PASSWORD = "WEAK_PASSWORD";
    public const string TOO_MANY_REQUESTS = "TOO_MANY_REQUESTS";
    public const string PROVIDER_NOT_CONFIGURED = "PROVIDER_NOT_CONFIGURED";
    // Use for **expected** failures talking to a third-party service
    // (timeout, bad gateway, malformed payload). Maps to 502 so the
    // caller can distinguish an upstream failure from a server bug.
    public const string UPSTREAM_UNAVAILABLE = "UPSTREAM_UNAVAILABLE";
    public const string PAYMENT_INIT_FAILED = "PAYMENT_INIT_FAILED";
    public const string PAYMENT_ALREADY_PAID = "PAYMENT_ALREADY_PAID";
    public const string PAYMENT_AMOUNT_MISMATCH = "PAYMENT_AMOUNT_MISMATCH";
    // Phase 51 — customer attempted to create a new order while another
    // non-terminal order or active service already exists for them.
    // Backed by `OrderService.GetMyEligibilityAsync` and enforced again
    // inside `CreateMineAsync` so the API is the source of truth.
    public const string ORDER_ALREADY_IN_PROGRESS = "ORDER_ALREADY_IN_PROGRESS";
    // Openserve Product Qualification (the Fibre eligibility authority) says
    // the selected Fibre package can't be ordered at this address — no
    // Fibre, the product/speed isn't offered, or the address Openserve
    // resolved isn't confirmed as the customer's. Maps to 409; the message
    // is customer-safe.
    public const string FIBRE_NOT_ELIGIBLE = "FIBRE_NOT_ELIGIBLE";

    // Specific Fibre checkout refusals (all 409, customer-safe messages).
    // FIBRE_NOT_ELIGIBLE stays defined for older clients; these say WHY.
    // The Openserve premises for the customer's address isn't established
    // (no nearby Openserve address matched it) — NOT "no Fibre".
    public const string OPENSERVE_ADDRESS_UNRESOLVED = "OPENSERVE_ADDRESS_UNRESOLVED";
    // The customer's established Openserve premises has no immediately-available Fibre.
    public const string OPENSERVE_FTTH_UNAVAILABLE = "OPENSERVE_FTTH_UNAVAILABLE";
    // Fibre is there, but not the package's mapped Openserve product/speed.
    public const string OPENSERVE_PRODUCT_UNAVAILABLE = "OPENSERVE_PRODUCT_UNAVAILABLE";
    // Several Openserve units at the address and the customer's unit didn't match one.
    public const string OPENSERVE_BUILDING_UNIT_REQUIRED = "OPENSERVE_BUILDING_UNIT_REQUIRED";
    public const string WEBHOOK_SIGNATURE_INVALID = "WEBHOOK_SIGNATURE_INVALID";
    public const string EXCEPTION = "EXCEPTION";
    public const string SMS_NOT_CONFIGURED = "SMS_NOT_CONFIGURED";
    public const string VERIFICATION_CODE_INVALID = "VERIFICATION_CODE_INVALID";
    public const string VERIFICATION_CODE_EXPIRED = "VERIFICATION_CODE_EXPIRED";
    public const string VERIFICATION_CODE_ATTEMPTS_EXCEEDED = "VERIFICATION_CODE_ATTEMPTS_EXCEEDED";
    // Returned by Auth/login when the caller's password was correct but
    // the account hasn't completed identity verification. The client is
    // expected to route the user into the OTP flow with the same
    // identifier they just submitted — no token is issued by this path,
    // so an unverified user cannot reach any authenticated endpoint via
    // the password login.
    public const string ACCOUNT_VERIFICATION_REQUIRED = "ACCOUNT_VERIFICATION_REQUIRED";
    // Phase 3.6 — feature kill-switch trip (e.g. Provisioning.Enabled=false).
    // Distinct from PROVIDER_NOT_CONFIGURED, which means "config missing";
    // SERVICE_UNAVAILABLE means "config present but the operator turned it off."
    public const string SERVICE_UNAVAILABLE = "SERVICE_UNAVAILABLE";

    // Job Opportunities module — a person who already has a SmartFuture
    // account (e.g. a JobSubscriber) tried to register again for a
    // different product line, and the supplied password did NOT match
    // the existing account. This is deliberately distinct from
    // EMAIL_TAKEN: the client should route the user to sign-in and then
    // call the role-upgrade endpoint, not show a dead-end
    // "email already registered" error. Maps to 409.
    public const string ACCOUNT_EXISTS_SIGN_IN_REQUIRED = "ACCOUNT_EXISTS_SIGN_IN_REQUIRED";
}
