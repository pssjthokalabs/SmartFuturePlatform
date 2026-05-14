using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.SupportTickets.Dtos;
using SmartFuture.Domain.SupportTickets;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.SupportTickets;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.SupportTickets;

public class SupportTicketService : ISupportTicketService
{
    private const string TicketNumberPrefix = "SUP";
    private const int TicketNumberMaxAttempts = 5;
    private const string TicketNumberAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private static readonly SupportTicketStatus[] CustomerCloseBlockedStatuses =
    {
        SupportTicketStatus.Closed,
        SupportTicketStatus.Resolved,
        SupportTicketStatus.Cancelled
    };

    private static readonly SupportTicketStatus[] FirstResponseTriggers =
    {
        SupportTicketStatus.AwaitingCustomer,
        SupportTicketStatus.InProgress,
        SupportTicketStatus.Resolved
    };

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly INotificationService _notificationService;
    private readonly ILogger<SupportTicketService> _logger;

    public SupportTicketService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, INotificationService notificationService, ILogger<SupportTicketService> logger)
    {
        _dbContext = dbContext;
        _notificationService = notificationService;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<PagedResult<SupportTicketDto>>> SearchAdminAsync(SupportTicketFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new SupportTicketFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching support tickets (admin)");
            return Result<PagedResult<SupportTicketDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching support tickets.");
        }
    }

    public async Task<Result<PagedResult<SupportTicketDto>>> GetMineAsync(SupportTicketFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<SupportTicketDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new SupportTicketFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching support tickets (customer)");
            return Result<PagedResult<SupportTicketDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your support tickets.");
        }
    }

    public Task<Result<SupportTicketDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<SupportTicketDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<SupportTicketDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    private async Task<Result<SupportTicketDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<SupportTicketDto>.Failure(ErrorCodes.BAD_REQUEST, "Ticket id is required.");

            var query = _dbContext.SupportTickets
                .AsNoTracking()
                .Include(t => t.User)
                .Include(t => t.AssignedToUser)
                .Include(t => t.LastStatusChangedByUser)
                .Include(t => t.Order)
                .Include(t => t.Installation)
                .Include(t => t.Invoice)
                .Include(t => t.Payment)
                .Where(t => t.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(t => t.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            return entity is null
                ? Result<SupportTicketDto>.Failure(ErrorCodes.NOT_FOUND, "Support ticket not found.")
                : Result<SupportTicketDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching support ticket {Id}", id);
            return Result<SupportTicketDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the support ticket.");
        }
    }

    public async Task<Result<SupportTicketDto>> CreateMineAsync(CreateSupportTicketRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<SupportTicketDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null)
                return Result<SupportTicketDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var subjectValidation = ValidateSubjectAndDescription(request.Subject, request.Description);
            if (subjectValidation is not null) return subjectValidation;

            var relatedValidation = await ValidateRelatedRecordOwnershipAsync(request, currentUserId.Value, cancellationToken);
            if (relatedValidation is not null) return relatedValidation;

            var now = DateTime.UtcNow;

            var entity = new SupportTicket
            {
                UserId = currentUserId.Value,
                OrderId = request.OrderId,
                CoverageRequestId = request.CoverageRequestId,
                InstallationId = request.InstallationId,
                InvoiceId = request.InvoiceId,
                PaymentId = request.PaymentId,
                DebitOrderMandateId = request.DebitOrderMandateId,
                Status = SupportTicketStatus.Open,
                Category = request.Category,
                Priority = request.Priority ?? SupportTicketPriority.Normal,
                Source = SupportTicketSource.CustomerApp,
                Subject = request.Subject.Trim(),
                Description = request.Description.Trim(),
                LastStatusChangedByUserId = currentUserId
            };

            var ticketNumber = await GenerateUniqueTicketNumberAsync(now, cancellationToken);
            if (ticketNumber is null)
                return Result<SupportTicketDto>.Failure(
                    ErrorCodes.EXCEPTION, "Could not generate a unique ticket number. Please retry.");

            entity.TicketNumber = ticketNumber;

            _dbContext.SupportTickets.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitTicketAuditAsync(
                AuditActionType.SupportTicketCreated,
                AuditActorType.User,
                entity,
                summary: $"Support ticket created: {entity.TicketNumber} (Category={entity.Category}, Priority={entity.Priority})",
                metadata: BuildMetadata(new
                {
                    category = entity.Category,
                    priority = entity.Priority,
                    orderId = entity.OrderId,
                    coverageRequestId = entity.CoverageRequestId,
                    installationId = entity.InstallationId,
                    invoiceId = entity.InvoiceId,
                    paymentId = entity.PaymentId,
                    debitOrderMandateId = entity.DebitOrderMandateId
                }));

            return Result<SupportTicketDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Support ticket created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating support ticket");
            return Result<SupportTicketDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the support ticket.");
        }
    }

    public async Task<Result<SupportTicketDto>> AdminUpdateAsync(Guid id, AdminUpdateSupportTicketRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<SupportTicketDto>.Failure(ErrorCodes.BAD_REQUEST, "Ticket id is required.");

            if (request is null)
                return Result<SupportTicketDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var subjectValidation = ValidateSubjectAndDescription(request.Subject, request.Description);
            if (subjectValidation is not null) return subjectValidation;

            var entity = await _dbContext.SupportTickets
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            if (entity is null)
                return Result<SupportTicketDto>.Failure(ErrorCodes.NOT_FOUND, "Support ticket not found.");

            entity.Category = request.Category;
            entity.Priority = request.Priority;
            entity.Subject = request.Subject.Trim();
            entity.Description = request.Description.Trim();
            entity.InternalSummary = Trim(request.InternalSummary);
            entity.ResolutionSummary = Trim(request.ResolutionSummary);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<SupportTicketDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Support ticket updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating support ticket {Id}", id);
            return Result<SupportTicketDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the support ticket.");
        }
    }

    public async Task<Result<SupportTicketDto>> AdminUpdateStatusAsync(Guid id, AdminUpdateSupportTicketStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<SupportTicketDto>.Failure(ErrorCodes.BAD_REQUEST, "Ticket id is required.");

            if (request is null)
                return Result<SupportTicketDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var entity = await _dbContext.SupportTickets
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            if (entity is null)
                return Result<SupportTicketDto>.Failure(ErrorCodes.NOT_FOUND, "Support ticket not found.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = request.Status;
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            if (!string.IsNullOrWhiteSpace(request.ResolutionSummary))
                entity.ResolutionSummary = request.ResolutionSummary.Trim();

            switch (request.Status)
            {
                case SupportTicketStatus.Resolved:
                    if (entity.ResolvedAtUtc is null) entity.ResolvedAtUtc = now;
                    break;
                case SupportTicketStatus.Closed:
                    if (entity.ClosedAtUtc is null) entity.ClosedAtUtc = now;
                    break;
                case SupportTicketStatus.Cancelled:
                    if (entity.CancelledAtUtc is null) entity.CancelledAtUtc = now;
                    break;
                case SupportTicketStatus.Reopened:
                    if (entity.ReopenedAtUtc is null) entity.ReopenedAtUtc = now;
                    break;
            }

            if (entity.FirstRespondedAtUtc is null && FirstResponseTriggers.Contains(request.Status))
                entity.FirstRespondedAtUtc = now;

            if (!string.IsNullOrWhiteSpace(request.InternalComment))
            {
                _dbContext.SupportTicketComments.Add(new SupportTicketComment
                {
                    SupportTicketId = entity.Id,
                    AuthorUserId = _currentUser.UserId ?? Guid.Empty,
                    Body = request.InternalComment.Trim(),
                    IsInternal = true,
                    IsSystemGenerated = false
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (previous != entity.Status)
            {
                await EmitTicketAuditAsync(
                    AuditActionType.SupportTicketStatusChanged,
                    AuditActorType.Admin,
                    entity,
                    summary: $"Support ticket status changed: {previous} -> {entity.Status} ({entity.TicketNumber})",
                    metadata: BuildMetadata(new { previous, newStatus = entity.Status }));

                if (entity.Status == SupportTicketStatus.Resolved || entity.Status == SupportTicketStatus.Closed)
                {
                    await NotifyTicketCustomerAsync(
                        entity,
                        NotificationType.SupportTicketStatusChanged,
                        subject: $"Support ticket {entity.Status.ToString().ToLowerInvariant()}: {entity.TicketNumber}",
                        body: $"Your support ticket has been updated.\n\nTicket number: {entity.TicketNumber}\nSubject: {entity.Subject}\nStatus: {entity.Status}\n{(string.IsNullOrWhiteSpace(entity.ResolutionSummary) ? string.Empty : $"\nResolution: {entity.ResolutionSummary}")}",
                        cancellationToken: cancellationToken);
                }
            }

            return Result<SupportTicketDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Support ticket status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating support ticket status {Id}", id);
            return Result<SupportTicketDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the support ticket status.");
        }
    }

    public async Task<Result<SupportTicketDto>> AssignAsync(Guid id, AssignSupportTicketRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<SupportTicketDto>.Failure(ErrorCodes.BAD_REQUEST, "Ticket id is required.");

            if (request is null)
                return Result<SupportTicketDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var entity = await _dbContext.SupportTickets
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            if (entity is null)
                return Result<SupportTicketDto>.Failure(ErrorCodes.NOT_FOUND, "Support ticket not found.");

            // Existence of AssignedToUserId is enforced by the FK constraint at SaveChanges.
            // A non-existent Guid will surface as a DbUpdateException → NOT_FOUND below.
            entity.AssignedToUserId = request.AssignedToUserId.HasValue && request.AssignedToUserId.Value != Guid.Empty
                ? request.AssignedToUserId
                : null;

            var now = DateTime.UtcNow;
            if (entity.FirstRespondedAtUtc is null && entity.AssignedToUserId.HasValue)
                entity.FirstRespondedAtUtc = now;

            if (!string.IsNullOrWhiteSpace(request.InternalComment))
            {
                _dbContext.SupportTicketComments.Add(new SupportTicketComment
                {
                    SupportTicketId = entity.Id,
                    AuthorUserId = _currentUser.UserId ?? Guid.Empty,
                    Body = request.InternalComment.Trim(),
                    IsInternal = true,
                    IsSystemGenerated = false
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<SupportTicketDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                entity.AssignedToUserId.HasValue ? "Support ticket assigned." : "Support ticket unassigned.");
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Assignment failed; the assignee user may not exist. {Id}", id);
            return Result<SupportTicketDto>.Failure(
                ErrorCodes.NOT_FOUND, "Assignee user not found or assignment could not be persisted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error assigning support ticket {Id}", id);
            return Result<SupportTicketDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while assigning the support ticket.");
        }
    }

    public async Task<Result> CloseMineAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (id == Guid.Empty)
                return Result.Failure(ErrorCodes.BAD_REQUEST, "Ticket id is required.");

            var entity = await _dbContext.SupportTickets
                .FirstOrDefaultAsync(t => t.Id == id && t.UserId == currentUserId.Value, cancellationToken);

            if (entity is null)
                return Result.Failure(ErrorCodes.NOT_FOUND, "Support ticket not found.");

            if (CustomerCloseBlockedStatuses.Contains(entity.Status))
                return Result.Failure(
                    ErrorCodes.CONFLICT,
                    $"Support tickets in status '{entity.Status}' cannot be closed by the customer.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = SupportTicketStatus.Closed;
            entity.ClosedAtUtc = now;
            entity.LastStatusChangedByUserId = currentUserId;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitTicketAuditAsync(
                AuditActionType.SupportTicketStatusChanged,
                AuditActorType.User,
                entity,
                summary: $"Support ticket closed by customer: {previous} -> {entity.Status} ({entity.TicketNumber})",
                metadata: BuildMetadata(new { previous, newStatus = entity.Status }));

            return Result.Success("Support ticket closed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error closing support ticket {Id}", id);
            return Result.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while closing the support ticket.");
        }
    }

    public async Task<Result<PagedResult<SupportTicketCommentDto>>> GetCommentsAdminAsync(SupportTicketCommentFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            if (filter is null || filter.SupportTicketId == Guid.Empty)
                return Result<PagedResult<SupportTicketCommentDto>>.Failure(
                    ErrorCodes.BAD_REQUEST, "SupportTicketId is required.");

            var ticketExists = await _dbContext.SupportTickets
                .AnyAsync(t => t.Id == filter.SupportTicketId, cancellationToken);

            if (!ticketExists)
                return Result<PagedResult<SupportTicketCommentDto>>.Failure(
                    ErrorCodes.NOT_FOUND, "Support ticket not found.");

            var query = BuildCommentQuery(filter, includeInternal: true);
            return await ToCommentPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching comments (admin) for ticket {Id}", filter?.SupportTicketId);
            return Result<PagedResult<SupportTicketCommentDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching comments.");
        }
    }

    public async Task<Result<PagedResult<SupportTicketCommentDto>>> GetCommentsMineAsync(SupportTicketCommentFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<SupportTicketCommentDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (filter is null || filter.SupportTicketId == Guid.Empty)
                return Result<PagedResult<SupportTicketCommentDto>>.Failure(
                    ErrorCodes.BAD_REQUEST, "SupportTicketId is required.");

            var ownsTicket = await _dbContext.SupportTickets
                .AnyAsync(t => t.Id == filter.SupportTicketId && t.UserId == currentUserId.Value, cancellationToken);

            if (!ownsTicket)
                return Result<PagedResult<SupportTicketCommentDto>>.Failure(
                    ErrorCodes.NOT_FOUND, "Support ticket not found.");

            var query = BuildCommentQuery(filter, includeInternal: false);
            return await ToCommentPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching comments (customer) for ticket {Id}", filter?.SupportTicketId);
            return Result<PagedResult<SupportTicketCommentDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching comments.");
        }
    }

    public async Task<Result<SupportTicketCommentDto>> AddCommentAdminAsync(Guid ticketId, AddSupportTicketCommentRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (ticketId == Guid.Empty)
                return Result<SupportTicketCommentDto>.Failure(ErrorCodes.BAD_REQUEST, "Ticket id is required.");

            if (request is null || string.IsNullOrWhiteSpace(request.Body))
                return Result<SupportTicketCommentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Comment body is required.");

            if (request.Body.Length > 4000)
                return Result<SupportTicketCommentDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Comment body cannot exceed 4000 characters.");

            var ticket = await _dbContext.SupportTickets
                .FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

            if (ticket is null)
                return Result<SupportTicketCommentDto>.Failure(ErrorCodes.NOT_FOUND, "Support ticket not found.");

            var now = DateTime.UtcNow;

            var comment = new SupportTicketComment
            {
                SupportTicketId = ticket.Id,
                AuthorUserId = _currentUser.UserId ?? Guid.Empty,
                Body = request.Body.Trim(),
                IsInternal = request.IsInternal,
                IsSystemGenerated = false
            };

            _dbContext.SupportTicketComments.Add(comment);

            var previousStatus = ticket.Status;
            var statusChanged = false;

            if (!request.IsInternal)
            {
                if (ticket.Status == SupportTicketStatus.AwaitingAgent || ticket.Status == SupportTicketStatus.Open)
                {
                    ticket.Status = SupportTicketStatus.AwaitingCustomer;
                    ticket.LastStatusChangedByUserId = _currentUser.UserId;
                    statusChanged = true;
                }

                if (ticket.FirstRespondedAtUtc is null)
                    ticket.FirstRespondedAtUtc = now;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (statusChanged)
            {
                await EmitTicketAuditAsync(
                    AuditActionType.SupportTicketStatusChanged,
                    AuditActorType.Admin,
                    ticket,
                    summary: $"Support ticket status changed by admin comment: {previousStatus} -> {ticket.Status} ({ticket.TicketNumber})",
                    metadata: BuildMetadata(new { previous = previousStatus, newStatus = ticket.Status, triggeredBy = "AdminComment" }));
            }

            if (!request.IsInternal)
            {
                await NotifyTicketCustomerAsync(
                    ticket,
                    NotificationType.SupportTicketCommentAdded,
                    subject: $"New reply on support ticket {ticket.TicketNumber}",
                    body: $"An agent has replied to your support ticket.\n\nTicket number: {ticket.TicketNumber}\nSubject: {ticket.Subject}\nStatus: {ticket.Status}\n\nPlease sign in to view the full reply.",
                    cancellationToken: cancellationToken);
            }

            var reloaded = await _dbContext.SupportTicketComments
                .AsNoTracking()
                .Include(c => c.AuthorUser)
                .FirstOrDefaultAsync(c => c.Id == comment.Id, cancellationToken);

            return Result<SupportTicketCommentDto>.Success(
                MapCommentToDto(reloaded ?? comment),
                "Comment added.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error adding admin comment to ticket {Id}", ticketId);
            return Result<SupportTicketCommentDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while adding the comment.");
        }
    }

    public async Task<Result<SupportTicketCommentDto>> AddCommentMineAsync(Guid ticketId, AddSupportTicketCommentRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<SupportTicketCommentDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (ticketId == Guid.Empty)
                return Result<SupportTicketCommentDto>.Failure(ErrorCodes.BAD_REQUEST, "Ticket id is required.");

            if (request is null || string.IsNullOrWhiteSpace(request.Body))
                return Result<SupportTicketCommentDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Comment body is required.");

            if (request.Body.Length > 4000)
                return Result<SupportTicketCommentDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Comment body cannot exceed 4000 characters.");

            var ticket = await _dbContext.SupportTickets
                .FirstOrDefaultAsync(t => t.Id == ticketId && t.UserId == currentUserId.Value, cancellationToken);

            if (ticket is null)
                return Result<SupportTicketCommentDto>.Failure(ErrorCodes.NOT_FOUND, "Support ticket not found.");

            var comment = new SupportTicketComment
            {
                SupportTicketId = ticket.Id,
                AuthorUserId = currentUserId.Value,
                Body = request.Body.Trim(),
                IsInternal = false,
                IsSystemGenerated = false
            };

            _dbContext.SupportTicketComments.Add(comment);

            var previousStatus = ticket.Status;
            var statusChanged = false;

            if (ticket.Status == SupportTicketStatus.AwaitingCustomer)
            {
                ticket.Status = SupportTicketStatus.AwaitingAgent;
                ticket.LastStatusChangedByUserId = currentUserId;
                statusChanged = true;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (statusChanged)
            {
                await EmitTicketAuditAsync(
                    AuditActionType.SupportTicketStatusChanged,
                    AuditActorType.User,
                    ticket,
                    summary: $"Support ticket status changed by customer comment: {previousStatus} -> {ticket.Status} ({ticket.TicketNumber})",
                    metadata: BuildMetadata(new { previous = previousStatus, newStatus = ticket.Status, triggeredBy = "CustomerComment" }));
            }

            var reloaded = await _dbContext.SupportTicketComments
                .AsNoTracking()
                .Include(c => c.AuthorUser)
                .FirstOrDefaultAsync(c => c.Id == comment.Id, cancellationToken);

            return Result<SupportTicketCommentDto>.Success(
                MapCommentToDto(reloaded ?? comment),
                "Comment added.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error adding customer comment to ticket {Id}", ticketId);
            return Result<SupportTicketCommentDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while adding the comment.");
        }
    }

    private async Task<Result<SupportTicketDto>?> ValidateRelatedRecordOwnershipAsync(CreateSupportTicketRequestDto request, Guid currentUserId, CancellationToken cancellationToken)
    {
        if (request.OrderId.HasValue)
        {
            var owns = await _dbContext.Orders
                .AnyAsync(o => o.Id == request.OrderId.Value && o.UserId == currentUserId, cancellationToken);
            if (!owns)
                return Result<SupportTicketDto>.Failure(
                    ErrorCodes.NOT_FOUND, "Referenced order was not found for the current user.");
        }

        if (request.CoverageRequestId.HasValue)
        {
            var owns = await _dbContext.CoverageRequests
                .AnyAsync(c => c.Id == request.CoverageRequestId.Value && c.UserId == currentUserId, cancellationToken);
            if (!owns)
                return Result<SupportTicketDto>.Failure(
                    ErrorCodes.NOT_FOUND, "Referenced coverage request was not found for the current user.");
        }

        if (request.InstallationId.HasValue)
        {
            var owns = await _dbContext.Installations
                .AnyAsync(i => i.Id == request.InstallationId.Value
                            && i.Order!.UserId == currentUserId, cancellationToken);
            if (!owns)
                return Result<SupportTicketDto>.Failure(
                    ErrorCodes.NOT_FOUND, "Referenced installation was not found for the current user.");
        }

        if (request.InvoiceId.HasValue)
        {
            var owns = await _dbContext.Invoices
                .AnyAsync(i => i.Id == request.InvoiceId.Value
                            && i.Order!.UserId == currentUserId, cancellationToken);
            if (!owns)
                return Result<SupportTicketDto>.Failure(
                    ErrorCodes.NOT_FOUND, "Referenced invoice was not found for the current user.");
        }

        if (request.PaymentId.HasValue)
        {
            var owns = await _dbContext.Payments
                .AnyAsync(p => p.Id == request.PaymentId.Value
                            && p.Invoice!.Order!.UserId == currentUserId, cancellationToken);
            if (!owns)
                return Result<SupportTicketDto>.Failure(
                    ErrorCodes.NOT_FOUND, "Referenced payment was not found for the current user.");
        }

        if (request.DebitOrderMandateId.HasValue)
        {
            var owns = await _dbContext.DebitOrderMandates
                .AnyAsync(d => d.Id == request.DebitOrderMandateId.Value && d.UserId == currentUserId, cancellationToken);
            if (!owns)
                return Result<SupportTicketDto>.Failure(
                    ErrorCodes.NOT_FOUND, "Referenced debit order mandate was not found for the current user.");
        }

        return null;
    }

    private IQueryable<SupportTicket> BuildQuery(SupportTicketFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.SupportTickets
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.AssignedToUser)
            .Include(t => t.LastStatusChangedByUser)
            .Include(t => t.Order)
            .Include(t => t.Installation)
            .Include(t => t.Invoice)
            .Include(t => t.Payment)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(t => t.UserId == restrictToUserId.Value);
        else if (filter.UserId.HasValue)
            query = query.Where(t => t.UserId == filter.UserId.Value);

        if (filter.OrderId.HasValue)
            query = query.Where(t => t.OrderId == filter.OrderId.Value);

        if (filter.CoverageRequestId.HasValue)
            query = query.Where(t => t.CoverageRequestId == filter.CoverageRequestId.Value);

        if (filter.InstallationId.HasValue)
            query = query.Where(t => t.InstallationId == filter.InstallationId.Value);

        if (filter.InvoiceId.HasValue)
            query = query.Where(t => t.InvoiceId == filter.InvoiceId.Value);

        if (filter.PaymentId.HasValue)
            query = query.Where(t => t.PaymentId == filter.PaymentId.Value);

        if (filter.DebitOrderMandateId.HasValue)
            query = query.Where(t => t.DebitOrderMandateId == filter.DebitOrderMandateId.Value);

        if (filter.Status.HasValue)
            query = query.Where(t => t.Status == filter.Status.Value);

        if (filter.Category.HasValue)
            query = query.Where(t => t.Category == filter.Category.Value);

        if (filter.Priority.HasValue)
            query = query.Where(t => t.Priority == filter.Priority.Value);

        if (filter.Source.HasValue)
            query = query.Where(t => t.Source == filter.Source.Value);

        if (filter.AssignedToUserId.HasValue)
            query = query.Where(t => t.AssignedToUserId == filter.AssignedToUserId.Value);

        if (filter.CreatedFromUtc.HasValue)
            query = query.Where(t => t.CreatedAtUtc >= filter.CreatedFromUtc.Value);

        if (filter.CreatedToUtc.HasValue)
            query = query.Where(t => t.CreatedAtUtc <= filter.CreatedToUtc.Value);

        if (filter.ResolvedFromUtc.HasValue)
            query = query.Where(t => t.ResolvedAtUtc != null && t.ResolvedAtUtc >= filter.ResolvedFromUtc.Value);

        if (filter.ResolvedToUtc.HasValue)
            query = query.Where(t => t.ResolvedAtUtc != null && t.ResolvedAtUtc <= filter.ResolvedToUtc.Value);

        if (filter.FromUtc.HasValue)
            query = query.Where(t => t.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(t => t.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(t =>
                EF.Functions.Like(t.TicketNumber, $"%{s}%") ||
                EF.Functions.Like(t.Subject, $"%{s}%") ||
                EF.Functions.Like(t.Description, $"%{s}%"));
        }

        return query;
    }

    private static async Task<Result<PagedResult<SupportTicketDto>>> ToPagedResultAsync(IQueryable<SupportTicket> query, SupportTicketFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(t => t.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(t => new SupportTicketDto
            {
                Id = t.Id,
                TicketNumber = t.TicketNumber,
                UserId = t.UserId,
                UserEmail = t.User != null ? t.User.Email : null,
                OrderId = t.OrderId,
                OrderNumber = t.Order != null ? t.Order.OrderNumber : null,
                CoverageRequestId = t.CoverageRequestId,
                InstallationId = t.InstallationId,
                InstallationNumber = t.Installation != null ? t.Installation.InstallationNumber : null,
                InvoiceId = t.InvoiceId,
                InvoiceNumber = t.Invoice != null ? t.Invoice.InvoiceNumber : null,
                PaymentId = t.PaymentId,
                PaymentNumber = t.Payment != null ? t.Payment.PaymentNumber : null,
                DebitOrderMandateId = t.DebitOrderMandateId,
                Status = t.Status,
                Category = t.Category,
                Priority = t.Priority,
                Source = t.Source,
                Subject = t.Subject,
                Description = t.Description,
                AssignedToUserId = t.AssignedToUserId,
                AssignedToUserEmail = t.AssignedToUser != null ? t.AssignedToUser.Email : null,
                FirstRespondedAtUtc = t.FirstRespondedAtUtc,
                ResolvedAtUtc = t.ResolvedAtUtc,
                ClosedAtUtc = t.ClosedAtUtc,
                CancelledAtUtc = t.CancelledAtUtc,
                ReopenedAtUtc = t.ReopenedAtUtc,
                LastStatusChangedByUserId = t.LastStatusChangedByUserId,
                LastStatusChangedByUserEmail = t.LastStatusChangedByUser != null
                    ? t.LastStatusChangedByUser.Email
                    : null,
                InternalSummary = t.InternalSummary,
                ResolutionSummary = t.ResolutionSummary,
                CreatedAtUtc = t.CreatedAtUtc,
                UpdatedAtUtc = t.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<SupportTicketDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<SupportTicketDto>>.Success(paged);
    }

    private IQueryable<SupportTicketComment> BuildCommentQuery(SupportTicketCommentFilterRequestDto filter, bool includeInternal)
    {
        var query = _dbContext.SupportTicketComments
            .AsNoTracking()
            .Include(c => c.AuthorUser)
            .Where(c => c.SupportTicketId == filter.SupportTicketId);

        if (!includeInternal || !filter.IncludeInternal)
            query = query.Where(c => !c.IsInternal);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(c => EF.Functions.Like(c.Body, $"%{s}%"));
        }

        if (filter.FromUtc.HasValue)
            query = query.Where(c => c.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(c => c.CreatedAtUtc <= filter.ToUtc.Value);

        return query;
    }

    private static async Task<Result<PagedResult<SupportTicketCommentDto>>> ToCommentPagedResultAsync(IQueryable<SupportTicketComment> query, SupportTicketCommentFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(c => c.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(c => new SupportTicketCommentDto
            {
                Id = c.Id,
                SupportTicketId = c.SupportTicketId,
                AuthorUserId = c.AuthorUserId,
                AuthorUserEmail = c.AuthorUser != null ? c.AuthorUser.Email : null,
                Body = c.Body,
                IsInternal = c.IsInternal,
                IsSystemGenerated = c.IsSystemGenerated,
                CreatedAtUtc = c.CreatedAtUtc,
                UpdatedAtUtc = c.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<SupportTicketCommentDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<SupportTicketCommentDto>>.Success(paged);
    }

    private async Task<SupportTicket?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.SupportTickets
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.AssignedToUser)
            .Include(t => t.LastStatusChangedByUser)
            .Include(t => t.Order)
            .Include(t => t.Installation)
            .Include(t => t.Invoice)
            .Include(t => t.Payment)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    private static Result<SupportTicketDto>? ValidateSubjectAndDescription(string? subject, string? description)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return Result<SupportTicketDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Subject is required.");

        if (subject.Trim().Length > 200)
            return Result<SupportTicketDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Subject cannot exceed 200 characters.");

        if (string.IsNullOrWhiteSpace(description))
            return Result<SupportTicketDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Description is required.");

        if (description.Trim().Length > 4000)
            return Result<SupportTicketDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Description cannot exceed 4000 characters.");

        return null;
    }

    private async Task<string?> GenerateUniqueTicketNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var datePart = now.ToString("yyyyMMdd");

        for (var attempt = 0; attempt < TicketNumberMaxAttempts; attempt++)
        {
            var candidate = $"{TicketNumberPrefix}-{datePart}-{GenerateRandomSuffix(6)}";
            var exists = await _dbContext.SupportTickets
                .AnyAsync(t => t.TicketNumber == candidate, cancellationToken);

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
            chars[i] = TicketNumberAlphabet[buffer[i] % TicketNumberAlphabet.Length];

        return new string(chars);
    }

    private async Task EmitTicketAuditAsync(AuditActionType actionType, AuditActorType actorType, SupportTicket entity, string summary, string? metadata)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = actionType,
            EntityType = AuditEntityType.SupportTicket,
            EntityId = entity.Id,
            EntityName = entity.TicketNumber,
            Summary = summary,
            MetadataJson = metadata,
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

    private async Task NotifyTicketCustomerAsync(SupportTicket ticket, NotificationType type, string subject, string body, CancellationToken cancellationToken)
    {
        try
        {
            var contact = await _dbContext.SupportTickets
                .Where(t => t.Id == ticket.Id)
                .Select(t => new
                {
                    t.UserId,
                    Email = t.User != null ? t.User.Email : null,
                    Phone = t.User != null ? t.User.PhoneNumber : null
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (contact is null || string.IsNullOrWhiteSpace(contact.Email))
                return;

            await _notificationService.SendAsync(new SendNotificationRequestDto
            {
                UserId = contact.UserId,
                Channel = NotificationChannel.Email,
                Type = type,
                RecipientEmail = contact.Email,
                RecipientPhone = contact.Phone,
                Subject = subject,
                Body = body,
                RelatedEntityType = nameof(SupportTicket),
                RelatedEntityId = ticket.Id
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Type} notification for support ticket {Id}", type, ticket.Id);
        }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SupportTicketDto MapToDto(SupportTicket t) => new()
    {
        Id = t.Id,
        TicketNumber = t.TicketNumber,
        UserId = t.UserId,
        UserEmail = t.User?.Email,
        OrderId = t.OrderId,
        OrderNumber = t.Order?.OrderNumber,
        CoverageRequestId = t.CoverageRequestId,
        InstallationId = t.InstallationId,
        InstallationNumber = t.Installation?.InstallationNumber,
        InvoiceId = t.InvoiceId,
        InvoiceNumber = t.Invoice?.InvoiceNumber,
        PaymentId = t.PaymentId,
        PaymentNumber = t.Payment?.PaymentNumber,
        DebitOrderMandateId = t.DebitOrderMandateId,
        Status = t.Status,
        Category = t.Category,
        Priority = t.Priority,
        Source = t.Source,
        Subject = t.Subject,
        Description = t.Description,
        AssignedToUserId = t.AssignedToUserId,
        AssignedToUserEmail = t.AssignedToUser?.Email,
        FirstRespondedAtUtc = t.FirstRespondedAtUtc,
        ResolvedAtUtc = t.ResolvedAtUtc,
        ClosedAtUtc = t.ClosedAtUtc,
        CancelledAtUtc = t.CancelledAtUtc,
        ReopenedAtUtc = t.ReopenedAtUtc,
        LastStatusChangedByUserId = t.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = t.LastStatusChangedByUser?.Email,
        InternalSummary = t.InternalSummary,
        ResolutionSummary = t.ResolutionSummary,
        CreatedAtUtc = t.CreatedAtUtc,
        UpdatedAtUtc = t.UpdatedAtUtc
    };

    private static SupportTicketCommentDto MapCommentToDto(SupportTicketComment c) => new()
    {
        Id = c.Id,
        SupportTicketId = c.SupportTicketId,
        AuthorUserId = c.AuthorUserId,
        AuthorUserEmail = c.AuthorUser?.Email,
        Body = c.Body,
        IsInternal = c.IsInternal,
        IsSystemGenerated = c.IsSystemGenerated,
        CreatedAtUtc = c.CreatedAtUtc,
        UpdatedAtUtc = c.UpdatedAtUtc
    };
}
