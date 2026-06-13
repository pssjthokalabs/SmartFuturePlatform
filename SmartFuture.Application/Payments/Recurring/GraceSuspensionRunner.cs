using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0E — REPORT-ONLY grace/suspension-candidate detection. Finds active
/// services with an unpaid recurring invoice whose retry chain is exhausted
/// and whose grace period has expired, and records them as suspension
/// candidates. It NEVER mutates <c>NetworkAccount.Status</c>, notes, payment
/// rows, or retry rows, and sends NO notifications. Actual suspension is
/// deferred to Phase 0E2; <c>[grace][suspended]</c> is intentionally never
/// emitted here.
/// </summary>
public sealed class GraceSuspensionRunner : IGraceSuspensionRunner
{
    // Mirrors the other runners — a Pending PaymentInitiation younger than
    // this is treated as in-flight; older is abandoned.
    private const int PendingInitiationStalenessMinutes = 60;

    private readonly IAppDbContext _dbContext;
    private readonly AutoBillingSettings _settings;
    private readonly ILogger<GraceSuspensionRunner> _logger;

    public GraceSuspensionRunner(
        IAppDbContext dbContext,
        IOptions<AutoBillingSettings> settings,
        ILogger<GraceSuspensionRunner> logger)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
        _logger = logger;
    }

    private sealed record Candidate(
        Guid InvoiceId, string InvoiceNumber, DateTime DueAtUtc,
        Guid ScheduleId, Guid NetworkAccountId, decimal BalanceDue);

    public async Task<GraceSuspensionResult> DetectCandidatesAsync(
        RecurringBillingRunContext context, CancellationToken cancellationToken = default)
    {
        var result = new GraceSuspensionResult();

        if (!_settings.Enabled)
        {
            _logger.LogInformation("[recurring-billing][grace][skipped] stage skipped — AutoBilling.Enabled=false.");
            return result;
        }

        // Phase 0E is report-only. Make it impossible to silently assume real
        // suspension is happening if the flag is flipped on.
        if (_settings.SuspendAfterGracePeriodEnabled)
        {
            _logger.LogWarning(
                "[recurring-billing][grace] SuspendAfterGracePeriodEnabled=true, but the actual suspension path is NOT implemented in Phase 0E (report-only). " +
                "Candidates will be reported and forecast as wouldSuspend; NO account will be suspended.");
        }

        var now = context.NowUtc;
        var graceDays = Math.Max(0, _settings.GracePeriodDays);
        var graceCutoff = now.AddDays(-graceDays);
        var cap = _settings.MaxSuspensionCandidatesPerRun > 0 ? _settings.MaxSuspensionCandidatesPerRun : int.MaxValue;
        var initiationStaleCutoff = now.AddMinutes(-PendingInitiationStalenessMinutes);

        // Cheap filters: unpaid, schedule-linked recurring invoice whose
        // due-date + grace has expired, on an active schedule + active NA.
        var candidates = await (
            from i in _dbContext.Invoices
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
            select new Candidate(i.Id, i.InvoiceNumber, i.DueAtUtc!.Value, s.Id, n.Id, i.BalanceDue)
        ).ToListAsync(cancellationToken);

        result.CandidatesSelected = candidates.Count;

        var processed = 0;
        var seenInvoices = new HashSet<Guid>();
        var seenAccounts = new HashSet<Guid>();

        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seenInvoices.Add(c.InvoiceId) || !seenAccounts.Add(c.NetworkAccountId))
                continue;

            try
            {
                // Live invoice re-check — still unpaid?
                var liveInvoice = await _dbContext.Invoices
                    .AsNoTracking()
                    .Where(i => i.Id == c.InvoiceId)
                    .Select(i => new { i.Status, i.BalanceDue, i.DueAtUtc })
                    .FirstOrDefaultAsync(cancellationToken);
                if (liveInvoice is null
                    || liveInvoice.BalanceDue <= 0m
                    || liveInvoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled or InvoiceStatus.Void)
                {
                    result.SkippedPaidOrSettled++;
                    LogSkip(c, "paid_or_settled");
                    continue;
                }

                // Defensive grace re-check.
                if (liveInvoice.DueAtUtc is null || liveInvoice.DueAtUtc.Value >= graceCutoff)
                {
                    result.SkippedGraceNotExpired++;
                    LogSkip(c, "grace_not_expired");
                    continue;
                }

                // Network account still Active?
                var naStatus = await _dbContext.NetworkAccounts
                    .AsNoTracking()
                    .Where(n => n.Id == c.NetworkAccountId)
                    .Select(n => (NetworkAccountStatus?)n.Status)
                    .FirstOrDefaultAsync(cancellationToken);
                if (naStatus != NetworkAccountStatus.Active)
                {
                    result.SkippedInactiveService++;
                    LogSkip(c, "inactive_service");
                    continue;
                }

                // Still in the retry chain? (Stage 3 owns it.)
                var hasPendingRetry = await _dbContext.PaymentRetryAttempts
                    .AsNoTracking()
                    .AnyAsync(a => a.InvoiceId == c.InvoiceId
                                && a.Status == PaymentRetryAttemptStatus.Pending,
                              cancellationToken);
                if (hasPendingRetry)
                {
                    result.SkippedPendingRetry++;
                    LogSkip(c, "pending_retry");
                    continue;
                }

                // Retry chain exhausted? (highest attempt number reached max)
                var maxAttemptNumber = await _dbContext.PaymentRetryAttempts
                    .AsNoTracking()
                    .Where(a => a.InvoiceId == c.InvoiceId)
                    .Select(a => (int?)a.AttemptNumber)
                    .MaxAsync(cancellationToken) ?? 0;
                if (maxAttemptNumber < _settings.MaxRetryAttempts)
                {
                    result.SkippedRetriesNotExhausted++;
                    LogSkip(c, "retries_not_exhausted");
                    continue;
                }

                // No non-stale pending initiation in flight.
                var pendingInitiation = await _dbContext.PaymentInitiations
                    .AsNoTracking()
                    .AnyAsync(pi => pi.InvoiceId == c.InvoiceId
                                 && pi.Status == PaymentInitiationStatus.Pending
                                 && pi.CreatedAtUtc >= initiationStaleCutoff,
                              cancellationToken);
                if (pendingInitiation)
                {
                    result.SkippedPendingInitiation++;
                    LogSkip(c, "pending_initiation");
                    continue;
                }

                // ── Confirmed suspension candidate (REPORT ONLY) ──
                if (processed >= cap) { result.Truncated = true; LogTruncated(cap); break; }
                processed++;
                result.SuspensionCandidates++;

                var daysOverdue = Math.Round((now - liveInvoice.DueAtUtc!.Value).TotalDays, 1);
                _logger.LogInformation(
                    "[recurring-billing][grace][candidate] invoice {InvoiceNumber} ({InvoiceId}) schedule {ScheduleId} networkAccount {NetworkAccountId} " +
                    "balance {Balance} daysOverdue {DaysOverdue} maxAttempt {MaxAttempt} — suspension candidate (report-only).",
                    c.InvoiceNumber, c.InvoiceId, c.ScheduleId, c.NetworkAccountId, liveInvoice.BalanceDue, daysOverdue, maxAttemptNumber);

                // Forecast only — NO suspension occurs in Phase 0E.
                if (_settings.SuspendAfterGracePeriodEnabled)
                {
                    result.WouldSuspend++;
                    _logger.LogInformation(
                        "[recurring-billing][grace][would_suspend] networkAccount {NetworkAccountId} invoice {InvoiceNumber} ({InvoiceId}) — WOULD suspend if the suspension path were implemented (Phase 0E2).",
                        c.NetworkAccountId, c.InvoiceNumber, c.InvoiceId);
                }
                // result.Suspended stays 0 — no actual suspension in Phase 0E.
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Errors.Add($"Invoice {c.InvoiceId} (account {c.NetworkAccountId}): {ex.Message}");
                _logger.LogError(ex,
                    "[recurring-billing][grace][skipped] invoice {InvoiceNumber} ({InvoiceId}) threw during candidate detection; continuing.",
                    c.InvoiceNumber, c.InvoiceId);
            }
        }

        _logger.LogInformation(
            "[recurring-billing][grace][summary] selected={Selected} candidates={Candidates} wouldSuspend={Would} suspended={Suspended} " +
            "skippedPaidOrSettled={Settled} skippedPendingRetry={PendingRetry} skippedPendingInitiation={PendingInit} " +
            "skippedRetriesNotExhausted={NotExhausted} skippedInactiveService={Inactive} skippedGraceNotExpired={GraceNotExpired} " +
            "truncated={Truncated} errors={Errors}",
            result.CandidatesSelected, result.SuspensionCandidates, result.WouldSuspend, result.Suspended,
            result.SkippedPaidOrSettled, result.SkippedPendingRetry, result.SkippedPendingInitiation,
            result.SkippedRetriesNotExhausted, result.SkippedInactiveService, result.SkippedGraceNotExpired,
            result.Truncated, result.ErrorCount);

        return result;
    }

    private void LogSkip(Candidate c, string reason) =>
        _logger.LogInformation(
            "[recurring-billing][grace][skipped] invoice {InvoiceNumber} ({InvoiceId}) networkAccount {NetworkAccountId} reason={Reason}.",
            c.InvoiceNumber, c.InvoiceId, c.NetworkAccountId, reason);

    private void LogTruncated(int cap) =>
        _logger.LogWarning(
            "[recurring-billing][grace] candidate cap {Cap} reached; remaining candidates deferred to the next run.", cap);
}
