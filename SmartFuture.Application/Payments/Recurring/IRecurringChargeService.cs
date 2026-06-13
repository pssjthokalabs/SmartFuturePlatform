using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0A — provider-neutral recurring-charge seam. The recurring
/// billing engine resolves an implementation by
/// <see cref="CustomerPaymentMandate.Provider"/> and charges through it,
/// so the engine never knows provider-specific token/charge details.
///
/// Phase 0A registers ONLY the Paystack implementation
/// (<c>PaystackRecurringChargeService</c>), which wraps the existing
/// <c>PaystackChargeAuthorizationService</c> with byte-equivalent
/// behaviour. PayFast's implementation (and the asynchronous
/// <see cref="RecurringChargeOutcomeKind.Pending"/> settlement path) is
/// deferred to the PayFast recurring phase (Phase 2).
/// </summary>
public interface IRecurringChargeService
{
    PaymentProviderType Provider { get; }

    Task<RecurringChargeResult> ChargeAsync(
        CustomerPaymentMandate mandate,
        decimal amountZar,
        string reference,
        RecurringChargeContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome shape of a recurring charge. Supports BOTH synchronous
/// settlement (Paystack: Success/Failed returned inline) and future
/// asynchronous settlement (PayFast: <see cref="Pending"/> — confirmed
/// later via ITN/webhook, never marked paid on the API response alone).
/// </summary>
public enum RecurringChargeOutcomeKind
{
    Success,
    Failed,
    Pending
}

/// <summary>
/// Provider-neutral charge result. <see cref="ProviderTransactionId"/>
/// and <see cref="PaidAtUtc"/> mirror the fields the existing
/// Paystack path threads into Payment/PaymentInitiation.
/// </summary>
public sealed record RecurringChargeResult(
    RecurringChargeOutcomeKind Kind,
    string? ProviderTransactionId,
    DateTime? PaidAtUtc,
    string? ProviderStatus,
    string? Message)
{
    public static RecurringChargeResult Success(string? providerTransactionId, DateTime? paidAtUtc, string? providerStatus = null)
        => new(RecurringChargeOutcomeKind.Success, providerTransactionId, paidAtUtc, providerStatus, null);

    public static RecurringChargeResult Failed(string? message, string? providerTransactionId = null, string? providerStatus = null)
        => new(RecurringChargeOutcomeKind.Failed, providerTransactionId, null, providerStatus, message);

    public static RecurringChargeResult Pending(string? providerTransactionId, string? message = null, string? providerStatus = null)
        => new(RecurringChargeOutcomeKind.Pending, providerTransactionId, null, providerStatus, message);
}

/// <summary>
/// Ambient context for a recurring charge — lets a provider implementation
/// log/branch without re-querying. Carried by value so it stays cheap.
/// </summary>
public sealed record RecurringChargeContext(
    Guid InvoiceId,
    Guid CustomerId,
    AutoBillingChargeSource Source,
    bool IsDryRun);
