using SmartFuture.Application.Webhooks.Dtos;

namespace SmartFuture.Application.Webhooks;

public interface IWebhookSignatureValidator
{
    Task<bool> IsValidAsync(
        PaymentWebhookRequestDto request,
        CancellationToken cancellationToken = default);
}
