namespace SmartFuture.Application.Payments.BillingOps;

/// <summary>
/// Billing Ops v1 settings. The read/monitoring surface reuses
/// <c>AutoBilling__AdminReportingEnabled</c>; this section only governs the
/// manual service-invoice creation endpoint and the attention-list
/// thresholds.
///
/// Every gate defaults to the safe value: manual creation is OFF until an
/// operator opts in, and both confirmation phrases must be matched exactly.
/// No secret lives here — the phrases are deliberately human-readable
/// guards, not credentials.
/// </summary>
public class BillingOpsSettings
{
    public const string SectionName = "BillingOps";

    /// <summary>
    /// Master switch for <c>POST /api/admin/billing-ops/invoices/manual-service</c>.
    /// Default FALSE — the endpoint returns 503 until enabled.
    /// </summary>
    public bool ManualInvoiceEnabled { get; set; } = false;

    /// <summary>Required confirmation phrase for a normal (non-force) manual create.</summary>
    public string ManualInvoiceConfirmationPhrase { get; set; } = "CREATE_MANUAL_SERVICE_INVOICE";

    /// <summary>Required confirmation phrase for a forced (schedule-detached) duplicate create.</summary>
    public string ForceCreateConfirmationPhrase { get; set; } = "FORCE_CREATE_DUPLICATE_SERVICE_INVOICE";

    /// <summary>
    /// A <c>Pending</c> PayFast settlement older than this many minutes is
    /// flagged in the attention list as "stuck awaiting ITN". Default 24h.
    /// </summary>
    public int PendingSettlementStaleMinutes { get; set; } = 1440;
}
