using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Domain.Billing;

/// <summary>
/// One row per attempted (or scheduled) auto-debit of an invoice.
///
/// The first row is the InstallationCompletion / MonthlyRenewal /
/// AdminManual call itself; subsequent rows are the daily retries
/// scheduled by <c>AutoBillingService</c> after a failure.
///
/// Retry policy (driven by <c>AutoBillingSettings</c>):
///   MaxRetryAttempts        — total attempts incl. initial (default 3)
///   RetryIntervalDays       — days between retries (default 1)
///   GracePeriodDays         — outer window after first failure (default 3)
///
/// The retry worker (Phase 5, deferred) reads <see cref="Status"/>
/// == <c>Pending</c> rows whose <see cref="ScheduledForUtc"/> has
/// passed and re-runs the charge. For UAT we exercise this via the
/// admin /run-test endpoint instead.
///
/// Customer manual payment cancels remaining retries by flipping any
/// outstanding Pending rows for the invoice to <c>Skipped</c>.
/// </summary>
public class PaymentRetryAttempt : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public Guid CustomerId { get; set; }
    public User? Customer { get; set; }

    /// <summary>Mandate this attempt was scheduled against. Nullable so admin can later swap mandates without orphaning history.</summary>
    public Guid? MandateId { get; set; }
    public CustomerPaymentMandate? Mandate { get; set; }

    public PaymentProviderType Provider { get; set; } = PaymentProviderType.Paystack;

    /// <summary>1-based attempt number. Attempt 1 = initial; attempts 2..N = retries.</summary>
    public int AttemptNumber { get; set; }

    /// <summary>Amount we intend to charge (invoice balance at the time of scheduling).</summary>
    public decimal Amount { get; set; }

    /// <summary>Actual amount sent to the provider (= <see cref="Amount"/> normally; differs only when UAT test-override applied).</summary>
    public decimal? ProviderAmount { get; set; }

    public PaymentRetryAttemptStatus Status { get; set; } = PaymentRetryAttemptStatus.Pending;

    public string? FailureReason { get; set; }
    public string? ProviderReference { get; set; }

    /// <summary>Where the attempt was launched from.</summary>
    public AutoBillingChargeSource Source { get; set; }

    /// <summary>UTC the worker should pick this attempt up. Set to now+RetryIntervalDays after a failure.</summary>
    public DateTime ScheduledForUtc { get; set; }

    /// <summary>UTC the attempt actually ran. Null while Pending.</summary>
    public DateTime? AttemptedUtc { get; set; }

    // PaymentId is the foreign key to the Payment row we minted for
    // this attempt — set after the charge call (or even before, for
    // dry-run scheduling). Nullable so a Skipped-before-attempted row
    // doesn't have to mint a Payment.
    public Guid? PaymentId { get; set; }
    public Payment? Payment { get; set; }
}
