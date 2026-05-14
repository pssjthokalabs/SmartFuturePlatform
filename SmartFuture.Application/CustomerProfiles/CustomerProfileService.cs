using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.CustomerProfiles.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Customers;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.CustomerProfiles;

public class CustomerProfileService : ICustomerProfileService
{
    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<CustomerProfileService> _logger;

    public CustomerProfileService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, ILogger<CustomerProfileService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<CustomerProfileDto>> GetMineAsync(Guid userId)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<CustomerProfileDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var profile = await _dbContext.CustomerProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId);

            if (profile is null)
                return Result<CustomerProfileDto>.Failure(ErrorCodes.NOT_FOUND, "Customer profile not found.");

            return Result<CustomerProfileDto>.Success(MapToDto(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching customer profile for {UserId}", userId);
            return Result<CustomerProfileDto>.Failure(ErrorCodes.EXCEPTION,
                "An unexpected error occurred fetching the customer profile.");
        }
    }

    public async Task<Result<CustomerProfileDto>> CreateOrUpdateMineAsync(Guid userId, UpdateCustomerProfileRequestDto request)
    {
        try
        {
            if (userId == Guid.Empty)
                return Result<CustomerProfileDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request is null)
                return Result<CustomerProfileDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var profile = await _dbContext.CustomerProfiles
                .FirstOrDefaultAsync(p => p.UserId == userId);

            var isNew = profile is null;
            profile ??= new CustomerProfile { UserId = userId };

            profile.IdNumber = Trim(request.IdNumber);
            profile.AddressLine1 = Trim(request.AddressLine1);
            profile.AddressLine2 = Trim(request.AddressLine2);
            profile.Suburb = Trim(request.Suburb);
            profile.City = Trim(request.City);
            profile.Province = Trim(request.Province);
            profile.PostalCode = Trim(request.PostalCode);
            profile.Country = Trim(request.Country);
            profile.Latitude = request.Latitude;
            profile.Longitude = request.Longitude;
            profile.Notes = Trim(request.Notes);
            profile.PreferredContactMethod = Trim(request.PreferredContactMethod);
            profile.AcceptsMarketing = request.AcceptsMarketing;
            profile.AcceptsPaymentReminders = request.AcceptsPaymentReminders;
            profile.AcceptsInstallationUpdates = request.AcceptsInstallationUpdates;
            profile.AcceptsNetworkAlerts = request.AcceptsNetworkAlerts;

            if (isNew)
                _dbContext.CustomerProfiles.Add(profile);

            await _dbContext.SaveChangesAsync();

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = userId,
                ActorType = AuditActorType.User,
                ActionType = isNew
                    ? AuditActionType.CustomerProfileCreated
                    : AuditActionType.CustomerProfileUpdated,
                EntityType = AuditEntityType.CustomerProfile,
                EntityId = profile.Id,
                Summary = isNew
                    ? "Customer profile created"
                    : "Customer profile updated",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true
            });

            return Result<CustomerProfileDto>.Success(
                MapToDto(profile),
                isNew ? "Customer profile created." : "Customer profile updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error saving customer profile for {UserId}", userId);
            return Result<CustomerProfileDto>.Failure(ErrorCodes.EXCEPTION,
                "An unexpected error occurred saving the customer profile.");
        }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CustomerProfileDto MapToDto(CustomerProfile p) => new()
    {
        Id = p.Id,
        UserId = p.UserId,
        IdNumber = p.IdNumber,
        AddressLine1 = p.AddressLine1,
        AddressLine2 = p.AddressLine2,
        Suburb = p.Suburb,
        City = p.City,
        Province = p.Province,
        PostalCode = p.PostalCode,
        Country = p.Country,
        Latitude = p.Latitude,
        Longitude = p.Longitude,
        Notes = p.Notes,
        PreferredContactMethod = p.PreferredContactMethod,
        AcceptsMarketing = p.AcceptsMarketing,
        AcceptsPaymentReminders = p.AcceptsPaymentReminders,
        AcceptsInstallationUpdates = p.AcceptsInstallationUpdates,
        AcceptsNetworkAlerts = p.AcceptsNetworkAlerts,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc
    };
}
