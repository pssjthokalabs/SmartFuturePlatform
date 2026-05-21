using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.OrderIntents;

public interface IOrderIntentService
{
    Task<Result<OrderIntentDto>> CreatePublicAsync(
        CreateOrderIntentRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 50D — combines user registration with order-intent creation
    /// and issues a one-time portal-auth-handoff token. Atomic: either
    /// every row is created or nothing is. The returned response holds
    /// the raw handoff token (single use) — never persist it client-side.
    /// </summary>
    Task<Result<OrderIntentWithRegistrationResponseDto>> RegisterAndCreateIntentAsync(
        CreateOrderIntentWithRegistrationRequestDto request,
        CancellationToken cancellationToken = default);

    Task<Result<PublicOrderIntentPreviewDto>> GetPublicPreviewAsync(
        string intentToken, CancellationToken cancellationToken = default);

    Task<Result<OrderIntentDto>> ClaimAsync(
        string intentToken, CancellationToken cancellationToken = default);

    Task<Result<OrderDto>> ConvertAsync(
        string intentToken,
        ConvertOrderIntentRequestDto? overrides,
        CancellationToken cancellationToken = default);
}
