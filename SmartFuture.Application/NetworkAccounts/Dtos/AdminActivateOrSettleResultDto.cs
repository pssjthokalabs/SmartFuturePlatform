namespace SmartFuture.Application.NetworkAccounts.Dtos;

/// <summary>
/// Outcome of the admin "Activate Service / Force Settle" button on
/// the Client Service Detail page. The endpoint intelligently routes
/// based on the service's current state:
///
///   - Pending Payment → ensure the monthly invoice exists, attempt
///     Paystack auto-debit using the saved default mandate, return
///     success / failure outcome.
///   - Pending Activation → delegate to the standard
///     OrderService.AdminActivateServiceAsync path (manual Openserve
///     completion is still required when that flag is on).
///   - Pending Installation → conflict, with helpful message.
///   - Active → idempotent no-op success.
///
/// Mirrors the InstallationCompletionBillingOutcomeDto shape so the
/// admin portal can render the same success/warning modal regardless
/// of which entry-point fired the auto-debit attempt.
/// </summary>
public class AdminActivateOrSettleResultDto
{
    public System.Guid ServiceId { get; set; }
    public string? PreviousServiceStatus { get; set; }
    public string? NewServiceStatus { get; set; }

    public bool MonthlyInvoiceCreated { get; set; }
    public System.Guid? MonthlyInvoiceId { get; set; }
    public string? MonthlyInvoiceNumber { get; set; }
    public decimal? InvoiceAmount { get; set; }

    public bool AutoBillingAttempted { get; set; }
    public bool AutoBillingSucceeded { get; set; }
    public decimal? ProviderAmount { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>Short admin-friendly headline the portal renders verbatim.</summary>
    public string Message { get; set; } = string.Empty;
}
