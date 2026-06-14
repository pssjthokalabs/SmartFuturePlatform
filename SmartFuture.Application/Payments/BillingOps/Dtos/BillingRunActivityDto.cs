using SmartFuture.Application.Payments.Recurring.Dtos;

namespace SmartFuture.Application.Payments.BillingOps.Dtos;

/// <summary>
/// Per-run activity, attributed by the run's time window. v1 has no
/// <c>BillingRunLogId</c> FK on invoices/charges/retries, so this is an
/// APPROXIMATION — <see cref="Approximate"/> is always true and the UI
/// labels it accordingly. Grace candidates are a current snapshot (not
/// historically attributable) and flagged via <see cref="GraceIsCurrentSnapshot"/>.
/// </summary>
public sealed class BillingRunActivityDto
{
    public Guid RunId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Always true in v1 — attribution is by time window, not a hard FK.</summary>
    public bool Approximate { get; set; } = true;

    /// <summary>True — grace candidates reflect the current state, not the run moment.</summary>
    public bool GraceIsCurrentSnapshot { get; set; } = true;

    public List<GeneratedInvoiceRowDto> GeneratedInvoices { get; set; } = new();
    public List<ChargeRowDto> Charges { get; set; } = new();
    public List<RetryRowDto> Retries { get; set; } = new();
    public List<SuspensionCandidateDto> GraceCandidates { get; set; } = new();
}
