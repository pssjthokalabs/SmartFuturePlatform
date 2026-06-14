using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;

namespace SmartFuture.Application.Payments.Recurring;

/// <summary>
/// Phase 0B — generates the next recurring service invoice for each due
/// <c>ServiceBillingSchedule</c>. Honours dry-run (reports without writing),
/// a per-run cap, and a duplicate guard. NO charging, retries, or suspension.
/// </summary>
public sealed class RecurringInvoiceGenerator : IRecurringInvoiceGenerator
{
    private const string InvoiceNumberPrefix = "INV";
    private const int InvoiceNumberMaxAttempts = 5;

    private readonly IAppDbContext _dbContext;
    private readonly AutoBillingSettings _settings;
    private readonly IBillingNotificationService _notifications;
    private readonly ILogger<RecurringInvoiceGenerator> _logger;

    public RecurringInvoiceGenerator(
        IAppDbContext dbContext,
        IOptions<AutoBillingSettings> settings,
        IBillingNotificationService notifications,
        ILogger<RecurringInvoiceGenerator> logger)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<RecurringInvoiceGenerationResult> GenerateDueInvoicesAsync(
        RecurringBillingRunContext context, CancellationToken cancellationToken = default)
    {
        var result = new RecurringInvoiceGenerationResult();
        var now = context.NowUtc;
        var cap = _settings.MaxInvoicesPerRun > 0 ? _settings.MaxInvoicesPerRun : int.MaxValue;
        var daysBeforeDue = Math.Max(0, _settings.GenerateInvoicesDaysBeforeDue);

        // Due schedules: active, auto-billable, and the "generate N days
        // before due" window has opened (NextInvoiceDateUtc <= now).
        var dueSchedules = await _dbContext.ServiceBillingSchedules
            .Where(s => s.Status == ServiceBillingScheduleStatus.Active
                     && s.IsAutoBillable
                     && s.NextInvoiceDateUtc != null
                     && s.NextInvoiceDateUtc <= now
                     && s.NextDueDateUtc != null)
            .OrderBy(s => s.NextInvoiceDateUtc)
            .ToListAsync(cancellationToken);

        result.SchedulesConsidered = dueSchedules.Count;

        var processed = 0;
        foreach (var schedule in dueSchedules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Batch cap — stop cleanly and report truncation.
            if (processed >= cap)
            {
                result.Truncated = true;
                _logger.LogWarning(
                    "[recurring-billing][generate] batch cap {Cap} reached; {Remaining} due schedule(s) deferred to the next run.",
                    cap, dueSchedules.Count - processed);
                break;
            }

            try
            {
                var interval = BillingCycleCalculator.IntervalFor(schedule.BillingCycle);
                if (interval is null || interval.Value <= TimeSpan.Zero)
                {
                    result.NonRecurringSkipped++;
                    continue;
                }

                // Service must still be billable.
                var networkAccount = await _dbContext.NetworkAccounts
                    .FirstOrDefaultAsync(n => n.Id == schedule.NetworkAccountId, cancellationToken);
                if (networkAccount is null || networkAccount.Status != NetworkAccountStatus.Active)
                {
                    result.InactiveSkipped++;
                    _logger.LogInformation(
                        "[recurring-billing][generate] schedule {ScheduleId} skipped — network account {NaStatus}.",
                        schedule.Id, networkAccount?.Status.ToString() ?? "missing");
                    continue;
                }

                // The next service period = [NextDueDateUtc, NextDueDateUtc + interval],
                // due at the start of the period (prepaid). Generated daysBeforeDue early.
                var periodStart = schedule.NextDueDateUtc!.Value;
                var periodEnd = periodStart + interval.Value;
                var dueAt = periodStart;

                // Duplicate guard (app-level; backed by a filtered unique index).
                var duplicate = await _dbContext.Invoices
                    .FirstOrDefaultAsync(i => i.ServiceBillingScheduleId == schedule.Id
                                           && i.PeriodStartUtc == periodStart, cancellationToken);
                if (duplicate is not null)
                {
                    result.DuplicatesSkipped++;
                    _logger.LogWarning(
                        "[recurring-billing][generate] schedule {ScheduleId} already has invoice {InvoiceNumber} for period start {PeriodStart:o}; advancing past it.",
                        schedule.Id, duplicate.InvoiceNumber, periodStart);

                    if (!context.DryRun)
                    {
                        AdvanceSchedule(schedule, periodStart, periodEnd, interval.Value, daysBeforeDue, duplicate.Id);
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }
                    processed++;
                    continue;
                }

                if (context.DryRun)
                {
                    result.WouldGenerate++;
                    _logger.LogInformation(
                        "[recurring-billing][generate][dry_run] schedule {ScheduleId} WOULD generate invoice: period {PeriodStart:o}–{PeriodEnd:o} due {DueAt:o} amount {Amount} {Currency}.",
                        schedule.Id, periodStart, periodEnd, dueAt, schedule.Amount, schedule.CurrencyCode);
                    processed++;
                    continue;
                }

                var invoiceNumber = await AllocateInvoiceNumberAsync(now, cancellationToken);
                if (invoiceNumber is null)
                {
                    result.ErrorCount++;
                    result.Errors.Add($"Schedule {schedule.Id}: could not allocate a unique invoice number.");
                    continue;
                }

                var description = string.IsNullOrWhiteSpace(networkAccount.PackageName)
                    ? $"Service subscription — {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}"
                    : $"{networkAccount.PackageName} — {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}";

                var invoice = new Invoice
                {
                    InvoiceNumber = invoiceNumber,
                    OrderId = schedule.OrderId,
                    ServiceBillingScheduleId = schedule.Id,
                    PeriodStartUtc = periodStart,
                    PeriodEndUtc = periodEnd,
                    Status = InvoiceStatus.Issued,
                    SubtotalAmount = schedule.Amount,
                    TaxAmount = 0m,
                    TotalAmount = schedule.Amount,
                    AmountPaid = 0m,
                    BalanceDue = schedule.Amount,
                    CurrencyCode = string.IsNullOrWhiteSpace(schedule.CurrencyCode) ? "ZAR" : schedule.CurrencyCode,
                    IssuedAtUtc = now,
                    DueAtUtc = dueAt,
                    Notes = "Recurring service subscription invoice — auto-generated by the recurring billing engine."
                };
                _dbContext.Invoices.Add(invoice);

                _dbContext.InvoiceLineItems.Add(new InvoiceLineItem
                {
                    Invoice = invoice,
                    LineType = InvoiceLineItemType.ServicePackage,
                    Description = description,
                    Quantity = 1,
                    UnitAmount = schedule.Amount,
                    TotalAmount = schedule.Amount,
                    SortOrder = 0
                });

                AdvanceSchedule(schedule, periodStart, periodEnd, interval.Value, daysBeforeDue, invoice.Id);

                await _dbContext.SaveChangesAsync(cancellationToken);

                result.InvoicesGenerated++;
                processed++;

                _logger.LogInformation(
                    "[recurring-billing][generate] schedule {ScheduleId} generated invoice {InvoiceNumber}: period {PeriodStart:o}–{PeriodEnd:o} due {DueAt:o} amount {Amount} {Currency}.",
                    schedule.Id, invoice.InvoiceNumber, periodStart, periodEnd, dueAt, schedule.Amount, invoice.CurrencyCode);

                // Phase 0F-notify — best-effort, deduped, default-OFF customer
                // "invoice generated" email. Real run only (dry-run never
                // reaches here). Never blocks generation.
                await _notifications.NotifyInvoiceGeneratedAsync(
                    invoice.Id, invoice.InvoiceNumber, schedule.UserId,
                    schedule.Amount, dueAt, invoice.CurrencyCode, cancellationToken);
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Errors.Add($"Schedule {schedule.Id}: {ex.Message}");
                _logger.LogError(ex,
                    "[recurring-billing][generate] schedule {ScheduleId} threw; continuing with the next schedule.",
                    schedule.Id);
            }
        }

        return result;
    }

    private static void AdvanceSchedule(
        ServiceBillingSchedule schedule, DateTime periodStart, DateTime periodEnd,
        TimeSpan interval, int daysBeforeDue, Guid invoiceId)
    {
        schedule.CurrentPeriodStartUtc = periodStart;
        schedule.CurrentPeriodEndUtc = periodEnd;
        schedule.NextDueDateUtc = periodEnd;
        schedule.NextInvoiceDateUtc = periodEnd - TimeSpan.FromDays(daysBeforeDue);
        schedule.LastInvoicedPeriodEndUtc = periodEnd;
        schedule.LastInvoiceId = invoiceId;
        schedule.UpdatedAtUtc = DateTime.UtcNow;
    }

    private async Task<string?> AllocateInvoiceNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < InvoiceNumberMaxAttempts; attempt++)
        {
            var candidate = BillingNumberGenerator.BuildCandidate(InvoiceNumberPrefix, now);
            var exists = await _dbContext.Invoices.AnyAsync(i => i.InvoiceNumber == candidate, cancellationToken);
            if (!exists) return candidate;
        }
        return null;
    }
}
