using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Payments;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0C — charges due recurring service invoices. Thin select → skip →
/// delegate layer over <see cref="IAutoBillingService.ChargeInvoiceAsync"/>;
/// it owns the run-level guards (dry-run, cap, pending-initiation/retry skip,
/// re-check-before-charge, in-run dedupe) while the charge service owns
/// mandate resolution, settlement (via PaymentApplierService), the
/// test-amount override + production guard, and failure/retry-row creation.
///
/// Paystack only. Customers without an active default reusable Paystack
/// mandate are skipped + reported. No retries are consumed, no account is
/// suspended.
/// </summary>
public sealed class DueInvoiceChargeRunner : IDueInvoiceChargeRunner
{
    // A Pending PaymentInitiation younger than this blocks a re-charge.
    // Paystack charge_authorization is synchronous (~30s timeout), so a
    // Pending row older than this is an abandoned/crashed attempt and must
    // not block the invoice forever. Could become config later.
    private const int PendingInitiationStalenessMinutes = 60;

    private readonly IAppDbContext _dbContext;
    private readonly IAutoBillingService _autoBilling;
    private readonly IRecurringMandateSelector _mandateSelector;
    private readonly AutoBillingSettings _settings;
    private readonly ILogger<DueInvoiceChargeRunner> _logger;

    public DueInvoiceChargeRunner(
        IAppDbContext dbContext,
        IAutoBillingService autoBilling,
        IRecurringMandateSelector mandateSelector,
        IOptions<AutoBillingSettings> settings,
        ILogger<DueInvoiceChargeRunner> logger)
    {
        _dbContext = dbContext;
        _autoBilling = autoBilling;
        _mandateSelector = mandateSelector;
        _settings = settings.Value;
        _logger = logger;
    }

    private sealed record Candidate(
        Guid InvoiceId, string InvoiceNumber, decimal BalanceDue, string CurrencyCode,
        Guid ScheduleId, Guid UserId);

    public async Task<DueInvoiceChargeResult> ChargeDueInvoicesAsync(
        RecurringBillingRunContext context, CancellationToken cancellationToken = default)
    {
        var result = new DueInvoiceChargeResult();

        // Master gate. If auto-billing is off entirely, do nothing.
        if (!_settings.Enabled)
        {
            _logger.LogInformation(
                "[recurring-billing][charge][skipped] stage skipped — AutoBilling.Enabled=false.");
            return result;
        }

        var now = context.NowUtc;
        var cap = _settings.MaxChargesPerRun > 0 ? _settings.MaxChargesPerRun : int.MaxValue;
        var initiationStaleCutoff = now.AddMinutes(-PendingInitiationStalenessMinutes);

        // ─── Selection: due, schedule-linked recurring invoices ────────
        var candidates = await (
            from i in _dbContext.Invoices
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
            select new Candidate(i.Id, i.InvoiceNumber, i.BalanceDue, i.CurrencyCode, s.Id, s.UserId)
        ).ToListAsync(cancellationToken);

        result.CandidatesSelected = candidates.Count;

        var processed = 0;
        var seen = new HashSet<Guid>();

        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // In-run dedupe — never act on the same invoice twice per run.
            if (!seen.Add(c.InvoiceId))
                continue;

            try
            {
                // Re-fetch CURRENT invoice state — it may have settled
                // between selection and now.
                var live = await _dbContext.Invoices
                    .AsNoTracking()
                    .Where(i => i.Id == c.InvoiceId)
                    .Select(i => new { i.Status, i.BalanceDue })
                    .FirstOrDefaultAsync(cancellationToken);

                if (live is null
                    || live.BalanceDue <= 0m
                    || live.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled or InvoiceStatus.Void)
                {
                    result.SkippedNotDue++;
                    LogSkip(c, "not_due_or_settled");
                    continue;
                }

                // Customer auto-billing opt-in.
                var optedIn = await _dbContext.CustomerProfiles
                    .AsNoTracking()
                    .Where(p => p.UserId == c.UserId)
                    .Select(p => (bool?)p.AutoBillingEnabled)
                    .FirstOrDefaultAsync(cancellationToken);
                if (optedIn != true)
                {
                    result.SkippedNoOptIn++;
                    LogSkip(c, "no_opt_in");
                    continue;
                }

                // Active default reusable mandate for an ENABLED provider
                // (Phase 1C — provider-neutral via the shared selector, so
                // this pre-check agrees with ChargeInvoiceAsync's selection).
                var availability = await _mandateSelector.GetAvailabilityAsync(c.UserId, cancellationToken);
                if (availability == MandateAvailability.PayFastRecurringDisabled)
                {
                    result.SkippedNoMandate++;
                    LogSkip(c, "payfast_recurring_disabled");
                    continue;
                }
                if (availability != MandateAvailability.Available)
                {
                    result.SkippedNoMandate++;
                    LogSkip(c, "no_reusable_mandate");
                    continue;
                }

                // Non-stale Pending PaymentInitiation already in flight.
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

                // Any pending retry attempt (future OR already due) means the
                // invoice is in the retry chain — Stage 3 (the retry worker)
                // owns it exclusively. Stage 2 must NOT charge a retry-chain
                // invoice as a fresh monthly renewal. (Phase 0D coordination.)
                var pendingRetry = await _dbContext.PaymentRetryAttempts
                    .AsNoTracking()
                    .AnyAsync(a => a.InvoiceId == c.InvoiceId
                                && a.Status == PaymentRetryAttemptStatus.Pending,
                              cancellationToken);
                if (pendingRetry)
                {
                    result.SkippedPendingRetry++;
                    LogSkip(c, "pending_retry_chain");
                    continue;
                }

                // ─── Dry-run: report only, never call the charge service ──
                if (context.DryRun)
                {
                    if (processed >= cap) { result.Truncated = true; LogTruncated(cap); break; }
                    result.WouldCharge++;
                    processed++;
                    _logger.LogInformation(
                        "[recurring-billing][charge][dry_run] invoice {InvoiceNumber} ({InvoiceId}) schedule {ScheduleId} " +
                        "balance {Balance} {Currency} provider=Paystack mandatePresent=true — WOULD charge.",
                        c.InvoiceNumber, c.InvoiceId, c.ScheduleId, live.BalanceDue, c.CurrencyCode);
                    continue;
                }

                // ─── Real run ─────────────────────────────────────────
                // Charge-authorization gate: when off, do not attempt; report.
                if (!_settings.ChargeAuthorizationEnabled)
                {
                    result.SkippedChargeAuthDisabled++;
                    LogSkip(c, "charge_authorization_disabled");
                    continue;
                }

                if (processed >= cap) { result.Truncated = true; LogTruncated(cap); break; }

                result.ChargesAttempted++;
                processed++;
                _logger.LogInformation(
                    "[recurring-billing][charge][attempt] invoice {InvoiceNumber} ({InvoiceId}) schedule {ScheduleId} balance {Balance} {Currency} provider=Paystack.",
                    c.InvoiceNumber, c.InvoiceId, c.ScheduleId, live.BalanceDue, c.CurrencyCode);

                // Delegate to the canonical charge path. It mints
                // Payment/PaymentInitiation/PaymentRetryAttempt, charges,
                // and settles via PaymentApplierService. We never mark the
                // invoice paid here.
                var charge = await _autoBilling.ChargeInvoiceAsync(
                    c.InvoiceId, AutoBillingChargeSource.MonthlyRenewal, cancellationToken: cancellationToken);

                if (charge.IsSuccess && charge.Data?.Charged == true)
                {
                    result.ChargesSucceeded++;
                    _logger.LogInformation(
                        "[recurring-billing][charge][success] invoice {InvoiceNumber} ({InvoiceId}) reference {Reference}.",
                        c.InvoiceNumber, c.InvoiceId, charge.Data.Reference);
                }
                else
                {
                    result.ChargesFailed++;
                    var reason = charge.IsSuccess ? charge.Data?.FailureReason : charge.Message;
                    _logger.LogWarning(
                        "[recurring-billing][charge][failed] invoice {InvoiceNumber} ({InvoiceId}) reason='{Reason}'.",
                        c.InvoiceNumber, c.InvoiceId, reason ?? "(none)");
                }
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Errors.Add($"Invoice {c.InvoiceId}: {ex.Message}");
                _logger.LogError(ex,
                    "[recurring-billing][charge][failed] invoice {InvoiceNumber} ({InvoiceId}) threw; continuing with the next invoice.",
                    c.InvoiceNumber, c.InvoiceId);
            }
        }

        return result;
    }

    private void LogSkip(Candidate c, string reason) =>
        _logger.LogInformation(
            "[recurring-billing][charge][skipped] invoice {InvoiceNumber} ({InvoiceId}) schedule {ScheduleId} reason={Reason}.",
            c.InvoiceNumber, c.InvoiceId, c.ScheduleId, reason);

    private void LogTruncated(int cap) =>
        _logger.LogWarning(
            "[recurring-billing][charge] batch cap {Cap} reached; remaining due invoices deferred to the next run.", cap);
}
