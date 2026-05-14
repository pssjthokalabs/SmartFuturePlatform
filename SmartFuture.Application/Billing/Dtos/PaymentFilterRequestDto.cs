using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Billing.Dtos;

public class PaymentFilterRequestDto : PagedListQueryBase
{
    public Guid? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public Guid? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public new PaymentStatus? Status { get; set; }
    public PaymentMethodType? Method { get; set; }

    public DateTime? PaidFromUtc { get; set; }
    public DateTime? PaidToUtc { get; set; }

    public string? GatewayReference { get; set; }
    public string? GatewayTransactionId { get; set; }
}
