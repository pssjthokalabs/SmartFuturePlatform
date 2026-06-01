using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Payments.Paystack;
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

    /// <summary>
    /// Phase 53 — "Order and Pay" client checkout. Creates an
    /// OrderIntent (NOT a real Order) and initiates a Paystack
    /// transaction against the intent. The returned payload has the
    /// Paystack inline access code + redirect URL so the portal can
    /// launch the customer straight into the Paystack overlay without
    /// any real Order/Invoice/Payment row existing yet.
    ///
    /// On Paystack confirmation (webhook OR verify-and-apply OR admin
    /// reconcile) the reference is detected as intent-bound (SF-INTENT-…)
    /// and <see cref="ConvertIntentPaymentToPaidOrderAsync"/> atomically
    /// creates Order + Invoice (Paid) + Payment (Completed) +
    /// Pending NetworkAccount.
    /// </summary>
    Task<Result<InitiateOrderIntentPaymentResponseDto>> InitiateClientPaymentAsync(
        InitiateOrderIntentPaymentRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 53 — convert an SF-INTENT-… Paystack reference into a
    /// real Order + Invoice (Paid) + Payment (Completed) + Pending
    /// NetworkAccount, atomically. Called from PaystackNotifyHandler /
    /// PaystackReconciliationService / PaystackVerifyAndApply when the
    /// inbound reference is intent-bound (not invoice-bound).
    /// Idempotent — second call with the same reference returns the
    /// already-created Order.
    /// </summary>
    Task<Result<ConvertIntentPaymentToPaidOrderOutcomeDto>> ConvertIntentPaymentToPaidOrderAsync(
        string intentPaymentReference,
        DateTime? paidAtUtc,
        string? gatewayTransactionId,
        PaystackVerifyAuthorizationSnapshot? authorizationSnapshot = null,
        CancellationToken cancellationToken = default);
}
