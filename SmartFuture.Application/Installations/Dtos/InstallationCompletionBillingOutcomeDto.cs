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
    /// The NetworkAccount (service) row linked to the order, when one
    /// exists. The portal uses this to render a "View Service" link
    /// straight from the completion modal.
    /// </summary>
    public Guid? ServiceId { get; set; }

    /// <summary>
    /// True when the order ended up at <c>Active</c> as a result of the
    /// completion flow (either the config-driven auto-activation path
    /// or an explicit admin opt-in via
    /// <see cref="Dtos.AdminUpdateInstallationStatusDto.ActivateServiceIfPaymentSucceeds"/>).
    /// </summary>
    public bool ServiceActivated { get; set; }

    /// <summary>
    /// Next billing date once the service is Active. Null if the order
    /// hasn't reached Active.
    /// </summary>
    public DateTime? NextPayDateUtc { get; set; }

    /// <summary>
    /// Echoes the per-action admin opt-in so the portal can render the
    /// resulting state honestly ("you asked for auto-activation and it
    /// happened" vs "the system activated this on policy").
    /// </summary>
    public bool ActivateRequestedByAdmin { get; set; }

    /// <summary>
    /// Short admin-friendly headline the portal renders verbatim.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
