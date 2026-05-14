using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Webhooks.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Webhooks;

public interface IWebhookInboxService
{
    Task<Result<WebhookInboxDto>> ReceivePaymentWebhookAsync(
        PaymentWebhookRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<WebhookInboxDto>>> SearchAdminAsync(
        WebhookInboxFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<WebhookInboxDto>> GetAdminByIdAsync(
        Guid id, CancellationToken cancellationToken = default);
}
