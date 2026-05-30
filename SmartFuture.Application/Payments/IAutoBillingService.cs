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
}

/// <summary>
/// Why an auto-charge attempt was launched. Surfaced in audit metadata
/// so we can later filter "all installation-completion charges that
/// failed".
/// </summary>
public enum AutoBillingChargeSource
{
    InstallationCompletion = 0,
    Retry = 1,
    MonthlyRenewal = 2,
    AdminManual = 3
}

public sealed record AutoBillingChargeOutcome(
    bool Charged,
    string? Reference,
    Guid? PaymentId,
    Guid? PaymentInitiationId,
    string? FailureReason);
