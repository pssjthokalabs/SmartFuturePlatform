using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Billing.Dtos;

public class CreatePaymentRequestDto
{
    public Guid InvoiceId { get; set; }
    public PaymentMethodType Method { get; set; } = PaymentMethodType.Other;
    public decimal Amount { get; set; }
    public DateTime? PaidAtUtc { get; set; }

    public string? GatewayName { get; set; }
    public string? GatewayReference { get; set; }
    public string? GatewayTransactionId { get; set; }
    public string? ExternalReference { get; set; }
    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }
}
