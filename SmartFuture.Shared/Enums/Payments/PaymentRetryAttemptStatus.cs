namespace SmartFuture.Shared.Enums.Payments;

/// <summary>
/// Status of a single <c>PaymentRetryAttempt</c> row. The auto-debit
/// pipeline writes one row per attempt; the retry worker reads
/// <see cref="Pending"/> rows whose <c>ScheduledForUtc</c> has passed
/// and re-runs the charge.
/// </summary>
public enum PaymentRetryAttemptStatus
{
    Pending = 0,
    Success = 1,
    Failed = 2,
    Skipped = 3
}

/// <summary>
/// Why an auto-charge attempt was launched. Surfaced in audit metadata
/// and on <c>PaymentRetryAttempt.Source</c>. Lives in Shared so the
/// Domain layer can reference it without an Application-layer
/// dependency.
/// </summary>
public enum AutoBillingChargeSource
{
    InstallationCompletion = 0,
    Retry = 1,
    MonthlyRenewal = 2,
    AdminManual = 3
}
