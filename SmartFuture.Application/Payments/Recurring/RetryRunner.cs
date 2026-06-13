using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0D — consumes due Pending <c>PaymentRetryAttempt</c> rows and
/// re-charges via <see cref="IAutoBillingService.ChargeInvoiceAsync"/> in
/// REUSE mode (passing the attempt id), so the existing row is reused rather
/// than duplicated. Owns the run-level guards (dry-run, cap, dedupe,
/// re-check, terminal-vs-transient skip handling); the charge service owns
/// settlement (via PaymentApplierService), the test-amount/production guard,
/// success sibling-cancel, and next-attempt creation on failure.
///
/// Paystack only. No grace/suspension, no NetworkAccount mutation.
/// </summary>
public sealed class RetryRunner : IRetryRunner
{
    // Mirrors DueInvoiceChargeRunner — a Pending PaymentInitiation younger
    // than this blocks a re-charge; older is treated as abandoned.
    private const int PendingInitiationStalenessMinutes = 60;

    private readonly IAppDbContext _dbContext;
    private readonly IAutoBillingService _autoBilling;
    private readonly AutoBillingSettings _settings;
    private readonly ILogger<RetryRunner> _logger;

    public RetryRunner(
        IAppDbContext dbContext,
        IAutoBillingService autoBilling,
        IOptions<AutoBillingSettings> settings,
        ILogger<RetryRunner> logger)
    {
        _dbContext = dbContext;
        _autoBilling = autoBilling;
        _settings = settings.Value;
        _logger = logger;
    }

    private sealed record AttemptRow(
        Guid AttemptId, int AttemptNumber, Guid CustomerId,
        Guid InvoiceId, string InvoiceNumber, Guid? ScheduleId, decimal Amount);

    public async Task<RetryRunResult> RunDueRetriesAsync(
        RecurringBillingRunContext context, CancellationToken cancellationToken = default)
    {
        var result = new RetryRunResult();

        if (!_settings.Enabled)
        {
            _logger.LogInformation("[recurring-billing][retry][skipped] stage skipped — AutoBilling.Enabled=false.");
            return result;
        }
        if (!_settings.RetryJobEnabled)
        {
            _logger.LogInformation("[recurring-billing][retry][skipped] stage skipped — AutoBilling.RetryJobEnabled=false.");
            return result;
        }

        var now = context.NowUtc;
        var cap = _settings.MaxChargesPerRun > 0 ? _settings.MaxChargesPerRun : int.MaxValue;
        var initiationStaleCutoff = now.AddMinutes(-PendingInitiationStalenessMinutes);

        // Due Pending retry attempts, oldest-scheduled first.
        var dueAttempts = await (
            from a in _dbContext.PaymentRetryAttempts
            where a.Status == PaymentRetryAttemptStatus.Pending && a.ScheduledForUtc <= now
            join i in _dbContext.Invoices on a.InvoiceId equals i.Id
            orderby a.ScheduledForUtc, a.AttemptNumber
            select new AttemptRow(a.Id, a.AttemptNumber, a.CustomerId, i.Id, i.InvoiceNumber, i.ServiceBillingScheduleId, a.Amount)
        ).ToListAsync(cancellationToken);

        result.AttemptsSelected = dueAttempts.Count;

        var processed = 0;
        var seenAttempts = new HashSet<Guid>();
        var seenInvoices = new HashSet<Guid>();

        foreach (var row in dueAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seenAttempts.Add(row.AttemptId))
                continue;

            // One invoice per run — extra due attempts for the same invoice
            // wait for the next run.
            if (!seenInvoices.Add(row.InvoiceId))
            {
                result.SkippedAlreadyProcessed++;
                LogSkip(row, "invoice_already_processed_this_run");
                continue;
            }

            try
            {
                // Attempt still Pending? (another run may have taken it.)
                var liveStatus = await _dbContext.PaymentRetryAttempts
                    .AsNoTracking()
                    .Where(a => a.Id == row.AttemptId)
                    .Select(a => (PaymentRetryAttemptStatus?)a.Status)
                    .FirstOrDefaultAsync(cancellationToken);
                if (liveStatus != PaymentRetryAttemptStatus.Pending)
                {
                    result.SkippedAlreadyProcessed++;
                    LogSkip(row, "attempt_not_pending");
                    continue;
                }

                // Invoice live state.
                var liveInvoice = await _dbContext.Invoices
                    .AsNoTracking()
                    .Where(i => i.Id == row.InvoiceId)
                    .Select(i => new { i.Status, i.BalanceDue })
                    .FirstOrDefaultAsync(cancellationToken);

                // ── Terminal skips ──
                // Real run: resolve the stale/invalid attempt → Skipped.
                // Dry-run: MUTATE NOTHING — only report would_skip_*.
                if (liveInvoice is null)
                {
                    if (!context.DryRun)
                        await MarkAttemptSkippedAsync(row.AttemptId, "invoice_missing", cancellationToken);
                    result.SkippedPaidOrSettled++;
                    LogSkip(row, context.DryRun ? "would_skip_missing_invoice" : "invoice_missing");
                    continue;
                }
                if (liveInvoice.BalanceDue <= 0m
                    || liveInvoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled or InvoiceStatus.Void)
                {
                    if (!context.DryRun)
                        await MarkAttemptSkippedAsync(row.AttemptId, "paid_or_settled", cancellationToken);
                    result.SkippedPaidOrSettled++;
                    LogSkip(row, context.DryRun ? "would_skip_paid_or_settled" : "paid_or_settled");
                    continue;
                }
                if (row.AttemptNumber > _settings.MaxRetryAttempts)
                {
                    if (!context.DryRun)
                        await MarkAttemptSkippedAsync(row.AttemptId, "max_attempts_exceeded", cancellationToken);
                    result.SkippedMaxAttempts++;
                    LogSkip(row, context.DryRun ? "would_skip_max_attempts" : "max_attempts_exceeded");
                    continue;
                }

                // ── Transient skips (leave Pending; resume when cleared) ──
                var optedIn = await _dbContext.CustomerProfiles
                    .AsNoTracking()
                    .Where(p => p.UserId == row.CustomerId)
                    .Select(p => (bool?)p.AutoBillingEnabled)
                    .FirstOrDefaultAsync(cancellationToken);
                if (optedIn != true)
                {
                    result.SkippedNoOptIn++;
                    LogSkip(row, "no_opt_in");
                    continue;
                }

                // Paystack-scoped mandate. TODO (PayFast recurring phase):
                // widen alongside AutoBillingService + DueInvoiceChargeRunner.
                var hasMandate = await _dbContext.CustomerPaymentMandates
                    .AsNoTracking()
                    .AnyAsync(m => m.UserId == row.CustomerId
                                && m.Provider == PaymentProviderType.Paystack
                                && m.IsActive && m.IsReusable && m.IsDefault,
                              cancellationToken);
                if (!hasMandate)
                {
                    result.SkippedNoMandate++;
                    LogSkip(row, "no_paystack_mandate");
                    continue;
                }

                var pendingInitiation = await _dbContext.PaymentInitiations
                    .AsNoTracking()
                    .AnyAsync(pi => pi.InvoiceId == row.InvoiceId
                                 && pi.Status == PaymentInitiationStatus.Pending
                                 && pi.CreatedAtUtc >= initiationStaleCutoff,
                              cancellationToken);
                if (pendingInitiation)
                {
                    result.SkippedPendingInitiation++;
                    LogSkip(row, "pending_initiation");
                    continue;
                }

                // Service still billable (only when the invoice is schedule-linked).
                if (row.ScheduleId is Guid scheduleId)
                {
                    var serviceOk = await (
                        from s in _dbContext.ServiceBillingSchedules
                        where s.Id == scheduleId
                           && s.Status == ServiceBillingScheduleStatus.Active
                           && s.IsAutoBillable
                        join n in _dbContext.NetworkAccounts on s.NetworkAccountId equals n.Id
                        where n.Status == NetworkAccountStatus.Active
                        select s.Id
                    ).AnyAsync(cancellationToken);
                    if (!serviceOk)
                    {
                        result.SkippedInactiveService++;
                        LogSkip(row, "inactive_service");
                        continue;
                    }
                }

                // ── Dry-run: report only, NO call/mutation ──
                if (context.DryRun)
                {
                    if (processed >= cap) { result.Truncated = true; LogTruncated(cap); break; }
                    result.WouldRetry++;
                    processed++;
                    _logger.LogInformation(
                        "[recurring-billing][retry][dry_run] attempt {AttemptId} (#{AttemptNumber}) invoice {InvoiceNumber} ({InvoiceId}) " +
                        "balance {Balance} provider=Paystack mandatePresent=true — WOULD retry.",
                        row.AttemptId, row.AttemptNumber, row.InvoiceNumber, row.InvoiceId, liveInvoice.BalanceDue);
                    continue;
                }

                // ── Real retry ──
                if (!_settings.ChargeAuthorizationEnabled)
                {
                    result.SkippedChargeAuthDisabled++;
                    LogSkip(row, "charge_authorization_disabled");
                    continue;
                }

                if (processed >= cap) { result.Truncated = true; LogTruncated(cap); break; }

                result.RetriesAttempted++;
                processed++;
                _logger.LogInformation(
                    "[recurring-billing][retry][attempt] attempt {AttemptId} (#{AttemptNumber}) invoice {InvoiceNumber} ({InvoiceId}) balance {Balance} provider=Paystack.",
                    row.AttemptId, row.AttemptNumber, row.InvoiceNumber, row.InvoiceId, liveInvoice.BalanceDue);

                // REUSE mode — pass the existing attempt id. The charge
                // service reuses the row, settles via PaymentApplierService,
                // marks Success + cancels siblings on success, or Failed +
                // creates the next attempt (if under max) on failure.
                var charge = await _autoBilling.ChargeInvoiceAsync(
                    row.InvoiceId, AutoBillingChargeSource.Retry,
                    executeRetryAttemptId: row.AttemptId,
                    cancellationToken: cancellationToken);

                if (charge.IsSuccess && charge.Data?.Charged == true)
                {
                    result.RetriesSucceeded++;
                    _logger.LogInformation(
                        "[recurring-billing][retry][success] attempt {AttemptId} invoice {InvoiceNumber} ({InvoiceId}) reference {Reference}.",
                        row.AttemptId, row.InvoiceNumber, row.InvoiceId, charge.Data.Reference);
                }
                else
                {
                    result.RetriesFailed++;
                    var reason = charge.IsSuccess ? charge.Data?.FailureReason : charge.Message;
                    _logger.LogWarning(
                        "[recurring-billing][retry][failed] attempt {AttemptId} invoice {InvoiceNumber} ({InvoiceId}) reason='{Reason}'.",
                        row.AttemptId, row.InvoiceNumber, row.InvoiceId, reason ?? "(none)");
                }
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Errors.Add($"Attempt {row.AttemptId} (invoice {row.InvoiceId}): {ex.Message}");
                _logger.LogError(ex,
                    "[recurring-billing][retry][failed] attempt {AttemptId} invoice {InvoiceNumber} ({InvoiceId}) threw; continuing.",
                    row.AttemptId, row.InvoiceNumber, row.InvoiceId);
            }
        }

        _logger.LogInformation(
            "[recurring-billing][retry][summary] selected={Selected} wouldRetry={Would} attempted={Attempted} succeeded={Succeeded} " +
            "failed={Failed} skippedPaidOrSettled={Settled} skippedNoMandate={NoMandate} skippedNoOptIn={NoOptIn} " +
            "skippedPendingInitiation={PendingInit} skippedInactiveService={Inactive} skippedMaxAttempts={MaxAtt} " +
            "skippedChargeAuthDisabled={AuthOff} skippedAlreadyProcessed={Already} truncated={Truncated} errors={Errors}",
            result.AttemptsSelected, result.WouldRetry, result.RetriesAttempted, result.RetriesSucceeded,
            result.RetriesFailed, result.SkippedPaidOrSettled, result.SkippedNoMandate, result.SkippedNoOptIn,
            result.SkippedPendingInitiation, result.SkippedInactiveService, result.SkippedMaxAttempts,
            result.SkippedChargeAuthDisabled, result.SkippedAlreadyProcessed, result.Truncated, result.ErrorCount);

        return result;
    }

    private async Task MarkAttemptSkippedAsync(Guid attemptId, string reason, CancellationToken cancellationToken)
    {
        var attempt = await _dbContext.PaymentRetryAttempts
            .FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
        if (attempt is null || attempt.Status != PaymentRetryAttemptStatus.Pending)
            return;
        attempt.Status = PaymentRetryAttemptStatus.Skipped;
        attempt.FailureReason = $"Retry skipped: {reason}.";
        attempt.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private void LogSkip(AttemptRow row, string reason) =>
        _logger.LogInformation(
            "[recurring-billing][retry][skipped] attempt {AttemptId} (#{AttemptNumber}) invoice {InvoiceNumber} ({InvoiceId}) reason={Reason}.",
            row.AttemptId, row.AttemptNumber, row.InvoiceNumber, row.InvoiceId, reason);

    private void LogTruncated(int cap) =>
        _logger.LogWarning(
            "[recurring-billing][retry] batch cap {Cap} reached; remaining due retry attempts deferred to the next run.", cap);
}
