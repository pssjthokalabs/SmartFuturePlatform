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

    // ─── Phase 0A — recurring billing engine (foundation only) ─────
    //
    // The recurring billing hosted service + orchestrator are introduced
    // in Phase 0A as a SHELL: the worker is disabled by default, runs in
    // dry-run by default, and every stage is a no-op. None of the flags
    // below cause an invoice to be generated, a customer to be charged, a
    // retry to fire, or an account to be suspended in Phase 0A.

    /// <summary>
    /// Master switch for the recurring billing hosted service. False (the
    /// safe default) means the worker never loops — no scheduled run at all.
    /// </summary>
    public bool RecurringWorkerEnabled { get; set; } = false;

    /// <summary>UTC hour (0–23) the daily run fires at.</summary>
    public int DailyRunHour { get; set; } = 2;

    /// <summary>UTC minute (0–59) the daily run fires at.</summary>
    public int DailyRunMinute { get; set; } = 0;

    /// <summary>
    /// Lead time for the (future, Phase 0B) recurring invoice generator —
    /// generate an upcoming invoice this many days before its due date.
    /// Unused in Phase 0A (generation stage is a no-op).
    /// </summary>
    public int GenerateInvoicesDaysBeforeDue { get; set; } = 5;

    /// <summary>
    /// When true (the safe default), the recurring run evaluates and
    /// reports what it WOULD do but performs no money-moving side effects.
    /// In Phase 0A all stages are no-op regardless, so this changes nothing
    /// yet — it is threaded through now so later phases honour it from day one.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// When true (default), a run will not start if another run is still
    /// <c>Running</c> within <see cref="RunLockStalenessMinutes"/> — the
    /// DB-backed concurrency lock.
    /// </summary>
    public bool PreventConcurrentRuns { get; set; } = true;

    /// <summary>Per-run cap on invoices generated (future Phase 0B). Unused in Phase 0A.</summary>
    public int MaxInvoicesPerRun { get; set; } = 100;

    /// <summary>Per-run cap on charges attempted (future Phase 0C/0D). Unused in Phase 0A.</summary>
    public int MaxChargesPerRun { get; set; } = 100;

    /// <summary>
    /// When true, the (future, Phase 0E) grace-period stage may transition
    /// a non-paying account to Suspended. False (the safe default) means the
    /// stage only reports suspension candidates. No effect in Phase 0A
    /// (grace stage is a no-op).
    /// </summary>
    public bool SuspendAfterGracePeriodEnabled { get; set; } = false;

    /// <summary>
    /// A <c>Running</c> <c>BillingRunLog</c> row older than this many minutes
    /// is treated as stale (crashed run) and no longer blocks a new run.
    /// </summary>
    public int RunLockStalenessMinutes { get; set; } = 120;

    /// <summary>
    /// Per-run cap on suspension candidates evaluated by the Phase 0E grace
    /// stage. Bounds a runaway scan; excess candidates are deferred to the
    /// next run (truncation logged). Report-only in Phase 0E.
    /// </summary>
    public int MaxSuspensionCandidatesPerRun { get; set; } = 500;

    /// <summary>
    /// Phase 0F — master switch for the read-only admin recurring-billing
    /// reporting endpoints. Default true (the endpoints are already
    /// RequireAdmin). When false, those endpoints return 503 — a kill-switch
    /// with no effect on the billing pipeline itself.
    /// </summary>
    public bool AdminReportingEnabled { get; set; } = true;
}
