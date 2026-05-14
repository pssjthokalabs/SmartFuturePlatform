using SmartFuture.Application.Webhooks.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Webhooks;

public interface IWebhookPayloadParser
{
    Task<Result<ParsedPaymentWebhookDto>> ParsePaymentWebhookAsync(
        PaymentWebhookRequestDto request,
        CancellationToken cancellationToken = default);
}
