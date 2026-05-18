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
    public const string WEAK_PASSWORD = "WEAK_PASSWORD";
    public const string TOO_MANY_REQUESTS = "TOO_MANY_REQUESTS";
    public const string PROVIDER_NOT_CONFIGURED = "PROVIDER_NOT_CONFIGURED";
    public const string PAYMENT_INIT_FAILED = "PAYMENT_INIT_FAILED";
    public const string PAYMENT_ALREADY_PAID = "PAYMENT_ALREADY_PAID";
    public const string PAYMENT_AMOUNT_MISMATCH = "PAYMENT_AMOUNT_MISMATCH";
    public const string WEBHOOK_SIGNATURE_INVALID = "WEBHOOK_SIGNATURE_INVALID";
    public const string EXCEPTION = "EXCEPTION";
    public const string SMS_NOT_CONFIGURED = "SMS_NOT_CONFIGURED";
    public const string VERIFICATION_CODE_INVALID = "VERIFICATION_CODE_INVALID";
    public const string VERIFICATION_CODE_EXPIRED = "VERIFICATION_CODE_EXPIRED";
    public const string VERIFICATION_CODE_ATTEMPTS_EXCEEDED = "VERIFICATION_CODE_ATTEMPTS_EXCEEDED";
}
