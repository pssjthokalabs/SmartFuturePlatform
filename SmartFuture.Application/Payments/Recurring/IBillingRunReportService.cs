using SmartFuture.Application.Payments.Recurring.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Read-only reporting over the recurring-billing engine for admin
/// visibility. No mutation, no charge trigger, no notifications.
/// </summary>
public interface IBillingRunReportService
{
    Task<Result<IReadOnlyList<BillingRunLogDto>>> GetRecentRunsAsync(
        int take, CancellationToken cancellationToken = default);

    Task<Result<BillingRunDetailDto>> GetRunByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<ServiceBillingScheduleDto>>> GetSchedulesAsync(
        string? status, int take, CancellationToken cancellationToken = default);

    Task<Result<ServiceBillingScheduleDto>> GetScheduleByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<SuspensionCandidateDto>>> GetSuspensionCandidatesAsync(
        int take, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DueInvoiceDto>>> GetDueInvoicesAsync(
        int take, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DueRetryDto>>> GetDueRetriesAsync(
        int take, CancellationToken cancellationToken = default);
}
