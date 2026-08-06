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
    private readonly RoleManager<IdentityRole<Guid>> _roleManager;
    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<UserRoleUpgradeService> _logger;

    public UserRoleUpgradeService(UserManager<User> userManager, RoleManager<IdentityRole<Guid>> roleManager, IAppDbContext dbContext,
        IAuditService auditService, ICurrentUserService currentUser, ILogger<UserRoleUpgradeService> logger)
    {
        _userManager = userManager;
        _roleManager = roleManager;
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
            // Include the real reason: the previous message named no cause,
            // which is how a plain missing-role row read as a mystery on Live.
            return Result.Failure(ErrorCodes.EXCEPTION,
                $"Could not enable job seeker access: {ex.Message}");
        }
    }

    /// <summary>
    /// Guarantees the AspNetRoles row for a SYSTEM role exists.
    ///
    /// THE LIVE FAILURE THIS FIXES
    ///
    /// Role rows are created by DbInitializer.SeedRolesAsync, which runs
    /// only from ApplyDatabaseMigrationsAsync — and only AFTER its
    /// `if (!enabled) return;` guard on Database:ApplyMigrationsOnStartup.
    /// Live has that flag off and had its Jobs tables applied from the
    /// idempotent SQL script instead, so the schema arrived but the
    /// seeder never ran and `JobSubscriber` was never inserted.
    ///
    /// UserManager.AddToRoleAsync THROWS InvalidOperationException when
    /// the role row is absent. That exception was caught upstream and
    /// reported as "An unexpected error occurred while enabling job
    /// seeker access" — the exact error Live showed, for a cause with
    /// nothing unexpected about it.
    ///
    /// Restricted to SystemRoles.All on purpose: this can only
    /// materialise roles the application already defines, never an
    /// arbitrary name from a caller.
    /// </summary>
    private async Task<Result> EnsureSystemRoleExistsAsync(string role)
    {
        if (!SystemRoles.All.Contains(role))
        {
            _logger.LogError("Refusing to create non-system role {Role}", role);
            return Result.Failure(ErrorCodes.EXCEPTION, $"'{role}' is not a known system role.");
        }

        if (await _roleManager.RoleExistsAsync(role)) return Result.Success();

        _logger.LogWarning(
            "[RoleUpgrade] System role {Role} was missing from AspNetRoles and is being created. " +
            "This means startup role seeding never ran in this environment — check Database:ApplyMigrationsOnStartup.",
            role);

        var created = await _roleManager.CreateAsync(new IdentityRole<Guid>(role)
        {
            Id = Guid.NewGuid(),
            NormalizedName = role.ToUpperInvariant()
        });

        if (created.Succeeded) return Result.Success();

        // A concurrent request may have won the race — that is a success
        // for our purposes.
        if (await _roleManager.RoleExistsAsync(role)) return Result.Success();

        var errors = string.Join("; ", created.Errors.Select(e => e.Description));
        _logger.LogError("Could not create missing system role {Role}: {Errors}", role, errors);
        return Result.Failure(ErrorCodes.EXCEPTION,
            $"The {role} role is not configured on this environment and could not be created automatically. {errors}");
    }

    private async Task<Result> AddRoleIfMissingAsync(User user, string role, string reason)
    {
        if (await _userManager.IsInRoleAsync(user, role))
            return Result.Success($"User already holds the {role} role.");

        var roleExists = await EnsureSystemRoleExistsAsync(role);
        if (!roleExists.IsSuccess) return roleExists;

        var result = await _userManager.AddToRoleAsync(user, role);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger.LogError("Failed to add role {Role} to user {UserId}: {Errors}", role, user.Id, errors);
            // Carry the real Identity reason. "Could not enable X access"
            // on its own is unactionable for whoever is reading the log
            // or the API response.
            return Result.Failure(ErrorCodes.EXCEPTION, $"Could not enable {role} access on this account: {errors}");
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
