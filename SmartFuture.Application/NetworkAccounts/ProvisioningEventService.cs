using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.NetworkAccounts;

public class ProvisioningEventService : IProvisioningEventService
{
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<ProvisioningEventService> _logger;

    public ProvisioningEventService(IAppDbContext dbContext, ILogger<ProvisioningEventService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<PagedResult<ProvisioningEventDto>>> SearchAsync(
        ProvisioningEventFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _dbContext.ProvisioningEvents
                .AsNoTracking()
                .Include(e => e.NetworkAccount)
                .Include(e => e.TriggeredByUser)
                .AsQueryable();

            if (filter.NetworkAccountId is { } accountId) query = query.Where(e => e.NetworkAccountId == accountId);
            if (filter.EventType is { } evType) query = query.Where(e => e.EventType == evType);
            if (filter.IsSuccess is { } success) query = query.Where(e => e.IsSuccess == success);
            if (filter.FromUtc is { } from) query = query.Where(e => e.CreatedAtUtc >= from);
            if (filter.ToUtc is { } to) query = query.Where(e => e.CreatedAtUtc <= to);

            var total = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderByDescending(e => e.CreatedAtUtc)
                .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
                .Select(e => new ProvisioningEventDto
                {
                    Id = e.Id, NetworkAccountId = e.NetworkAccountId,
                    NetworkAccountNumber = e.NetworkAccount != null ? e.NetworkAccount.AccountNumber : null,
                    EventType = e.EventType, ProviderName = e.ProviderName,
                    ProviderReference = e.ProviderReference, IsSuccess = e.IsSuccess,
                    FailureReason = e.FailureReason, Summary = e.Summary,
                    TriggeredByUserId = e.TriggeredByUserId,
                    TriggeredByUserEmail = e.TriggeredByUser != null ? e.TriggeredByUser.Email : null,
                    CreatedAtUtc = e.CreatedAtUtc
                })
                .ToListAsync(cancellationToken);

            return Result<PagedResult<ProvisioningEventDto>>.Success(
                new PagedResult<ProvisioningEventDto>(items, filter.Page, filter.PageSize, total));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching ProvisioningEvents");
            return Result<PagedResult<ProvisioningEventDto>>.Failure(ErrorCodes.EXCEPTION, "Could not load provisioning events.");
        }
    }

    public async Task<Result<ProvisioningEventDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ProvisioningEvents
            .AsNoTracking()
            .Include(e => e.NetworkAccount)
            .Include(e => e.TriggeredByUser)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (entity is null) return Result<ProvisioningEventDto>.Failure(ErrorCodes.NOT_FOUND, "Provisioning event not found.");

        return Result<ProvisioningEventDto>.Success(new ProvisioningEventDto
        {
            Id = entity.Id, NetworkAccountId = entity.NetworkAccountId,
            NetworkAccountNumber = entity.NetworkAccount?.AccountNumber,
            EventType = entity.EventType, ProviderName = entity.ProviderName,
            ProviderReference = entity.ProviderReference, IsSuccess = entity.IsSuccess,
            FailureReason = entity.FailureReason, Summary = entity.Summary,
            TriggeredByUserId = entity.TriggeredByUserId,
            TriggeredByUserEmail = entity.TriggeredByUser?.Email,
            CreatedAtUtc = entity.CreatedAtUtc
        });
    }
}
