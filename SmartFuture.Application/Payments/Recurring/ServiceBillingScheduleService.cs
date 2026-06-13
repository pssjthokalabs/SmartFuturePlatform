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
/// Phase 0B implementation. Creates and activates a
/// <c>ServiceBillingSchedule</c> the first time a service-fee invoice is
/// paid, anchoring the recurring cycle on the payment date.
/// </summary>
public sealed class ServiceBillingScheduleService : IServiceBillingScheduleService
{
    private readonly IAppDbContext _dbContext;
    private readonly AutoBillingSettings _settings;
    private readonly ILogger<ServiceBillingScheduleService> _logger;

    public ServiceBillingScheduleService(
        IAppDbContext dbContext,
        IOptions<AutoBillingSettings> settings,
        ILogger<ServiceBillingScheduleService> logger)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task EnsureActivatedForPaidServiceInvoiceAsync(
        Guid invoiceId, DateTime paidAtUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            var invoice = await _dbContext.Invoices
                .Include(i => i.Order)
                .Include(i => i.LineItems)
                .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

            if (invoice is null || invoice.Order is null)
                return;

            // Only a service-fee invoice (carries a ServicePackage line)
            // anchors recurring billing. Installation-fee-only invoices do not.
            var isServiceInvoice = invoice.LineItems.Any(li => li.LineType == InvoiceLineItemType.ServicePackage);
            if (!isServiceInvoice)
                return;

            var order = invoice.Order;

            // Idempotency: one schedule per order. If it already exists (e.g.
            // a later recurring invoice being paid), DO NOTHING — we must not
            // re-anchor the cycle on a subsequent payment.
            var alreadyHasSchedule = await _dbContext.ServiceBillingSchedules
                .AnyAsync(s => s.OrderId == order.Id, cancellationToken);
            if (alreadyHasSchedule)
                return;

            // The network account is created by the applier's
            // EnsurePending/Provision hook on the same invoice-paid event;
            // pick the most recent non-terminated one for this order.
            var networkAccount = await _dbContext.NetworkAccounts
                .Where(n => n.OrderId == order.Id && n.Status != NetworkAccountStatus.Terminated)
                .OrderByDescending(n => n.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (networkAccount is null)
            {
                _logger.LogInformation(
                    "[recurring-billing][schedule_activate] order {OrderNumber} invoice {InvoiceNumber}: no network account yet; schedule not created (will retry on next paid service invoice).",
                    order.OrderNumber, invoice.InvoiceNumber);
                return;
            }

            var cycle = order.PackageBillingCycle;
            var interval = BillingCycleCalculator.IntervalFor(cycle);
            if (interval is null || interval.Value <= TimeSpan.Zero)
            {
                _logger.LogInformation(
                    "[recurring-billing][schedule_activate] order {OrderNumber} invoice {InvoiceNumber}: billing cycle {Cycle} is non-recurring; no schedule created.",
                    order.OrderNumber, invoice.InvoiceNumber, cycle);
                return;
            }

            var daysBeforeDue = Math.Max(0, _settings.GenerateInvoicesDaysBeforeDue);

            // Anchor on the PAID date. The first invoice covers
            // [paid, paid + interval]; the next (recurring) invoice is due at
            // the end of that period and is generated daysBeforeDue earlier.
            var periodStart = paidAtUtc;
            var periodEnd = paidAtUtc + interval.Value;
            var nextDue = periodEnd;
            var nextInvoice = periodEnd - TimeSpan.FromDays(daysBeforeDue);

            var schedule = new ServiceBillingSchedule
            {
                NetworkAccountId = networkAccount.Id,
                OrderId = order.Id,
                UserId = order.UserId,
                BillingCycle = cycle,
                Amount = order.PackagePrice,
                CurrencyCode = string.IsNullOrWhiteSpace(invoice.CurrencyCode) ? "ZAR" : invoice.CurrencyCode,
                AnchorDayOfMonth = paidAtUtc.Day,
                CurrentPeriodStartUtc = periodStart,
                CurrentPeriodEndUtc = periodEnd,
                NextDueDateUtc = nextDue,
                NextInvoiceDateUtc = nextInvoice,
                LastInvoicedPeriodEndUtc = periodEnd,
                LastInvoiceId = invoice.Id,
                Status = ServiceBillingScheduleStatus.Active,
                IsAutoBillable = true,
                Notes = "Anchored on first paid service-fee invoice (Phase 0B)."
            };

            _dbContext.ServiceBillingSchedules.Add(schedule);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "[recurring-billing][schedule_activate] order {OrderNumber} invoice {InvoiceNumber}: schedule {ScheduleId} ACTIVE, cycle={Cycle}, " +
                "periodStart={PeriodStart:o} periodEnd={PeriodEnd:o} nextDue={NextDue:o} nextInvoice={NextInvoice:o} amount={Amount}.",
                order.OrderNumber, invoice.InvoiceNumber, schedule.Id, cycle,
                periodStart, periodEnd, nextDue, nextInvoice, schedule.Amount);
        }
        catch (Exception ex)
        {
            // Best-effort — never disrupt the payment that triggered this.
            _logger.LogError(ex,
                "[recurring-billing][schedule_activate] threw for invoice {InvoiceId}; payment integrity unaffected, schedule can be created on a later paid service invoice.",
                invoiceId);
        }
    }
}
