using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.Mandates;

public interface ICustomerPaymentMandateService
{
    /// <summary>
    /// Internal hook — Paystack webhook calls this after a successful
    /// charge that returned a reusable authorization. Idempotent on the
    /// (UserId, Provider, AuthorizationSignature) tuple: re-runs on
    /// the same authorization update fields in place rather than
    /// duplicating. Returns the persisted mandate id.
    /// </summary>
    Task<Result<Guid>> UpsertPaystackMandateAsync(
        UpsertPaystackMandateRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Internal hook — the PayFast ITN handler calls this after a
    /// successful tokenization-setup payment that returned a reusable
    /// token (Phase 1A). Idempotent on the (UserId, Provider=PayFast,
    /// AuthorizationSignature) tuple, where the signature is a hash of the
    /// token — re-runs update in place rather than duplicating. The raw
    /// token is encrypted before persisting and never logged. Returns the
    /// persisted mandate id. Does NOT charge — charging is Phase 1B.
    /// </summary>
    Task<Result<Guid>> UpsertPayFastMandateAsync(
        UpsertPayFastMandateRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>Customer-scoped list — masked. Excludes the protected authorization code.</summary>
    Task<Result<IReadOnlyList<CustomerPaymentMandateDto>>> GetMineAsync(
        Guid userId, CancellationToken cancellationToken = default);

    Task<Result<CustomerPaymentMandateDto>> SetDefaultAsync(
        Guid userId, Guid mandateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deactivate. Sets IsActive=false, ConsentRevokedUtc=now,
    /// and clears IsDefault. Auto-debit job will skip the mandate
    /// on its next pass.
    /// </summary>
    Task<Result> DeactivateAsync(
        Guid userId, Guid mandateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the customer's auto-billing opt-in flag along with
    /// derived mandate-availability fields.
    /// </summary>
    Task<Result<AutoBillingPreferenceDto>> GetAutoBillingPreferenceAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the customer's auto-billing opt-in flag. Server-side
    /// rule: cannot enable unless an active reusable mandate exists
    /// — the toggle is otherwise blocked with a clear failure.
    /// Disabling is always allowed.
    /// </summary>
    Task<Result<AutoBillingPreferenceDto>> UpdateAutoBillingPreferenceAsync(
        Guid userId, UpdateAutoBillingPreferenceRequestDto request,
        CancellationToken cancellationToken = default);
}
