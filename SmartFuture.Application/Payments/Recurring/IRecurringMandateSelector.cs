using SmartFuture.Domain.Billing;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 1C — the single authoritative resolver for "which reusable mandate
/// does the recurring engine charge for this customer?". Used by both
/// <c>AutoBillingService.ChargeInvoiceAsync</c> (authoritative selection) and
/// the Stage 2/3 runner pre-checks, so eligibility can't drift between them.
///
/// Rules:
///   • Supported providers: Paystack always; PayFast only when
///     <c>AutoBilling__EnablePayFastRecurring == true</c>.
///   • Considers only active, reusable, DEFAULT mandates.
///   • Deterministic tie-break: Paystack first (proven + synchronous), then
///     most-recently-updated default.
/// Reads only non-sensitive mandate fields — never the protected token.
/// </summary>
public interface IRecurringMandateSelector
{
    /// <summary>The mandate to charge, or null when none is eligible.</summary>
    Task<CustomerPaymentMandate?> ResolveDefaultChargeableMandateAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Why a customer is/!chargeable — drives runner skip reporting.</summary>
    Task<MandateAvailability> GetAvailabilityAsync(
        Guid userId, CancellationToken cancellationToken = default);
}

public enum MandateAvailability
{
    /// <summary>An eligible default reusable mandate exists for an enabled provider.</summary>
    Available = 0,

    /// <summary>No active default reusable mandate for any supported provider.</summary>
    NoReusableMandate = 1,

    /// <summary>The only default reusable mandate is PayFast, but EnablePayFastRecurring is off.</summary>
    PayFastRecurringDisabled = 2
}
