using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.OrderIntents;

public interface IOrderIntentService
{
    Task<Result<OrderIntentDto>> CreatePublicAsync(
        CreateOrderIntentRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<PublicOrderIntentPreviewDto>> GetPublicPreviewAsync(
        string intentToken, CancellationToken cancellationToken = default);

    Task<Result<OrderIntentDto>> ClaimAsync(
        string intentToken, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> ConvertAsync(
        string intentToken,
        ConvertOrderIntentRequestDto? overrides,
        CancellationToken cancellationToken = default);
}
