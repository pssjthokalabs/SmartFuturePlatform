using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Notifications;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Notifications;

public class NotificationService : INotificationService
{
    private const int MaxBodyLength = 8000;
    private const int MaxSubjectLength = 300;
    private const int MaxRecipientEmailLength = 256;
    private const int MaxRecipientPhoneLength = 50;
    private const int MaxFailureReasonLength = 2000;
    private const int MaxProviderNameLength = 100;
    private const int MaxProviderMessageIdLength = 200;
    private const int MaxRelatedEntityTypeLength = 100;

    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IAppDbContext _dbContext;
    private readonly INotificationSender _sender;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IAppDbContext dbContext, INotificationSender sender, ILogger<NotificationService> logger)
    {
        _dbContext = dbContext;
        _sender = sender;
        _logger = logger;
    }

    public async Task<Result<OutboundNotificationDto>> SendAsync(SendNotificationRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<OutboundNotificationDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateSend(request);
            if (validation is not null) return validation;

            var entity = new OutboundNotification
            {
                UserId = request.UserId,
                Channel = request.Channel,
                Type = request.Type,
                Status = NotificationStatus.Pending,
                RecipientEmail = Truncate(request.RecipientEmail, MaxRecipientEmailLength),
                RecipientPhone = Truncate(request.RecipientPhone, MaxRecipientPhoneLength),
                Subject = Truncate(request.Subject, MaxSubjectLength),
                Body = Truncate(request.Body.Trim(), MaxBodyLength) ?? string.Empty,
                RelatedEntityType = Truncate(request.RelatedEntityType, MaxRelatedEntityTypeLength),
                RelatedEntityId = request.RelatedEntityId,
                MetadataJson = request.MetadataJson,
                AttemptCount = 0
            };

            _dbContext.OutboundNotifications.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            NotificationSendResult sendResult;
            try
            {
                sendResult = await _sender.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "INotificationSender threw while dispatching notification {Id} (Channel={Channel}, Type={Type})",
                    entity.Id, entity.Channel, entity.Type);
                sendResult = NotificationSendResult.FailedResult(
                    providerName: entity.ProviderName ?? "unknown",
                    failureReason: "Sender threw an exception. See server logs.");
            }

            entity.AttemptCount += 1;
            entity.LastAttemptAtUtc = DateTime.UtcNow;
            entity.ProviderName = Truncate(sendResult.ProviderName, MaxProviderNameLength);

            if (sendResult.Success)
            {
                entity.Status = NotificationStatus.Sent;
                entity.SentAtUtc = DateTime.UtcNow;
                entity.ProviderMessageId = Truncate(sendResult.ProviderMessageId, MaxProviderMessageIdLength);
                entity.FailureReason = null;
            }
            else
            {
                entity.Status = NotificationStatus.Failed;
                entity.FailureReason = Truncate(sendResult.FailureReason, MaxFailureReasonLength);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<OutboundNotificationDto>.Success(
                MapToDto(entity),
                sendResult.Success ? "Notification sent." : "Notification recorded as failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending notification");
            return Result<OutboundNotificationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while sending the notification.");
        }
    }

    public async Task<Result<PagedResult<OutboundNotificationDto>>> SearchAdminAsync(NotificationFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new NotificationFilterRequestDto();
            var query = _dbContext.OutboundNotifications.AsNoTracking().AsQueryable();

            if (filter.UserId.HasValue)
                query = query.Where(n => n.UserId == filter.UserId.Value);

            if (filter.Channel.HasValue)
                query = query.Where(n => n.Channel == filter.Channel.Value);

            if (filter.Type.HasValue)
                query = query.Where(n => n.Type == filter.Type.Value);

            if (filter.StatusFilter.HasValue)
                query = query.Where(n => n.Status == filter.StatusFilter.Value);

            if (!string.IsNullOrWhiteSpace(filter.RecipientEmail))
            {
                var v = filter.RecipientEmail.Trim();
                query = query.Where(n => n.RecipientEmail != null && EF.Functions.Like(n.RecipientEmail, $"%{v}%"));
            }

            if (!string.IsNullOrWhiteSpace(filter.RecipientPhone))
            {
                var v = filter.RecipientPhone.Trim();
                query = query.Where(n => n.RecipientPhone != null && EF.Functions.Like(n.RecipientPhone, $"%{v}%"));
            }

            if (!string.IsNullOrWhiteSpace(filter.RelatedEntityType))
            {
                var v = filter.RelatedEntityType.Trim();
                query = query.Where(n => n.RelatedEntityType == v);
            }

            if (filter.RelatedEntityId.HasValue)
                query = query.Where(n => n.RelatedEntityId == filter.RelatedEntityId.Value);

            if (filter.SentFromUtc.HasValue)
                query = query.Where(n => n.SentAtUtc != null && n.SentAtUtc >= filter.SentFromUtc.Value);

            if (filter.SentToUtc.HasValue)
                query = query.Where(n => n.SentAtUtc != null && n.SentAtUtc <= filter.SentToUtc.Value);

            if (filter.FromUtc.HasValue)
                query = query.Where(n => n.CreatedAtUtc >= filter.FromUtc.Value);

            if (filter.ToUtc.HasValue)
                query = query.Where(n => n.CreatedAtUtc <= filter.ToUtc.Value);

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var s = filter.Search.Trim();
                query = query.Where(n =>
                    (n.Subject != null && EF.Functions.Like(n.Subject, $"%{s}%")) ||
                    EF.Functions.Like(n.Body, $"%{s}%") ||
                    (n.RecipientEmail != null && EF.Functions.Like(n.RecipientEmail, $"%{s}%")) ||
                    (n.RecipientPhone != null && EF.Functions.Like(n.RecipientPhone, $"%{s}%")));
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                .OrderByDescending(n => n.CreatedAtUtc)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .Select(n => new OutboundNotificationDto
                {
                    Id = n.Id,
                    UserId = n.UserId,
                    Channel = n.Channel,
                    Type = n.Type,
                    Status = n.Status,
                    RecipientEmail = n.RecipientEmail,
                    RecipientPhone = n.RecipientPhone,
                    Subject = n.Subject,
                    Body = n.Body,
                    ProviderName = n.ProviderName,
                    ProviderMessageId = n.ProviderMessageId,
                    FailureReason = n.FailureReason,
                    AttemptCount = n.AttemptCount,
                    LastAttemptAtUtc = n.LastAttemptAtUtc,
                    SentAtUtc = n.SentAtUtc,
                    RelatedEntityType = n.RelatedEntityType,
                    RelatedEntityId = n.RelatedEntityId,
                    CreatedAtUtc = n.CreatedAtUtc,
                    UpdatedAtUtc = n.UpdatedAtUtc
                })
                .ToListAsync(cancellationToken);

            var paged = new PagedResult<OutboundNotificationDto>(items, filter.Page, filter.PageSize, totalCount);
            return Result<PagedResult<OutboundNotificationDto>>.Success(paged);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching notifications");
            return Result<PagedResult<OutboundNotificationDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching notifications.");
        }
    }

    public async Task<Result<OutboundNotificationDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<OutboundNotificationDto>.Failure(
                    ErrorCodes.BAD_REQUEST, "Notification id is required.");

            var entity = await _dbContext.OutboundNotifications
                .AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

            return entity is null
                ? Result<OutboundNotificationDto>.Failure(ErrorCodes.NOT_FOUND, "Notification not found.")
                : Result<OutboundNotificationDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching notification {Id}", id);
            return Result<OutboundNotificationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the notification.");
        }
    }

    private static Result<OutboundNotificationDto>? ValidateSend(SendNotificationRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return Result<OutboundNotificationDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Body is required.");

        if (request.Body.Length > MaxBodyLength)
            return Result<OutboundNotificationDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, $"Body cannot exceed {MaxBodyLength} characters.");

        switch (request.Channel)
        {
            case NotificationChannel.Email:
                if (string.IsNullOrWhiteSpace(request.RecipientEmail))
                    return Result<OutboundNotificationDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR, "RecipientEmail is required for the Email channel.");
                if (!EmailRegex.IsMatch(request.RecipientEmail.Trim()))
                    return Result<OutboundNotificationDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR, "RecipientEmail is not in a valid format.");
                if (string.IsNullOrWhiteSpace(request.Subject))
                    return Result<OutboundNotificationDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR, "Subject is required for the Email channel.");
                break;

            case NotificationChannel.Sms:
                if (string.IsNullOrWhiteSpace(request.RecipientPhone))
                    return Result<OutboundNotificationDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR, "RecipientPhone is required for the Sms channel.");
                var phone = request.RecipientPhone.Trim();
                if (phone.Length < 6 || phone.Length > MaxRecipientPhoneLength)
                    return Result<OutboundNotificationDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR, $"RecipientPhone length must be between 6 and {MaxRecipientPhoneLength} characters.");
                break;

            case NotificationChannel.Push:
            case NotificationChannel.System:
                // Either user id or recipient hint is required for downstream targeting.
                if (request.UserId is null
                    && string.IsNullOrWhiteSpace(request.RecipientEmail)
                    && string.IsNullOrWhiteSpace(request.RecipientPhone))
                {
                    return Result<OutboundNotificationDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR,
                        "UserId or a recipient hint is required for Push/System notifications.");
                }
                break;
        }

        return null;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }

    private static OutboundNotificationDto MapToDto(OutboundNotification n) => new()
    {
        Id = n.Id,
        UserId = n.UserId,
        Channel = n.Channel,
        Type = n.Type,
        Status = n.Status,
        RecipientEmail = n.RecipientEmail,
        RecipientPhone = n.RecipientPhone,
        Subject = n.Subject,
        Body = n.Body,
        ProviderName = n.ProviderName,
        ProviderMessageId = n.ProviderMessageId,
        FailureReason = n.FailureReason,
        AttemptCount = n.AttemptCount,
        LastAttemptAtUtc = n.LastAttemptAtUtc,
        SentAtUtc = n.SentAtUtc,
        RelatedEntityType = n.RelatedEntityType,
        RelatedEntityId = n.RelatedEntityId,
        CreatedAtUtc = n.CreatedAtUtc,
        UpdatedAtUtc = n.UpdatedAtUtc
    };
}
