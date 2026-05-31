namespace SmartFuture.Application.Orders.Dtos;

/// <summary>
/// Request body for <c>POST /api/admin/orders/{id}/activate-service</c>.
///
/// Admin uses this AFTER manually activating the customer's line on
/// the Openserve portal. SmartFuture cannot automate that step today
/// — Openserve has no activation API — so the lifecycle terminates
/// with an explicit admin click.
/// </summary>
public class AdminActivateServiceRequestDto
{
    /// <summary>
    /// Free-text Openserve ticket / activation reference so SmartFuture
    /// admin can correlate orders with Openserve operations. Optional
    /// but strongly encouraged.
    /// </summary>
    public string? OpenserveActivationReference { get; set; }

    /// <summary>Free-text notes captured against the activation audit row.</summary>
    public string? ActivationNotes { get; set; }

    /// <summary>
    /// When the line was actually activated on Openserve. Defaults to
    /// "now" if omitted. Always used as the billing anchor — the first
    /// recurring monthly invoice is due 30 days from this date.
    /// </summary>
    public DateTime? ActivationDateUtc { get; set; }
}
