using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.CoverageRequests.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.CoverageRequests;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.CoverageRequests;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.CoverageRequests;

public class CoverageRequestService : ICoverageRequestService
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly CoverageRequestStatus[] CustomerCancellableStatuses =
    {
        CoverageRequestStatus.Submitted,
        CoverageRequestStatus.InReview,
        CoverageRequestStatus.MoreInfoRequired
    };

    private static readonly CoverageRequestStatus[] ReviewedStatuses =
    {
        CoverageRequestStatus.Available,
        CoverageRequestStatus.Unavailable,
        CoverageRequestStatus.MoreInfoRequired,
        CoverageRequestStatus.Closed,
        CoverageRequestStatus.Cancelled
    };

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<CoverageRequestService> _logger;

    public CoverageRequestService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, ILogger<CoverageRequestService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<PagedResult<CoverageRequestDto>>> SearchAdminAsync(CoverageRequestFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new CoverageRequestFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching coverage requests (admin)");
            return Result<PagedResult<CoverageRequestDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching coverage requests.");
        }
    }

    public async Task<Result<PagedResult<CoverageRequestDto>>> GetMineAsync(CoverageRequestFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<CoverageRequestDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new CoverageRequestFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching coverage requests (customer)");
            return Result<PagedResult<CoverageRequestDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your coverage requests.");
        }
    }

    public Task<Result<CoverageRequestDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<CoverageRequestDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<CoverageRequestDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    private async Task<Result<CoverageRequestDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<CoverageRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Coverage request id is required.");

            var query = _dbContext.CoverageRequests
                .AsNoTracking()
                .Include(c => c.ServicePackage)
                .Include(c => c.ReviewedByUser)
                .Where(c => c.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(c => c.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            return entity is null
                ? Result<CoverageRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Coverage request not found.")
                : Result<CoverageRequestDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching coverage request {Id}", id);
            return Result<CoverageRequestDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the coverage request.");
        }
    }

    public async Task<Result<CoverageRequestDto>> CreateMineAsync(CreateCoverageRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<CoverageRequestDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null)
                return Result<CoverageRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateAddressAndContact(
                request.AddressLine1, request.Latitude, request.Longitude, request.Email, request.PhoneNumber);
            if (validation is not null) return validation;

            if (request.ServicePackageId.HasValue)
            {
                var packageGuard = await ValidateServicePackageAsync(request.ServicePackageId.Value, cancellationToken);
                if (packageGuard is not null) return packageGuard;
            }

            var customerProfileId = await _dbContext.CustomerProfiles
                .Where(p => p.UserId == currentUserId.Value)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(cancellationToken);

            var entity = new CoverageRequest
            {
                UserId = currentUserId.Value,
                CustomerProfileId = customerProfileId,
                ServicePackageId = request.ServicePackageId,
                RequestedServiceType = request.RequestedServiceType,
                Status = CoverageRequestStatus.Submitted,
                Source = CoverageRequestSource.CustomerApp,
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
                CustomerNotes = Trim(request.CustomerNotes)
            };

            _dbContext.CoverageRequests.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.CoverageRequestCreated,
                AuditActorType.User,
                entity,
                summary: $"Coverage request submitted: {BuildEntityName(entity)}",
                metadata: BuildMetadata(new
                {
                    entity.RequestedServiceType,
                    entity.ServicePackageId,
                    entity.City,
                    entity.Suburb,
                    entity.Province
                }));

            return Result<CoverageRequestDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Coverage request submitted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating coverage request");
            return Result<CoverageRequestDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the coverage request.");
        }
    }

    public async Task<Result<CoverageRequestDto>> AdminUpdateAsync(Guid id, AdminUpdateCoverageRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<CoverageRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Coverage request id is required.");

            if (request is null)
                return Result<CoverageRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var validation = ValidateAddressAndContact(
                request.AddressLine1, request.Latitude, request.Longitude, request.Email, request.PhoneNumber);
            if (validation is not null) return validation;

            if (request.ServicePackageId.HasValue)
            {
                var packageGuard = await ValidateServicePackageAsync(request.ServicePackageId.Value, cancellationToken);
                if (packageGuard is not null) return packageGuard;
            }

            var entity = await _dbContext.CoverageRequests
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

            if (entity is null)
                return Result<CoverageRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Coverage request not found.");

            entity.ServicePackageId = request.ServicePackageId;
            entity.RequestedServiceType = request.RequestedServiceType;
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
            entity.AdminNotes = Trim(request.AdminNotes);
            entity.CoverageResultSummary = Trim(request.CoverageResultSummary);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<CoverageRequestDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Coverage request updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating coverage request {Id}", id);
            return Result<CoverageRequestDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the coverage request.");
        }
    }

    public async Task<Result<CoverageRequestDto>> AdminUpdateStatusAsync(Guid id, AdminUpdateCoverageRequestStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<CoverageRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Coverage request id is required.");

            if (request is null)
                return Result<CoverageRequestDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var entity = await _dbContext.CoverageRequests
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

            if (entity is null)
                return Result<CoverageRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Coverage request not found.");

            var previous = entity.Status;
            entity.Status = request.Status;

            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            if (!string.IsNullOrWhiteSpace(request.CoverageResultSummary))
                entity.CoverageResultSummary = request.CoverageResultSummary.Trim();

            if (ReviewedStatuses.Contains(request.Status))
            {
                entity.ReviewedAtUtc = DateTime.UtcNow;
                entity.ReviewedByUserId = _currentUser.UserId;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (previous != entity.Status)
            {
                await EmitAuditAsync(
                    AuditActionType.CoverageRequestStatusChanged,
                    AuditActorType.Admin,
                    entity,
                    summary: $"Coverage request status changed: {previous} -> {entity.Status}",
                    metadata: BuildMetadata(new { previous, newStatus = entity.Status }));
            }

            return Result<CoverageRequestDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Coverage request status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating coverage request status {Id}", id);
            return Result<CoverageRequestDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the coverage request status.");
        }
    }

    public async Task<Result> CancelMineAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (id == Guid.Empty)
                return Result.Failure(ErrorCodes.BAD_REQUEST, "Coverage request id is required.");

            var entity = await _dbContext.CoverageRequests
                .FirstOrDefaultAsync(c => c.Id == id && c.UserId == currentUserId.Value, cancellationToken);

            if (entity is null)
                return Result.Failure(ErrorCodes.NOT_FOUND, "Coverage request not found.");

            if (!CustomerCancellableStatuses.Contains(entity.Status))
                return Result.Failure(
                    ErrorCodes.CONFLICT,
                    $"Coverage requests in status '{entity.Status}' cannot be cancelled.");

            var previous = entity.Status;
            entity.Status = CoverageRequestStatus.Cancelled;
            entity.ReviewedAtUtc = DateTime.UtcNow;
            entity.ReviewedByUserId = currentUserId;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.CoverageRequestStatusChanged,
                AuditActorType.User,
                entity,
                summary: $"Coverage request cancelled by customer: {previous} -> {entity.Status}",
                metadata: BuildMetadata(new { previous, newStatus = entity.Status }));

            return Result.Success("Coverage request cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error cancelling coverage request {Id}", id);
            return Result.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while cancelling the coverage request.");
        }
    }

    private IQueryable<CoverageRequest> BuildQuery(CoverageRequestFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.CoverageRequests
            .AsNoTracking()
            .Include(c => c.ServicePackage)
            .Include(c => c.ReviewedByUser)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(c => c.UserId == restrictToUserId.Value);
        else if (filter.UserId.HasValue)
            query = query.Where(c => c.UserId == filter.UserId.Value);

        if (filter.CustomerProfileId.HasValue)
            query = query.Where(c => c.CustomerProfileId == filter.CustomerProfileId.Value);

        if (filter.ServicePackageId.HasValue)
            query = query.Where(c => c.ServicePackageId == filter.ServicePackageId.Value);

        if (filter.RequestedServiceType.HasValue)
            query = query.Where(c => c.RequestedServiceType == filter.RequestedServiceType.Value);

        if (filter.Status.HasValue)
            query = query.Where(c => c.Status == filter.Status.Value);

        if (filter.Source.HasValue)
            query = query.Where(c => c.Source == filter.Source.Value);

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(c => c.City != null && EF.Functions.Like(c.City, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Suburb))
        {
            var v = filter.Suburb.Trim();
            query = query.Where(c => c.Suburb != null && EF.Functions.Like(c.Suburb, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(c => c.Province != null && EF.Functions.Like(c.Province, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.PostalCode))
        {
            var v = filter.PostalCode.Trim();
            query = query.Where(c => c.PostalCode != null && EF.Functions.Like(c.PostalCode, $"%{v}%"));
        }

        if (filter.FromUtc.HasValue)
            query = query.Where(c => c.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(c => c.CreatedAtUtc <= filter.ToUtc.Value);

        if (filter.ReviewedFromUtc.HasValue)
            query = query.Where(c => c.ReviewedAtUtc != null && c.ReviewedAtUtc >= filter.ReviewedFromUtc.Value);

        if (filter.ReviewedToUtc.HasValue)
            query = query.Where(c => c.ReviewedAtUtc != null && c.ReviewedAtUtc <= filter.ReviewedToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(c =>
                EF.Functions.Like(c.AddressLine1, $"%{s}%") ||
                (c.City != null && EF.Functions.Like(c.City, $"%{s}%")) ||
                (c.Suburb != null && EF.Functions.Like(c.Suburb, $"%{s}%")) ||
                (c.Province != null && EF.Functions.Like(c.Province, $"%{s}%")) ||
                (c.PostalCode != null && EF.Functions.Like(c.PostalCode, $"%{s}%")) ||
                (c.FullName != null && EF.Functions.Like(c.FullName, $"%{s}%")) ||
                (c.Email != null && EF.Functions.Like(c.Email, $"%{s}%")));
        }

        return query;
    }

    private static async Task<Result<PagedResult<CoverageRequestDto>>> ToPagedResultAsync(IQueryable<CoverageRequest> query, CoverageRequestFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(c => c.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(c => new CoverageRequestDto
            {
                Id = c.Id,
                UserId = c.UserId,
                CustomerProfileId = c.CustomerProfileId,
                ServicePackageId = c.ServicePackageId,
                ServicePackageName = c.ServicePackage != null ? c.ServicePackage.Name : null,
                RequestedServiceType = c.RequestedServiceType,
                Status = c.Status,
                Source = c.Source,
                FullName = c.FullName,
                Email = c.Email,
                PhoneNumber = c.PhoneNumber,
                AddressLine1 = c.AddressLine1,
                AddressLine2 = c.AddressLine2,
                Suburb = c.Suburb,
                City = c.City,
                Province = c.Province,
                PostalCode = c.PostalCode,
                Country = c.Country,
                Latitude = c.Latitude,
                Longitude = c.Longitude,
                GooglePlaceId = c.GooglePlaceId,
                CustomerNotes = c.CustomerNotes,
                AdminNotes = c.AdminNotes,
                CoverageResultSummary = c.CoverageResultSummary,
                ReviewedAtUtc = c.ReviewedAtUtc,
                ReviewedByUserId = c.ReviewedByUserId,
                ReviewedByUserEmail = c.ReviewedByUser != null ? c.ReviewedByUser.Email : null,
                CreatedAtUtc = c.CreatedAtUtc,
                UpdatedAtUtc = c.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<CoverageRequestDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<CoverageRequestDto>>.Success(paged);
    }

    private async Task<CoverageRequest?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.CoverageRequests
            .AsNoTracking()
            .Include(c => c.ServicePackage)
            .Include(c => c.ReviewedByUser)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    private async Task<Result<CoverageRequestDto>?> ValidateServicePackageAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await _dbContext.ServicePackages
            .AsNoTracking()
            .Where(p => p.Id == packageId)
            .Select(p => new { p.Id, p.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (package is null)
            return Result<CoverageRequestDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced service package was not found.");

        if (package.Status == ServicePackageStatus.Archived)
            return Result<CoverageRequestDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Referenced service package is archived and cannot be used.");

        return null;
    }

    private static Result<CoverageRequestDto>? ValidateAddressAndContact(string? addressLine1, decimal? latitude, decimal? longitude, string? email, string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(addressLine1))
            return Result<CoverageRequestDto>.Failure(ErrorCodes.VALIDATION_ERROR, "AddressLine1 is required.");

        if (latitude.HasValue && (latitude.Value < -90m || latitude.Value > 90m))
            return Result<CoverageRequestDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Latitude must be between -90 and 90.");

        if (longitude.HasValue && (longitude.Value < -180m || longitude.Value > 180m))
            return Result<CoverageRequestDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Longitude must be between -180 and 180.");

        if (!string.IsNullOrWhiteSpace(email) && !EmailRegex.IsMatch(email.Trim()))
            return Result<CoverageRequestDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Email is not in a valid format.");

        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            var p = phoneNumber.Trim();
            if (p.Length < 6 || p.Length > 50)
                return Result<CoverageRequestDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "PhoneNumber must be between 6 and 50 characters.");
        }

        return null;
    }

    private async Task EmitAuditAsync(AuditActionType actionType, AuditActorType actorType, CoverageRequest entity, string summary, string? metadata)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = actionType,
            EntityType = AuditEntityType.CoverageRequest,
            EntityId = entity.Id,
            EntityName = BuildEntityName(entity),
            Summary = summary,
            MetadataJson = metadata,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private static string BuildEntityName(CoverageRequest entity)
    {
        var parts = new[] { entity.City, entity.Suburb, entity.Province }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        return parts.Length > 0
            ? string.Join(", ", parts)
            : entity.AddressLine1;
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

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CoverageRequestDto MapToDto(CoverageRequest c) => new()
    {
        Id = c.Id,
        UserId = c.UserId,
        CustomerProfileId = c.CustomerProfileId,
        ServicePackageId = c.ServicePackageId,
        ServicePackageName = c.ServicePackage?.Name,
        RequestedServiceType = c.RequestedServiceType,
        Status = c.Status,
        Source = c.Source,
        FullName = c.FullName,
        Email = c.Email,
        PhoneNumber = c.PhoneNumber,
        AddressLine1 = c.AddressLine1,
        AddressLine2 = c.AddressLine2,
        Suburb = c.Suburb,
        City = c.City,
        Province = c.Province,
        PostalCode = c.PostalCode,
        Country = c.Country,
        Latitude = c.Latitude,
        Longitude = c.Longitude,
        GooglePlaceId = c.GooglePlaceId,
        CustomerNotes = c.CustomerNotes,
        AdminNotes = c.AdminNotes,
        CoverageResultSummary = c.CoverageResultSummary,
        ReviewedAtUtc = c.ReviewedAtUtc,
        ReviewedByUserId = c.ReviewedByUserId,
        ReviewedByUserEmail = c.ReviewedByUser?.Email,
        CreatedAtUtc = c.CreatedAtUtc,
        UpdatedAtUtc = c.UpdatedAtUtc
    };
}
