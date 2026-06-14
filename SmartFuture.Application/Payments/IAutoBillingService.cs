using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments;

/// <summary>
/// Orchestrates a Paystack auto-debit against a stored reusable
/// mandate. This is the seam every auto-charge entry-point (install
/// completion hook, future retry job, future monthly job, admin
/// retry button) should call — keeps all the gate-checks,
/// PaymentInitiation row creation, charge call, verify, audit, and
/// PaymentApplier dispatch in ONE place.
///
/// Every method is a NO-OP unless ALL of the following are true:
///   - AutoBilling.Enabled
///   - AutoBilling.ChargeAuthorizationEnabled
///   - CustomerProfile.AutoBillingEnabled
///   - The customer has an active default reusable mandate
///   - Invoice is in a non-terminal payable state
///
/// Manual invoice payment is unaffected.
/// </summary>
public interface IAutoBillingService
{
    /// <param name="executeRetryAttemptId">
    /// Phase 0D — when supplied (by the retry worker), the existing Pending
    /// <c>PaymentRetryAttempt</c> with this id is REUSED for this execution
    /// instead of creating a new attempt row. This keeps attempt numbering
    /// correct and prevents duplicate/runaway retry rows. When null (install
    /// hook, monthly Stage 2, admin run-test) behaviour is unchanged: a fresh
    /// attempt row is created.
    /// </param>
    Task<Result<AutoBillingChargeOutcome>> ChargeInvoiceAsync(
        Guid invoiceId, AutoBillingChargeSource source,
        Guid? executeRetryAttemptId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// UAT/admin manual driver for the full auto-billing cycle. Iterates
    /// eligible customer invoices, attempts a charge_authorization
    /// against the default mandate, applies success/failure, schedules
    /// retries, and returns a summary the operator can verify.
    ///
    /// HARD-BLOCKED in Production unless
    /// <c>AutoBillingSettings.ManualTestEndpointEnabled</c> is true AND
    /// env is non-production — the implementation refuses the call
    /// otherwise so a stale flag from a UAT restore can't fire real
    /// charges in prod.
    /// </summary>
    Task<Result<AutoBillingCycleSummaryDto>> RunAutoBillingCycleAsync(
        string? userEmail = null, bool dryRun = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 1C2 — reconcile the auto-billing rows for a charge that settled
    /// ASYNCHRONOUSLY (PayFast ad-hoc → ITN), after the invoice itself has
    /// been settled by <c>PaymentApplierService</c>. Matched by the
    /// <c>PaymentRetryAttempt.PaymentId == paymentId</c> link.
    ///
    ///   • <paramref name="completed"/> true  → mark the Pending attempt
    ///     Success, skip sibling Pending attempts, mark the PaymentInitiation
    ///     Succeeded. No email (the applier already sent InvoicePaid).
    ///   • <paramref name="completed"/> false → mark the attempt + initiation
    ///     Failed, schedule exactly one next Pending retry if budget remains,
    ///     send the failure email once.
    ///
    /// Idempotent (no-op if the attempt is already terminal or if no
    /// auto-billing attempt exists) and best-effort — callers must not let it
    /// disrupt ITN settlement.
    /// </summary>
    Task ReconcileProviderSettlementAsync(
        Guid paymentId, bool completed, CancellationToken cancellationToken = default);
}

public sealed record AutoBillingChargeOutcome(
    bool Charged,
    string? Reference,
    Guid? PaymentId,
    Guid? PaymentInitiationId,
    string? FailureReason);
