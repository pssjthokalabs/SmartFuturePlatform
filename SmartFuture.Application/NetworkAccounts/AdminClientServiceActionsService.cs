using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.NetworkAccounts;

/// <summary>
/// Admin Client Service Detail orchestrator (go-live Activate Service
/// / Force Settle button). See
/// <see cref="IAdminClientServiceActionsService"/> for the contract.
/// </summary>
public class AdminClientServiceActionsService : IAdminClientServiceActionsService
{
    private readonly IAppDbContext _dbContext;
    private readonly IAutoBillingService _autoBilling;
    private readonly IOrderService _orderService;
    private readonly IOptions<ServiceActivationSettings> _activationSettings;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<AdminClientServiceActionsService> _logger;

    public AdminClientServiceActionsService(
        IAppDbContext dbContext,
        IAutoBillingService autoBilling,
        IOrderService orderService,
        IOptions<ServiceActivationSettings> activationSettings,
        ICurrentUserService currentUser,
        ILogger<AdminClientServiceActionsService> logger)
    {
        _dbContext = dbContext;
        _autoBilling = autoBilling;
        _orderService = orderService;
        _activationSettings = activationSettings;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<AdminActivateOrSettleResultDto>> ActivateOrSettleAsync(
        Guid networkAccountId, CancellationToken cancellationToken = default)
    {
        if (networkAccountId == Guid.Empty)
            return Result<AdminActivateOrSettleResultDto>.Failure(
                ErrorCodes.BAD_REQUEST, "Network account id is required.");

        var account = await _dbContext.NetworkAccounts
            .Include(n => n.Order)
            .FirstOrDefaultAsync(n => n.Id == networkAccountId, cancellationToken);
        if (account is null || account.Order is null)
            return Result<AdminActivateOrSettleResultDto>.Failure(
                ErrorCodes.NOT_FOUND, "Service not found.");

        var order = account.Order;
        var result = new AdminActivateOrSettleResultDto
        {
            ServiceId             = account.Id,
            PreviousServiceStatus = LifecycleLabel(order.Status, account.Status),
        };

        // ── Already Active → idempotent ────────────────────────────
        if (order.Status == OrderStatus.Active && account.Status == NetworkAccountStatus.Active)
        {
            result.NewServiceStatus = result.PreviousServiceStatus;
            result.Message = "Service is already active.";
            return Result<AdminActivateOrSettleResultDto>.Success(result, result.Message);
        }

        // ── Pending Installation → not yet ─────────────────────────
        // We refuse rather than silently issuing an invoice for a
        // service whose dispatch hasn't happened yet.
        var hasCompletedInstall = await _dbContext.Installations
            .AsNoTracking()
            .AnyAsync(i => i.OrderId == order.Id
                        && i.Status == Shared.Enums.Installations.InstallationStatus.Completed,
                      cancellationToken);
        var installScheduled = await _dbContext.Installations
            .AsNoTracking()
            .AnyAsync(i => i.OrderId == order.Id, cancellationToken);
        if (!hasCompletedInstall
            && order.Status != OrderStatus.PendingPayment
            && order.Status != OrderStatus.PendingActivation
            && order.Status != OrderStatus.Active)
        {
            result.NewServiceStatus = result.PreviousServiceStatus;
            result.Message = installScheduled
                ? "Installation is scheduled but not completed yet — activation can't proceed."
                : "Installation must be completed before activation.";
            return Result<AdminActivateOrSettleResultDto>.Failure(ErrorCodes.CONFLICT, result.Message);
        }

        // ── Pending Activation → delegate to the existing admin
        //    Openserve-completion path. When manual-activation is
        //    disabled, OrderService.AdminActivateServiceAsync still
        //    sets ActivatedAtUtc / BillingAnchorDateUtc /
        //    NextPayDateUtc / NetworkAccount.Active in one step, so
        //    this is the right hook for both flag values.
        if (order.Status == OrderStatus.PendingActivation)
        {
            var activate = await _orderService.AdminActivateServiceAsync(order.Id,
                new AdminActivateServiceRequestDto(), cancellationToken);
            if (!activate.IsSuccess || activate.Data is null)
            {
                result.NewServiceStatus = result.PreviousServiceStatus;
                result.Message = activate.Message ?? "Activate failed.";
                return Result<AdminActivateOrSettleResultDto>.Failure(
                    activate.Code ?? ErrorCodes.EXCEPTION, result.Message);
            }
            result.NewServiceStatus = "Active";
            result.Message = "Service activated.";
            return Result<AdminActivateOrSettleResultDto>.Success(result, result.Message);
        }

        // ── Pending Payment → ensure a monthly invoice exists and
        //    attempt auto-debit via the existing AutoBillingService.
        //    AutoBillingService respects all the flag gates +
        //    customer-level opt-out + UAT TestAmount override, so we
        //    don't reimplement any of that here.
        var monthlyInvoice = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.OrderId == order.Id
                     && i.Status != InvoiceStatus.Cancelled
                     && i.LineItems.Any(li => li.LineType == InvoiceLineItemType.ServicePackage))
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new { i.Id, i.InvoiceNumber, i.TotalAmount, i.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (monthlyInvoice is null)
        {
            result.NewServiceStatus = result.PreviousServiceStatus;
            result.Message = "No monthly service invoice exists yet for this order. Mark the installation completed first so the first monthly invoice is raised.";
            return Result<AdminActivateOrSettleResultDto>.Failure(ErrorCodes.CONFLICT, result.Message);
        }

        result.MonthlyInvoiceCreated = true;
        result.MonthlyInvoiceId      = monthlyInvoice.Id;
        result.MonthlyInvoiceNumber  = monthlyInvoice.InvoiceNumber;
        result.InvoiceAmount         = monthlyInvoice.TotalAmount;

        if (monthlyInvoice.Status == InvoiceStatus.Paid)
        {
            // Invoice already paid — just escalate the service state.
            // (AdminActivateServiceAsync handles the
            // PendingActivation → Active flip + dates + provisioning;
            // when the flag is false the customer hasn't reached
            // PendingActivation in the first place though, so the
            // service is already Active and we land in the idempotent
            // branch above.)
            if (order.Status == OrderStatus.PendingPayment)
            {
                // Defensive: invoice Paid but order didn't flip. Re-run
                // the canonical path — applier branches on the flag.
                _logger.LogWarning(
                    "[ActivateOrSettle] order={OrderNumber} invoice {InvoiceNumber} is Paid but order is still PendingPayment — re-running auto-billing to nudge the lifecycle.",
                    order.OrderNumber, monthlyInvoice.InvoiceNumber);
            }
        }

        var charge = await _autoBilling.ChargeInvoiceAsync(
            monthlyInvoice.Id, AutoBillingChargeSource.AdminManual, cancellationToken);

        result.AutoBillingAttempted = true;
        if (charge.IsSuccess && charge.Data?.Charged == true)
        {
            result.AutoBillingSucceeded = true;
            // Pull the actual provider-charged amount from the payment row
            // (override-aware) for the admin's confirmation modal.
            if (charge.Data.PaymentId is Guid chargedPaymentId)
            {
                var pay = await _dbContext.Payments
                    .AsNoTracking()
                    .Where(p => p.Id == chargedPaymentId)
                    .Select(p => new {
                        p.Amount,
                        p.IsTestAmountOverrideApplied,
                        p.ActualProviderAmount,
                    })
                    .FirstOrDefaultAsync(cancellationToken);
                if (pay is not null)
                {
                    result.ProviderAmount = pay.IsTestAmountOverrideApplied
                        ? (pay.ActualProviderAmount ?? pay.Amount)
                        : pay.Amount;
                }
            }
        }
        else
        {
            result.AutoBillingSucceeded = false;
            result.FailureReason        = charge.Data?.FailureReason
                                       ?? charge.Message
                                       ?? "Auto-debit did not complete.";
        }

        // Re-read the order so we can report the resulting service
        // status — PaymentApplierService might have flipped it in the
        // same call.
        var refreshedOrderStatus = await _dbContext.Orders
            .AsNoTracking()
            .Where(o => o.Id == order.Id)
            .Select(o => o.Status)
            .FirstOrDefaultAsync(cancellationToken);
        var refreshedAccountStatus = await _dbContext.NetworkAccounts
            .AsNoTracking()
            .Where(n => n.Id == account.Id)
            .Select(n => n.Status)
            .FirstOrDefaultAsync(cancellationToken);
        result.NewServiceStatus = LifecycleLabel(refreshedOrderStatus, refreshedAccountStatus);

        var manualOpenserve = _activationSettings.Value.RequireManualOpenserveActivation;
        result.Message = result.AutoBillingSucceeded
            ? (manualOpenserve
                ? "Payment successful. Service is now Pending Activation — complete the Openserve activation to finish."
                : "Payment successful. Service is now Active.")
            : "Auto-debit failed. A service invoice has been issued to the customer — they can still pay it manually.";

        return Result<AdminActivateOrSettleResultDto>.Success(result, result.Message);
    }

    private static string LifecycleLabel(OrderStatus orderStatus, NetworkAccountStatus accountStatus)
    {
        return accountStatus switch
        {
            NetworkAccountStatus.Suspended  => "Suspended",
            NetworkAccountStatus.Terminated => "Terminated",
            NetworkAccountStatus.Failed     => "Activation Failed",
            NetworkAccountStatus.Active     => "Active",
            _ => orderStatus switch
            {
                OrderStatus.Active             => "Active",
                OrderStatus.PendingActivation  => "Pending Activation",
                OrderStatus.PendingPayment     => "Pending Payment",
                _                              => "Pending Installation",
            },
        };
    }
}
