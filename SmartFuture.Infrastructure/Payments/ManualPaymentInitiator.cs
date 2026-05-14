using Microsoft.Extensions.Logging;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Infrastructure.Payments;

/// <summary>
/// Manual / admin-reconciliation initiator. Generates a deterministic-format internal
/// reference and never produces a redirect URL. Used when the customer pays via EFT/cash
/// out-of-band and the admin records the payment intent.
/// </summary>
public class ManualPaymentInitiator : IPaymentInitiator
{
    private readonly ILogger<ManualPaymentInitiator> _logger;

    public ManualPaymentInitiator(ILogger<ManualPaymentInitiator> logger)
    {
        _logger = logger;
    }

    public PaymentProviderType Provider => PaymentProviderType.Manual;

    public Task<PaymentProviderInitiationResult> InitiateAsync(Invoice invoice, Payment payment, InitiateInvoicePaymentRequestDto request, CancellationToken cancellationToken = default)
    {
        var providerReference = $"MANUAL-{Guid.NewGuid():N}";

        _logger.LogInformation(
            "ManualPaymentInitiator created reference {Reference} for payment {PaymentNumber} (invoice {InvoiceNumber})",
            providerReference, payment.PaymentNumber, invoice.InvoiceNumber);

        return Task.FromResult(PaymentProviderInitiationResult.Succeeded(
            providerReference: providerReference,
            providerCheckoutId: null,
            redirectUrl: null,
            expiresAtUtc: null,
            metadataJson: null));
    }
}
