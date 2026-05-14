using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Auditing;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Auditing;

public class AuditService : IAuditService
{
    private const int MaxEntityNameLength = 200;
    private const int MaxSummaryLength = 1000;
    private const int MaxFailureReasonLength = 1000;
    private const int MaxIpAddressLength = 100;
    private const int MaxUserAgentLength = 500;

    private readonly IAppDbContext _dbContext;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IAppDbContext dbContext, ILogger<AuditService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task LogAsync(CreateAuditLogRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null) return;

            var entry = new AuditLog
            {
                ActorUserId = request.ActorUserId,
                ActorType = request.ActorType,
                ActionType = request.ActionType,
                EntityType = request.EntityType,
                EntityId = request.EntityId,
                EntityName = Truncate(request.EntityName, MaxEntityNameLength),
                Summary = Truncate(request.Summary, MaxSummaryLength),
                MetadataJson = request.MetadataJson,
                IpAddress = Truncate(request.IpAddress, MaxIpAddressLength),
                UserAgent = Truncate(request.UserAgent, MaxUserAgentLength),
                IsSuccess = request.IsSuccess,
                FailureReason = Truncate(request.FailureReason, MaxFailureReasonLength)
            };

            _dbContext.AuditLogs.Add(entry);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write audit log entry. ActionType={ActionType}, EntityType={EntityType}, EntityId={EntityId}",
                request?.ActionType, request?.EntityType, request?.EntityId);
        }
    }

    public async Task<Result<PagedResult<AuditLogDto>>> SearchAsync(AuditLogFilterRequestDto filter)
    {
        try
        {
            filter ??= new AuditLogFilterRequestDto();

            var query = _dbContext.AuditLogs
                .AsNoTracking()
                .Include(a => a.ActorUser)
                .AsQueryable();

            if (filter.ActorUserId.HasValue)
                query = query.Where(a => a.ActorUserId == filter.ActorUserId.Value);

            if (filter.ActorType.HasValue)
                query = query.Where(a => a.ActorType == filter.ActorType.Value);

            if (filter.ActionType.HasValue)
                query = query.Where(a => a.ActionType == filter.ActionType.Value);

            if (filter.EntityType.HasValue)
                query = query.Where(a => a.EntityType == filter.EntityType.Value);

            if (filter.EntityId.HasValue)
                query = query.Where(a => a.EntityId == filter.EntityId.Value);

            if (filter.IsSuccess.HasValue)
                query = query.Where(a => a.IsSuccess == filter.IsSuccess.Value);

            if (filter.FromUtc.HasValue)
                query = query.Where(a => a.CreatedAtUtc >= filter.FromUtc.Value);

            if (filter.ToUtc.HasValue)
                query = query.Where(a => a.CreatedAtUtc <= filter.ToUtc.Value);

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var s = filter.Search.Trim();
                query = query.Where(a =>
                    (a.EntityName != null && EF.Functions.Like(a.EntityName, $"%{s}%")) ||
                    (a.Summary != null && EF.Functions.Like(a.Summary, $"%{s}%")) ||
                    (a.FailureReason != null && EF.Functions.Like(a.FailureReason, $"%{s}%")));
            }

            var totalCount = await query.CountAsync();

            var items = await query
                .OrderByDescending(a => a.CreatedAtUtc)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .Select(a => new AuditLogDto
                {
                    Id = a.Id,
                    CreatedAtUtc = a.CreatedAtUtc,
                    ActorUserId = a.ActorUserId,
                    ActorUserEmail = a.ActorUser != null ? a.ActorUser.Email : null,
                    ActorUserFullName = a.ActorUser != null
                        ? (a.ActorUser.FirstName + " " + a.ActorUser.LastName).Trim()
                        : null,
                    ActorType = a.ActorType,
                    ActionType = a.ActionType,
                    EntityType = a.EntityType,
                    EntityId = a.EntityId,
                    EntityName = a.EntityName,
                    Summary = a.Summary,
                    MetadataJson = a.MetadataJson,
                    IpAddress = a.IpAddress,
                    UserAgent = a.UserAgent,
                    IsSuccess = a.IsSuccess,
                    FailureReason = a.FailureReason
                })
                .ToListAsync();

            var paged = new PagedResult<AuditLogDto>(items, filter.Page, filter.PageSize, totalCount);
            return Result<PagedResult<AuditLogDto>>.Success(paged);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching audit logs");
            return Result<PagedResult<AuditLogDto>>.Failure(ErrorCodes.EXCEPTION,
                "An unexpected error occurred while searching audit logs.");
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}
