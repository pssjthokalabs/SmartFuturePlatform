using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.BillingOps.Dtos;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.BillingOps;

/// <summary>
/// Read-only Billing Ops aggregation. Every query is <c>AsNoTracking</c>
/// and capped. No writes, no charging, no notifications. The per-run
/// activity is a TIME-WINDOW approximation (no per-run FK exists in v1).
/// </summary>
public sealed class BillingOpsService : IBillingOpsService
{
    private const int DefaultTake = 50;
    private const int MaxTake = 200;
    private const int CountScanWindow = 500;

    // Notification types that belong to the billing surface (used for the
    // dashboard sent/skipped counts + the notifications view filter).
    private static readonly NotificationType[] BillingNotificationTypes =
    {
        NotificationType.RecurringInvoiceGenerated,
        NotificationType.BillingGraceCandidate,
        NotificationType.BillingInternalAlert,
        NotificationType.InvoiceIssued,
        NotificationType.InvoicePaid
    };

    private static readonly InvoiceStatus[] OpenInvoiceStatuses =
    {
        InvoiceStatus.Issued, InvoiceStatus.Overdue, InvoiceStatus.PartiallyPaid
    };

    private readonly IAppDbContext _dbContext;
    private readonly IBillingRunReportService _report;
    private readonly AutoBillingSettings _autoSettings;
    private readonly BillingOpsSettings _opsSettings;
    private readonly PayFastSettings _payFastSettings;

    public BillingOpsService(
        IAppDbContext dbContext,
        IBillingRunReportService report,
        IOptions<AutoBillingSettings> autoSettings,
        IOptions<BillingOpsSettings> opsSettings,
        IOptions<PayFastSettings> payFastSettings)
    {
        _dbContext = dbContext;
        _report = report;
        _autoSettings = autoSettings.Value;
        _opsSettings = opsSettings.Value;
        _payFastSettings = payFastSettings.Value;
    }

    private static int Clamp(int take) => take <= 0 ? DefaultTake : Math.Min(take, MaxTake);

    // ─── Dashboard ──────────────────────────────────────────────────

    public async Task<Result<BillingOpsDashboardDto>> GetDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        var dto = new BillingOpsDashboardDto
        {
            WorkerEnabled = _autoSettings.RecurringWorkerEnabled,
            DryRun = _autoSettings.DryRun,
            RetryJobEnabled = _autoSettings.RetryJobEnabled,
            PayFastRecurringEnabled = _autoSettings.EnablePayFastRecurring,
            PayFastAdhocEnabled = _payFastSettings.AdhocChargingEnabled,
            ReportingEnabled = _autoSettings.AdminReportingEnabled,
            ManualInvoiceEnabled = _opsSettings.ManualInvoiceEnabled
        };

        var lastRun = await _dbContext.BillingRunLogs
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastRun is not null)
        {
            dto.LastRunId = lastRun.Id;
            dto.LastRunStatus = lastRun.Status.ToString();
            dto.LastRunTrigger = lastRun.TriggeredBy.ToString();
            dto.LastRunDryRun = lastRun.DryRun;
            dto.LastRunStartedAtUtc = lastRun.StartedAtUtc;
            dto.LastRunFinishedAtUtc = lastRun.FinishedAtUtc;
            dto.LastRunDurationSeconds = lastRun.FinishedAtUtc.HasValue
                ? Math.Round((lastRun.FinishedAtUtc.Value - lastRun.StartedAtUtc).TotalSeconds, 1)
                : null;
            dto.LastRunInvoicesGenerated = lastRun.InvoicesGenerated;
            dto.LastRunChargesAttempted = lastRun.ChargesAttempted;
            dto.LastRunChargesSucceeded = lastRun.ChargesSucceeded;
            dto.LastRunChargesFailed = lastRun.ChargesFailed;
            dto.LastRunRetriesProcessed = lastRun.RetriesProcessed;
            dto.LastRunSuspensionCandidates = lastRun.SuspensionCandidates;
            dto.LastRunErrorCount = lastRun.ErrorCount;
        }

        // Reuse the existing capped report mirrors for the "current work" counts.
        dto.DueInvoices = (await _report.GetDueInvoicesAsync(MaxTake, cancellationToken)).Data?.Count ?? 0;
        dto.DueRetries = (await _report.GetDueRetriesAsync(MaxTake, cancellationToken)).Data?.Count ?? 0;
        dto.GraceCandidates = (await _report.GetSuspensionCandidatesAsync(MaxTake, cancellationToken)).Data?.Count ?? 0;

        dto.PendingSettlements = await _dbContext.PaymentInitiations
            .AsNoTracking()
            .CountAsync(pi => pi.Provider == PaymentProviderType.PayFast
                           && pi.Status == PaymentInitiationStatus.Pending,
                       cancellationToken);

        dto.NotificationsSent = await _dbContext.OutboundNotifications
            .AsNoTracking()
            .CountAsync(n => BillingNotificationTypes.Contains(n.Type)
                          && n.Status == NotificationStatus.Sent,
                       cancellationToken);

        dto.NotificationsSkippedOrFailed = await _dbContext.OutboundNotifications
            .AsNoTracking()
            .CountAsync(n => BillingNotificationTypes.Contains(n.Type)
                          && (n.Status == NotificationStatus.Failed
                           || n.Status == NotificationStatus.Skipped
                           || n.Status == NotificationStatus.Cancelled),
                       cancellationToken);

        var actions = await GetManualActionRequiredAsync(cancellationToken);
        dto.ManualActionRequiredCount = actions.Data?.Sum(a => a.Count) ?? 0;

        return Result<BillingOpsDashboardDto>.Success(dto);
    }

    // ─── Per-run activity (time-window approximation) ───────────────

    public async Task<Result<BillingRunActivityDto>> GetRunActivityAsync(
        Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.BillingRunLogs
            .AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Id, r.StartedAtUtc, r.FinishedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (run is null)
            return Result<BillingRunActivityDto>.Failure(ErrorCodes.NOT_FOUND, "Billing run not found.");

        var start = run.StartedAtUtc;
        var end = run.FinishedAtUtc ?? DateTime.UtcNow;

        var dto = new BillingRunActivityDto
        {
            RunId = run.Id,
            StartedAtUtc = start,
            FinishedAtUtc = run.FinishedAtUtc,
            Approximate = true,
            GraceIsCurrentSnapshot = true
        };

        // Generated invoices — schedule-linked invoices issued within the window.
        dto.GeneratedInvoices = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.ServiceBillingScheduleId != null
                     && i.IssuedAtUtc != null
                     && i.IssuedAtUtc >= start && i.IssuedAtUtc <= end)
            .OrderByDescending(i => i.IssuedAtUtc)
            .Take(MaxTake)
            .Select(i => new GeneratedInvoiceRowDto
            {
                InvoiceId = i.Id,
                InvoiceNumber = i.InvoiceNumber,
                ScheduleId = i.ServiceBillingScheduleId,
                Status = i.Status,
                TotalAmount = i.TotalAmount,
                BalanceDue = i.BalanceDue,
                CurrencyCode = i.CurrencyCode,
                PeriodStartUtc = i.PeriodStartUtc,
                PeriodEndUtc = i.PeriodEndUtc,
                DueAtUtc = i.DueAtUtc,
                IssuedAtUtc = i.IssuedAtUtc
            })
            .ToListAsync(cancellationToken);

        // Charges — initiations created within the window.
        dto.Charges = await (
            from pi in _dbContext.PaymentInitiations.AsNoTracking()
            where pi.CreatedAtUtc >= start && pi.CreatedAtUtc <= end
            join i in _dbContext.Invoices on pi.InvoiceId equals i.Id
            orderby pi.CreatedAtUtc descending
            select new ChargeRowDto
            {
                InitiationId = pi.Id,
                PaymentId = pi.PaymentId,
                InvoiceId = i.Id,
                InvoiceNumber = i.InvoiceNumber,
                Provider = pi.Provider,
                Status = pi.Status,
                Amount = pi.Amount,
                CurrencyCode = pi.CurrencyCode,
                CreatedAtUtc = pi.CreatedAtUtc,
                WebhookLastReceivedAtUtc = pi.WebhookLastReceivedAtUtc,
                FailureReason = pi.FailureReason
            })
            .Take(MaxTake)
            .ToListAsync(cancellationToken);

        // Retries — attempts that actually ran within the window.
        dto.Retries = await (
            from a in _dbContext.PaymentRetryAttempts.AsNoTracking()
            where a.AttemptedUtc != null && a.AttemptedUtc >= start && a.AttemptedUtc <= end
            join i in _dbContext.Invoices on a.InvoiceId equals i.Id
            orderby a.AttemptedUtc descending
            select new RetryRowDto
            {
                AttemptId = a.Id,
                AttemptNumber = a.AttemptNumber,
                InvoiceId = i.Id,
                InvoiceNumber = i.InvoiceNumber,
                Provider = a.Provider,
                Status = a.Status,
                Amount = a.Amount,
                ScheduledForUtc = a.ScheduledForUtc,
                AttemptedUtc = a.AttemptedUtc,
                FailureReason = a.FailureReason
            })
            .Take(MaxTake)
            .ToListAsync(cancellationToken);

        // Grace candidates — current snapshot (not historically attributable).
        dto.GraceCandidates = (await _report.GetSuspensionCandidatesAsync(MaxTake, cancellationToken))
            .Data?.ToList() ?? new();

        return Result<BillingRunActivityDto>.Success(dto);
    }

    // ─── Charges ────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<ChargeRowDto>>> GetChargesAsync(
        string? status, string? provider, DateTime? fromUtc, DateTime? toUtc,
        int take, CancellationToken cancellationToken = default)
    {
        var limit = Clamp(take);

        var query = _dbContext.PaymentInitiations.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<PaymentInitiationStatus>(status, ignoreCase: true, out var st))
            query = query.Where(pi => pi.Status == st);

        if (!string.IsNullOrWhiteSpace(provider)
            && Enum.TryParse<PaymentProviderType>(provider, ignoreCase: true, out var prov))
            query = query.Where(pi => pi.Provider == prov);

        if (fromUtc.HasValue) query = query.Where(pi => pi.CreatedAtUtc >= fromUtc.Value);
        if (toUtc.HasValue) query = query.Where(pi => pi.CreatedAtUtc <= toUtc.Value);

        var rows = await (
            from pi in query
            join i in _dbContext.Invoices on pi.InvoiceId equals i.Id
            orderby pi.CreatedAtUtc descending
            select new ChargeRowDto
            {
                InitiationId = pi.Id,
                PaymentId = pi.PaymentId,
                InvoiceId = i.Id,
                InvoiceNumber = i.InvoiceNumber,
                Provider = pi.Provider,
                Status = pi.Status,
                Amount = pi.Amount,
                CurrencyCode = pi.CurrencyCode,
                CreatedAtUtc = pi.CreatedAtUtc,
                WebhookLastReceivedAtUtc = pi.WebhookLastReceivedAtUtc,
                FailureReason = pi.FailureReason
            })
            .Take(limit)
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<ChargeRowDto>>.Success(rows);
    }

    // ─── Retries ────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<RetryRowDto>>> GetRetriesAsync(
        string? status, DateTime? fromUtc, DateTime? toUtc,
        int take, CancellationToken cancellationToken = default)
    {
        var limit = Clamp(take);

        var query = _dbContext.PaymentRetryAttempts.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<PaymentRetryAttemptStatus>(status, ignoreCase: true, out var st))
            query = query.Where(a => a.Status == st);

        if (fromUtc.HasValue) query = query.Where(a => a.ScheduledForUtc >= fromUtc.Value);
        if (toUtc.HasValue) query = query.Where(a => a.ScheduledForUtc <= toUtc.Value);

        var rows = await (
            from a in query
            join i in _dbContext.Invoices on a.InvoiceId equals i.Id
            orderby a.ScheduledForUtc descending, a.AttemptNumber descending
            select new RetryRowDto
            {
                AttemptId = a.Id,
                AttemptNumber = a.AttemptNumber,
                InvoiceId = i.Id,
                InvoiceNumber = i.InvoiceNumber,
                Provider = a.Provider,
                Status = a.Status,
                Amount = a.Amount,
                ScheduledForUtc = a.ScheduledForUtc,
                AttemptedUtc = a.AttemptedUtc,
                FailureReason = a.FailureReason
            })
            .Take(limit)
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<RetryRowDto>>.Success(rows);
    }

    // ─── Notifications ──────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<NotificationRowDto>>> GetNotificationsAsync(
        Guid? invoiceId, string? type, DateTime? fromUtc, DateTime? toUtc,
        int take, CancellationToken cancellationToken = default)
    {
        var limit = Clamp(take);

        var query = _dbContext.OutboundNotifications
            .AsNoTracking()
            .Where(n => BillingNotificationTypes.Contains(n.Type));

        if (invoiceId.HasValue)
            query = query.Where(n => n.RelatedEntityType == "Invoice" && n.RelatedEntityId == invoiceId.Value);

        if (!string.IsNullOrWhiteSpace(type)
            && Enum.TryParse<NotificationType>(type, ignoreCase: true, out var nt))
            query = query.Where(n => n.Type == nt);

        if (fromUtc.HasValue) query = query.Where(n => n.CreatedAtUtc >= fromUtc.Value);
        if (toUtc.HasValue) query = query.Where(n => n.CreatedAtUtc <= toUtc.Value);

        var rows = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(limit)
            .Select(n => new NotificationRowDto
            {
                Id = n.Id,
                Type = n.Type,
                Status = n.Status,
                RecipientEmail = n.RecipientEmail,
                Subject = n.Subject,
                RelatedEntityType = n.RelatedEntityType,
                RelatedEntityId = n.RelatedEntityId,
                CreatedAtUtc = n.CreatedAtUtc,
                SentAtUtc = n.SentAtUtc,
                FailureReason = n.FailureReason
            })
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<NotificationRowDto>>.Success(rows);
    }

    // ─── Attention list ─────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<ManualActionItemDto>>> GetManualActionRequiredAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var items = new List<ManualActionItemDto>();

        // Grace candidates (reuse the report mirror).
        var graceCount = (await _report.GetSuspensionCandidatesAsync(MaxTake, cancellationToken)).Data?.Count ?? 0;
        items.Add(new ManualActionItemDto
        {
            Key = "grace_candidates", Label = "Grace / suspension candidates",
            Count = graceCount, Severity = "warning", Link = "/admin/billing-ops?tab=grace"
        });

        // Repeated failed retries — invoices whose retry chain is exhausted but still unpaid.
        var maxAttempts = _autoSettings.MaxRetryAttempts;
        var repeatedFailed = await (
            from a in _dbContext.PaymentRetryAttempts.AsNoTracking()
            where a.Status == PaymentRetryAttemptStatus.Failed && a.AttemptNumber >= maxAttempts
            join i in _dbContext.Invoices on a.InvoiceId equals i.Id
            where i.BalanceDue > 0m && OpenInvoiceStatuses.Contains(i.Status)
            select a.InvoiceId)
            .Distinct()
            .CountAsync(cancellationToken);
        items.Add(new ManualActionItemDto
        {
            Key = "repeated_failed_retries", Label = "Repeated failed retries (chain exhausted)",
            Count = repeatedFailed, Severity = "critical", Link = "/admin/billing-ops?tab=retries"
        });

        // Old pending PayFast settlements.
        var staleCut = now.AddMinutes(-Math.Max(0, _opsSettings.PendingSettlementStaleMinutes));
        var oldPendingPayfast = await _dbContext.PaymentInitiations
            .AsNoTracking()
            .CountAsync(pi => pi.Provider == PaymentProviderType.PayFast
                           && pi.Status == PaymentInitiationStatus.Pending
                           && pi.CreatedAtUtc < staleCut,
                       cancellationToken);
        items.Add(new ManualActionItemDto
        {
            Key = "old_pending_payfast_settlements", Label = "PayFast settlements awaiting ITN (stale)",
            Count = oldPendingPayfast, Severity = "warning", Link = "/admin/billing-ops?tab=charges"
        });

        // Due service invoices with no active reusable mandate.
        var dueServiceInvoices = await (
            from i in _dbContext.Invoices.AsNoTracking()
            where i.ServiceBillingScheduleId != null
               && OpenInvoiceStatuses.Contains(i.Status)
               && i.BalanceDue > 0m
               && i.DueAtUtc != null && i.DueAtUtc <= now
            join s in _dbContext.ServiceBillingSchedules on i.ServiceBillingScheduleId equals (Guid?)s.Id
            where s.Status == ServiceBillingScheduleStatus.Active && s.IsAutoBillable
            join n in _dbContext.NetworkAccounts on s.NetworkAccountId equals n.Id
            where n.Status == NetworkAccountStatus.Active
            select new { InvoiceId = i.Id, s.UserId })
            .Take(CountScanWindow)
            .ToListAsync(cancellationToken);

        var dueUserIds = dueServiceInvoices.Select(x => x.UserId).Distinct().ToList();
        var usersWithMandate = await _dbContext.CustomerPaymentMandates
            .AsNoTracking()
            .Where(m => dueUserIds.Contains(m.UserId) && m.IsActive && m.IsReusable)
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var mandateSet = usersWithMandate.ToHashSet();
        var dueNoMandate = dueServiceInvoices.Count(x => !mandateSet.Contains(x.UserId));
        items.Add(new ManualActionItemDto
        {
            Key = "due_no_mandate", Label = "Invoices due but no reusable mandate",
            Count = dueNoMandate, Severity = "warning", Link = "/admin/billing-ops?tab=overview"
        });

        // PayFast reusable mandate exists but recurring is disabled.
        var payfastMandateButDisabled = 0;
        if (!_autoSettings.EnablePayFastRecurring)
        {
            payfastMandateButDisabled = await _dbContext.CustomerPaymentMandates
                .AsNoTracking()
                .Where(m => m.Provider == PaymentProviderType.PayFast && m.IsActive && m.IsReusable)
                .Select(m => m.UserId)
                .Distinct()
                .CountAsync(cancellationToken);
        }
        items.Add(new ManualActionItemDto
        {
            Key = "payfast_mandate_recurring_disabled", Label = "PayFast mandates idle (recurring disabled)",
            Count = payfastMandateButDisabled, Severity = "info", Link = "/admin/billing-ops?tab=overview"
        });

        // Failed billing notification sends.
        var failedNotifs = await _dbContext.OutboundNotifications
            .AsNoTracking()
            .CountAsync(n => BillingNotificationTypes.Contains(n.Type)
                          && n.Status == NotificationStatus.Failed,
                       cancellationToken);
        items.Add(new ManualActionItemDto
        {
            Key = "failed_notifications", Label = "Failed billing notification sends",
            Count = failedNotifs, Severity = "warning", Link = "/admin/billing-ops?tab=notifications"
        });

        // Forced / manual invoice audit items (last 30 days) — informational.
        var since = now.AddDays(-30);
        var manualInvoiceAudits = await _dbContext.AuditLogs
            .AsNoTracking()
            .CountAsync(a => (a.ActionType == Shared.Enums.Auditing.AuditActionType.ManualServiceInvoiceCreated
                           || a.ActionType == Shared.Enums.Auditing.AuditActionType.ForcedDuplicateServiceInvoiceCreated)
                          && a.CreatedAtUtc >= since,
                       cancellationToken);
        items.Add(new ManualActionItemDto
        {
            Key = "manual_invoice_audits", Label = "Manual / forced invoices (last 30 days)",
            Count = manualInvoiceAudits, Severity = "info", Link = "/admin/audit-logs"
        });

        // Last billing run failed.
        var lastRun = await _dbContext.BillingRunLogs
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .Select(r => new { r.Status })
            .FirstOrDefaultAsync(cancellationToken);
        items.Add(new ManualActionItemDto
        {
            Key = "last_run_failed", Label = "Last billing run failed",
            Count = lastRun?.Status == BillingRunStatus.Failed ? 1 : 0,
            Severity = "critical", Link = "/admin/billing-ops?tab=runs"
        });

        return Result<IReadOnlyList<ManualActionItemDto>>.Success(items);
    }
}
