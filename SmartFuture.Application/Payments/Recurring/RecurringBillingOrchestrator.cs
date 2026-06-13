using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Billing;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0A orchestrator shell. Acquires the DB-backed run lock (a
/// <c>BillingRunLog</c> row in <see cref="BillingRunStatus.Running"/>),
/// runs four NO-OP stages, then finalizes the row.
///
/// NOTHING here generates invoices, charges customers, processes retries,
/// or suspends accounts — those stages are intentionally empty and will be
/// filled in Phase 0B–0E. The only side effect is writing a BillingRunLog
/// row for visibility + locking.
/// </summary>
public sealed class RecurringBillingOrchestrator : IRecurringBillingOrchestrator
{
    private readonly IAppDbContext _dbContext;
    private readonly AutoBillingSettings _settings;
    private readonly IRecurringInvoiceGenerator _invoiceGenerator;
    private readonly IDueInvoiceChargeRunner _chargeRunner;
    private readonly IRetryRunner _retryRunner;
    private readonly IGraceSuspensionRunner _graceRunner;
    private readonly ILogger<RecurringBillingOrchestrator> _logger;

    public RecurringBillingOrchestrator(
        IAppDbContext dbContext,
        IOptions<AutoBillingSettings> settings,
        IRecurringInvoiceGenerator invoiceGenerator,
        IDueInvoiceChargeRunner chargeRunner,
        IRetryRunner retryRunner,
        IGraceSuspensionRunner graceRunner,
        ILogger<RecurringBillingOrchestrator> logger)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
        _invoiceGenerator = invoiceGenerator;
        _chargeRunner = chargeRunner;
        _retryRunner = retryRunner;
        _graceRunner = graceRunner;
        _logger = logger;
    }

    public async Task<RecurringBillingRunSummary> RunAsync(
        RecurringBillingRunContext context,
        CancellationToken cancellationToken = default)
    {
        var summary = new RecurringBillingRunSummary
        {
            RunId = context.RunId,
            DryRun = context.DryRun,
            StartedAtUtc = context.NowUtc
        };

        // ─── Concurrency lock ──────────────────────────────────────────
        if (_settings.PreventConcurrentRuns)
        {
            var staleCutoff = context.NowUtc.AddMinutes(-Math.Max(1, _settings.RunLockStalenessMinutes));
            var lockHeld = await _dbContext.BillingRunLogs
                .AsNoTracking()
                .AnyAsync(r => r.Status == BillingRunStatus.Running && r.StartedAtUtc >= staleCutoff,
                          cancellationToken);
            if (lockHeld)
            {
                summary.SkippedDueToLock = true;
                summary.FinishedAtUtc = context.NowUtc;
                _logger.LogInformation(
                    "[recurring-billing][lock_skipped] runId={RunId} reason=another run is Running within {Staleness}min — skipping.",
                    context.RunId, _settings.RunLockStalenessMinutes);
                return summary;
            }
        }

        var runLog = new BillingRunLog
        {
            StartedAtUtc = context.NowUtc,
            Status = BillingRunStatus.Running,
            TriggeredBy = context.TriggeredBy,
            DryRun = context.DryRun,
            MachineName = Environment.MachineName,
            InstanceId = context.RunId.ToString("N")
        };
        _dbContext.BillingRunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);
        summary.BillingRunLogId = runLog.Id;

        _logger.LogInformation(
            "[recurring-billing][run_start] runId={RunId} dryRun={DryRun} trigger={Trigger} machine={Machine}",
            context.RunId, context.DryRun, context.TriggeredBy, runLog.MachineName);
        _logger.LogInformation(
            "[recurring-billing][lock_acquired] runId={RunId} billingRunLogId={LogId}",
            context.RunId, runLog.Id);

        try
        {
            // ─── Stage 1 — recurring invoice generation (Phase 0B, LIVE) ──
            // Honours dry-run internally; generates invoices only — no
            // charge/retry/suspend.
            var generation = await _invoiceGenerator.GenerateDueInvoicesAsync(context, cancellationToken);

            // ─── Stage 2 — due-invoice auto-charge (Phase 0C, LIVE) ───────
            // Paystack only; dry-run safe; settles via PaymentApplierService.
            var charging = await _chargeRunner.ChargeDueInvoicesAsync(context, cancellationToken);

            // ─── Stage 3 — retry worker (Phase 0D, LIVE) ──────────────────
            // Consumes due Pending PaymentRetryAttempt rows in REUSE mode;
            // gated by AutoBilling__RetryJobEnabled; dry-run safe.
            var retrying = await _retryRunner.RunDueRetriesAsync(context, cancellationToken);

            // ─── Stage 4 — grace / suspension-candidate detection (Phase 0E) ──
            // REPORT-ONLY: detects candidates; never suspends, never mutates
            // NetworkAccount.Status, never notifies.
            var grace = await _graceRunner.DetectCandidatesAsync(context, cancellationToken);

            // Charge counters are aggregated across Stage 2 (renewals) and
            // Stage 3 (retries) so the BillingRunLog totals reflect both.
            runLog.InvoicesGenerated = generation.InvoicesGenerated;
            runLog.ChargesAttempted = charging.ChargesAttempted + retrying.RetriesAttempted;
            runLog.ChargesSucceeded = charging.ChargesSucceeded + retrying.RetriesSucceeded;
            runLog.ChargesFailed = charging.ChargesFailed + retrying.RetriesFailed;
            runLog.RetriesProcessed = retrying.RetriesAttempted;
            runLog.SuspensionCandidates = grace.SuspensionCandidates;
            runLog.ErrorCount += generation.ErrorCount + charging.ErrorCount + retrying.ErrorCount + grace.ErrorCount;

            runLog.Status = BillingRunStatus.Completed;
            runLog.FinishedAtUtc = DateTime.UtcNow;
            runLog.SummaryJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                phase = "0E",
                note = "stages 1-3 live; stage 4 grace detection REPORT-ONLY (no suspension)",
                dryRun = context.DryRun,
                generation = new
                {
                    schedulesConsidered = generation.SchedulesConsidered,
                    invoicesGenerated = generation.InvoicesGenerated,
                    wouldGenerate = generation.WouldGenerate,
                    duplicatesSkipped = generation.DuplicatesSkipped,
                    inactiveSkipped = generation.InactiveSkipped,
                    nonRecurringSkipped = generation.NonRecurringSkipped,
                    truncated = generation.Truncated,
                    errorCount = generation.ErrorCount,
                    errors = generation.Errors
                },
                charge = new
                {
                    candidatesSelected = charging.CandidatesSelected,
                    chargesAttempted = charging.ChargesAttempted,
                    chargesSucceeded = charging.ChargesSucceeded,
                    chargesFailed = charging.ChargesFailed,
                    wouldCharge = charging.WouldCharge,
                    skippedNotDue = charging.SkippedNotDue,
                    skippedNoOptIn = charging.SkippedNoOptIn,
                    skippedNoMandate = charging.SkippedNoMandate,
                    skippedPendingInitiation = charging.SkippedPendingInitiation,
                    skippedPendingRetry = charging.SkippedPendingRetry,
                    skippedInactiveService = charging.SkippedInactiveService,
                    skippedChargeAuthDisabled = charging.SkippedChargeAuthDisabled,
                    truncated = charging.Truncated,
                    errorCount = charging.ErrorCount,
                    errors = charging.Errors
                },
                retry = new
                {
                    attemptsSelected = retrying.AttemptsSelected,
                    wouldRetry = retrying.WouldRetry,
                    retriesAttempted = retrying.RetriesAttempted,
                    retriesSucceeded = retrying.RetriesSucceeded,
                    retriesFailed = retrying.RetriesFailed,
                    skippedPaidOrSettled = retrying.SkippedPaidOrSettled,
                    skippedNoMandate = retrying.SkippedNoMandate,
                    skippedNoOptIn = retrying.SkippedNoOptIn,
                    skippedPendingInitiation = retrying.SkippedPendingInitiation,
                    skippedInactiveService = retrying.SkippedInactiveService,
                    skippedMaxAttempts = retrying.SkippedMaxAttempts,
                    skippedRetryJobDisabled = retrying.SkippedRetryJobDisabled,
                    skippedChargeAuthDisabled = retrying.SkippedChargeAuthDisabled,
                    skippedAlreadyProcessed = retrying.SkippedAlreadyProcessed,
                    truncated = retrying.Truncated,
                    errorCount = retrying.ErrorCount,
                    errors = retrying.Errors
                },
                grace = new
                {
                    candidatesSelected = grace.CandidatesSelected,
                    suspensionCandidates = grace.SuspensionCandidates,
                    wouldSuspend = grace.WouldSuspend,
                    suspended = grace.Suspended,
                    skippedPaidOrSettled = grace.SkippedPaidOrSettled,
                    skippedPendingRetry = grace.SkippedPendingRetry,
                    skippedPendingInitiation = grace.SkippedPendingInitiation,
                    skippedRetriesNotExhausted = grace.SkippedRetriesNotExhausted,
                    skippedInactiveService = grace.SkippedInactiveService,
                    skippedGraceNotExpired = grace.SkippedGraceNotExpired,
                    truncated = grace.Truncated,
                    errorCount = grace.ErrorCount,
                    errors = grace.Errors
                }
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            summary.InvoicesGenerated = generation.InvoicesGenerated;
            summary.ChargesAttempted = charging.ChargesAttempted + retrying.RetriesAttempted;
            summary.ChargesSucceeded = charging.ChargesSucceeded + retrying.RetriesSucceeded;
            summary.ChargesFailed = charging.ChargesFailed + retrying.RetriesFailed;
            summary.RetriesProcessed = retrying.RetriesAttempted;
            summary.SuspensionCandidates = grace.SuspensionCandidates;
            summary.FinishedAtUtc = runLog.FinishedAtUtc;
            _logger.LogInformation(
                "[recurring-billing][run_complete] runId={RunId} billingRunLogId={LogId} dryRun={DryRun} " +
                "invoicesGenerated={Generated} wouldGenerate={WouldGen} " +
                "chargeCandidates={Candidates} chargesAttempted={Attempted} chargesSucceeded={Succeeded} chargesFailed={Failed} wouldCharge={WouldCharge} " +
                "retryAttemptsSelected={RetrySelected} retriesProcessed={RetriesProcessed} retriesSucceeded={RetriesSucceeded} retriesFailed={RetriesFailed} wouldRetry={WouldRetry} " +
                "suspensionCandidates={SuspensionCandidates} wouldSuspend={WouldSuspend} suspended={Suspended} " +
                "truncated={Truncated} errors={Errors}",
                context.RunId, runLog.Id, context.DryRun,
                generation.InvoicesGenerated, generation.WouldGenerate,
                charging.CandidatesSelected, charging.ChargesAttempted, charging.ChargesSucceeded,
                charging.ChargesFailed, charging.WouldCharge,
                retrying.AttemptsSelected, retrying.RetriesAttempted, retrying.RetriesSucceeded,
                retrying.RetriesFailed, retrying.WouldRetry,
                grace.SuspensionCandidates, grace.WouldSuspend, grace.Suspended,
                (generation.Truncated || charging.Truncated || retrying.Truncated || grace.Truncated),
                (generation.ErrorCount + charging.ErrorCount + retrying.ErrorCount + grace.ErrorCount));
        }
        catch (Exception ex)
        {
            summary.Errors.Add(ex.Message);
            summary.FinishedAtUtc = DateTime.UtcNow;
            try
            {
                runLog.Status = BillingRunStatus.Failed;
                runLog.FinishedAtUtc = summary.FinishedAtUtc;
                runLog.ErrorCount += 1;
                runLog.ErrorText = ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx,
                    "[recurring-billing][run_failed] runId={RunId} — additionally failed to persist the failed run-log row.",
                    context.RunId);
            }

            _logger.LogError(ex,
                "[recurring-billing][run_failed] runId={RunId} billingRunLogId={LogId}: {Message}",
                context.RunId, runLog.Id, ex.Message);
        }

        return summary;
    }

    private void RunStageNoOp(string stage, RecurringBillingRunContext context)
    {
        // Phase 0A: deliberately does nothing. No DB reads/writes, no
        // charges, no notifications. Present so the run pipeline + logging
        // are observable before any real stage logic lands.
        _logger.LogInformation(
            "[recurring-billing][stage_noop] runId={RunId} stage={Stage} dryRun={DryRun}",
            context.RunId, stage, context.DryRun);
    }
}
