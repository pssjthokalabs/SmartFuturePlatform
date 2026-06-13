using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Recurring.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Read-only reporting over the recurring-billing engine. Every method is a
/// projection (AsNoTracking) — no writes, no charging, no notifications. The
/// "due/candidates" views are observational mirrors of the runner selection
/// predicates and are capped.
/// </summary>
public sealed class BillingRunReportService : IBillingRunReportService
{
    private const int DefaultTake = 25;
    private const int DefaultListTake = 50;
    private const int MaxTake = 200;

    // Scan window for the suspension-candidate mirror (bounds per-row checks).
    private const int CandidateScanWindow = 200;
    // Mirrors the runners' pending-initiation staleness window.
    private const int PendingInitiationStalenessMinutes = 60;

    private static readonly JsonSerializerOptions SummaryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppDbContext _dbContext;
    private readonly AutoBillingSettings _settings;

    public BillingRunReportService(IAppDbContext dbContext, IOptions<AutoBillingSettings> settings)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
    }

    private static int Clamp(int take, int fallback) =>
        take <= 0 ? fallback : Math.Min(take, MaxTake);

    public async Task<Result<IReadOnlyList<BillingRunLogDto>>> GetRecentRunsAsync(
        int take, CancellationToken cancellationToken = default)
    {
        var limit = Clamp(take, DefaultTake);

        var rows = await _dbContext.BillingRunLogs
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(limit)
            .Select(r => new BillingRunLogDto
            {
                Id = r.Id,
                StartedAtUtc = r.StartedAtUtc,
                FinishedAtUtc = r.FinishedAtUtc,
                Status = r.Status,
                TriggeredBy = r.TriggeredBy,
                DryRun = r.DryRun,
                InvoicesGenerated = r.InvoicesGenerated,
                ChargesAttempted = r.ChargesAttempted,
                ChargesSucceeded = r.ChargesSucceeded,
                ChargesFailed = r.ChargesFailed,
                RetriesProcessed = r.RetriesProcessed,
                SuspensionCandidates = r.SuspensionCandidates,
                ErrorCount = r.ErrorCount,
                MachineName = r.MachineName,
                SummaryJson = r.SummaryJson,
                ErrorText = r.ErrorText
            })
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<BillingRunLogDto>>.Success(rows);
    }

    public async Task<Result<BillingRunDetailDto>> GetRunByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.BillingRunLogs
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new BillingRunDetailDto
            {
                Id = r.Id,
                StartedAtUtc = r.StartedAtUtc,
                FinishedAtUtc = r.FinishedAtUtc,
                Status = r.Status,
                TriggeredBy = r.TriggeredBy,
                DryRun = r.DryRun,
                InvoicesGenerated = r.InvoicesGenerated,
                ChargesAttempted = r.ChargesAttempted,
                ChargesSucceeded = r.ChargesSucceeded,
                ChargesFailed = r.ChargesFailed,
                RetriesProcessed = r.RetriesProcessed,
                SuspensionCandidates = r.SuspensionCandidates,
                ErrorCount = r.ErrorCount,
                MachineName = r.MachineName,
                ErrorText = r.ErrorText,
                SummaryJsonRaw = r.SummaryJson
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
            return Result<BillingRunDetailDto>.Failure(ErrorCodes.NOT_FOUND, "Billing run not found.");

        // Best-effort parse — never throw on an old/odd SummaryJson shape.
        if (!string.IsNullOrWhiteSpace(row.SummaryJsonRaw))
        {
            try
            {
                row.Summary = JsonSerializer.Deserialize<BillingRunSummaryDto>(row.SummaryJsonRaw, SummaryJsonOptions);
            }
            catch
            {
                row.Summary = null; // raw remains available
            }
        }

        return Result<BillingRunDetailDto>.Success(row);
    }

    public async Task<Result<IReadOnlyList<ServiceBillingScheduleDto>>> GetSchedulesAsync(
        string? status, int take, CancellationToken cancellationToken = default)
    {
        var limit = Clamp(take, DefaultListTake);

        var query = _dbContext.ServiceBillingSchedules.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<ServiceBillingScheduleStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(s => s.Status == parsed);
        }

        // Materialize the capped page, then map in memory (the shared
        // Project helper isn't an EF-translatable expression).
        var entities = await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = entities.Select(Project).ToList();
        return Result<IReadOnlyList<ServiceBillingScheduleDto>>.Success(rows);
    }

    public async Task<Result<ServiceBillingScheduleDto>> GetScheduleByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ServiceBillingSchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        return entity is null
            ? Result<ServiceBillingScheduleDto>.Failure(ErrorCodes.NOT_FOUND, "Schedule not found.")
            : Result<ServiceBillingScheduleDto>.Success(Project(entity));
    }

    public async Task<Result<IReadOnlyList<DueInvoiceDto>>> GetDueInvoicesAsync(
        int take, CancellationToken cancellationToken = default)
    {
        var limit = Clamp(take, DefaultListTake);
        var now = DateTime.UtcNow;

        // Mirrors the Stage 2 selection query (read-only).
        var rows = await (
            from i in _dbContext.Invoices.AsNoTracking()
            where i.ServiceBillingScheduleId != null
               && (i.Status == InvoiceStatus.Issued
                   || i.Status == InvoiceStatus.Overdue
                   || i.Status == InvoiceStatus.PartiallyPaid)
               && i.BalanceDue > 0m
               && i.DueAtUtc != null
               && i.DueAtUtc <= now
            join s in _dbContext.ServiceBillingSchedules on i.ServiceBillingScheduleId equals (Guid?)s.Id
            where s.Status == ServiceBillingScheduleStatus.Active && s.IsAutoBillable
            join n in _dbContext.NetworkAccounts on s.NetworkAccountId equals n.Id
            where n.Status == NetworkAccountStatus.Active
            orderby i.DueAtUtc
            select new DueInvoiceDto
            {
                InvoiceId = i.Id,
                InvoiceNumber = i.InvoiceNumber,
                ScheduleId = s.Id,
                OrderId = i.OrderId,
                DueAtUtc = i.DueAtUtc,
                BalanceDue = i.BalanceDue,
                Status = i.Status,
                CurrencyCode = i.CurrencyCode
            })
            .Take(limit)
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<DueInvoiceDto>>.Success(rows);
    }

    public async Task<Result<IReadOnlyList<DueRetryDto>>> GetDueRetriesAsync(
        int take, CancellationToken cancellationToken = default)
    {
        var limit = Clamp(take, DefaultListTake);
        var now = DateTime.UtcNow;

        // Mirrors the Stage 3 selection query (read-only).
        var rows = await (
            from a in _dbContext.PaymentRetryAttempts.AsNoTracking()
            where a.Status == PaymentRetryAttemptStatus.Pending && a.ScheduledForUtc <= now
            join i in _dbContext.Invoices on a.InvoiceId equals i.Id
            orderby a.ScheduledForUtc, a.AttemptNumber
            select new DueRetryDto
            {
                AttemptId = a.Id,
                AttemptNumber = a.AttemptNumber,
                InvoiceId = i.Id,
                InvoiceNumber = i.InvoiceNumber,
                ScheduledForUtc = a.ScheduledForUtc,
                Amount = a.Amount,
                Status = a.Status
            })
            .Take(limit)
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<DueRetryDto>>.Success(rows);
    }

    public async Task<Result<IReadOnlyList<SuspensionCandidateDto>>> GetSuspensionCandidatesAsync(
        int take, CancellationToken cancellationToken = default)
    {
        var limit = Clamp(take, DefaultListTake);
        var now = DateTime.UtcNow;
        var graceDays = Math.Max(0, _settings.GracePeriodDays);
        var graceCutoff = now.AddDays(-graceDays);
        var initiationStaleCutoff = now.AddMinutes(-PendingInitiationStalenessMinutes);

        // Cheap SQL filter (mirrors Stage 4), bounded scan window.
        var scanned = await (
            from i in _dbContext.Invoices.AsNoTracking()
            where i.ServiceBillingScheduleId != null
               && (i.Status == InvoiceStatus.Issued
                   || i.Status == InvoiceStatus.Overdue
                   || i.Status == InvoiceStatus.PartiallyPaid)
               && i.BalanceDue > 0m
               && i.DueAtUtc != null
               && i.DueAtUtc < graceCutoff
            join s in _dbContext.ServiceBillingSchedules on i.ServiceBillingScheduleId equals (Guid?)s.Id
            where s.Status == ServiceBillingScheduleStatus.Active && s.IsAutoBillable
            join n in _dbContext.NetworkAccounts on s.NetworkAccountId equals n.Id
            where n.Status == NetworkAccountStatus.Active
            orderby i.DueAtUtc
            select new
            {
                InvoiceId = i.Id,
                i.InvoiceNumber,
                ScheduleId = s.Id,
                NetworkAccountId = n.Id,
                i.BalanceDue,
                DueAtUtc = i.DueAtUtc!.Value
            })
            .Take(CandidateScanWindow)
            .ToListAsync(cancellationToken);

        var confirmed = new List<SuspensionCandidateDto>();
        foreach (var c in scanned)
        {
            if (confirmed.Count >= limit) break;

            // Still in retry chain? → not a candidate.
            var hasPendingRetry = await _dbContext.PaymentRetryAttempts
                .AsNoTracking()
                .AnyAsync(a => a.InvoiceId == c.InvoiceId && a.Status == PaymentRetryAttemptStatus.Pending,
                          cancellationToken);
            if (hasPendingRetry) continue;

            // Retry chain exhausted?
            var maxAttemptNumber = await _dbContext.PaymentRetryAttempts
                .AsNoTracking()
                .Where(a => a.InvoiceId == c.InvoiceId)
                .Select(a => (int?)a.AttemptNumber)
                .MaxAsync(cancellationToken) ?? 0;
            if (maxAttemptNumber < _settings.MaxRetryAttempts) continue;

            // No non-stale pending initiation.
            var pendingInitiation = await _dbContext.PaymentInitiations
                .AsNoTracking()
                .AnyAsync(pi => pi.InvoiceId == c.InvoiceId
                             && pi.Status == PaymentInitiationStatus.Pending
                             && pi.CreatedAtUtc >= initiationStaleCutoff,
                          cancellationToken);
            if (pendingInitiation) continue;

            confirmed.Add(new SuspensionCandidateDto
            {
                InvoiceId = c.InvoiceId,
                InvoiceNumber = c.InvoiceNumber,
                ScheduleId = c.ScheduleId,
                NetworkAccountId = c.NetworkAccountId,
                BalanceDue = c.BalanceDue,
                DueAtUtc = c.DueAtUtc,
                DaysOverdue = Math.Round((now - c.DueAtUtc).TotalDays, 1),
                MaxAttemptNumber = maxAttemptNumber,
                Reason = "RetryExhaustedGraceExpired"
            });
        }

        return Result<IReadOnlyList<SuspensionCandidateDto>>.Success(confirmed);
    }

    private static ServiceBillingScheduleDto Project(Domain.Billing.ServiceBillingSchedule s) => new()
    {
        Id = s.Id,
        NetworkAccountId = s.NetworkAccountId,
        OrderId = s.OrderId,
        UserId = s.UserId,
        BillingCycle = s.BillingCycle,
        Amount = s.Amount,
        CurrencyCode = s.CurrencyCode,
        AnchorDayOfMonth = s.AnchorDayOfMonth,
        CurrentPeriodStartUtc = s.CurrentPeriodStartUtc,
        CurrentPeriodEndUtc = s.CurrentPeriodEndUtc,
        NextInvoiceDateUtc = s.NextInvoiceDateUtc,
        NextDueDateUtc = s.NextDueDateUtc,
        LastInvoicedPeriodEndUtc = s.LastInvoicedPeriodEndUtc,
        LastInvoiceId = s.LastInvoiceId,
        Status = s.Status,
        IsAutoBillable = s.IsAutoBillable,
        CreatedAtUtc = s.CreatedAtUtc,
        UpdatedAtUtc = s.UpdatedAtUtc
    };
}
