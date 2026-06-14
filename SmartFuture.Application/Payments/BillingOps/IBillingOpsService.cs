using SmartFuture.Application.Payments.BillingOps.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.BillingOps;

/// <summary>
/// Read-only monitoring aggregation for the Billing Ops admin module. No
/// mutation, no charge trigger, no notifications. Composes the existing
/// recurring-billing report service where useful and adds the dashboard,
/// per-run activity, filtered charge/retry/notification views, and the
/// attention list.
/// </summary>
public interface IBillingOpsService
{
    Task<Result<BillingOpsDashboardDto>> GetDashboardAsync(
        CancellationToken cancellationToken = default);

    Task<Result<BillingRunActivityDto>> GetRunActivityAsync(
        Guid runId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<ChargeRowDto>>> GetChargesAsync(
        string? status, string? provider, DateTime? fromUtc, DateTime? toUtc,
        int take, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<RetryRowDto>>> GetRetriesAsync(
        string? status, DateTime? fromUtc, DateTime? toUtc,
        int take, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<NotificationRowDto>>> GetNotificationsAsync(
        Guid? invoiceId, string? type, DateTime? fromUtc, DateTime? toUtc,
        int take, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<ManualActionItemDto>>> GetManualActionRequiredAsync(
        CancellationToken cancellationToken = default);
}
