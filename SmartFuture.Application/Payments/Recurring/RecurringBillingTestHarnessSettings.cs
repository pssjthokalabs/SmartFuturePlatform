namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// UAT-ONLY recurring-billing Swagger test harness settings. Every flag
/// defaults to the safe value (everything OFF). The harness is ADDITIONALLY
/// hard-blocked in Production at runtime (<c>IHostEnvironment.IsProduction()</c>)
/// regardless of these flags, requires <c>RequireAdmin</c>, and requires the
/// confirmation phrase on every call.
///
/// The harness NEVER bypasses the real engine: <c>run-once</c> calls the same
/// <see cref="IRecurringBillingOrchestrator"/> the hosted service uses, and the
/// date nudges only move a single date field so the real stages pick the row
/// up on the next run. No provider call, no apply, no mark-paid.
/// </summary>
public class RecurringBillingTestHarnessSettings
{
    public const string SectionName = "RecurringBillingTestHarness";

    /// <summary>Master switch. False (default) → every endpoint returns 503.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Gates the three date-nudge endpoints (schedule / invoice / retry).</summary>
    public bool AllowDateNudges { get; set; } = false;

    /// <summary>Gates <c>POST /run-once</c>.</summary>
    public bool AllowRunOnce { get; set; } = false;

    /// <summary>Additionally required for a real (non-dry-run) <c>run-once</c>.</summary>
    public bool AllowRealChargeRun { get; set; } = false;

    /// <summary>Must match the <c>confirmationPhrase</c> on every request (ordinal, non-empty).</summary>
    public string ConfirmationPhrase { get; set; } = "UAT_RECURRING_BILLING_TEST";
}
