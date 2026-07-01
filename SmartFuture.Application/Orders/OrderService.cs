using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Communication.Email.Templates;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Installations;
using SmartFuture.Application.Installations.Dtos;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.Billing.ProRata;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.CoverageRequests;
using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Orders;

public class OrderService : IOrderService
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly OrderStatus[] CustomerCancellableStatuses =
    {
        OrderStatus.Draft,
        OrderStatus.Submitted,
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment
    };

    // Excludes 0, O, 1, I, L to avoid order-number ambiguity.
    private const string OrderNumberAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int OrderNumberSuffixLength = 6;
    private const int OrderNumberMaxAttempts = 5;

    private static readonly OrderStatus[] OrderStatusesThatTerminateNetwork =
    {
        OrderStatus.Cancelled,
        OrderStatus.Failed,
        OrderStatus.Rejected
    };

    // Phase 51 — any Order in one of these statuses blocks the same
    // customer from creating a *new* order. Mirror of the inverse:
    // Cancelled / Failed / Rejected are the only terminal statuses, so
    // everything else still "owns" a service slot for the customer.
    private static readonly OrderStatus[] NonTerminalOrderStatuses =
    {
        OrderStatus.Draft,
        OrderStatus.Submitted,
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment,
        OrderStatus.PaymentReceived,
        OrderStatus.Provisioning,
        OrderStatus.Active
    };

    // Order statuses for which auto-scheduling can create an
    // Installation row. Mirrors `InstallationCreatableOrderStatuses` in
    // InstallationService so the auto-create hook fails fast when the
    // order isn't yet in a confirm-or-later state.
    private static readonly OrderStatus[] InstallationSchedulableStatuses =
    {
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment,
        OrderStatus.PaymentReceived,
        OrderStatus.Provisioning,
        OrderStatus.Active
    };

    private static readonly InstallationStatus[] InstallationActiveStatuses =
    {
        InstallationStatus.PendingScheduling,
        InstallationStatus.Scheduled,
        InstallationStatus.TechnicianAssigned,
        InstallationStatus.EnRoute,
        InstallationStatus.OnSite,
        InstallationStatus.Rescheduled
    };

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly INotificationService _notificationService;
    private readonly INetworkAccountService _networkAccountService;
    private readonly IInstallationService _installationService;
    private readonly ICoverageCheckService _coverageCheckService;
    private readonly PaymentSettings _paymentSettings;
    private readonly BillingSettings _billingSettings;
    private readonly ILogger<OrderService> _logger;

    public OrderService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, INotificationService notificationService, INetworkAccountService networkAccountService,
        IInstallationService installationService, ICoverageCheckService coverageCheckService, IOptions<PaymentSettings> paymentSettings, IOptions<BillingSettings> billingSettings, ILogger<OrderService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _notificationService = notificationService;
        _networkAccountService = networkAccountService;
        _installationService = installationService;
        _coverageCheckService = coverageCheckService;
        _paymentSettings = paymentSettings.Value;
        _billingSettings = billingSettings.Value;
        _logger = logger;
    }

    // Fibre pro-rata generator invoked by AdminActivateServiceAsync.
    // Idempotent via Order.FirstProRataInvoiceGeneratedAtUtc — a repeat
    // activation call, or a Security order whose pro-rata was already
    // charged at checkout, becomes a no-op. Returns the created invoice
    // for logging or `null` when skipped.
    private async Task<Invoice?> TryGenerateFibreProRataInvoiceAsync(
        Order order, DateTime activationDate, CancellationToken cancellationToken)
    {
        // Idempotency stamp — already invoiced this order's first
        // pro-rata (either here on a prior activation, or at intent
        // conversion for a Security package).
        if (order.FirstProRataInvoiceGeneratedAtUtc.HasValue) return null;

        // Only run for product lines where the service fee starts AFTER
        // activation. Security packages had pro-rata included at checkout.
        if (_billingSettings.ChargeProRataAtCheckout(order.PackageType)) return null;

        var quote = ProRataCalculator.Quote(order.PackagePrice, activationDate, order.PreferredBillingDay);
        if (quote.BillableDays <= 0 || quote.ProRataAmount <= 0m) return null; // customer activated on billing day

        var now = DateTime.UtcNow;
        var stamp = now.ToString("yyyyMMdd");
        var shortId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var invoice = new Invoice
        {
            InvoiceNumber = $"INV-{stamp}-{shortId}",
            OrderId = order.Id,
            Status = InvoiceStatus.Issued,
            SubtotalAmount = quote.ProRataAmount,
            TotalAmount = quote.ProRataAmount,
            BalanceDue = quote.ProRataAmount,
            AmountPaid = 0m,
            CurrencyCode = "ZAR",
            IssuedAtUtc = now,
            DueAtUtc = quote.NextBillingDateUtc, // due on customer's next billing day
            PeriodStartUtc = quote.StartDateUtc,
            PeriodEndUtc = quote.NextBillingDateUtc,
            LastStatusChangedByUserId = _currentUser.UserId,
            Notes = "First pro-rata invoice generated on service activation.",
        };
        invoice.LineItems.Add(new InvoiceLineItem
        {
            Invoice = invoice,
            LineType = InvoiceLineItemType.ProRata,
            Description = ProRataCalculator.FormatProRataDescription(quote.StartDateUtc, quote.NextBillingDateUtc),
            Quantity = 1,
            UnitAmount = quote.ProRataAmount,
            TotalAmount = quote.ProRataAmount,
            SortOrder = 0,
        });
        _dbContext.Invoices.Add(invoice);

        order.FirstProRataInvoiceGeneratedAtUtc = now;

        _logger.LogInformation(
            "[ProRata][ActivationInvoice] {OrderNumber} generated {Amount} for {Days} days ({Start:d} → {End:d}, billingDay={BillingDay}).",
            order.OrderNumber, quote.ProRataAmount, quote.BillableDays,
            quote.StartDateUtc, quote.NextBillingDateUtc, order.PreferredBillingDay);

        return invoice;
    }

    public async Task<Result<PagedResult<OrderDto>>> SearchAdminAsync(OrderFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new OrderFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching orders (admin)");
            return Result<PagedResult<OrderDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching orders.");
        }
    }

    public async Task<Result<PagedResult<OrderDto>>> GetMineAsync(OrderFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<OrderDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new OrderFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching orders (customer)");
            return Result<PagedResult<OrderDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your orders.");
        }
    }

    public Task<Result<OrderDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<OrderDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<OrderDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    private async Task<Result<OrderDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");

            var query = _dbContext.Orders
                .AsNoTracking()
                .Include(o => o.LastStatusChangedByUser)
                .Where(o => o.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(o => o.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            if (entity is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            var dto = MapToDto(entity);
            dto.Payment = await ResolvePaymentSummaryAsync(entity.Id, cancellationToken);
            dto.Installation = await ResolveInstallationSummaryAsync(entity.Id, cancellationToken);
            dto.Service = await ResolveServiceSummaryAsync(entity.Id, cancellationToken);
            return Result<OrderDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching order {Id}", id);
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the order.");
        }
    }

    // Phase 39 — pick the most recent non-terminal Installation for this
    // order so the admin Order detail page can deep-link into it. Falls
    // back to the most recent terminal one if no active installation
    // exists (e.g. a Completed install for an already-Active order).
    private async Task<OrderInstallationSummaryDto?> ResolveInstallationSummaryAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var installation = await _dbContext.Installations
            .AsNoTracking()
            .Where(i => i.OrderId == orderId)
            .OrderByDescending(i => InstallationActiveStatuses.Contains(i.Status) ? 1 : 0)
            .ThenByDescending(i => i.ScheduledForUtc ?? i.CreatedAtUtc)
            .Select(i => new OrderInstallationSummaryDto
            {
                Id                 = i.Id,
                InstallationNumber = i.InstallationNumber,
                Status             = i.Status,
                ScheduledForUtc    = i.ScheduledForUtc,
                CompletedAtUtc     = i.CompletedAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        return installation;
    }

    // Linked-service summary so admin/customer order detail pages can
    // render a "View Service" link without firing a second round-trip.
    // Mirrors ResolveInstallationSummaryAsync — detail/create endpoints
    // call this; the paged-list projection skips it to avoid an N+1
    // join (and the list rows don't need a service deep-link anyway).
    private async Task<OrderServiceSummaryDto?> ResolveServiceSummaryAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var na = await _dbContext.NetworkAccounts
            .AsNoTracking()
            .Where(n => n.OrderId == orderId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Select(n => new { n.Id, n.AccountNumber, n.Status, OrderStatus = (OrderStatus?)n.Order!.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (na is null) return null;

        return new OrderServiceSummaryDto
        {
            Id            = na.Id,
            AccountNumber = na.AccountNumber,
            Status        = na.Status,
            DisplayStatus = NetworkAccountService.ResolveDisplayStatus(na.Status, na.OrderStatus)
        };
    }

    // Phase 39 — best-effort: when admin sets/updates the expected
    // installation date AND the order has reached Confirmed-or-later,
    // ensure a non-terminal Installation row exists for the order with
    // that schedule. Idempotent — re-uses an existing active row if one
    // is on file, otherwise calls IInstallationService.CreateAsync.
    // Failures (e.g. order not yet confirmed) are swallowed and logged
    // so the order update itself still succeeds — the admin keeps
    // ownership of when to actually transition the order status.
    private async Task EnsureInstallationScheduledAsync(Order order, DateTime? scheduledForUtc, CancellationToken cancellationToken)
    {
        if (order is null || !scheduledForUtc.HasValue) return;
        if (!InstallationSchedulableStatuses.Contains(order.Status))
        {
            _logger.LogInformation(
                "Skipping installation auto-schedule for order {OrderId}: status {Status} is not creatable.",
                order.Id, order.Status);
            return;
        }

        var existing = await _dbContext.Installations
            .Where(i => i.OrderId == order.Id && InstallationActiveStatuses.Contains(i.Status))
            .OrderByDescending(i => i.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            if (existing.ScheduledForUtc != scheduledForUtc.Value)
            {
                existing.RescheduledFromUtc = existing.ScheduledForUtc;
                existing.ScheduledForUtc    = scheduledForUtc.Value;
                if (existing.Status == InstallationStatus.PendingScheduling)
                    existing.Status = InstallationStatus.Scheduled;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        var createResult = await _installationService.CreateAsync(new CreateInstallationRequestDto
        {
            OrderId         = order.Id,
            ScheduledForUtc = scheduledForUtc.Value,
            AdminNotes      = "Auto-scheduled from admin order update."
        }, cancellationToken);

        if (!createResult.IsSuccess)
        {
            _logger.LogWarning(
                "Auto-creating installation for order {OrderId} failed: {Code} {Message}",
                order.Id, createResult.Code, createResult.Message);
        }
    }

    // Phase 46 — date-less companion of EnsureInstallationScheduledAsync.
    // Fires on every transition into an install-eligible Order status
    // (Confirmed / AwaitingPayment / PaymentReceived / Provisioning /
    // Active) so the Portal's "Update Status → Installation Scheduled"
    // action — which doesn't carry a date in the form — still produces
    // an Installation row. Created in PendingScheduling so the admin
    // can pick a date afterwards via "Set Install Date". Idempotent:
    // skips when an active installation already exists for the order,
    // so it composes cleanly with EnsureInstallationScheduledAsync.
    private async Task EnsureInstallationExistsAsync(Order order, CancellationToken cancellationToken)
    {
        if (order is null) return;
        if (!InstallationSchedulableStatuses.Contains(order.Status)) return;

        var existing = await _dbContext.Installations
            .AnyAsync(i => i.OrderId == order.Id
                        && InstallationActiveStatuses.Contains(i.Status), cancellationToken);
        if (existing) return;

        var createResult = await _installationService.CreateAsync(new CreateInstallationRequestDto
        {
            OrderId    = order.Id,
            AdminNotes = "Auto-created from admin order status change."
        }, cancellationToken);

        if (!createResult.IsSuccess)
        {
            _logger.LogWarning(
                "Auto-creating installation (status-driven) for order {OrderId} failed: {Code} {Message}",
                order.Id, createResult.Code, createResult.Message);
        }
    }

    // Phase 46 — cancel the active Installation (if any) when admin
    // terminates the Order. Direct entity write so we don't recurse
    // back into InstallationService.AdminUpdateStatusAsync (which would
    // try to mutate this same Order). Best-effort: failure is logged
    // and the order's own termination still stands.
    private async Task TryCancelActiveInstallationForOrderAsync(Order order, string reason, CancellationToken cancellationToken)
    {
        if (order is null) return;

        try
        {
            var active = await _dbContext.Installations
                .Where(i => i.OrderId == order.Id && InstallationActiveStatuses.Contains(i.Status))
                .OrderByDescending(i => i.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (active is null) return;

            var now = DateTime.UtcNow;
            active.Status                    = InstallationStatus.Cancelled;
            active.CancelledAtUtc            = active.CancelledAtUtc ?? now;
            active.CancellationReason        = Trim(reason) ?? active.CancellationReason;
            active.LastStatusChangedByUserId = _currentUser.UserId;
            active.UpdatedAtUtc              = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Cancel-active-installation hook threw for order {OrderNumber}.", order.OrderNumber);
        }
    }

    // Resolve the customer-safe payment summary for an order. Prefers
    // the most recent Completed payment on the latest Invoice; falls
    // back to the latest payment regardless of status (e.g. Pending or
    // Failed) so the UI can still surface "Method: Ozow, Status:
    // Failed". Returns null when no Invoice/Payment exists yet.
    private async Task<OrderPaymentSummaryDto?> ResolvePaymentSummaryAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // Most recent invoice for the order — mock-checkout creates one
        // per checkout; admin can issue additional invoices later. We
        // pick the latest so the panel reflects the freshest activity.
        var invoice = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.OrderId == orderId)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new { i.Id, i.InvoiceNumber, i.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (invoice is null) return null;

        // Most recent Completed payment for that invoice; fall back to
        // any latest payment so the UI still shows pending/failed.
        var payment = await _dbContext.Payments
            .AsNoTracking()
            .Where(p => p.InvoiceId == invoice.Id)
            .OrderByDescending(p => p.Status == PaymentStatus.Completed ? 1 : 0)
            .ThenByDescending(p => p.PaidAtUtc ?? p.CreatedAtUtc)
            .Select(p => new OrderPaymentSummaryDto
            {
                PaymentId = p.Id,
                PaymentNumber = p.PaymentNumber,
                Status = p.Status,
                Method = p.Method,
                Amount = p.Amount,
                CurrencyCode = p.CurrencyCode,
                PaidAtUtc = p.PaidAtUtc,
                GatewayName = p.GatewayName,
                GatewayReference = p.GatewayReference,
                InvoiceId = invoice.Id,
                InvoiceNumber = invoice.InvoiceNumber,
                InvoiceStatus = invoice.Status,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Invoice exists but no payment yet — surface invoice-only
        // metadata so the UI can still link to it and show the
        // invoice's status.
        return payment ?? new OrderPaymentSummaryDto
        {
            InvoiceId = invoice.Id,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceStatus = invoice.Status,
            Status = PaymentStatus.Pending,
            Method = PaymentMethodType.Other,
            Amount = 0m,
            CurrencyCode = "ZAR",
        };
    }

    public async Task<Result<OrderDto>> CreateMineAsync(CreateOrderRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateAddressAndContact(
                request.AddressLine1, request.Latitude, request.Longitude,
                request.Email, request.PhoneNumber, expectedInstallationDateUtc: null);
            if (validation is not null) return validation;

            if (request.ServicePackageId == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "ServicePackageId is required.");

            var package = await _dbContext.ServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.ServicePackageId, cancellationToken);

            if (package is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced service package was not found.");

            if (package.Status != ServicePackageStatus.Active)
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Referenced service package is not active and cannot be ordered.");

            // Phase 51 — one-active-order rule, now category-aware. A
            // customer can hold one open order per category (Fibre and
            // Security), so an active Fibre service does NOT block a new
            // Security order and vice versa. Two open Fibre orders, or
            // two open Security orders, are still rejected.
            var eligibility = await ComputeEligibilityAsync(
                currentUserId.Value, package.Type, cancellationToken);
            if (!eligibility.CanCreateOrder)
            {
                return Result<OrderDto>.Failure(
                    ErrorCodes.ORDER_ALREADY_IN_PROGRESS,
                    eligibility.Message ?? "You already have an order in progress.");
            }

            if (request.CoverageRequestId.HasValue)
            {
                var coverageGuard = await ValidateCoverageRequestAsync(
                    request.CoverageRequestId.Value, currentUserId.Value, package, cancellationToken);
                if (coverageGuard is not null) return coverageGuard;
            }

            // Go-live alignment — customer orders MUST come from a
            // coverage-confirmed address. Acceptable proofs (in order):
            //   1. Latitude + Longitude set (came from Google Places
            //      autocomplete + /api/coverage/check).
            //   2. A confirmed CoverageRequestId (admin-marked Available
            //      and bound to this user — already validated above).
            // Anything else is rejected — a free-text-only customer
            // submission can't be acted on for service activation.
            //
            // Security packages are exempt — there's no upstream coverage
            // grid for CCTV; fulfilment is a manual on-site install once
            // the customer confirms an address. The address fields above
            // are still validated, lat/lng / coverage-request are not
            // required.
            var hasGeoCoordinates = request.Latitude.HasValue && request.Longitude.HasValue;
            var hasConfirmedCoverageRequest = request.CoverageRequestId.HasValue;
            var skipCoverageGate = package.Type == ServicePackageType.Security;
            if (!skipCoverageGate && !hasGeoCoordinates && !hasConfirmedCoverageRequest)
            {
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Please confirm coverage for your installation address before placing an order.");
            }

            var customerProfileId = await _dbContext.CustomerProfiles
                .Where(p => p.UserId == currentUserId.Value)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(cancellationToken);

            var now = DateTime.UtcNow;

            var entity = new Order
            {
                UserId = currentUserId.Value,
                CustomerProfileId = customerProfileId,
                ServicePackageId = package.Id,
                CoverageRequestId = request.CoverageRequestId,
                Status = OrderStatus.Submitted,
                Source = OrderSource.CustomerApp,
                SubmittedAtUtc = now,
                LastStatusChangedByUserId = currentUserId,

                PackageName = package.Name,
                PackageType = package.Type,
                PackageSpeedLabel = package.SpeedLabel,
                PackageDataAllowanceLabel = package.DataAllowanceLabel,
                PackageIsUncapped = package.IsUncapped,
                PackagePrice = package.Price,
                PackageBillingCycle = package.BillingCycle,
                PackageContractMonths = package.ContractMonths,
                PackageHasFreeInstallation = package.HasFreeInstallation,
                PackageInstallationFee = package.InstallationFee,
                PackageIncludesRouter = package.IncludesRouter,

                FullName = Trim(request.FullName),
                Email = Trim(request.Email),
                PhoneNumber = Trim(request.PhoneNumber),
                AddressLine1 = request.AddressLine1.Trim(),
                AddressLine2 = Trim(request.AddressLine2),
                Suburb = Trim(request.Suburb),
                City = Trim(request.City),
                Province = Trim(request.Province),
                PostalCode = Trim(request.PostalCode),
                Country = Trim(request.Country),
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                GooglePlaceId = Trim(request.GooglePlaceId),
                MapProviderReference = Trim(request.MapProviderReference),
                CustomerNotes = Trim(request.CustomerNotes),
                // Phase 44 — capture the customer's preferred date as
                // an immutable "requested" value; admin scheduling
                // writes to ExpectedInstallationDateUtc separately.
                RequestedInstallationDateUtc = request.RequestedInstallationDateUtc,
                // Customer-selectable billing day for recurring cadence.
                // Legacy callers (mobile pre-picker) omit it — fall back
                // to the entity default (30). Validation of "must be an
                // enabled option" happens at the intent path; the
                // freestyle POST /api/orders path accepts any 1..31.
                PreferredBillingDay = request.PreferredBillingDay ?? 30,
            };

            var orderNumber = await GenerateUniqueOrderNumberAsync(now, cancellationToken);
            if (orderNumber is null)
                return Result<OrderDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Could not generate a unique order number. Please retry.");

            entity.OrderNumber = orderNumber;

            _dbContext.Orders.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.OrderCreated,
                AuditActorType.User,
                entity,
                summary: $"Order submitted: {entity.OrderNumber} ({entity.PackageName})",
                metadata: BuildMetadata(new
                {
                    servicePackageId = entity.ServicePackageId,
                    coverageRequestId = entity.CoverageRequestId,
                    packageName = entity.PackageName,
                    packagePrice = entity.PackagePrice
                }));

            // Optional mock-checkout invoice + payment persistence.
            // Gated by `PaymentSettings:MockCheckoutEnabled` (UAT only),
            // and only fires when the client sent the Ozow mock-checkout
            // hint. Service activation is unchanged: the order stays
            // Submitted and admin still owns activation.
            var mockCheckoutProvider = request.MockCheckoutPaymentProvider;
            var mockCheckoutRequested = string.Equals(
                mockCheckoutProvider, "Ozow", StringComparison.OrdinalIgnoreCase);
            var hasMockCheckoutReference = !string.IsNullOrWhiteSpace(request.MockCheckoutPaymentReference);
            var mockCheckoutAttempted = _paymentSettings.MockCheckoutEnabled && mockCheckoutRequested;

            // Structured gate trace. Non-secret: we mask the reference
            // (it's a customer-portal-generated OZOW-MOCK-… ID anyway,
            // not a real payment token, but masking keeps logs tidy).
            _logger.LogInformation(
                "MockCheckout gate for {OrderNumber}: provider='{Provider}', hasReference={HasReference}, " +
                "MockCheckoutEnabled={Enabled}, requested={Requested}, attempted={Attempted}",
                entity.OrderNumber,
                mockCheckoutProvider ?? "(null)",
                hasMockCheckoutReference,
                _paymentSettings.MockCheckoutEnabled,
                mockCheckoutRequested,
                mockCheckoutAttempted);

            var mockCheckoutPersisted = false;
            if (mockCheckoutAttempted)
            {
                mockCheckoutPersisted = await PersistMockCheckoutAsync(entity, request, now, cancellationToken);

                // Phase 44 — mock-checkout completed the payment server-
                // side, which is our trigger to reserve a Pending
                // NetworkAccount. Best-effort; if it fails the order
                // (and the billing pair) still stand and the admin can
                // re-trigger provisioning later.
                if (mockCheckoutPersisted)
                {
                    try
                    {
                        await _networkAccountService.EnsurePendingForOrderAsync(
                            entity.Id, NetworkAccountSource.SystemAutomated, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Pending network-account reservation hook threw for order {OrderNumber}.",
                            entity.OrderNumber);
                    }
                }
            }
            else if (mockCheckoutRequested)
            {
                // Client asked for mock checkout but the environment has
                // it disabled (production-safe default). Log a single
                // structured warning so UAT operators can spot a missing
                // `PaymentSettings__MockCheckoutEnabled=true` env var.
                // No customer-visible warning — the order itself is
                // valid and admin-driven billing flow still works.
                _logger.LogWarning(
                    "Order {OrderNumber} included mock-checkout fields but PaymentSettings:MockCheckoutEnabled is false. " +
                    "Invoice + payment were NOT created. Set the env var to true in UAT to persist mock billing.",
                    entity.OrderNumber);
            }
            else
            {
                // Phase 52 — no mock-checkout requested. Mint an Issued
                // (unpaid) invoice for the installation fee so the
                // customer can settle it via Ozow (or any other real
                // gateway) using PaymentGatewayService.InitiateInvoicePaymentAsync.
                // Free-installation packages skip this — the first
                // monthly invoice is still raised when the install
                // completes.
                var installationFee = entity.PackageHasFreeInstallation ? 0m : (entity.PackageInstallationFee ?? 0m);
                if (installationFee > 0m)
                {
                    await PersistIssuedInstallationInvoiceAsync(entity, installationFee, now, cancellationToken);
                }
            }

            // Order-confirmation email. Template owns subject + body
            // composition; only failure is logged (email failure must
            // not unwind a successfully-created order). Includes the
            // payment summary inline when mock checkout persisted one,
            // so a single email covers both the order and its receipt.
            var orderEmail = OrderEmailTemplates.OrderSubmitted(new OrderEmailTemplates.OrderSubmittedModel
            {
                CustomerFirstName = FirstWord(entity.FullName),
                CustomerFullName = entity.FullName ?? string.Empty,
                OrderNumber = entity.OrderNumber,
                PackageName = entity.PackageName,
                SpeedLabel = entity.PackageSpeedLabel,
                DataAllowanceLabel = entity.PackageDataAllowanceLabel,
                IsUncapped = entity.PackageIsUncapped,
                PackagePrice = entity.PackagePrice,
                InstallationFee = entity.PackageInstallationFee,
                HasFreeInstallation = entity.PackageHasFreeInstallation,
                AddressLine1 = entity.AddressLine1,
                Suburb = entity.Suburb,
                City = entity.City,
                Province = entity.Province,
                PostalCode = entity.PostalCode,
                OrderStatusLabel = entity.Status.ToString(),
                PaymentProvider = mockCheckoutPersisted ? "Ozow" : null,
                PaymentReference = mockCheckoutPersisted ? request.MockCheckoutPaymentReference : null,
                PaymentAmount = mockCheckoutPersisted ? entity.PackagePrice + (entity.PackageHasFreeInstallation ? 0m : (entity.PackageInstallationFee ?? 0m)) : null,
            });
            await TryNotifyAsync(
                userId: entity.UserId,
                type: NotificationType.OrderCreated,
                email: entity.Email,
                phone: entity.PhoneNumber,
                subject: orderEmail.Subject,
                body: orderEmail.PlainTextBody,
                htmlBody: orderEmail.HtmlBody,
                senderType: orderEmail.SenderType,
                relatedEntityType: nameof(Order),
                relatedEntityId: entity.Id,
                cancellationToken: cancellationToken);

            // When mock checkout was requested but persistence failed, the
            // order is still valid — but surface a warning so the client
            // UI can show "your order went through, but we couldn't record
            // the payment yet, billing will catch up". The detection of
            // "checkout attempted but not persisted" lets us avoid
            // claiming the payment succeeded when it didn't.
            var successMessage = mockCheckoutAttempted && !mockCheckoutPersisted
                ? "Order submitted. The payment record couldn't be saved automatically — our billing team will reconcile this shortly."
                : "Order submitted.";

            var reloaded = await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity;
            var responseDto = MapToDto(reloaded);
            // Mock-checkout persistence (Phase 31) creates Invoice +
            // Payment inside the same call, so the create response can
            // already surface them — no extra round-trip needed from
            // the client just to learn "method: Ozow".
            responseDto.Payment = await ResolvePaymentSummaryAsync(entity.Id, cancellationToken);
            responseDto.Installation = await ResolveInstallationSummaryAsync(entity.Id, cancellationToken);
            responseDto.Service = await ResolveServiceSummaryAsync(entity.Id, cancellationToken);

            return Result<OrderDto>.Success(responseDto, successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating order");
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the order.");
        }
    }

    // ── Free-activation order placement ──────────────────────────────────
    //
    // For packages whose activation once-off fee is waived
    // (ServicePackage.HasFreeInstallation == true). There is NO payment:
    // no gateway, no invoice, no Payment row. We run the exact same
    // validation a paid order does (address/contact, package Active,
    // category-aware eligibility, coverage gate with Security exempt) and
    // then create the Order + a pending NetworkAccount — the same activation
    // handoff a paid order reaches AFTER its payment settles. The order
    // lands in PaymentReceived (the "successful, ready-to-install" state)
    // so it flows through the identical installation/admin pipeline as a
    // paid order; the difference is purely the absence of billing rows.
    public async Task<Result<OrderDto>> CreateFreeActivationMineAsync(CreateOrderRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateAddressAndContact(
                request.AddressLine1, request.Latitude, request.Longitude,
                request.Email, request.PhoneNumber, expectedInstallationDateUtc: null);
            if (validation is not null) return validation;

            if (request.ServicePackageId == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "ServicePackageId is required.");

            var package = await _dbContext.ServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.ServicePackageId, cancellationToken);

            if (package is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced service package was not found.");

            if (package.Status != ServicePackageStatus.Active)
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Referenced service package is not active and cannot be ordered.");

            // The free endpoint is ONLY for free-activation packages. A paid
            // package must go through the normal gateway checkout — reject
            // with CONFLICT so the client can fall back cleanly.
            if (!package.HasFreeInstallation)
                return Result<OrderDto>.Failure(
                    ErrorCodes.CONFLICT,
                    "This package requires a paid activation fee. Please use the standard checkout to pay.");

            // Category-aware one-active-order rule (Fibre blocks Fibre,
            // Security blocks Security; cross-category is allowed).
            var eligibility = await ComputeEligibilityAsync(
                currentUserId.Value, package.Type, cancellationToken);
            if (!eligibility.CanCreateOrder)
                return Result<OrderDto>.Failure(
                    ErrorCodes.ORDER_ALREADY_IN_PROGRESS,
                    eligibility.Message ?? "You already have an order in progress.");

            if (request.CoverageRequestId.HasValue)
            {
                var coverageGuard = await ValidateCoverageRequestAsync(
                    request.CoverageRequestId.Value, currentUserId.Value, package, cancellationToken);
                if (coverageGuard is not null) return coverageGuard;
            }

            // Same coverage gate as paid orders: Fibre needs a coverage-
            // confirmed address (lat/lng or confirmed CoverageRequestId);
            // Security is exempt (manual on-site install).
            var hasGeoCoordinates = request.Latitude.HasValue && request.Longitude.HasValue;
            var hasConfirmedCoverageRequest = request.CoverageRequestId.HasValue;
            var skipCoverageGate = package.Type == ServicePackageType.Security;
            if (!skipCoverageGate && !hasGeoCoordinates && !hasConfirmedCoverageRequest)
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Please confirm coverage for your installation address before placing an order.");

            var customerProfileId = await _dbContext.CustomerProfiles
                .Where(p => p.UserId == currentUserId.Value)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(cancellationToken);

            var now = DateTime.UtcNow;

            var entity = new Order
            {
                UserId = currentUserId.Value,
                CustomerProfileId = customerProfileId,
                ServicePackageId = package.Id,
                CoverageRequestId = request.CoverageRequestId,
                // Free activation skips payment entirely — land directly in
                // the "successful, ready-to-install" state a paid order
                // reaches after settlement (NOT PendingPayment).
                Status = OrderStatus.PaymentReceived,
                Source = OrderSource.CustomerApp,
                SubmittedAtUtc = now,
                LastStatusChangedByUserId = currentUserId,

                PackageName = package.Name,
                PackageType = package.Type,
                PackageSpeedLabel = package.SpeedLabel,
                PackageDataAllowanceLabel = package.DataAllowanceLabel,
                PackageIsUncapped = package.IsUncapped,
                PackagePrice = package.Price,
                PackageBillingCycle = package.BillingCycle,
                PackageContractMonths = package.ContractMonths,
                PackageHasFreeInstallation = package.HasFreeInstallation,
                PackageInstallationFee = package.InstallationFee,
                PackageIncludesRouter = package.IncludesRouter,

                FullName = Trim(request.FullName),
                Email = Trim(request.Email),
                PhoneNumber = Trim(request.PhoneNumber),
                AddressLine1 = request.AddressLine1.Trim(),
                AddressLine2 = Trim(request.AddressLine2),
                Suburb = Trim(request.Suburb),
                City = Trim(request.City),
                Province = Trim(request.Province),
                PostalCode = Trim(request.PostalCode),
                Country = Trim(request.Country),
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                GooglePlaceId = Trim(request.GooglePlaceId),
                MapProviderReference = Trim(request.MapProviderReference),
                CustomerNotes = Trim(request.CustomerNotes),
                RequestedInstallationDateUtc = request.RequestedInstallationDateUtc
            };

            var orderNumber = await GenerateUniqueOrderNumberAsync(now, cancellationToken);
            if (orderNumber is null)
                return Result<OrderDto>.Failure(
                    ErrorCodes.EXCEPTION, "Could not generate a unique order number. Please retry.");
            entity.OrderNumber = orderNumber;

            _dbContext.Orders.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.OrderCreated,
                AuditActorType.User,
                entity,
                summary: $"Free-activation order placed: {entity.OrderNumber} ({entity.PackageName})",
                metadata: BuildMetadata(new
                {
                    servicePackageId = entity.ServicePackageId,
                    packageName = entity.PackageName,
                    freeActivation = true
                }));

            // Reserve the pending NetworkAccount — same handoff a paid order
            // gets via the payment applier's post-settlement hook. Best-
            // effort: the order already stands if this throws, and admin can
            // re-trigger provisioning.
            try
            {
                await _networkAccountService.EnsurePendingForOrderAsync(
                    entity.Id, NetworkAccountSource.SystemAutomated, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Pending network-account reservation hook threw for free-activation order {OrderNumber}.",
                    entity.OrderNumber);
            }

            var orderEmail = OrderEmailTemplates.OrderSubmitted(new OrderEmailTemplates.OrderSubmittedModel
            {
                CustomerFirstName = FirstWord(entity.FullName),
                CustomerFullName = entity.FullName ?? string.Empty,
                OrderNumber = entity.OrderNumber,
                PackageName = entity.PackageName,
                SpeedLabel = entity.PackageSpeedLabel,
                DataAllowanceLabel = entity.PackageDataAllowanceLabel,
                IsUncapped = entity.PackageIsUncapped,
                PackagePrice = entity.PackagePrice,
                InstallationFee = entity.PackageInstallationFee,
                HasFreeInstallation = entity.PackageHasFreeInstallation,
                AddressLine1 = entity.AddressLine1,
                Suburb = entity.Suburb,
                City = entity.City,
                Province = entity.Province,
                PostalCode = entity.PostalCode,
                OrderStatusLabel = entity.Status.ToString(),
            });
            await TryNotifyAsync(
                userId: entity.UserId,
                type: NotificationType.OrderCreated,
                email: entity.Email,
                phone: entity.PhoneNumber,
                subject: orderEmail.Subject,
                body: orderEmail.PlainTextBody,
                htmlBody: orderEmail.HtmlBody,
                senderType: orderEmail.SenderType,
                relatedEntityType: nameof(Order),
                relatedEntityId: entity.Id,
                cancellationToken: cancellationToken);

            var reloaded = await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity;
            var responseDto = MapToDto(reloaded);
            responseDto.Installation = await ResolveInstallationSummaryAsync(entity.Id, cancellationToken);
            responseDto.Service = await ResolveServiceSummaryAsync(entity.Id, cancellationToken);

            return Result<OrderDto>.Success(
                responseDto,
                "Your order has been placed. SmartFuture will contact you to complete activation.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating free-activation order");
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while placing the order.");
        }
    }

    public async Task<Result<OrderDto>> AdminUpdateAsync(Guid id, AdminUpdateOrderRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");

            if (request is null)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateAddressAndContact(
                request.AddressLine1, request.Latitude, request.Longitude,
                request.Email, request.PhoneNumber, request.ExpectedInstallationDateUtc);
            if (validation is not null) return validation;

            var entity = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

            if (entity is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            entity.FullName = Trim(request.FullName);
            entity.Email = Trim(request.Email);
            entity.PhoneNumber = Trim(request.PhoneNumber);
            entity.AddressLine1 = request.AddressLine1.Trim();
            entity.AddressLine2 = Trim(request.AddressLine2);
            entity.Suburb = Trim(request.Suburb);
            entity.City = Trim(request.City);
            entity.Province = Trim(request.Province);
            entity.PostalCode = Trim(request.PostalCode);
            entity.Country = Trim(request.Country);
            entity.Latitude = request.Latitude;
            entity.Longitude = request.Longitude;
            entity.GooglePlaceId = Trim(request.GooglePlaceId);
            entity.MapProviderReference = Trim(request.MapProviderReference);
            entity.ExpectedInstallationDateUtc = request.ExpectedInstallationDateUtc;
            entity.AdminNotes = Trim(request.AdminNotes);

            await _dbContext.SaveChangesAsync(cancellationToken);

            // Phase 39 — auto-create / re-schedule the matching
            // Installation row when admin sets the date. Best-effort:
            // the order update is the source of truth, the installation
            // hook only adds convenience.
            await EnsureInstallationScheduledAsync(entity, request.ExpectedInstallationDateUtc, cancellationToken);

            var reloaded = await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity;
            var dto = MapToDto(reloaded);
            dto.Payment = await ResolvePaymentSummaryAsync(entity.Id, cancellationToken);
            dto.Installation = await ResolveInstallationSummaryAsync(entity.Id, cancellationToken);
            dto.Service = await ResolveServiceSummaryAsync(entity.Id, cancellationToken);
            return Result<OrderDto>.Success(dto, "Order updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating order {Id}", id);
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the order.");
        }
    }

    // Phase 44 — dedicated narrow path so admins can set/clear the
    // scheduled installation date without re-supplying the entire
    // address payload. The previous flow forced clients to PUT the
    // full update DTO, which then tripped the address validators when
    // those fields came across blank.
    public async Task<Result<OrderDto>> AdminSetInstallationDateAsync(Guid id, AdminSetOrderInstallationDateDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");
            if (request is null)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.ExpectedInstallationDateUtc.HasValue
                && request.ExpectedInstallationDateUtc.Value < DateTime.UtcNow.Date)
            {
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Installation date cannot be in the past.");
            }

            var entity = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
            if (entity is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            entity.ExpectedInstallationDateUtc = request.ExpectedInstallationDateUtc;
            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            await _dbContext.SaveChangesAsync(cancellationToken);

            // Same Phase 39 auto-schedule semantics as AdminUpdateAsync:
            // creating/updating the linked Installation row when an
            // admin sets the date, so the Installations index reflects
            // it immediately.
            await EnsureInstallationScheduledAsync(entity, request.ExpectedInstallationDateUtc, cancellationToken);

            var reloaded = await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity;
            var dto = MapToDto(reloaded);
            dto.Payment = await ResolvePaymentSummaryAsync(entity.Id, cancellationToken);
            dto.Installation = await ResolveInstallationSummaryAsync(entity.Id, cancellationToken);
            dto.Service = await ResolveServiceSummaryAsync(entity.Id, cancellationToken);
            return Result<OrderDto>.Success(dto, "Installation date updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error setting installation date on order {Id}", id);
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while saving the installation date.");
        }
    }

    public async Task<Result<OrderDto>> AdminUpdateStatusAsync(Guid id, AdminUpdateOrderStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");

            if (request is null)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.ExpectedInstallationDateUtc.HasValue
                && request.ExpectedInstallationDateUtc.Value < DateTime.UtcNow.Date)
            {
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "ExpectedInstallationDateUtc cannot be in the past.");
            }

            var entity = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

            if (entity is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = request.Status;
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            if (request.ExpectedInstallationDateUtc.HasValue)
                entity.ExpectedInstallationDateUtc = request.ExpectedInstallationDateUtc;

            switch (request.Status)
            {
                case OrderStatus.Confirmed:
                    if (entity.ConfirmedAtUtc is null) entity.ConfirmedAtUtc = now;
                    break;
                case OrderStatus.Active:
                    if (entity.ActivatedAtUtc is null) entity.ActivatedAtUtc = now;
                    break;
                case OrderStatus.Cancelled:
                    if (entity.CancelledAtUtc is null) entity.CancelledAtUtc = now;
                    entity.CancellationReason = Trim(request.CancellationReason) ?? entity.CancellationReason;
                    break;
                case OrderStatus.Failed:
                    entity.FailureReason = Trim(request.FailureReason) ?? entity.FailureReason;
                    break;
                case OrderStatus.Rejected:
                    entity.RejectionReason = Trim(request.RejectionReason) ?? entity.RejectionReason;
                    break;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (previous != entity.Status)
            {
                await EmitAuditAsync(
                    AuditActionType.OrderStatusChanged,
                    AuditActorType.Admin,
                    entity,
                    summary: $"Order status changed: {previous} -> {entity.Status} ({entity.OrderNumber})",
                    metadata: BuildMetadata(new { previous, newStatus = entity.Status }));
            }

            if (previous != entity.Status && OrderStatusesThatTerminateNetwork.Contains(entity.Status))
            {
                await TryTerminateNetworkForOrderAsync(
                    entity.Id, entity.OrderNumber, entity.Status, NetworkAccountSource.AdminManual, cancellationToken);
            }

            // Phase 44 — admin moved the order into a paid/active state.
            // Reserve a Pending NetworkAccount so it surfaces under
            // /admin/client-services and /client/services straight
            // away; installation completion later flips it to Active.
            // Direct Activated transitions also activate the service.
            if (previous != entity.Status
                && (entity.Status == OrderStatus.PaymentReceived
                    || entity.Status == OrderStatus.Provisioning))
            {
                try
                {
                    await _networkAccountService.EnsurePendingForOrderAsync(
                        entity.Id, NetworkAccountSource.AdminManual, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Pending network-account reservation hook threw for order {OrderNumber}.",
                        entity.OrderNumber);
                }
            }
            else if (previous != entity.Status && entity.Status == OrderStatus.Active)
            {
                // Admin marked the order Active directly (no installation
                // workflow). Provisioning treats this as "go fully Active"
                // — idempotent against a prior Pending row.
                try
                {
                    await _networkAccountService.ProvisionForOrderAsync(
                        entity.Id, NetworkAccountSource.AdminManual, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Direct activation provisioning hook threw for order {OrderNumber}.",
                        entity.OrderNumber);
                }
            }

            // Phase 39 — same auto-schedule hook as AdminUpdateAsync.
            // Fires when the admin moves the order to a scheduling status
            // (e.g. Confirmed) while passing an install date in the same
            // call, OR sets just the date.
            await EnsureInstallationScheduledAsync(entity, request.ExpectedInstallationDateUtc, cancellationToken);

            // Phase 46 — date-less companion. The Portal's "Update Status
            // → Installation Scheduled" form has no date input, so the
            // hook above no-ops. This one fires on the status transition
            // alone, creating a PendingScheduling installation that the
            // admin can date later via "Set Install Date".
            if (previous != entity.Status)
            {
                await EnsureInstallationExistsAsync(entity, cancellationToken);

                // Phase 46 — order terminated → cancel any active install
                // so it falls off the operational Installations index.
                if (OrderStatusesThatTerminateNetwork.Contains(entity.Status))
                {
                    var terminationReason = entity.Status switch
                    {
                        OrderStatus.Cancelled => Trim(request.CancellationReason) ?? "Order was cancelled.",
                        OrderStatus.Failed    => Trim(request.FailureReason)      ?? "Order was marked failed.",
                        OrderStatus.Rejected  => Trim(request.RejectionReason)    ?? "Order was rejected.",
                        _                     => $"Order reached terminal status '{entity.Status}'."
                    };
                    await TryCancelActiveInstallationForOrderAsync(entity, terminationReason, cancellationToken);
                }
            }

            var reloaded = await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity;
            var dto = MapToDto(reloaded);
            dto.Payment = await ResolvePaymentSummaryAsync(entity.Id, cancellationToken);
            dto.Installation = await ResolveInstallationSummaryAsync(entity.Id, cancellationToken);
            dto.Service = await ResolveServiceSummaryAsync(entity.Id, cancellationToken);
            return Result<OrderDto>.Success(dto, "Order status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating order status {Id}", id);
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the order status.");
        }
    }

    /// <summary>
    /// Admin "Mark service activated on Openserve" — the ONLY path
    /// to OrderStatus.Active for a service order. Requires the order
    /// to be in PendingActivation (i.e. installation Completed AND
    /// first monthly invoice Paid). Records the activation date as
    /// the billing anchor and sets NextPayDateUtc to anchor + 30 days.
    /// </summary>
    public async Task<Result<OrderDto>> AdminActivateServiceAsync(
        Guid id, AdminActivateServiceRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");
            request ??= new AdminActivateServiceRequestDto();

            var entity = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
            if (entity is null)
                return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            // State guard. Activation is only legal from PendingActivation
            // (the state the system reaches after install Completed + the
            // first monthly invoice flips to Paid). Idempotent re-call
            // when already Active returns success without mutating.
            if (entity.Status == OrderStatus.Active)
            {
                var dtoAlready = MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity);
                dtoAlready.Payment = await ResolvePaymentSummaryAsync(entity.Id, cancellationToken);
                dtoAlready.Installation = await ResolveInstallationSummaryAsync(entity.Id, cancellationToken);
                dtoAlready.Service = await ResolveServiceSummaryAsync(entity.Id, cancellationToken);
                return Result<OrderDto>.Success(dtoAlready, "Order is already Active.");
            }
            if (entity.Status != OrderStatus.PendingActivation)
            {
                return Result<OrderDto>.Failure(
                    ErrorCodes.CONFLICT,
                    $"Order must be in PendingActivation to activate service. Current status: {entity.Status}.");
            }

            var now = DateTime.UtcNow;
            var activationDate = request.ActivationDateUtc ?? now;

            var previousStatus = entity.Status;
            entity.Status = OrderStatus.Active;
            entity.ActivatedAtUtc = activationDate;
            entity.BillingAnchorDateUtc = activationDate;
            entity.OpenserveActivationReference = Trim(request.OpenserveActivationReference);
            entity.ActivationNotes = Trim(request.ActivationNotes);
            entity.ActivatedByUserId = _currentUser.UserId;
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            // Post-activation pro-rata invoice for Fibre-family orders.
            // Security orders had their pro-rata charged at checkout
            // (guarded by Order.FirstProRataInvoiceGeneratedAtUtc set
            // during OrderIntent conversion) — the idempotency stamp
            // below prevents this path from writing a duplicate line.
            //
            // The invoice sits Issued (unpaid) and is payable through
            // the existing invoice-pay endpoints. No new gateway wiring.
            var proRataInvoice = await TryGenerateFibreProRataInvoiceAsync(entity, activationDate, cancellationToken);

            // NextPayDateUtc points at the customer's next billing day —
            // the same day the pro-rata period ends. When the pro-rata
            // invoice is paid, ServiceBillingScheduleService anchors the
            // recurring schedule off this date via invoice.PeriodEndUtc.
            var nextBillingDate = SmartFuture.Application.Billing.ProRata.ProRataCalculator.NextBillingDate(
                activationDate, entity.PreferredBillingDay);
            entity.NextPayDateUtc = nextBillingDate == activationDate
                ? nextBillingDate.AddDays(30) // customer joined ON their billing day → next cycle is +30d
                : nextBillingDate;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = AuditActorType.Admin,
                ActionType = AuditActionType.OrderStatusChanged,
                EntityType = AuditEntityType.Order,
                EntityId = entity.Id,
                EntityName = entity.OrderNumber,
                Summary = $"Service activated on Openserve by admin ({entity.OrderNumber})",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    previousStatus,
                    newStatus = entity.Status,
                    activationDateUtc = activationDate,
                    billingAnchorDateUtc = entity.BillingAnchorDateUtc,
                    nextPayDateUtc = entity.NextPayDateUtc,
                    openserveActivationReference = entity.OpenserveActivationReference,
                    activatedByUserId = entity.ActivatedByUserId
                }),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            _logger.LogInformation(
                "[OrderActivated] {OrderNumber} activated by admin {AdminId} openserveRef={OpenserveRef} activationDate={ActivationDate:o} nextPay={NextPay:o}",
                entity.OrderNumber, _currentUser.UserId, entity.OpenserveActivationReference,
                activationDate, entity.NextPayDateUtc);

            // Flip the linked NetworkAccount Pending → Active. Best-
            // effort: failure here does NOT undo the order activation
            // (the order is authoritative for billing; the network
            // account is for service-state display). Provisioner is a
            // NoOp in the current phase, so this just promotes the row.
            try
            {
                var provisionResult = await _networkAccountService.ProvisionForOrderAsync(
                    entity.Id, NetworkAccountSource.AdminManual, cancellationToken);
                if (!provisionResult.IsSuccess)
                {
                    _logger.LogWarning(
                        "[NetworkAccountActivate] {OrderNumber} promote-to-Active skipped: {Code} {Message}",
                        entity.OrderNumber, provisionResult.Code, provisionResult.Message);
                }
            }
            catch (Exception naEx)
            {
                _logger.LogError(naEx,
                    "[NetworkAccountActivate] {OrderNumber} promote-to-Active threw; order activation stands.",
                    entity.OrderNumber);
            }

            var reloaded = await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity;
            var dto = MapToDto(reloaded);
            dto.Payment = await ResolvePaymentSummaryAsync(entity.Id, cancellationToken);
            dto.Installation = await ResolveInstallationSummaryAsync(entity.Id, cancellationToken);
            dto.Service = await ResolveServiceSummaryAsync(entity.Id, cancellationToken);
            return Result<OrderDto>.Success(dto, "Service activated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error activating service for order {Id}", id);
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while activating the service.");
        }
    }

    public async Task<Result> CancelMineAsync(Guid id, string? cancellationReason = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (id == Guid.Empty)
                return Result.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");

            var entity = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == id && o.UserId == currentUserId.Value, cancellationToken);

            if (entity is null)
                return Result.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            if (!CustomerCancellableStatuses.Contains(entity.Status))
                return Result.Failure(
                    ErrorCodes.CONFLICT,
                    $"Orders in status '{entity.Status}' cannot be cancelled by the customer.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = OrderStatus.Cancelled;
            entity.CancelledAtUtc = now;
            entity.LastStatusChangedByUserId = currentUserId;
            entity.CancellationReason = Trim(cancellationReason) ?? entity.CancellationReason;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.OrderStatusChanged,
                AuditActorType.User,
                entity,
                summary: $"Order cancelled by customer: {previous} -> {entity.Status} ({entity.OrderNumber})",
                metadata: BuildMetadata(new { previous, newStatus = entity.Status }));

            await TryTerminateNetworkForOrderAsync(
                entity.Id, entity.OrderNumber, entity.Status, NetworkAccountSource.SystemAutomated, cancellationToken);

            // Phase 46 — customer cancelled their own order, so cancel
            // any active Installation too (mirrors AdminUpdateStatusAsync's
            // terminal-state branch). Best-effort — failure stays out of
            // the customer's success path.
            await TryCancelActiveInstallationForOrderAsync(
                entity,
                Trim(cancellationReason) ?? "Customer cancelled the order.",
                cancellationToken);

            return Result.Success("Order cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error cancelling order {Id}", id);
            return Result.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while cancelling the order.");
        }
    }

    // Phase 51 — customer-initiated install-address change. Re-runs
    // coverage on the new lat/lng (no manual override possible) and,
    // on success, updates the Order's address fields plus any
    // PendingScheduling Installation's address so the technician
    // dispatch reflects the new location. Refuses with CONFLICT once
    // the install has been scheduled past PendingScheduling — at that
    // point a support ticket / admin path is required.
    public async Task<Result<OrderDto>> RequestAddressChangeMineAsync(Guid id, RequestAddressChangeRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (id == Guid.Empty)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Order id is required.");
            if (request is null)
                return Result<OrderDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            // Coordinates are mandatory: the gate is "address must come
            // from a Google Places pick". Reject any free-text submission
            // so the wizard can't be bypassed via a curl call.
            if (!request.Latitude.HasValue || !request.Longitude.HasValue)
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Pick the new address from the suggestions list — we need the coordinates to re-validate coverage.");

            var addressValidation = ValidateAddressAndContact(
                request.AddressLine1, request.Latitude, request.Longitude,
                email: null, phoneNumber: null, expectedInstallationDateUtc: null);
            if (addressValidation is not null) return addressValidation;

            var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == id && o.UserId == currentUserId.Value, cancellationToken);
            if (order is null) return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            // Order-status eligibility: anything *after* PaymentReceived
            // means we're already provisioning or live, so address
            // changes go through admin/support. Terminal statuses are
            // also blocked.
            var allowedStatuses = new[]
            {
                OrderStatus.Draft,
                OrderStatus.Submitted,
                OrderStatus.Confirmed,
                OrderStatus.AwaitingPayment,
                OrderStatus.PaymentReceived
            };
            if (!allowedStatuses.Contains(order.Status))
            {
                return Result<OrderDto>.Failure(
                    ErrorCodes.CONFLICT,
                    $"Address changes are no longer available for orders in '{order.Status}'. Please contact support.");
            }

            // Installation eligibility: once a technician has been
            // assigned (or the install is en route / on site /
            // completed) the address is locked — dispatch has already
            // taken it. PendingScheduling and Scheduled rows can still
            // be redirected; we update them in lock-step below.
            var lockedInstallStatuses = new[]
            {
                InstallationStatus.TechnicianAssigned,
                InstallationStatus.EnRoute,
                InstallationStatus.OnSite,
                InstallationStatus.Completed
            };
            var hasLockedInstall = await _dbContext.Installations
                .AnyAsync(i => i.OrderId == order.Id && lockedInstallStatuses.Contains(i.Status), cancellationToken);
            if (hasLockedInstall)
            {
                return Result<OrderDto>.Failure(
                    ErrorCodes.CONFLICT,
                    "This installation has already been assigned. Please contact support to change the address.");
            }

            // Re-run coverage check. We refuse any address that isn't
            // covered — the customer never sees a "your install may fail
            // because we don't service this address" surprise later.
            var coverage = await _coverageCheckService.CheckAsync(new CoverageCheckRequestDto
            {
                AddressText = request.AddressLine1,
                Latitude    = request.Latitude,
                Longitude   = request.Longitude
            }, cancellationToken);

            if (!coverage.IsSuccess || coverage.Data is null)
            {
                _logger.LogWarning(
                    "Address-change coverage check failed for order {OrderNumber}: {Code} {Message}",
                    order.OrderNumber, coverage.Code, coverage.Message);
                return Result<OrderDto>.Failure(
                    ErrorCodes.UPSTREAM_UNAVAILABLE,
                    "We couldn't verify coverage for the new address. Please try again in a moment.");
            }

            if (!coverage.Data.CoverageAvailable)
            {
                return Result<OrderDto>.Failure(
                    ErrorCodes.CONFLICT,
                    "Coverage is not available at the new address. Submit a Coverage Request instead and we'll get back to you.");
            }

            // Apply the change. We snapshot the address fields onto the
            // Order *and* mirror them onto any active Installation row
            // so dispatch always reads the latest. Audit captures the
            // before/after so support can reconstruct the timeline.
            var previousAddress = $"{order.AddressLine1}, {order.Suburb}, {order.City}";

            order.AddressLine1         = request.AddressLine1.Trim();
            order.AddressLine2         = Trim(request.AddressLine2);
            order.Suburb               = Trim(request.Suburb)   ?? Trim(coverage.Data.Suburb);
            order.City                 = Trim(request.City)     ?? Trim(coverage.Data.Town);
            order.Province             = Trim(request.Province) ?? Trim(coverage.Data.Province);
            order.PostalCode           = Trim(request.PostalCode);
            order.Country              = Trim(request.Country)  ?? "South Africa";
            order.Latitude             = request.Latitude;
            order.Longitude            = request.Longitude;
            order.GooglePlaceId        = Trim(request.GooglePlaceId);
            order.MapProviderReference = Trim(request.MapProviderReference);
            if (!string.IsNullOrWhiteSpace(request.CustomerNotes))
            {
                var addendum = $"[Address change requested] {request.CustomerNotes.Trim()}";
                order.CustomerNotes = string.IsNullOrWhiteSpace(order.CustomerNotes)
                    ? addendum
                    : $"{order.CustomerNotes}\n\n{addendum}";
            }
            order.LastStatusChangedByUserId = currentUserId;

            // Mirror onto any PendingScheduling / Scheduled / Rescheduled
            // installation row(s) for this order so the technician card
            // matches what the customer just confirmed.
            var mutableInstallStatuses = new[]
            {
                InstallationStatus.PendingScheduling,
                InstallationStatus.Scheduled,
                InstallationStatus.Rescheduled
            };
            var installs = await _dbContext.Installations
                .Where(i => i.OrderId == order.Id && mutableInstallStatuses.Contains(i.Status))
                .ToListAsync(cancellationToken);
            foreach (var install in installs)
            {
                install.AddressLine1         = order.AddressLine1;
                install.AddressLine2         = order.AddressLine2;
                install.Suburb               = order.Suburb;
                install.City                 = order.City;
                install.Province             = order.Province;
                install.PostalCode           = order.PostalCode;
                install.Country              = order.Country;
                install.Latitude             = order.Latitude;
                install.Longitude            = order.Longitude;
                install.GooglePlaceId        = order.GooglePlaceId;
                install.MapProviderReference = order.MapProviderReference;
                install.LastStatusChangedByUserId = currentUserId;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.OrderStatusChanged, AuditActorType.User, order,
                summary: $"Address changed by customer: {order.OrderNumber} ({previousAddress} -> {order.AddressLine1}, {order.Suburb}, {order.City})",
                metadata: BuildMetadata(new
                {
                    previousAddress,
                    newAddress     = $"{order.AddressLine1}, {order.Suburb}, {order.City}",
                    latitude       = order.Latitude,
                    longitude      = order.Longitude,
                    googlePlaceId  = order.GooglePlaceId,
                    installsTouched = installs.Count
                }));

            var reloaded = await ReloadWithIncludesAsync(order.Id, cancellationToken) ?? order;
            var dto = MapToDto(reloaded);
            dto.Payment      = await ResolvePaymentSummaryAsync(order.Id, cancellationToken);
            dto.Installation = await ResolveInstallationSummaryAsync(order.Id, cancellationToken);
            dto.Service      = await ResolveServiceSummaryAsync(order.Id, cancellationToken);
            return Result<OrderDto>.Success(dto, "Address updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing address change for order {Id}", id);
            return Result<OrderDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the address.");
        }
    }

    // Phase 51 — public eligibility probe. The mobile order wizard and
    // portal "Start new order" CTA call this *before* showing the form
    // so the customer gets a clean "you already have an order in
    // progress" panel instead of failing inside the create call.
    // `CreateMineAsync` also runs this same check server-side so the
    // API stays the source of truth.
    public async Task<Result<CustomerOrderEligibilityDto>> GetMyEligibilityAsync(
        ServicePackageType? requestedType = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<CustomerOrderEligibilityDto>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var dto = await ComputeEligibilityAsync(currentUserId.Value, requestedType, cancellationToken);
            return Result<CustomerOrderEligibilityDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error computing order eligibility");
            return Result<CustomerOrderEligibilityDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while checking your order eligibility.");
        }
    }

    // Map ServicePackageType → product-line category. Today only two
    // categories exist (Security and "Fibre" — which covers fibre, LTE,
    // wireless, Wi-Fi, voice, prepaid-fibre — every existing line). New
    // product lines should pick a category here so the one-active-order
    // gate keeps them isolated from the others.
    private static string ResolveOrderCategory(ServicePackageType type)
        => type == ServicePackageType.Security ? "Security" : "Fibre";

    // Shared helper: one query, no per-call allocation of the
    // non-terminal list. Returns a fully populated DTO so callers
    // (HTTP endpoint + internal CreateMineAsync gate) can re-use the
    // same blocking-order snapshot and message copy.
    //
    // `requestedType` is the type the customer is trying to order RIGHT
    // NOW. When supplied, only orders in the SAME category block
    // creation — so an Active Fibre order will not block a new Security
    // order, and vice versa. When omitted (legacy callers), behaviour
    // falls back to the original "any non-terminal order blocks"
    // semantics for backwards compatibility.
    private async Task<CustomerOrderEligibilityDto> ComputeEligibilityAsync(
        Guid userId, ServicePackageType? requestedType, CancellationToken cancellationToken)
    {
        // Active is "highest priority" blocker (the customer already
        // has a live service), then any pending/in-flight order. Sort
        // so an Active row wins over a stale Draft row on the same
        // account when categorising the message.
        var rows = await _dbContext.Orders
            .AsNoTracking()
            .Where(o => o.UserId == userId && NonTerminalOrderStatuses.Contains(o.Status))
            .OrderByDescending(o => o.Status == OrderStatus.Active ? 1 : 0)
            .ThenByDescending(o => o.CreatedAtUtc)
            .Select(o => new { o.Id, o.OrderNumber, o.Status, o.PackageName, o.PackageType })
            .ToListAsync(cancellationToken);

        var blocking = requestedType.HasValue
            ? rows.FirstOrDefault(r => ResolveOrderCategory(r.PackageType) == ResolveOrderCategory(requestedType.Value))
            : rows.FirstOrDefault();

        if (blocking is null)
        {
            return new CustomerOrderEligibilityDto { CanCreateOrder = true };
        }

        var isActiveService = blocking.Status == OrderStatus.Active;
        return new CustomerOrderEligibilityDto
        {
            CanCreateOrder           = false,
            Reason                   = isActiveService ? "active_service" : "pending_order",
            Message                  = isActiveService
                ? $"You already have an active SmartFuture service ({blocking.PackageName}). Only one service per account is supported right now."
                : $"You already have an order in progress for {blocking.PackageName}. Finish or cancel it before starting a new one.",
            BlockingOrderId          = blocking.Id,
            BlockingOrderNumber      = blocking.OrderNumber,
            BlockingOrderStatus      = blocking.Status,
            BlockingOrderPackageName = blocking.PackageName
        };
    }

    private IQueryable<Order> BuildQuery(OrderFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.LastStatusChangedByUser)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(o => o.UserId == restrictToUserId.Value);
        else if (filter.UserId.HasValue)
            query = query.Where(o => o.UserId == filter.UserId.Value);

        if (filter.CustomerProfileId.HasValue)
            query = query.Where(o => o.CustomerProfileId == filter.CustomerProfileId.Value);

        if (filter.ServicePackageId.HasValue)
            query = query.Where(o => o.ServicePackageId == filter.ServicePackageId.Value);

        if (filter.CoverageRequestId.HasValue)
            query = query.Where(o => o.CoverageRequestId == filter.CoverageRequestId.Value);

        if (filter.StatusFilter.HasValue)
            query = query.Where(o => o.Status == filter.StatusFilter.Value);

        if (filter.Source.HasValue)
            query = query.Where(o => o.Source == filter.Source.Value);

        if (filter.PackageType.HasValue)
            query = query.Where(o => o.PackageType == filter.PackageType.Value);

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(o => o.City != null && EF.Functions.Like(o.City, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Suburb))
        {
            var v = filter.Suburb.Trim();
            query = query.Where(o => o.Suburb != null && EF.Functions.Like(o.Suburb, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(o => o.Province != null && EF.Functions.Like(o.Province, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.PostalCode))
        {
            var v = filter.PostalCode.Trim();
            query = query.Where(o => o.PostalCode != null && EF.Functions.Like(o.PostalCode, $"%{v}%"));
        }

        if (filter.FromUtc.HasValue)
            query = query.Where(o => o.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(o => o.CreatedAtUtc <= filter.ToUtc.Value);

        if (filter.SubmittedFromUtc.HasValue)
            query = query.Where(o => o.SubmittedAtUtc != null && o.SubmittedAtUtc >= filter.SubmittedFromUtc.Value);

        if (filter.SubmittedToUtc.HasValue)
            query = query.Where(o => o.SubmittedAtUtc != null && o.SubmittedAtUtc <= filter.SubmittedToUtc.Value);

        if (filter.ExpectedInstallationFromUtc.HasValue)
            query = query.Where(o => o.ExpectedInstallationDateUtc != null
                                      && o.ExpectedInstallationDateUtc >= filter.ExpectedInstallationFromUtc.Value);

        if (filter.ExpectedInstallationToUtc.HasValue)
            query = query.Where(o => o.ExpectedInstallationDateUtc != null
                                      && o.ExpectedInstallationDateUtc <= filter.ExpectedInstallationToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(o =>
                EF.Functions.Like(o.OrderNumber, $"%{s}%") ||
                EF.Functions.Like(o.PackageName, $"%{s}%") ||
                EF.Functions.Like(o.AddressLine1, $"%{s}%") ||
                (o.FullName != null && EF.Functions.Like(o.FullName, $"%{s}%")) ||
                (o.Email != null && EF.Functions.Like(o.Email, $"%{s}%")) ||
                (o.City != null && EF.Functions.Like(o.City, $"%{s}%")) ||
                (o.Suburb != null && EF.Functions.Like(o.Suburb, $"%{s}%")) ||
                (o.PostalCode != null && EF.Functions.Like(o.PostalCode, $"%{s}%")));
        }

        return query;
    }

    // Non-static so it can reach the instance's _dbContext via
    // ResolvePaymentSummaryAsync — list rows need the Payment summary so the
    // SmartFutureApp's My Orders shows the same Paid / Pending pill as Order
    // Details (mock-checkout leaves order Status at Submitted, so the app
    // can't fall back to deriving from status alone).
    private async Task<Result<PagedResult<OrderDto>>> ToPagedResultAsync(IQueryable<Order> query, OrderFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(o => new OrderDto
            {
                Id = o.Id,
                OrderNumber = o.OrderNumber,
                UserId = o.UserId,
                CustomerProfileId = o.CustomerProfileId,
                ServicePackageId = o.ServicePackageId,
                CoverageRequestId = o.CoverageRequestId,
                Status = o.Status,
                Source = o.Source,
                PackageName = o.PackageName,
                PackageType = o.PackageType,
                PackageSpeedLabel = o.PackageSpeedLabel,
                PackageDataAllowanceLabel = o.PackageDataAllowanceLabel,
                PackageIsUncapped = o.PackageIsUncapped,
                PackagePrice = o.PackagePrice,
                PackageBillingCycle = o.PackageBillingCycle,
                PackageContractMonths = o.PackageContractMonths,
                PackageHasFreeInstallation = o.PackageHasFreeInstallation,
                PackageInstallationFee = o.PackageInstallationFee,
                PackageIncludesRouter = o.PackageIncludesRouter,
                FullName = o.FullName,
                Email = o.Email,
                PhoneNumber = o.PhoneNumber,
                AddressLine1 = o.AddressLine1,
                AddressLine2 = o.AddressLine2,
                Suburb = o.Suburb,
                City = o.City,
                Province = o.Province,
                PostalCode = o.PostalCode,
                Country = o.Country,
                Latitude = o.Latitude,
                Longitude = o.Longitude,
                GooglePlaceId = o.GooglePlaceId,
                MapProviderReference = o.MapProviderReference,
                CustomerNotes = o.CustomerNotes,
                AdminNotes = o.AdminNotes,
                SubmittedAtUtc = o.SubmittedAtUtc,
                ConfirmedAtUtc = o.ConfirmedAtUtc,
                CancelledAtUtc = o.CancelledAtUtc,
                ActivatedAtUtc = o.ActivatedAtUtc,
                ExpectedInstallationDateUtc = o.ExpectedInstallationDateUtc,
                LastStatusChangedByUserId = o.LastStatusChangedByUserId,
                LastStatusChangedByUserEmail = o.LastStatusChangedByUser != null
                    ? o.LastStatusChangedByUser.Email
                    : null,
                CancellationReason = o.CancellationReason,
                FailureReason = o.FailureReason,
                RejectionReason = o.RejectionReason,
                PreferredBillingDay = o.PreferredBillingDay,
                CreatedAtUtc = o.CreatedAtUtc,
                UpdatedAtUtc = o.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        // Per-row enrichment: one extra query each. PageSize is bounded
        // (default <=20), so the N+1 is bearable. If the customer list ever
        // grows long enough to feel slow, fold this into a single grouped
        // query joining Invoices+Payments on OrderId IN (…) and project.
        //
        // Phase 46 — also attach the Installation summary so list rows can
        // show "Installation scheduled / Pending" linkage in the mobile
        // My Orders + Portal Orders index without a per-row drill-in.
        foreach (var item in items)
        {
            item.Payment      = await ResolvePaymentSummaryAsync(item.Id, cancellationToken);
            item.Installation = await ResolveInstallationSummaryAsync(item.Id, cancellationToken);
        }

        var paged = new PagedResult<OrderDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<OrderDto>>.Success(paged);
    }

    private async Task<Order?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.LastStatusChangedByUser)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    private async Task<Result<OrderDto>?> ValidateCoverageRequestAsync(Guid coverageRequestId, Guid currentUserId, ServicePackage package, CancellationToken cancellationToken)
    {
        var coverage = await _dbContext.CoverageRequests
            .AsNoTracking()
            .Where(c => c.Id == coverageRequestId)
            .Select(c => new
            {
                c.Id,
                c.UserId,
                c.Status,
                c.ServicePackageId,
                c.RequestedServiceType
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (coverage is null)
            return Result<OrderDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced coverage request was not found.");

        if (coverage.UserId != currentUserId)
            return Result<OrderDto>.Failure(
                ErrorCodes.FORBIDDEN, "Coverage request does not belong to the current user.");

        if (coverage.Status != CoverageRequestStatus.Available)
            return Result<OrderDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Coverage request must be in 'Available' status to place an order against it.");

        if (coverage.ServicePackageId.HasValue && coverage.ServicePackageId.Value != package.Id)
        {
            return Result<OrderDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Coverage request is linked to a different service package.");
        }

        if (!coverage.ServicePackageId.HasValue && coverage.RequestedServiceType.HasValue
            && coverage.RequestedServiceType.Value != package.Type)
        {
            return Result<OrderDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Selected service package does not match the requested service type on the coverage request.");
        }

        return null;
    }

    private static Result<OrderDto>? ValidateAddressAndContact(string? addressLine1, decimal? latitude, decimal? longitude, string? email, string? phoneNumber,
        DateTime? expectedInstallationDateUtc)
    {
        if (string.IsNullOrWhiteSpace(addressLine1))
            return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "AddressLine1 is required.");

        if (latitude.HasValue && (latitude.Value < -90m || latitude.Value > 90m))
            return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Latitude must be between -90 and 90.");

        if (longitude.HasValue && (longitude.Value < -180m || longitude.Value > 180m))
            return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Longitude must be between -180 and 180.");

        if (!string.IsNullOrWhiteSpace(email) && !EmailRegex.IsMatch(email.Trim()))
            return Result<OrderDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Email is not in a valid format.");

        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            var p = phoneNumber.Trim();
            if (p.Length < 6 || p.Length > 50)
                return Result<OrderDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "PhoneNumber must be between 6 and 50 characters.");
        }

        if (expectedInstallationDateUtc.HasValue
            && expectedInstallationDateUtc.Value < DateTime.UtcNow.Date)
        {
            return Result<OrderDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "ExpectedInstallationDateUtc cannot be in the past.");
        }

        return null;
    }

    // ─── Mock-checkout invoice + payment persistence ───────────────────────
    //
    // **UAT-only.** Creates an Invoice + line items + Payment for the
    // just-submitted order using a server-authoritative amount derived
    // from the package snapshot stored on the order (so the client
    // can't influence the recorded total). The order's status is NOT
    // touched — admins still handle activation. Audited as a
    // mock-checkout entry on both the invoice and the payment so this
    // can be filtered out of real revenue reports later.
    //
    // **Atomicity (Phase 31 fix).** The invoice + line items + payment
    // writes go through one EF transaction. If any insert fails the
    // whole billing pair rolls back — no orphan invoice. The
    // already-committed Order row stays intact and the method returns
    // `false` so the caller can warn the client.
    //
    // **Execution strategy (Phase 33B-fix).** The SQL Server provider
    // is registered with a retrying execution strategy, which forbids
    // raw `BeginTransactionAsync` calls. The transaction body therefore
    // runs inside `strategy.ExecuteAsync` so EF can retry the entire
    // unit on transient connection failures. Side effects that should
    // **not** repeat on retry (audit logs, the success log line) live
    // outside the strategy block and only run if the transaction
    // commits exactly once.
    //
    // Phase 52 — mints an *unpaid* installation-fee invoice so the
    // customer can settle it via a real gateway (Ozow) immediately
    // after order creation. Differs from PersistMockCheckoutAsync:
    //   - Invoice status is Issued, not Paid
    //   - No InvoiceLineItem of ServicePackage type (only InstallationFee)
    //   - No Payment row is created — that's the gateway's job
    //
    // Best-effort: failure is logged and the order itself still stands.
    // The customer can re-trigger the invoice via admin if needed.
    private async Task PersistIssuedInstallationInvoiceAsync(Order order, decimal installationFee, DateTime now, CancellationToken cancellationToken)
    {
        if (installationFee <= 0m) return;

        try
        {
            var invoiceNumber = await GenerateUniqueBillingNumberAsync("INV", isInvoice: true, now, cancellationToken)
                ?? $"INV-{now:yyyyMMdd}-NEW";

            var strategy = _dbContext.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _dbContext.BeginTransactionAsync(cancellationToken);

                var invoice = new Invoice
                {
                    InvoiceNumber              = invoiceNumber,
                    OrderId                    = order.Id,
                    Status                     = InvoiceStatus.Issued,
                    SubtotalAmount             = installationFee,
                    TaxAmount                  = 0m,
                    TotalAmount                = installationFee,
                    AmountPaid                 = 0m,
                    BalanceDue                 = installationFee,
                    CurrencyCode               = "ZAR",
                    IssuedAtUtc                = now,
                    DueAtUtc                   = now.AddDays(7),
                    Notes                      = "Once-off activation fee — payable via Ozow / debit order. Monthly package billing starts after installation completion.",
                    LastStatusChangedByUserId  = order.UserId
                };
                _dbContext.Invoices.Add(invoice);

                _dbContext.InvoiceLineItems.Add(new InvoiceLineItem
                {
                    Invoice     = invoice,
                    LineType    = InvoiceLineItemType.InstallationFee,
                    Description = "Once-off activation fee",
                    Quantity    = 1,
                    UnitAmount  = installationFee,
                    TotalAmount = installationFee,
                    SortOrder   = 0
                });

                await _dbContext.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            });

            _logger.LogInformation(
                "Issued installation-fee invoice {InvoiceNumber} for order {OrderNumber}, amount {Amount} (awaiting gateway payment).",
                invoiceNumber, order.OrderNumber, installationFee);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist Issued installation invoice for order {OrderNumber}.",
                order.OrderNumber);
        }
    }

    // Returns `true` iff invoice + line items + payment all persisted.
    private async Task<bool> PersistMockCheckoutAsync(Order order, CreateOrderRequestDto request, DateTime now, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "PersistMockCheckoutAsync entered for order {OrderNumber} ({OrderId})",
            order.OrderNumber, order.Id);

        var installationFee = order.PackageHasFreeInstallation
            ? 0m
            : (order.PackageInstallationFee ?? 0m);
        // Pricing rule (changed): at order time the customer pays the
        // INSTALLATION FEE only. The package monthly price is billed
        // automatically when the installation is marked Completed (see
        // InstallationService.AdminUpdateStatusAsync ->
        // TryCreateFirstMonthlyInvoiceAsync). Until that hook runs, the
        // monthly price still lives on the Order snapshot
        // (Order.PackagePrice) so admin + portal can display it.
        var dueNow = installationFee;

        // Free installation = nothing to bill at order time. We persist
        // the order itself (already done in CreateMineAsync) and skip
        // invoice + payment creation entirely; the first monthly
        // invoice will be raised when the installation completes.
        if (dueNow <= 0m)
        {
            _logger.LogInformation(
                "MockCheckout: order {OrderNumber} has free installation; skipping invoice/payment at order time. " +
                "First monthly invoice will be raised when installation is Completed.",
                order.OrderNumber);
            return true;
        }

        // `MockCheckoutPaymentReference` is the per-checkout OZOW-MOCK-…
        // ref the client generates. Mirrored onto Invoice.ExternalReference
        // (unique filtered index) and Payment.GatewayReference. The literal
        // "Mock checkout (UAT)" label lives only in Notes, not in any
        // indexed column.
        var reference = Truncate(request.MockCheckoutPaymentReference, 100);

        // Captured by the strategy lambda; re-assigned on each attempt so
        // the post-commit audit block can read the final values. Using
        // locals (not closures over `out` params) keeps the lambda
        // analyser happy.
        Guid persistedInvoiceId = Guid.Empty;
        string persistedInvoiceNumber = string.Empty;
        Guid persistedPaymentId = Guid.Empty;
        string persistedPaymentNumber = string.Empty;

        try
        {
            var strategy = _dbContext.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                // Fresh transaction per attempt — required by the
                // retrying strategy contract.
                await using var tx = await _dbContext.BeginTransactionAsync(cancellationToken);

                // Billing numbers are random suffixes; safe to regenerate
                // on retry. Generate inside the strategy so a retry gets a
                // new number if the previous attempt half-claimed one.
                var invoiceNumber = await GenerateUniqueBillingNumberAsync("INV", isInvoice: true, now, cancellationToken)
                    ?? $"INV-{now:yyyyMMdd}-MOCK";
                var paymentNumber = await GenerateUniqueBillingNumberAsync("PAY", isInvoice: false, now, cancellationToken)
                    ?? $"PAY-{now:yyyyMMdd}-MOCK";

                _logger.LogInformation(
                    "MockCheckout amounts for {OrderNumber}: dueNow={DueNow} (installationFee only), " +
                    "monthlyPriceDeferred={Monthly}, invoice={InvoiceNumber}, payment={PaymentNumber}",
                    order.OrderNumber, dueNow, order.PackagePrice, invoiceNumber, paymentNumber);

                var invoice = new Invoice
                {
                    InvoiceNumber = invoiceNumber,
                    OrderId = order.Id,
                    Status = InvoiceStatus.Paid,
                    SubtotalAmount = dueNow,
                    TaxAmount = 0m,
                    TotalAmount = dueNow,
                    AmountPaid = dueNow,
                    BalanceDue = 0m,
                    CurrencyCode = "ZAR",
                    IssuedAtUtc = now,
                    DueAtUtc = now,
                    PaidAtUtc = now,
                    Notes = "Once-off activation fee — paid at order time (UAT mock checkout). " +
                            "Monthly package billing starts after installation is completed.",
                    ExternalReference = reference,
                    LastStatusChangedByUserId = order.UserId
                };
                _dbContext.Invoices.Add(invoice);

                // Single line item: installation fee. The first month
                // service-package line is NO LONGER created here — it
                // ships on the auto-generated invoice raised when the
                // installation is marked Completed. This matches the
                // business rule: customers pay the install fee up front,
                // and only start paying the monthly subscription once
                // the line is live.
                _dbContext.InvoiceLineItems.Add(new InvoiceLineItem
                {
                    Invoice = invoice,
                    LineType = InvoiceLineItemType.InstallationFee,
                    Description = "Once-off activation fee",
                    Quantity = 1,
                    UnitAmount = dueNow,
                    TotalAmount = dueNow,
                    SortOrder = 0
                });

                var payment = new Payment
                {
                    PaymentNumber = paymentNumber,
                    Invoice = invoice,
                    Status = PaymentStatus.Completed,
                    Method = PaymentMethodType.Gateway,
                    Amount = dueNow,
                    CurrencyCode = "ZAR",
                    PaidAtUtc = now,
                    GatewayName = "Ozow",
                    GatewayReference = reference,
                    // ExternalReference deliberately left null. The
                    // historic bug was using a literal label here, which
                    // then collided with the unique filtered index on
                    // the second mock-checkout payment ever.
                    ExternalReference = null,
                    Notes = "Once-off activation fee — UAT mock payment (not a real charge).",
                    LastStatusChangedByUserId = order.UserId
                };
                _dbContext.Payments.Add(payment);

                // One SaveChanges → one logical unit of work. EF will
                // insert the invoice first, then the line items (FK via
                // navigation), then the payment.
                await _dbContext.SaveChangesAsync(cancellationToken);

                await tx.CommitAsync(cancellationToken);

                persistedInvoiceId = invoice.Id;
                persistedInvoiceNumber = invoice.InvoiceNumber;
                persistedPaymentId = payment.Id;
                persistedPaymentNumber = payment.PaymentNumber;
            });
        }
        catch (Exception ex)
        {
            // Either a non-transient failure inside the lambda or the
            // strategy ran out of retries. Either way the transaction
            // is rolled back by `await using` dispose. The order row
            // remains valid; we surface false so the caller can attach
            // the "billing reconciliation needed" warning to the API
            // response.
            _logger.LogError(ex,
                "Mock-checkout invoice/payment persistence failed for order {OrderId}. Order remains valid; billing rolled back.",
                order.Id);
            return false;
        }

        _logger.LogInformation(
            "MockCheckout committed for {OrderNumber}: invoiceId={InvoiceId}, paymentId={PaymentId}",
            order.OrderNumber, persistedInvoiceId, persistedPaymentId);

        // Audit logs run after the strategy succeeds so a retried
        // attempt doesn't produce duplicate audit rows. Audit writes
        // use the same DbContext but go through their own SaveChanges
        // outside the user-initiated transaction, so a hiccup here
        // does not unwind the billing rows we just committed.
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = order.UserId,
            ActorType = AuditActorType.User,
            ActionType = AuditActionType.InvoiceCreated,
            EntityType = AuditEntityType.Invoice,
            EntityId = persistedInvoiceId,
            EntityName = persistedInvoiceNumber,
            Summary = $"Installation-fee invoice {persistedInvoiceNumber} created (order {order.OrderNumber}, {dueNow:0.00}).",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true,
            MetadataJson = BuildMetadata(new
            {
                orderId = order.Id,
                orderNumber = order.OrderNumber,
                mockCheckout = true,
                provider = "Ozow",
                reference,
                installationFee = dueNow,
                monthlyPriceDeferred = order.PackagePrice
            })
        });

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = order.UserId,
            ActorType = AuditActorType.User,
            ActionType = AuditActionType.PaymentStatusChanged,
            EntityType = AuditEntityType.Payment,
            EntityId = persistedPaymentId,
            EntityName = persistedPaymentNumber,
            Summary = $"Installation-fee payment {persistedPaymentNumber} recorded as Completed (Ozow, {dueNow:0.00}).",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true,
            MetadataJson = BuildMetadata(new
            {
                invoiceId = persistedInvoiceId,
                orderId = order.Id,
                mockCheckout = true,
                provider = "Ozow",
                reference,
                amount = dueNow
            })
        });

        return true;
    }

    private async Task<string?> GenerateUniqueBillingNumberAsync(string prefix, bool isInvoice, DateTime now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = BillingNumberGenerator.BuildCandidate(prefix, now);
            var exists = isInvoice
                ? await _dbContext.Invoices.AnyAsync(i => i.InvoiceNumber == candidate, cancellationToken)
                : await _dbContext.Payments.AnyAsync(p => p.PaymentNumber == candidate, cancellationToken);
            if (!exists) return candidate;
        }
        return null;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }

    private async Task<string?> GenerateUniqueOrderNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var datePart = now.ToString("yyyyMMdd");

        for (var attempt = 0; attempt < OrderNumberMaxAttempts; attempt++)
        {
            var candidate = $"SF-{datePart}-{GenerateRandomSuffix(OrderNumberSuffixLength)}";
            var exists = await _dbContext.Orders.AnyAsync(o => o.OrderNumber == candidate, cancellationToken);
            if (!exists) return candidate;
        }

        return null;
    }

    private static string GenerateRandomSuffix(int length)
    {
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);

        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = OrderNumberAlphabet[buffer[i] % OrderNumberAlphabet.Length];

        return new string(chars);
    }

    private async Task EmitAuditAsync(AuditActionType actionType, AuditActorType actorType, Order entity, string summary, string? metadata)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = actionType,
            EntityType = AuditEntityType.Order,
            EntityId = entity.Id,
            EntityName = entity.OrderNumber,
            Summary = summary,
            MetadataJson = metadata,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private static string? BuildMetadata(object payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload);
        }
        catch
        {
            return null;
        }
    }

    // Templated notification dispatch (Phase 35). When `htmlBody` is
    // provided the notification carries the HTML payload + plain-text
    // fallback; the multi-sender SMTP path sends both. The single-
    // sender legacy SMTP and the logging sender ignore HTML/SenderType
    // and fall back to plain text. Email failure is logged but never
    // unwinds the operation that triggered the notification.
    private async Task TryNotifyAsync(Guid userId, NotificationType type, string? email, string? phone, string subject,
        string body, string? htmlBody, Shared.Enums.Communication.EmailSenderType senderType,
        string relatedEntityType, Guid relatedEntityId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(email))
                return;

            await _notificationService.SendAsync(new SendNotificationRequestDto
            {
                UserId = userId,
                Channel = NotificationChannel.Email,
                Type = type,
                RecipientEmail = email,
                RecipientPhone = phone,
                Subject = subject,
                Body = body,
                IsHtml = !string.IsNullOrEmpty(htmlBody),
                HtmlBody = htmlBody,
                SenderType = senderType,
                RelatedEntityType = relatedEntityType,
                RelatedEntityId = relatedEntityId
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Type} notification for {EntityType} {EntityId}",
                type, relatedEntityType, relatedEntityId);
        }
    }

    private async Task TryTerminateNetworkForOrderAsync(Guid orderId, string orderNumber, OrderStatus newStatus, NetworkAccountSource source, CancellationToken cancellationToken)
    {
        try
        {
            var reason = $"Order {orderNumber} reached terminal status '{newStatus}'.";
            var result = await _networkAccountService.TerminateForOrderAsync(
                orderId, reason, source, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Network termination hook (Order {OrderNumber}) returned non-success: {Code} {Message}",
                    orderNumber, result.Code, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Network termination hook (Order {OrderNumber}) threw", orderNumber);
        }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Pull the first whitespace-separated word out of a full name for
    // email greetings ("Thabo Nkosi" → "Thabo"). Returns "" when the
    // input is blank — templates fall back to "there" in that case.
    private static string FirstWord(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
        var idx = fullName.IndexOf(' ');
        return idx < 0 ? fullName.Trim() : fullName[..idx].Trim();
    }

    private static OrderDto MapToDto(Order o) => new()
    {
        Id = o.Id,
        OrderNumber = o.OrderNumber,
        UserId = o.UserId,
        CustomerProfileId = o.CustomerProfileId,
        ServicePackageId = o.ServicePackageId,
        CoverageRequestId = o.CoverageRequestId,
        Status = o.Status,
        Source = o.Source,
        PackageName = o.PackageName,
        PackageType = o.PackageType,
        PackageSpeedLabel = o.PackageSpeedLabel,
        PackageDataAllowanceLabel = o.PackageDataAllowanceLabel,
        PackageIsUncapped = o.PackageIsUncapped,
        PackagePrice = o.PackagePrice,
        PackageBillingCycle = o.PackageBillingCycle,
        PackageContractMonths = o.PackageContractMonths,
        PackageHasFreeInstallation = o.PackageHasFreeInstallation,
        PackageInstallationFee = o.PackageInstallationFee,
        PackageIncludesRouter = o.PackageIncludesRouter,
        FullName = o.FullName,
        Email = o.Email,
        PhoneNumber = o.PhoneNumber,
        AddressLine1 = o.AddressLine1,
        AddressLine2 = o.AddressLine2,
        Suburb = o.Suburb,
        City = o.City,
        Province = o.Province,
        PostalCode = o.PostalCode,
        Country = o.Country,
        Latitude = o.Latitude,
        Longitude = o.Longitude,
        GooglePlaceId = o.GooglePlaceId,
        MapProviderReference = o.MapProviderReference,
        CustomerNotes = o.CustomerNotes,
        AdminNotes = o.AdminNotes,
        SubmittedAtUtc = o.SubmittedAtUtc,
        ConfirmedAtUtc = o.ConfirmedAtUtc,
        CancelledAtUtc = o.CancelledAtUtc,
        ActivatedAtUtc = o.ActivatedAtUtc,
        RequestedInstallationDateUtc = o.RequestedInstallationDateUtc,
        ExpectedInstallationDateUtc = o.ExpectedInstallationDateUtc,
        LastStatusChangedByUserId = o.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = o.LastStatusChangedByUser?.Email,
        CancellationReason = o.CancellationReason,
        FailureReason = o.FailureReason,
        RejectionReason = o.RejectionReason,
        PreferredBillingDay = o.PreferredBillingDay,
        CreatedAtUtc = o.CreatedAtUtc,
        UpdatedAtUtc = o.UpdatedAtUtc
    };
}
