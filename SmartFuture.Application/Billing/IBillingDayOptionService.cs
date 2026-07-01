using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

public interface IBillingDayOptionService
{
    // Admin — every option (enabled + disabled).
    Task<Result<IReadOnlyList<BillingDayOptionDto>>> GetAllAsync(CancellationToken cancellationToken = default);

    // Public / customer — only enabled options, ordered for display.
    Task<Result<IReadOnlyList<BillingDayOptionDto>>> GetEnabledAsync(CancellationToken cancellationToken = default);

    Task<Result<BillingDayOptionDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<BillingDayOptionDto>> CreateAsync(
        CreateBillingDayOptionRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<BillingDayOptionDto>> UpdateAsync(
        Guid id, UpdateBillingDayOptionRequestDto request, CancellationToken cancellationToken = default);

    // Assert the given day is currently accepted at checkout. Returns
    // Success with the resolved option (so callers can log/audit which
    // row they landed on), or a VALIDATION_ERROR / NOT_FOUND failure.
    Task<Result<BillingDayOptionDto>> ValidateForCheckoutAsync(
        int day, CancellationToken cancellationToken = default);

    // Resolve the "safe default" billing day used to backfill legacy
    // callers (existing mobile clients that don't send a picker, admin
    // manual invoice paths, migration). Falls back to
    // BillingSettings.DefaultBillingDay (30) if the seeded row is
    // missing.
    Task<int> ResolveDefaultBillingDayAsync(CancellationToken cancellationToken = default);
}
