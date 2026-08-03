using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Users;

// See IUserRoleUpgradeService for the contract. Implementation notes:
//
//   • Role addition is ADDITIVE ONLY. There is deliberately no
//     "SetRoles" / "ReplaceRole" method here — a bug in one product
//     line must not be able to strip the other product line's role.
//   • Every grant is audited with the reason string supplied by the
//     caller, so "why does this user have Customer?" is answerable from
//     the audit log alone.
//   • Idempotent by design: callers can invoke on every request without
//     checking first.
public class UserRoleUpgradeService : IUserRoleUpgradeService
{
    private readonly UserManager<User> _userManager;
    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<UserRoleUpgradeService> _logger;

    public UserRoleUpgradeService(UserManager<User> userManager, IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser,
        ILogger<UserRoleUpgradeService> logger)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result> EnsureCustomerRoleAsync(User user, string reason, CancellationToken cancellationToken = default)
    {
        if (user is null)
            return Result.Failure(ErrorCodes.BAD_REQUEST, "User is required.");

        try
        {
            var roleResult = await AddRoleIfMissingAsync(user, SystemRoles.Customer, reason);
            if (!roleResult.IsSuccess) return roleResult;

            // The customer journey assumes a CustomerProfile row exists
            // (orders, coverage, billing all read it). Create an empty
            // one when a job subscriber upgrades — same shape the normal
            // registration path produces.
            var hasProfile = await _dbContext.CustomerProfiles.AnyAsync(p => p.UserId == user.Id, cancellationToken);
            if (!hasProfile)
            {
                _dbContext.CustomerProfiles.Add(new CustomerProfile { UserId = user.Id });
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return Result.Success("Customer access is active on this account.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error granting Customer role to {UserId}", user.Id);
            return Result.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while enabling customer access.");
        }
    }

    public async Task<Result> EnsureJobSubscriberRoleAsync(User user, string reason, CancellationToken cancellationToken = default)
    {
        if (user is null)
            return Result.Failure(ErrorCodes.BAD_REQUEST, "User is required.");

        try
        {
            return await AddRoleIfMissingAsync(user, SystemRoles.JobSubscriber, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error granting JobSubscriber role to {UserId}", user.Id);
            return Result.Failure(ErrorCodes.EXCEPTION, "An unexpected error occurred while enabling job seeker access.");
        }
    }

    private async Task<Result> AddRoleIfMissingAsync(User user, string role, string reason)
    {
        if (await _userManager.IsInRoleAsync(user, role))
            return Result.Success($"User already holds the {role} role.");

        var result = await _userManager.AddToRoleAsync(user, role);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger.LogError("Failed to add role {Role} to user {UserId}: {Errors}", role, user.Id, errors);
            return Result.Failure(ErrorCodes.EXCEPTION, $"Could not enable {role} access on this account.");
        }

        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId ?? user.Id,
            ActorType = _currentUser.UserId.HasValue ? AuditActorType.User : AuditActorType.System,
            ActionType = AuditActionType.UserRoleChanged,
            EntityType = AuditEntityType.User,
            EntityId = user.Id,
            EntityName = user.Email,
            Summary = $"Role {role} added to {user.Email}. Reason: {reason}",
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });

        _logger.LogInformation("[RoleUpgrade] userId={UserId} role={Role} reason={Reason}", user.Id, role, reason);
        return Result.Success($"{role} access enabled.");
    }
}
