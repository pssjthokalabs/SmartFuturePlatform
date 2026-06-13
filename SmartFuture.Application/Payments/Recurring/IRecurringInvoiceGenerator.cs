namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0B — Stage 1 of the recurring billing run. Generates upcoming
/// recurring service invoices from due <c>ServiceBillingSchedule</c> rows.
/// Generates invoices ONLY — it never charges, retries, or suspends.
/// </summary>
public interface IRecurringInvoiceGenerator
{
    Task<RecurringInvoiceGenerationResult> GenerateDueInvoicesAsync(
        RecurringBillingRunContext context, CancellationToken cancellationToken = default);
}

/// <summary>Counters + notes for one generation stage run (surfaced in BillingRunLog).</summary>
public sealed class RecurringInvoiceGenerationResult
{
    public int SchedulesConsidered { get; set; }
    public int InvoicesGenerated { get; set; }
    public int WouldGenerate { get; set; }          // dry-run only
    public int DuplicatesSkipped { get; set; }
    public int InactiveSkipped { get; set; }        // service/network account not billable
    public int NonRecurringSkipped { get; set; }    // OnceOff cycle, etc.
    public bool Truncated { get; set; }             // batch cap hit
    public int ErrorCount { get; set; }
    public List<string> Errors { get; } = new();
}
