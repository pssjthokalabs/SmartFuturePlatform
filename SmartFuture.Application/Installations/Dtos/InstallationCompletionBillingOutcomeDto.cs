namespace SmartFuture.Application.Installations.Dtos;

/// <summary>
/// Returned on the AdminUpdateStatus response when an installation
/// transitions to Completed, so the admin portal can render a single
/// success/warning modal explaining what happened to the first
/// monthly-service invoice and the auto-debit attempt.
///
/// Always set when the transition is Completed; <c>null</c> otherwise.
/// Existing callers that only read <c>InstallationDto</c> are unaffected.
/// </summary>
public class InstallationCompletionBillingOutcomeDto
{
    public bool MonthlyInvoiceCreated { get; set; }
    public Guid? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public decimal? InvoiceAmount { get; set; }

    public bool AutoBillingAttempted { get; set; }
    public bool AutoBillingSucceeded { get; set; }
    public decimal? ProviderAmount { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>
    /// "Active" / "Pending Activation" / "Pending Payment" depending on
    /// the auto-billing outcome and
    /// <c>ServiceActivation:RequireManualOpenserveActivation</c>.
    /// </summary>
    public string? ResultingServiceStatus { get; set; }

    /// <summary>
    /// Short admin-friendly headline the portal renders verbatim.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
