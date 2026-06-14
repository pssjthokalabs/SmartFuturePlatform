namespace SmartFuture.Application.Payments.PayFast;

/// <summary>
/// Phase 1B — low-level client for PayFast's ad-hoc recurring charge API
/// (<c>POST /subscriptions/{token}/adhoc</c>). Signs with the PayFast API
/// signature, sends via HttpClient, and returns a transport-level result.
///
/// SECURITY: the token is used only in-memory to build the request URL/path
/// and is NEVER logged (not even as part of the URL). Gated by
/// <c>PayFast__AdhocChargingEnabled</c> at the caller
/// (<c>PayFastRecurringChargeService</c>), not here.
/// </summary>
public interface IPayFastAdhocChargeService
{
    /// <param name="token">Raw PayFast subscription token (decrypted by the caller). Never logged.</param>
    /// <param name="amountCents">Charge amount in cents (integer, no decimals).</param>
    /// <param name="itemName">Required item name for the charge.</param>
    /// <param name="mPaymentId">SmartFuture provider reference — echoed back in the ITN.</param>
    Task<PayFastAdhocChargeResult> ChargeAsync(
        string token,
        long amountCents,
        string itemName,
        string mPaymentId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Transport-level outcome of an ad-hoc charge API call. Contains NO token
/// and no sensitive data. <see cref="Accepted"/> means PayFast accepted the
/// request (HTTP 2xx + success indicator) — financial settlement is still
/// confirmed asynchronously via the ITN.
/// </summary>
public sealed record PayFastAdhocChargeResult(
    bool Accepted,
    int HttpStatusCode,
    string? ProviderTransactionId,
    string? ProviderStatus,
    string? Message)
{
    public static PayFastAdhocChargeResult AcceptedResult(int httpStatusCode, string? providerTransactionId, string? providerStatus, string? message)
        => new(true, httpStatusCode, providerTransactionId, providerStatus, message);

    public static PayFastAdhocChargeResult Rejected(int httpStatusCode, string? providerStatus, string? message)
        => new(false, httpStatusCode, null, providerStatus, message);

    public static PayFastAdhocChargeResult Transport(string message)
        => new(false, 0, null, null, message);
}
