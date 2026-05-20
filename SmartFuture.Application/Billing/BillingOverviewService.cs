using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

/// <summary>
/// Phase 48 — single round-trip aggregator for the customer billing
/// dashboard. Loads the authenticated user's services + a small window
/// of recent invoices/payments + debit-mandate count, then projects the
/// shape the /client/billing page renders directly.
///
/// Per-service "next payment date" is computed via BillingCycleCalculator
/// (no recurring invoice engine yet). Service-link metadata on invoice
/// and payment rows is best-effort — populated when the invoice's order
/// resolves to a non-terminated NetworkAccount, null otherwise.
/// </summary>
public class BillingOverviewService : IBillingOverviewService
{
    private const int RecentInvoiceLimit = 10;
    private const int RecentPaymentLimit = 10;

    private static readonly NetworkAccountStatus[] BillableStatuses =
    {
        NetworkAccountStatus.Pending,
        NetworkAccountStatus.Active,
        NetworkAccountStatus.Suspended,
        NetworkAccountStatus.Failed
    };

    private readonly IAppDbContext _dbContext;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<BillingOverviewService> _logger;

    public BillingOverviewService(IAppDbContext dbContext, ICurrentUserService currentUser, ILogger<BillingOverviewService> logger)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<BillingOverviewDto>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = _currentUser.UserId;
            if (userId is null || userId == Guid.Empty)
                return Result<BillingOverviewDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            // ── Customer's services (network accounts) ──────────────
            // Includes the Order so PackageBillingCycle is available
            // to BillingCycleCalculator without an extra round-trip.
            var serviceEntities = await _dbContext.NetworkAccounts
                .AsNoTracking()
                .Include(n => n.Order)
                .Where(n => n.Order!.UserId == userId.Value
                         && BillableStatuses.Contains(n.Status))
                .OrderBy(n => n.CreatedAtUtc)
                .ToListAsync(cancellationToken);

            var serviceRows = serviceEntities
                .Select(n => new BillingOverviewServiceDto
                {
                    ServiceId            = n.Id,
                    ServiceAccountNumber = n.AccountNumber,
                    PackageName          = n.PackageName,
                    PackageSpeedLabel    = n.PackageSpeedLabel,
                    Status               = n.Status,
                    MonthlyPrice         = n.PackagePrice,
                    NextPaymentDateUtc   = BillingCycleCalculator.ComputeNextPaymentDateUtc(n),
                    NextPaymentAmount    = n.Status == NetworkAccountStatus.Active ? n.PackagePrice : (decimal?)null,
                    BillingStatusLabel   = BillingCycleCalculator.BillingStatusLabel(n),
                    OrderId              = n.OrderId,
                    OrderNumber          = n.Order?.OrderNumber
                })
                .ToList();

            // Service-id lookup keyed by OrderId for invoice/payment
            // enrichment. Only Pending/Active/Suspended/Failed are
            // tracked here — terminated services intentionally don't
            // back-reference from historical invoices.
            var serviceByOrderId = serviceEntities
                .GroupBy(n => n.OrderId)
                .ToDictionary(g => g.Key, g => g.First());

            // ── Recent invoices + payments ──────────────────────────
            // The detail pages don't surface line items here — keeping
            // the row payload lean to match the existing /mine list
            // shape.
            var invoiceEntities = await _dbContext.Invoices
                .AsNoTracking()
                .Include(i => i.Order)
                .Where(i => i.Order!.UserId == userId.Value)
                .OrderByDescending(i => i.CreatedAtUtc)
                .Take(RecentInvoiceLimit)
                .ToListAsync(cancellationToken);

            var paymentEntities = await _dbContext.Payments
                .AsNoTracking()
                .Include(p => p.Invoice).ThenInclude(i => i!.Order)
                .Where(p => p.Invoice!.Order!.UserId == userId.Value)
                .OrderByDescending(p => p.CreatedAtUtc)
                .Take(RecentPaymentLimit)
                .ToListAsync(cancellationToken);

            // ── Counts (totals across all-time, not just the recent
            // window) so the KPI cards stay accurate when the page
            // shows only the latest few.
            var invoiceCount = await _dbContext.Invoices
                .AsNoTracking()
                .CountAsync(i => i.Order!.UserId == userId.Value, cancellationToken);

            var paymentCount = await _dbContext.Payments
                .AsNoTracking()
                .CountAsync(p => p.Invoice!.Order!.UserId == userId.Value, cancellationToken);

            var debitOrderCount = await _dbContext.DebitOrderMandates
                .AsNoTracking()
                .CountAsync(d => d.UserId == userId.Value, cancellationToken);

            // ── Outstanding balance: sum of BalanceDue across
            // non-cancelled / non-void invoices. Mirrors what the
            // legacy frontend computed client-side from the /mine list,
            // but uses every invoice the customer has (not the
            // pageSize=100 cap).
            var outstandingBalance = await _dbContext.Invoices
                .AsNoTracking()
                .Where(i => i.Order!.UserId == userId.Value
                         && i.Status != InvoiceStatus.Cancelled
                         && i.Status != InvoiceStatus.Void)
                .SumAsync(i => (decimal?)i.BalanceDue, cancellationToken) ?? 0m;

            var activeServices = serviceRows
                .Where(r => r.Status == NetworkAccountStatus.Active)
                .ToList();
            var pendingServices = serviceRows
                .Where(r => r.Status == NetworkAccountStatus.Pending)
                .ToList();

            var nextPayment = activeServices
                .Where(r => r.NextPaymentDateUtc.HasValue)
                .OrderBy(r => r.NextPaymentDateUtc!.Value)
                .FirstOrDefault();

            return Result<BillingOverviewDto>.Success(new BillingOverviewDto
            {
                OutstandingBalance              = outstandingBalance,
                InvoiceCount                    = invoiceCount,
                PaymentCount                    = paymentCount,
                DebitOrderCount                 = debitOrderCount,
                ActiveServicesCount             = activeServices.Count,
                PendingActivationServicesCount  = pendingServices.Count,
                ActiveMonthlyTotal              = activeServices.Sum(r => r.MonthlyPrice),
                PendingMonthlyTotal             = pendingServices.Sum(r => r.MonthlyPrice),
                NextPaymentDateUtc              = nextPayment?.NextPaymentDateUtc,
                NextPaymentAmount               = nextPayment?.NextPaymentAmount,
                NextPaymentServiceId            = nextPayment?.ServiceId,
                NextPaymentServiceName          = nextPayment?.PackageName,
                Services                        = serviceRows,
                RecentInvoices                  = invoiceEntities
                    .Select(i => MapInvoiceWithService(i, serviceByOrderId))
                    .ToList(),
                RecentPayments                  = paymentEntities
                    .Select(p => MapPaymentWithService(p, serviceByOrderId))
                    .ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building billing overview");
            return Result<BillingOverviewDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while building your billing overview.");
        }
    }

    private static InvoiceDto MapInvoiceWithService(Invoice i, IReadOnlyDictionary<Guid, Domain.NetworkAccounts.NetworkAccount> byOrderId)
    {
        byOrderId.TryGetValue(i.OrderId, out var service);
        return new InvoiceDto
        {
            Id                            = i.Id,
            InvoiceNumber                 = i.InvoiceNumber,
            OrderId                       = i.OrderId,
            OrderNumber                   = i.Order?.OrderNumber,
            OrderStatus                   = i.Order?.Status,
            Status                        = i.Status,
            SubtotalAmount                = i.SubtotalAmount,
            TaxAmount                     = i.TaxAmount,
            TotalAmount                   = i.TotalAmount,
            AmountPaid                    = i.AmountPaid,
            BalanceDue                    = i.BalanceDue,
            CurrencyCode                  = i.CurrencyCode,
            IssuedAtUtc                   = i.IssuedAtUtc,
            DueAtUtc                      = i.DueAtUtc,
            PaidAtUtc                     = i.PaidAtUtc,
            VoidedAtUtc                   = i.VoidedAtUtc,
            Notes                         = i.Notes,
            ExternalReference             = i.ExternalReference,
            CreatedAtUtc                  = i.CreatedAtUtc,
            UpdatedAtUtc                  = i.UpdatedAtUtc,
            ServiceId                     = service?.Id,
            ServiceAccountNumber          = service?.AccountNumber,
            ServicePackageName            = service?.PackageName
        };
    }

    private static PaymentDto MapPaymentWithService(Payment p, IReadOnlyDictionary<Guid, Domain.NetworkAccounts.NetworkAccount> byOrderId)
    {
        var orderId = p.Invoice?.OrderId;
        Domain.NetworkAccounts.NetworkAccount? service = null;
        if (orderId.HasValue) byOrderId.TryGetValue(orderId.Value, out service);

        return new PaymentDto
        {
            Id                       = p.Id,
            PaymentNumber            = p.PaymentNumber,
            InvoiceId                = p.InvoiceId,
            InvoiceNumber            = p.Invoice?.InvoiceNumber,
            OrderId                  = orderId,
            OrderNumber              = p.Invoice?.Order?.OrderNumber,
            Status                   = p.Status,
            Method                   = p.Method,
            Amount                   = p.Amount,
            CurrencyCode             = p.CurrencyCode,
            PaidAtUtc                = p.PaidAtUtc,
            FailedAtUtc              = p.FailedAtUtc,
            RefundedAtUtc            = p.RefundedAtUtc,
            GatewayName              = p.GatewayName,
            GatewayReference         = p.GatewayReference,
            GatewayTransactionId     = p.GatewayTransactionId,
            ExternalReference        = p.ExternalReference,
            FailureReason            = p.FailureReason,
            Notes                    = p.Notes,
            CreatedAtUtc             = p.CreatedAtUtc,
            UpdatedAtUtc             = p.UpdatedAtUtc,
            ServiceId                = service?.Id,
            ServiceAccountNumber     = service?.AccountNumber,
            ServicePackageName       = service?.PackageName
        };
    }
}
