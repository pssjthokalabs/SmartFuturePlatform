using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.Payments.Dtos;

public class PaymentInitiationFilterRequestDto : PagedListQueryBase
{
    public Guid? InvoiceId { get; set; }
    public Guid? PaymentId { get; set; }
    public PaymentProviderType? Provider { get; set; }
    public new PaymentInitiationStatus? Status { get; set; }
    public string? ProviderReference { get; set; }
    public string? ProviderCheckoutId { get; set; }
}
