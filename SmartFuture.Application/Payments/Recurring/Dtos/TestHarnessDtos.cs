namespace SmartFuture.Application.Payments.Recurring.Dtos;

// ─── Request bodies ─────────────────────────────────────────────────────────

/// <summary>Run the recurring engine once via the real orchestrator.</summary>
public sealed class RunOnceRequest
{
    public string ConfirmationPhrase { get; set; } = string.Empty;
    /// <summary>When false, requires AllowRealChargeRun=true.</summary>
    public bool DryRun { get; set; } = true;
    public string? Note { get; set; }
}

/// <summary>Nudge a schedule into the generation window (NextInvoiceDateUtc only).</summary>
public sealed class MakeGenerationDueRequest
{
    public string ConfirmationPhrase { get; set; } = string.Empty;
    /// <summary>Optional target; defaults to now (UTC) when omitted.</summary>
    public DateTime? NextInvoiceDateUtc { get; set; }
    public string? Note { get; set; }
}

/// <summary>Nudge an invoice due (DueAtUtc only).</summary>
public sealed class MakeInvoiceDueRequest
{
    public string ConfirmationPhrase { get; set; } = string.Empty;
    /// <summary>Optional target; defaults to now (UTC) when omitted. Back-date beyond GracePeriodDays to drive Stage 4.</summary>
    public DateTime? DueAtUtc { get; set; }
    public string? Note { get; set; }
}

/// <summary>Nudge a Pending retry attempt due (ScheduledForUtc only).</summary>
public sealed class MakeRetryDueRequest
{
    public string ConfirmationPhrase { get; set; } = string.Empty;
    /// <summary>Optional target; defaults to now (UTC) when omitted.</summary>
    public DateTime? ScheduledForUtc { get; set; }
    public string? Note { get; set; }
}

// ─── Response envelope ──────────────────────────────────────────────────────

/// <summary>
/// Uniform UAT test-harness response. <see cref="Before"/>/<see cref="After"/>
/// carry only non-sensitive date/status fields (or the run summary). No token,
/// authorization code, card data, raw gateway payload, passphrase, or signature.
/// </summary>
public sealed class TestHarnessActionResult
{
    public bool Success { get; set; } = true;
    public string Environment { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public object? Before { get; set; }
    public object? After { get; set; }
    public string Warning { get; set; } = "UAT test harness only";
}
