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
    Task<Result<AutoBillingChargeOutcome>> ChargeInvoiceAsync(
        Guid invoiceId, AutoBillingChargeSource source, CancellationToken cancellationToken = default);

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
}

public sealed record AutoBillingChargeOutcome(
    bool Charged,
    string? Reference,
    Guid? PaymentId,
    Guid? PaymentInitiationId,
    string? FailureReason);
