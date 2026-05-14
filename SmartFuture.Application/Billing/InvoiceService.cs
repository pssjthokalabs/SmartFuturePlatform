using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

public class InvoiceService : IInvoiceService
{
    private const string DefaultCurrencyCode = "ZAR";
    private const string InvoiceNumberPrefix = "INV";
    private const int InvoiceNumberMaxAttempts = 5;

    private static readonly OrderStatus[] InvoiceCreatableOrderStatuses =
    {
        OrderStatus.Draft,
        OrderStatus.Submitted,
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment,
        OrderStatus.PaymentReceived,
        OrderStatus.Provisioning,
        OrderStatus.Active
    };

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly INotificationService _notificationService;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, INotificationService notificationService, ILogger<InvoiceService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<Result<PagedResult<InvoiceDto>>> SearchAdminAsync(InvoiceFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new InvoiceFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching invoices (admin)");
            return Result<PagedResult<InvoiceDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching invoices.");
        }
    }

    public async Task<Result<PagedResult<InvoiceDto>>> GetMineAsync(InvoiceFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<InvoiceDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new InvoiceFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching invoices (customer)");
            return Result<PagedResult<InvoiceDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your invoices.");
        }
    }

    public Task<Result<InvoiceDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<InvoiceDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<InvoiceDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    private async Task<Result<InvoiceDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InvoiceDto>.Failure(ErrorCodes.BAD_REQUEST, "Invoice id is required.");

            var query = _dbContext.Invoices
                .AsNoTracking()
                .Include(i => i.Order)
                .Include(i => i.LastStatusChangedByUser)
                .Where(i => i.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(i => i.Order!.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            return entity is null
                ? Result<InvoiceDto>.Failure(ErrorCodes.NOT_FOUND, "Invoice not found.")
                : Result<InvoiceDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching invoice {Id}", id);
            return Result<InvoiceDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the invoice.");
        }
    }

    public async Task<Result<InvoiceDto>> CreateAsync(CreateInvoiceRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<InvoiceDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.OrderId == Guid.Empty)
                return Result<InvoiceDto>.Failure(ErrorCodes.VALIDATION_ERROR, "OrderId is required.");

            if (request.SubtotalAmount < 0)
                return Result<InvoiceDto>.Failure(ErrorCodes.VALIDATION_ERROR, "SubtotalAmount cannot be negative.");

            if (request.TaxAmount < 0)
                return Result<InvoiceDto>.Failure(ErrorCodes.VALIDATION_ERROR, "TaxAmount cannot be negative.");

            var now = DateTime.UtcNow;

            if (request.DueAtUtc.HasValue && request.DueAtUtc.Value < now.Date)
                return Result<InvoiceDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "DueAtUtc cannot be before today.");

            var order = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);

            if (order is null)
                return Result<InvoiceDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced order was not found.");

            if (!InvoiceCreatableOrderStatuses.Contains(order.Status))
            {
                return Result<InvoiceDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Invoices cannot be created for orders in status '{order.Status}'.");
            }

            var total = request.SubtotalAmount + request.TaxAmount;

            var entity = new Invoice
            {
                OrderId = order.Id,
                Status = InvoiceStatus.Issued,
                SubtotalAmount = request.SubtotalAmount,
                TaxAmount = request.TaxAmount,
                TotalAmount = total,
                AmountPaid = 0m,
                BalanceDue = total,
                CurrencyCode = DefaultCurrencyCode,
                IssuedAtUtc = now,
                DueAtUtc = request.DueAtUtc,
                Notes = Trim(request.Notes),
                AdminNotes = Trim(request.AdminNotes),
                ExternalReference = Trim(request.ExternalReference),
                LastStatusChangedByUserId = _currentUser.UserId
            };

            var invoiceNumber = await GenerateUniqueInvoiceNumberAsync(now, cancellationToken);
            if (invoiceNumber is null)
                return Result<InvoiceDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Could not generate a unique invoice number. Please retry.");

            entity.InvoiceNumber = invoiceNumber;

            _dbContext.Invoices.Add(entity);

            OrderStatus? orderPrev = null;
            OrderStatus? orderNew = null;
            if (order.Status == OrderStatus.Confirmed)
            {
                orderPrev = order.Status;
                order.Status = OrderStatus.AwaitingPayment;
                order.LastStatusChangedByUserId = _currentUser.UserId;
                orderNew = order.Status;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitInvoiceAuditAsync(
                AuditActionType.InvoiceCreated,
                AuditActorType.Admin,
                entity,
                summary: $"Invoice issued: {entity.InvoiceNumber} ({order.OrderNumber}, {entity.TotalAmount:0.00} {entity.CurrencyCode})",
                metadata: BuildMetadata(new
                {
                    orderId = entity.OrderId,
                    orderNumber = order.OrderNumber,
                    totalAmount = entity.TotalAmount,
                    dueAtUtc = entity.DueAtUtc
                }));

            if (orderPrev.HasValue && orderNew.HasValue)
                await EmitOrderStatusChangedAuditAsync(order, orderPrev.Value, orderNew.Value,
                    triggeredBy: "InvoiceIssued",
                    triggeredByNumber: entity.InvoiceNumber);

            var contact = await _dbContext.Orders
                .Where(o => o.Id == order.Id)
                .Select(o => new
                {
                    o.UserId,
                    Email = o.Email ?? (o.User != null ? o.User.Email : null),
                    Phone = o.PhoneNumber ?? (o.User != null ? o.User.PhoneNumber : null)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (contact is not null)
            {
                await TryNotifyAsync(
                    userId: contact.UserId,
                    type: NotificationType.InvoiceIssued,
                    email: contact.Email,
                    phone: contact.Phone,
                    subject: $"Invoice issued: {entity.InvoiceNumber}",
                    body: $"A new invoice has been issued for your Smart Future account.\n\nInvoice number: {entity.InvoiceNumber}\nOrder: {order.OrderNumber}\nAmount due: {entity.BalanceDue:0.00} {entity.CurrencyCode}\nDue date: {(entity.DueAtUtc?.ToString("yyyy-MM-dd") ?? "n/a")}",
                    relatedEntityType: nameof(Invoice),
                    relatedEntityId: entity.Id,
                    cancellationToken: cancellationToken);
            }

            return Result<InvoiceDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Invoice created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating invoice");
            return Result<InvoiceDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the invoice.");
        }
    }

    public async Task<Result<InvoiceDto>> AdminUpdateAsync(Guid id, AdminUpdateInvoiceRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InvoiceDto>.Failure(ErrorCodes.BAD_REQUEST, "Invoice id is required.");

            if (request is null)
                return Result<InvoiceDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var entity = await _dbContext.Invoices
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

            if (entity is null)
                return Result<InvoiceDto>.Failure(ErrorCodes.NOT_FOUND, "Invoice not found.");

            if (request.DueAtUtc.HasValue && entity.IssuedAtUtc.HasValue
                && request.DueAtUtc.Value < entity.IssuedAtUtc.Value.Date)
            {
                return Result<InvoiceDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "DueAtUtc cannot be before IssuedAtUtc.");
            }

            entity.DueAtUtc = request.DueAtUtc ?? entity.DueAtUtc;
            entity.Notes = Trim(request.Notes);
            entity.AdminNotes = Trim(request.AdminNotes);
            entity.ExternalReference = Trim(request.ExternalReference);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<InvoiceDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Invoice updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating invoice {Id}", id);
            return Result<InvoiceDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the invoice.");
        }
    }

    public async Task<Result<InvoiceDto>> AdminUpdateStatusAsync(Guid id, AdminUpdateInvoiceStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InvoiceDto>.Failure(ErrorCodes.BAD_REQUEST, "Invoice id is required.");

            if (request is null)
                return Result<InvoiceDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var entity = await _dbContext.Invoices
                .Include(i => i.Order)
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

            if (entity is null)
                return Result<InvoiceDto>.Failure(ErrorCodes.NOT_FOUND, "Invoice not found.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = request.Status;
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            switch (request.Status)
            {
                case InvoiceStatus.Issued:
                    if (entity.IssuedAtUtc is null) entity.IssuedAtUtc = now;
                    break;
                case InvoiceStatus.Paid:
                    if (entity.PaidAtUtc is null) entity.PaidAtUtc = now;
                    break;
                case InvoiceStatus.Void:
                    if (entity.VoidedAtUtc is null) entity.VoidedAtUtc = now;
                    break;
            }

            OrderStatus? orderPrev = null;
            OrderStatus? orderNew = null;

            if (entity.Order is not null)
            {
                if (previous != InvoiceStatus.Issued && request.Status == InvoiceStatus.Issued
                    && entity.Order.Status == OrderStatus.Confirmed)
                {
                    orderPrev = entity.Order.Status;
                    entity.Order.Status = OrderStatus.AwaitingPayment;
                    entity.Order.LastStatusChangedByUserId = _currentUser.UserId;
                    orderNew = entity.Order.Status;
                }
                else if (request.Status == InvoiceStatus.Paid
                         && entity.Order.Status == OrderStatus.AwaitingPayment)
                {
                    orderPrev = entity.Order.Status;
                    entity.Order.Status = OrderStatus.PaymentReceived;
                    entity.Order.LastStatusChangedByUserId = _currentUser.UserId;
                    orderNew = entity.Order.Status;
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (orderPrev.HasValue && orderNew.HasValue && entity.Order is not null)
            {
                await EmitOrderStatusChangedAuditAsync(entity.Order, orderPrev.Value, orderNew.Value,
                    triggeredBy: "InvoiceStatus",
                    triggeredByNumber: entity.InvoiceNumber);
            }

            return Result<InvoiceDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Invoice status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating invoice status {Id}", id);
            return Result<InvoiceDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the invoice status.");
        }
    }

    private IQueryable<Invoice> BuildQuery(InvoiceFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.Invoices
            .AsNoTracking()
            .Include(i => i.Order)
            .Include(i => i.LastStatusChangedByUser)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(i => i.Order!.UserId == restrictToUserId.Value);

        if (filter.OrderId.HasValue)
            query = query.Where(i => i.OrderId == filter.OrderId.Value);

        if (!string.IsNullOrWhiteSpace(filter.OrderNumber))
        {
            var v = filter.OrderNumber.Trim();
            query = query.Where(i => i.Order != null && EF.Functions.Like(i.Order.OrderNumber, $"%{v}%"));
        }

        if (filter.StatusFilter.HasValue)
            query = query.Where(i => i.Status == filter.StatusFilter.Value);

        if (filter.IssuedFromUtc.HasValue)
            query = query.Where(i => i.IssuedAtUtc != null && i.IssuedAtUtc >= filter.IssuedFromUtc.Value);

        if (filter.IssuedToUtc.HasValue)
            query = query.Where(i => i.IssuedAtUtc != null && i.IssuedAtUtc <= filter.IssuedToUtc.Value);

        if (filter.DueFromUtc.HasValue)
            query = query.Where(i => i.DueAtUtc != null && i.DueAtUtc >= filter.DueFromUtc.Value);

        if (filter.DueToUtc.HasValue)
            query = query.Where(i => i.DueAtUtc != null && i.DueAtUtc <= filter.DueToUtc.Value);

        if (filter.PaidFromUtc.HasValue)
            query = query.Where(i => i.PaidAtUtc != null && i.PaidAtUtc >= filter.PaidFromUtc.Value);

        if (filter.PaidToUtc.HasValue)
            query = query.Where(i => i.PaidAtUtc != null && i.PaidAtUtc <= filter.PaidToUtc.Value);

        if (filter.FromUtc.HasValue)
            query = query.Where(i => i.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(i => i.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(i =>
                EF.Functions.Like(i.InvoiceNumber, $"%{s}%") ||
                (i.Order != null && EF.Functions.Like(i.Order.OrderNumber, $"%{s}%")) ||
                (i.ExternalReference != null && EF.Functions.Like(i.ExternalReference, $"%{s}%")));
        }

        return query;
    }

    private static async Task<Result<PagedResult<InvoiceDto>>> ToPagedResultAsync(IQueryable<Invoice> query, InvoiceFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(i => new InvoiceDto
            {
                Id = i.Id,
                InvoiceNumber = i.InvoiceNumber,
                OrderId = i.OrderId,
                OrderNumber = i.Order != null ? i.Order.OrderNumber : null,
                OrderStatus = i.Order != null ? i.Order.Status : (OrderStatus?)null,
                Status = i.Status,
                SubtotalAmount = i.SubtotalAmount,
                TaxAmount = i.TaxAmount,
                TotalAmount = i.TotalAmount,
                AmountPaid = i.AmountPaid,
                BalanceDue = i.BalanceDue,
                CurrencyCode = i.CurrencyCode,
                IssuedAtUtc = i.IssuedAtUtc,
                DueAtUtc = i.DueAtUtc,
                PaidAtUtc = i.PaidAtUtc,
                VoidedAtUtc = i.VoidedAtUtc,
                Notes = i.Notes,
                AdminNotes = i.AdminNotes,
                ExternalReference = i.ExternalReference,
                LastStatusChangedByUserId = i.LastStatusChangedByUserId,
                LastStatusChangedByUserEmail = i.LastStatusChangedByUser != null
                    ? i.LastStatusChangedByUser.Email
                    : null,
                CreatedAtUtc = i.CreatedAtUtc,
                UpdatedAtUtc = i.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<InvoiceDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<InvoiceDto>>.Success(paged);
    }

    private async Task<Invoice?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.Invoices
            .AsNoTracking()
            .Include(i => i.Order)
            .Include(i => i.LastStatusChangedByUser)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    private async Task<string?> GenerateUniqueInvoiceNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < InvoiceNumberMaxAttempts; attempt++)
        {
            var candidate = BillingNumberGenerator.BuildCandidate(InvoiceNumberPrefix, now);
            var exists = await _dbContext.Invoices.AnyAsync(i => i.InvoiceNumber == candidate, cancellationToken);
            if (!exists) return candidate;
        }

        return null;
    }

    private async Task EmitInvoiceAuditAsync(AuditActionType actionType, AuditActorType actorType, Invoice entity, string summary, string? metadata)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = actionType,
            EntityType = AuditEntityType.Invoice,
            EntityId = entity.Id,
            EntityName = entity.InvoiceNumber,
            Summary = summary,
            MetadataJson = metadata,
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

    private async Task TryNotifyAsync(Guid userId, NotificationType type, string? email, string? phone, string subject,
        string body, string relatedEntityType, Guid relatedEntityId, CancellationToken cancellationToken)
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

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static InvoiceDto MapToDto(Invoice i) => new()
    {
        Id = i.Id,
        InvoiceNumber = i.InvoiceNumber,
        OrderId = i.OrderId,
        OrderNumber = i.Order?.OrderNumber,
        OrderStatus = i.Order?.Status,
        Status = i.Status,
        SubtotalAmount = i.SubtotalAmount,
        TaxAmount = i.TaxAmount,
        TotalAmount = i.TotalAmount,
        AmountPaid = i.AmountPaid,
        BalanceDue = i.BalanceDue,
        CurrencyCode = i.CurrencyCode,
        IssuedAtUtc = i.IssuedAtUtc,
        DueAtUtc = i.DueAtUtc,
        PaidAtUtc = i.PaidAtUtc,
        VoidedAtUtc = i.VoidedAtUtc,
        Notes = i.Notes,
        AdminNotes = i.AdminNotes,
        ExternalReference = i.ExternalReference,
        LastStatusChangedByUserId = i.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = i.LastStatusChangedByUser?.Email,
        CreatedAtUtc = i.CreatedAtUtc,
        UpdatedAtUtc = i.UpdatedAtUtc
    };
}
