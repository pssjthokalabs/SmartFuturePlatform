using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.CustomerProfiles.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
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
        // Correlation token surfaced to the client + logged with the
        // exception so support can grep CloudWatch by the same value
        // the user reports. Prefer the active Activity trace id (set
        // by ASP.NET Core's W3C tracing); fall back to a fresh Guid
        // if no Activity is on the call. 16 chars is enough to be
        // unique in practice without being noisy.
        var correlationId = Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N")[..16];

        try
        {
            if (userId == Guid.Empty)
                return Result<CustomerProfileDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var profile = await _dbContext.CustomerProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId);

            if (profile is null)
                return Result<CustomerProfileDto>.Failure(ErrorCodes.NOT_FOUND, "Customer profile not found.");

            var user = await _dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId);

            return Result<CustomerProfileDto>.Success(MapToDto(profile, user));
        }
        catch (Exception ex)
        {
            // Schema-mismatch detection — typical phrasing from
            // Microsoft.Data.SqlClient. If we see one of these we
            // upgrade the log to Critical with a clearer hint:
            // a migration is pending. The user-facing message stays
            // generic; the correlation id is the bridge between the
            // 500 they reported and the structured log line.
            var schemaMismatch = LooksLikeSchemaMismatch(ex);
            if (schemaMismatch)
            {
                _logger.LogCritical(ex,
                    "[CustomerProfile:SchemaMismatch] correlationId={CorrelationId} userId={UserId} exceptionType={ExceptionType} message='{Message}'. " +
                    "This usually means a database migration has not been applied. Compare AppDbContext.GetPendingMigrationsAsync() against __EFMigrationsHistory.",
                    correlationId, userId, ex.GetType().Name, ex.Message);
            }
            else
            {
                _logger.LogError(ex,
                    "[CustomerProfile:Error] correlationId={CorrelationId} userId={UserId} exceptionType={ExceptionType} message='{Message}'",
                    correlationId, userId, ex.GetType().Name, ex.Message);
            }

            return Result<CustomerProfileDto>.Failure(ErrorCodes.EXCEPTION,
                $"An unexpected error occurred fetching the customer profile. (ref: {correlationId})");
        }
    }

    // Heuristic — checks the exception (and any inner) for the
    // phrases SQL Server uses when an entity column or table is
    // missing. Provider-specific names live in
    // Microsoft.Data.SqlClient, but a string-match keeps Application
    // free of an Infrastructure dependency.
    private static bool LooksLikeSchemaMismatch(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var msg = current.Message;
            if (string.IsNullOrEmpty(msg)) continue;
            if (msg.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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

            var user = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.Id == userId);

            var isNew = profile is null;
            profile ??= new CustomerProfile { UserId = userId };

            // Identity fields live on the User entity. Email and phone
            // are intentionally not part of UpdateCustomerProfileRequestDto
            // because changing them requires a verification flow that is
            // not yet implemented — see the DTO comment.
            if (user is not null)
            {
                var newFirst = Trim(request.FirstName);
                var newLast = Trim(request.LastName);
                if (newFirst is not null) user.FirstName = newFirst;
                if (newLast is not null) user.LastName = newLast;
                user.UpdatedAtUtc = DateTime.UtcNow;
            }

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
                MapToDto(profile, user),
                isNew ? "Customer profile created." : "Customer profile updated.");
        }
        catch (Exception ex)
        {
            var correlationId = Activity.Current?.TraceId.ToString()
                ?? Guid.NewGuid().ToString("N")[..16];
            if (LooksLikeSchemaMismatch(ex))
            {
                _logger.LogCritical(ex,
                    "[CustomerProfile:SchemaMismatch] correlationId={CorrelationId} userId={UserId} exceptionType={ExceptionType} message='{Message}' op=save. " +
                    "Migration likely not applied.",
                    correlationId, userId, ex.GetType().Name, ex.Message);
            }
            else
            {
                _logger.LogError(ex,
                    "[CustomerProfile:Error] correlationId={CorrelationId} userId={UserId} exceptionType={ExceptionType} message='{Message}' op=save",
                    correlationId, userId, ex.GetType().Name, ex.Message);
            }
            return Result<CustomerProfileDto>.Failure(ErrorCodes.EXCEPTION,
                $"An unexpected error occurred saving the customer profile. (ref: {correlationId})");
        }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CustomerProfileDto MapToDto(CustomerProfile p, User? user) => new()
    {
        Id = p.Id,
        UserId = p.UserId,
        FirstName = user?.FirstName ?? string.Empty,
        LastName = user?.LastName ?? string.Empty,
        Email = user?.Email ?? string.Empty,
        PhoneNumber = user?.PhoneNumber,
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
