using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

public class PaymentService : IPaymentService
{
    private const string PaymentNumberPrefix = "PAY";
    private const int PaymentNumberMaxAttempts = 5;

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly IPaymentApplierService _paymentApplier;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, IPaymentApplierService paymentApplier, ILogger<PaymentService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _paymentApplier = paymentApplier;
        _logger = logger;
    }

    public async Task<Result<PagedResult<PaymentDto>>> SearchAdminAsync(PaymentFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new PaymentFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching payments (admin)");
            return Result<PagedResult<PaymentDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching payments.");
        }
    }

    public async Task<Result<PagedResult<PaymentDto>>> GetMineAsync(PaymentFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<PaymentDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new PaymentFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching payments (customer)");
            return Result<PagedResult<PaymentDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your payments.");
        }
    }

    public Task<Result<PaymentDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<PaymentDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<PaymentDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    private async Task<Result<PaymentDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<PaymentDto>.Failure(ErrorCodes.BAD_REQUEST, "Payment id is required.");

            var query = _dbContext.Payments
                .AsNoTracking()
                .Include(p => p.Invoice).ThenInclude(i => i!.Order).ThenInclude(o => o!.User)
                .Include(p => p.LastStatusChangedByUser)
                .Where(p => p.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(p => p.Invoice!.Order!.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            return entity is null
                ? Result<PaymentDto>.Failure(ErrorCodes.NOT_FOUND, "Payment not found.")
                : Result<PaymentDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching payment {Id}", id);
            return Result<PaymentDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the payment.");
        }
    }

    public async Task<Result<PaymentDto>> CreateAsync(CreatePaymentRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<PaymentDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.InvoiceId == Guid.Empty)
                return Result<PaymentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "InvoiceId is required.");

            if (request.Amount <= 0)
                return Result<PaymentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Amount must be greater than zero.");

            var invoice = await _dbContext.Invoices
                .Include(i => i.Order)
                .FirstOrDefaultAsync(i => i.Id == request.InvoiceId, cancellationToken);

            if (invoice is null)
                return Result<PaymentDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced invoice was not found.");

            if (invoice.Status == InvoiceStatus.Cancelled || invoice.Status == InvoiceStatus.Void)
                return Result<PaymentDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Payments cannot be recorded against invoices in status '{invoice.Status}'.");

            if (request.Method != PaymentMethodType.ManualAdjustment
                && request.Amount > invoice.BalanceDue)
            {
                return Result<PaymentDto>.Failure(
                    ErrorCodes.PAYMENT_AMOUNT_MISMATCH,
                    $"Payment amount ({request.Amount:0.00}) exceeds invoice balance due ({invoice.BalanceDue:0.00}).");
            }

            var now = DateTime.UtcNow;

            var entity = new Payment
            {
                InvoiceId = invoice.Id,
                Status = PaymentStatus.Completed,
                Method = request.Method,
                Amount = request.Amount,
                CurrencyCode = invoice.CurrencyCode,
                PaidAtUtc = request.PaidAtUtc ?? now,
                GatewayName = Trim(request.GatewayName),
                GatewayReference = Trim(request.GatewayReference),
                GatewayTransactionId = Trim(request.GatewayTransactionId),
                ExternalReference = Trim(request.ExternalReference),
                Notes = Trim(request.Notes),
                AdminNotes = Trim(request.AdminNotes),
                LastStatusChangedByUserId = _currentUser.UserId
            };

            var paymentNumber = await GenerateUniquePaymentNumberAsync(now, cancellationToken);
            if (paymentNumber is null)
                return Result<PaymentDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Could not generate a unique payment number. Please retry.");

            entity.PaymentNumber = paymentNumber;

            _dbContext.Payments.Add(entity);

            ApplyPaymentToInvoice(invoice, deltaCompletedAmount: entity.Amount, now);

            OrderStatus? orderPrev = null;
            OrderStatus? orderNew = null;
            if (invoice.Status == InvoiceStatus.Paid
                && invoice.Order is not null
                && invoice.Order.Status == OrderStatus.AwaitingPayment)
            {
                orderPrev = invoice.Order.Status;
                invoice.Order.Status = OrderStatus.PaymentReceived;
                invoice.Order.LastStatusChangedByUserId = _currentUser.UserId;
                orderNew = invoice.Order.Status;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitPaymentAuditAsync(
                AuditActorType.Admin,
                entity,
                invoice,
                previous: (PaymentStatus?)null,
                summary: $"Payment recorded: {entity.PaymentNumber} ({entity.Amount:0.00} {entity.CurrencyCode} for invoice {invoice.InvoiceNumber})");

            if (orderPrev.HasValue && orderNew.HasValue && invoice.Order is not null)
            {
                await EmitOrderStatusChangedAuditAsync(
                    invoice.Order, orderPrev.Value, orderNew.Value,
                    triggeredBy: "PaymentCompleted", triggeredByNumber: entity.PaymentNumber);
            }

            return Result<PaymentDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Payment recorded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating payment");
            return Result<PaymentDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the payment.");
        }
    }

    public async Task<Result<PaymentDto>> AdminUpdateStatusAsync(Guid id, AdminUpdatePaymentStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<PaymentDto>.Failure(ErrorCodes.BAD_REQUEST, "Payment id is required.");

            if (request is null)
                return Result<PaymentDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            // AdminNotes is an admin-only field not handled by PaymentApplierService.
            // Apply it directly to the payment row before the state transition runs so the
            // applier's audit metadata captures the up-to-date payment shape.
            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
            {
                var payment = await _dbContext.Payments
                    .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

                if (payment is null)
                    return Result<PaymentDto>.Failure(ErrorCodes.NOT_FOUND, "Payment not found.");

                payment.AdminNotes = request.AdminNotes.Trim();
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            // Delegate the actual state transition (Payment + Invoice + Order + audit +
            // optional notification) to PaymentApplierService — the canonical implementation
            // shared with the webhook path.
            return await _paymentApplier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
            {
                PaymentId = id,
                NewStatus = request.Status,
                FailureReason = request.FailureReason,
                TriggerNotifications = true
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating payment status {Id}", id);
            return Result<PaymentDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the payment status.");
        }
    }

    private static void ApplyPaymentToInvoice(Invoice invoice, decimal deltaCompletedAmount, DateTime now)
    {
        invoice.AmountPaid += deltaCompletedAmount;
        if (invoice.AmountPaid < 0) invoice.AmountPaid = 0;

        invoice.BalanceDue = invoice.TotalAmount - invoice.AmountPaid;
        if (invoice.BalanceDue < 0) invoice.BalanceDue = 0;

        if (invoice.AmountPaid >= invoice.TotalAmount && invoice.TotalAmount > 0)
        {
            invoice.Status = InvoiceStatus.Paid;
            if (invoice.PaidAtUtc is null) invoice.PaidAtUtc = now;
        }
        else if (invoice.AmountPaid > 0)
        {
            invoice.Status = InvoiceStatus.PartiallyPaid;
            invoice.PaidAtUtc = null;
        }
        else
        {
            // Drop back to Issued only if currently in a paid/partially-paid status
            if (invoice.Status == InvoiceStatus.Paid || invoice.Status == InvoiceStatus.PartiallyPaid)
            {
                invoice.Status = InvoiceStatus.Issued;
                invoice.PaidAtUtc = null;
            }
        }
    }

    private IQueryable<Payment> BuildQuery(PaymentFilterRequestDto filter, Guid? restrictToUserId)
    {
        // Phase 49 — include Order.User so the Customer column renders
        // with real names instead of "—".
        var query = _dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Invoice).ThenInclude(i => i!.Order).ThenInclude(o => o!.User)
            .Include(p => p.LastStatusChangedByUser)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(p => p.Invoice!.Order!.UserId == restrictToUserId.Value);

        if (filter.CustomerUserId.HasValue && filter.CustomerUserId.Value != Guid.Empty)
            query = query.Where(p => p.Invoice!.Order!.UserId == filter.CustomerUserId.Value);

        if (filter.ServiceId.HasValue && filter.ServiceId.Value != Guid.Empty)
        {
            var serviceOrderIds = _dbContext.NetworkAccounts
                .Where(n => n.Id == filter.ServiceId.Value)
                .Select(n => n.OrderId);
            query = query.Where(p => p.Invoice != null && serviceOrderIds.Contains(p.Invoice.OrderId));
        }

        if (filter.InvoiceId.HasValue)
            query = query.Where(p => p.InvoiceId == filter.InvoiceId.Value);

        if (!string.IsNullOrWhiteSpace(filter.InvoiceNumber))
        {
            var v = filter.InvoiceNumber.Trim();
            query = query.Where(p => p.Invoice != null && EF.Functions.Like(p.Invoice.InvoiceNumber, $"%{v}%"));
        }

        if (filter.OrderId.HasValue)
            query = query.Where(p => p.Invoice!.OrderId == filter.OrderId.Value);

        if (!string.IsNullOrWhiteSpace(filter.OrderNumber))
        {
            var v = filter.OrderNumber.Trim();
            query = query.Where(p => p.Invoice != null
                                  && p.Invoice.Order != null
                                  && EF.Functions.Like(p.Invoice.Order.OrderNumber, $"%{v}%"));
        }

        if (filter.StatusFilter.HasValue)
            query = query.Where(p => p.Status == filter.StatusFilter.Value);

        if (filter.Method.HasValue)
            query = query.Where(p => p.Method == filter.Method.Value);

        if (filter.MinAmount.HasValue)
            query = query.Where(p => p.Amount >= filter.MinAmount.Value);

        if (filter.MaxAmount.HasValue)
            query = query.Where(p => p.Amount <= filter.MaxAmount.Value);

        if (!string.IsNullOrWhiteSpace(filter.GatewayName))
        {
            var v = filter.GatewayName.Trim();
            query = query.Where(p => p.GatewayName != null && EF.Functions.Like(p.GatewayName, $"%{v}%"));
        }

        if (filter.PaidFromUtc.HasValue)
            query = query.Where(p => p.PaidAtUtc != null && p.PaidAtUtc >= filter.PaidFromUtc.Value);

        if (filter.PaidToUtc.HasValue)
            query = query.Where(p => p.PaidAtUtc != null && p.PaidAtUtc <= filter.PaidToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.GatewayReference))
        {
            var v = filter.GatewayReference.Trim();
            query = query.Where(p => p.GatewayReference != null && EF.Functions.Like(p.GatewayReference, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.GatewayTransactionId))
        {
            var v = filter.GatewayTransactionId.Trim();
            query = query.Where(p => p.GatewayTransactionId != null && EF.Functions.Like(p.GatewayTransactionId, $"%{v}%"));
        }

        if (filter.FromUtc.HasValue)
            query = query.Where(p => p.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(p => p.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Phase 49 — extend search to customer fields + package name.
            var s = filter.Search.Trim();
            query = query.Where(p =>
                EF.Functions.Like(p.PaymentNumber, $"%{s}%") ||
                (p.Invoice != null && EF.Functions.Like(p.Invoice.InvoiceNumber, $"%{s}%")) ||
                (p.Invoice != null && p.Invoice.Order != null && EF.Functions.Like(p.Invoice.Order.OrderNumber, $"%{s}%")) ||
                (p.Invoice != null && p.Invoice.Order != null && p.Invoice.Order.FullName != null && EF.Functions.Like(p.Invoice.Order.FullName, $"%{s}%")) ||
                (p.Invoice != null && p.Invoice.Order != null && p.Invoice.Order.Email != null && EF.Functions.Like(p.Invoice.Order.Email, $"%{s}%")) ||
                (p.Invoice != null && p.Invoice.Order != null && p.Invoice.Order.PhoneNumber != null && EF.Functions.Like(p.Invoice.Order.PhoneNumber, $"%{s}%")) ||
                (p.Invoice != null && p.Invoice.Order != null && p.Invoice.Order.User != null && p.Invoice.Order.User.Email != null && EF.Functions.Like(p.Invoice.Order.User.Email, $"%{s}%")) ||
                (p.Invoice != null && p.Invoice.Order != null && EF.Functions.Like(p.Invoice.Order.PackageName, $"%{s}%")) ||
                (p.GatewayReference != null && EF.Functions.Like(p.GatewayReference, $"%{s}%")) ||
                (p.GatewayName != null && EF.Functions.Like(p.GatewayName, $"%{s}%")) ||
                (p.GatewayTransactionId != null && EF.Functions.Like(p.GatewayTransactionId, $"%{s}%")) ||
                (p.ExternalReference != null && EF.Functions.Like(p.ExternalReference, $"%{s}%")));
        }

        return query;
    }

    private async Task<Result<PagedResult<PaymentDto>>> ToPagedResultAsync(IQueryable<Payment> query, PaymentFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(p => new PaymentDto
            {
                Id = p.Id,
                PaymentNumber = p.PaymentNumber,
                InvoiceId = p.InvoiceId,
                InvoiceNumber = p.Invoice != null ? p.Invoice.InvoiceNumber : null,
                OrderId = p.Invoice != null ? p.Invoice.OrderId : (Guid?)null,
                OrderNumber = p.Invoice != null && p.Invoice.Order != null ? p.Invoice.Order.OrderNumber : null,
                Status = p.Status,
                Method = p.Method,
                Amount = p.Amount,
                CurrencyCode = p.CurrencyCode,
                PaidAtUtc = p.PaidAtUtc,
                FailedAtUtc = p.FailedAtUtc,
                RefundedAtUtc = p.RefundedAtUtc,
                GatewayName = p.GatewayName,
                GatewayReference = p.GatewayReference,
                GatewayTransactionId = p.GatewayTransactionId,
                ExternalReference = p.ExternalReference,
                FailureReason = p.FailureReason,
                Notes = p.Notes,
                AdminNotes = p.AdminNotes,
                LastStatusChangedByUserId = p.LastStatusChangedByUserId,
                LastStatusChangedByUserEmail = p.LastStatusChangedByUser != null
                    ? p.LastStatusChangedByUser.Email
                    : null,
                CreatedAtUtc = p.CreatedAtUtc,
                UpdatedAtUtc = p.UpdatedAtUtc,
                // Phase 49 — customer snapshot via Invoice → Order.
                CustomerUserId = p.Invoice != null && p.Invoice.Order != null
                    ? p.Invoice.Order.UserId
                    : (Guid?)null,
                CustomerFullName = p.Invoice != null && p.Invoice.Order != null
                    ? p.Invoice.Order.FullName
                    : null,
                CustomerEmail = p.Invoice != null && p.Invoice.Order != null
                    ? (p.Invoice.Order.Email ?? (p.Invoice.Order.User != null ? p.Invoice.Order.User.Email : null))
                    : null,
                CustomerPhoneNumber = p.Invoice != null && p.Invoice.Order != null
                    ? (p.Invoice.Order.PhoneNumber ?? (p.Invoice.Order.User != null ? p.Invoice.Order.User.PhoneNumber : null))
                    : null,
                ServicePackageName = p.Invoice != null && p.Invoice.Order != null
                    ? p.Invoice.Order.PackageName
                    : null
            })
            .ToListAsync(cancellationToken);

        await EnrichWithServiceLinksAsync(items, cancellationToken);

        var paged = new PagedResult<PaymentDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<PaymentDto>>.Success(paged);
    }

    // Phase 49 — batch-fill ServiceId/ServiceAccountNumber. Mirrors the
    // InvoiceService helper; one extra round-trip per page.
    private async Task EnrichWithServiceLinksAsync(IReadOnlyList<PaymentDto> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var orderIds = rows
            .Where(r => r.OrderId.HasValue && r.OrderId.Value != Guid.Empty)
            .Select(r => r.OrderId!.Value)
            .Distinct()
            .ToArray();
        if (orderIds.Length == 0) return;
        var byOrder = await _dbContext.NetworkAccounts
            .AsNoTracking()
            .Where(n => orderIds.Contains(n.OrderId))
            .Select(n => new { n.Id, n.OrderId, n.AccountNumber, n.PackageName })
            .ToListAsync(cancellationToken);
        var map = byOrder.GroupBy(n => n.OrderId).ToDictionary(g => g.Key, g => g.First());
        foreach (var row in rows)
        {
            if (row.OrderId.HasValue && map.TryGetValue(row.OrderId.Value, out var svc))
            {
                row.ServiceId = svc.Id;
                row.ServiceAccountNumber = svc.AccountNumber;
                if (string.IsNullOrWhiteSpace(row.ServicePackageName))
                    row.ServicePackageName = svc.PackageName;
            }
        }
    }

    private async Task<Payment?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Invoice).ThenInclude(i => i!.Order).ThenInclude(o => o!.User)
            .Include(p => p.LastStatusChangedByUser)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    private async Task<string?> GenerateUniquePaymentNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < PaymentNumberMaxAttempts; attempt++)
        {
            var candidate = BillingNumberGenerator.BuildCandidate(PaymentNumberPrefix, now);
            var exists = await _dbContext.Payments.AnyAsync(p => p.PaymentNumber == candidate, cancellationToken);
            if (!exists) return candidate;
        }

        return null;
    }

    private async Task EmitPaymentAuditAsync(AuditActorType actorType, Payment entity, Invoice invoice, PaymentStatus? previous, string summary)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = AuditActionType.PaymentStatusChanged,
            EntityType = AuditEntityType.Payment,
            EntityId = entity.Id,
            EntityName = entity.PaymentNumber,
            Summary = summary,
            MetadataJson = BuildMetadata(new
            {
                invoiceId = invoice.Id,
                invoiceNumber = invoice.InvoiceNumber,
                previous,
                newStatus = entity.Status,
                amount = entity.Amount,
                currencyCode = entity.CurrencyCode
            }),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private async Task EmitOrderStatusChangedAuditAsync(Order order, OrderStatus previous, OrderStatus newStatus, string triggeredBy, string triggeredByNumber)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.System,
            ActionType = AuditActionType.OrderStatusChanged,
            EntityType = AuditEntityType.Order,
            EntityId = order.Id,
            EntityName = order.OrderNumber,
            Summary = $"Order status changed by {triggeredBy}: {previous} -> {newStatus} ({order.OrderNumber})",
            MetadataJson = BuildMetadata(new
            {
                previous,
                newStatus,
                triggeredBy,
                triggeredByNumber
            }),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private static string? BuildMetadata(object payload)
    {
        try { return JsonSerializer.Serialize(payload); }
        catch { return null; }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PaymentDto MapToDto(Payment p) => new()
    {
        Id = p.Id,
        PaymentNumber = p.PaymentNumber,
        InvoiceId = p.InvoiceId,
        InvoiceNumber = p.Invoice?.InvoiceNumber,
        OrderId = p.Invoice?.OrderId,
        OrderNumber = p.Invoice?.Order?.OrderNumber,
        Status = p.Status,
        Method = p.Method,
        Amount = p.Amount,
        CurrencyCode = p.CurrencyCode,
        PaidAtUtc = p.PaidAtUtc,
        FailedAtUtc = p.FailedAtUtc,
        RefundedAtUtc = p.RefundedAtUtc,
        GatewayName = p.GatewayName,
        GatewayReference = p.GatewayReference,
        GatewayTransactionId = p.GatewayTransactionId,
        ExternalReference = p.ExternalReference,
        FailureReason = p.FailureReason,
        Notes = p.Notes,
        AdminNotes = p.AdminNotes,
        LastStatusChangedByUserId = p.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = p.LastStatusChangedByUser?.Email,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc,
        // Phase 49 — customer snapshot for the payment detail view.
        CustomerUserId = p.Invoice?.Order?.UserId,
        CustomerFullName = p.Invoice?.Order?.FullName,
        CustomerEmail = p.Invoice?.Order?.Email ?? p.Invoice?.Order?.User?.Email,
        CustomerPhoneNumber = p.Invoice?.Order?.PhoneNumber ?? p.Invoice?.Order?.User?.PhoneNumber,
        ServicePackageName = p.Invoice?.Order?.PackageName
    };
}
