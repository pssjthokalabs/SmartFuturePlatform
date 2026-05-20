using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Billing.Dtos;

public class PaymentFilterRequestDto : PagedListQueryBase
{
    public Guid? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public Guid? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public PaymentStatus? StatusFilter { get; set; }
    public PaymentMethodType? Method { get; set; }

    public DateTime? PaidFromUtc { get; set; }
    public DateTime? PaidToUtc { get; set; }

    public string? GatewayReference { get; set; }
    public string? GatewayTransactionId { get; set; }

    // Phase 49 — finance UX filters.
    public Guid? CustomerUserId { get; set; }
    public Guid? ServiceId { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public string? GatewayName { get; set; }
}
