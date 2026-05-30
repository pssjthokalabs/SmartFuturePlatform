namespace SmartFuture.Application.Payments;

/// <summary>
/// Phase 4–7 feature flags for the Paystack auto-debit pipeline.
///
///   <see cref="Enabled"/>                    — master switch. False
///                                              means the entire
///                                              auto-billing subsystem
///                                              is dormant.
///   <see cref="ChargeAuthorizationEnabled"/> — gate on
///                                              <c>PaystackChargeAuthorizationService</c>
///                                              actually issuing
///                                              charge-authorization
///                                              calls. False keeps the
///                                              service callable in
///                                              skeleton form (returns
///                                              "disabled") so the
///                                              install hook + retry
///                                              job can be wired and
///                                              tested without real
///                                              charges happening.
///   <see cref="RetryJobEnabled"/>            — gate on the daily retry
///                                              worker (Phase 5).
///
/// All three default to false. Real production rollout flips them on
/// one at a time after a controlled UAT mandate test succeeds.
/// </summary>
public class AutoBillingSettings
{
    public const string SectionName = "AutoBilling";

    public bool Enabled { get; set; } = false;
    public bool ChargeAuthorizationEnabled { get; set; } = false;
    public bool RetryJobEnabled { get; set; } = false;

    /// <summary>
    /// Phase 4 — when true, the InstallationService completion hook
    /// will try to auto-charge the first-monthly invoice via Paystack
    /// using the customer's default reusable mandate. Off by default
    /// so the hook stays a breadcrumb log until the operator
    /// explicitly opts in.
    /// </summary>
    public bool InstallationCompletionAutoChargeEnabled { get; set; } = false;
}
