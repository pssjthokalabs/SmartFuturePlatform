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

    // ─── UAT live-amount override (DO NOT enable in Production) ────
    //
    // When true, server-side Paystack charge_authorization calls
    // (auto-debit of monthly invoices) send the override TestAmount
    // to Paystack instead of the real invoice balance, while the
    // payment apply still settles the full invoice via the override
    // audit fields on Payment / PaymentInitiation.
    //
    // Hard-blocked in Production regardless of the flag value:
    // <see cref="AutoBillingService"/> consults IHostEnvironment and
    // refuses to apply the override when env=Production. Mirrors the
    // existing pattern in PaystackPaymentInitiator.
    /// <summary>
    /// UAT-only. When true (and env is non-production AND the
    /// Paystack live-override is permitted), auto-debit charges send
    /// <see cref="TestAmount"/> to Paystack instead of the real
    /// invoice balance. Audited per-payment via
    /// <c>Payment.IsTestAmountOverrideApplied</c>.
    /// </summary>
    public bool UseTestAmountOverride { get; set; } = false;

    /// <summary>
    /// Amount (in ZAR) sent to Paystack when
    /// <see cref="UseTestAmountOverride"/> is active. Must be &gt; 0.
    /// </summary>
    public decimal? TestAmount { get; set; }

    // ─── Retry foundation ──────────────────────────────────────────
    //
    // Per-attempt records are created by <see cref="AutoBillingService"/>.
    // The actual daily worker that consumes them is still Phase 5
    // (deferred); the manual /run-test endpoint exercises retry
    // attempts inline so UAT can prove the lifecycle works.

    /// <summary>Maximum number of auto-debit attempts per invoice (initial + retries).</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Days between retry attempts.</summary>
    public int RetryIntervalDays { get; set; } = 1;

    /// <summary>Grace-period window after the initial failure inside which retries fire.</summary>
    public int GracePeriodDays { get; set; } = 3;

    // ─── Notifications ─────────────────────────────────────────────

    /// <summary>When false, billing skips the failure email entirely (silent retry).</summary>
    public bool SendFailureEmails { get; set; } = true;

    /// <summary>
    /// When true (the safe default), an email-send failure is logged
    /// but never aborts the auto-debit pipeline. False makes failed
    /// emails hard-stop billing — useful for diagnostics, dangerous
    /// for production.
    /// </summary>
    public bool EmailFailuresDoNotBlockBilling { get; set; } = true;

    // ─── Test endpoint (admin-only, prod-blocked) ──────────────────

    /// <summary>
    /// When true (and env is non-production), enables
    /// <c>POST /api/admin/auto-billing/run-test</c>. Production
    /// always rejects the endpoint regardless of this flag — see
    /// <see cref="AutoBillingService.RunAutoBillingCycleAsync"/>.
    /// </summary>
    public bool ManualTestEndpointEnabled { get; set; } = false;
}
