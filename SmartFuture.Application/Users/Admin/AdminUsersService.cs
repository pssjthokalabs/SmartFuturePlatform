using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.Users.Admin.Dtos;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Users.Admin;

public class AdminUsersService : IAdminUsersService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 200;

    private static readonly OrderStatus[] ActiveOrderStatuses =
    {
        OrderStatus.Confirmed, OrderStatus.AwaitingPayment, OrderStatus.PaymentReceived,
        OrderStatus.Provisioning, OrderStatus.Active
    };

    private static readonly InvoiceStatus[] UnpaidInvoiceStatuses =
    {
        InvoiceStatus.Issued, InvoiceStatus.PartiallyPaid, InvoiceStatus.Overdue
    };

    private readonly UserManager<User> _userManager;
    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<AdminUsersService> _logger;

    public AdminUsersService(UserManager<User> userManager, IAppDbContext dbContext, IAuditService auditService,
        ICurrentUserService currentUser, ILogger<AdminUsersService> logger)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<AdminUsersSearchResultDto>> SearchAsync(AdminUsersFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new AdminUsersFilterRequestDto();
            var page = Math.Max(1, filter.Page ?? 1);
            var pageSize = Math.Clamp(filter.PageSize ?? DefaultPageSize, 1, MaxPageSize);

            // Build canonical user-id sets per role once. The sets feed both
            // the global counts (chip-row totals) and the type filter, so
            // we don't need to re-query the role tables per request stage.
            var adminIds      = await GetUserIdsInRolesAsync(SystemRoles.Admin, SystemRoles.SuperAdmin);
            var agentIds      = await GetUserIdsInRolesAsync(SystemRoles.Agent);
            var technicianIds = await GetUserIdsInRolesAsync(SystemRoles.Technician);
            var supportIds    = await GetUserIdsInRolesAsync(SystemRoles.Support);

            var staffIds = new HashSet<Guid>(adminIds);
            staffIds.UnionWith(agentIds);
            staffIds.UnionWith(technicianIds);
            staffIds.UnionWith(supportIds);

            var totalUsers = await _dbContext.Users.CountAsync(cancellationToken);
            var counts = new AdminUserTypeCountsDto
            {
                All        = totalUsers,
                Customers  = totalUsers - staffIds.Count,
                Admins     = adminIds.Count,
                Agents     = agentIds.Count,
                Technicians = technicianIds.Count,
                Support    = supportIds.Count
            };

            var query = _dbContext.Users.AsNoTracking();

            // Type filter — uses the role-id sets we already computed.
            // "Customer" = "no staff role"; matches users with a
            // CustomerProfile *and* users who simply have no role yet, so
            // legacy customer accounts created before role assignment
            // still appear under the Customer bucket.
            var typeKey = (filter.Type ?? string.Empty).Trim().ToLowerInvariant();
            switch (typeKey)
            {
                case "customer":
                    query = query.Where(u => !staffIds.Contains(u.Id));
                    break;
                case "admin":
                    query = query.Where(u => adminIds.Contains(u.Id));
                    break;
                case "agent":
                    query = query.Where(u => agentIds.Contains(u.Id));
                    break;
                case "technician":
                    query = query.Where(u => technicianIds.Contains(u.Id));
                    break;
                case "support":
                    query = query.Where(u => supportIds.Contains(u.Id));
                    break;
                // "" / "all" / unknown → no narrowing
            }

            var search = (filter.Search ?? string.Empty).Trim();
            if (search.Length > 0)
            {
                var s = search.ToLower();
                query = query.Where(u =>
                    (u.FirstName + " " + u.LastName).ToLower().Contains(s)
                    || (u.Email != null && u.Email.ToLower().Contains(s))
                    || (u.PhoneNumber != null && u.PhoneNumber.Contains(search)));
            }

            if (!string.IsNullOrWhiteSpace(filter.Status))
            {
                if (Enum.TryParse<UserAccountStatus>(NormalizeStatus(filter.Status), ignoreCase: true, out var statusEnum))
                    query = query.Where(u => u.AccountStatus == statusEnum);
            }

            var totalCount = await query.CountAsync(cancellationToken);

            // Project with the customer-rollup fields. Staff users will
            // have null Profile / ActiveOrder / Outstanding=0, which the
            // mapper hides from the DTO.
            var projected = await query
                .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new
                {
                    User = u,
                    Profile = _dbContext.CustomerProfiles.FirstOrDefault(p => p.UserId == u.Id),
                    ActiveOrder = _dbContext.Orders
                        .Where(o => o.UserId == u.Id && ActiveOrderStatuses.Contains(o.Status))
                        .OrderByDescending(o => o.CreatedAtUtc)
                        .Select(o => new { o.PackageName })
                        .FirstOrDefault(),
                    Outstanding = _dbContext.Invoices
                        .Where(i => i.Order != null && i.Order.UserId == u.Id
                                 && UnpaidInvoiceStatuses.Contains(i.Status) && i.BalanceDue > 0)
                        .Sum(i => (decimal?)i.BalanceDue) ?? 0m
                })
                .ToListAsync(cancellationToken);

            var items = new List<AdminUserListItemDto>(projected.Count);
            foreach (var row in projected)
            {
                // GetRolesAsync round-trips per user; tolerable at page
                // size ≤ 200. The role-tables are tiny and the rows are
                // cached by UserManager for the request scope.
                var roles = await _userManager.GetRolesAsync(row.User);
                var userType = AdminUserTypes.FromRoles(roles);

                var item = new AdminUserListItemDto
                {
                    Id            = row.User.Id,
                    UserNumber    = row.User.UserNumber,
                    FirstName     = row.User.FirstName,
                    LastName      = row.User.LastName,
                    Email         = row.User.Email,
                    PhoneNumber   = row.User.PhoneNumber,
                    UserType      = userType,
                    Roles         = roles.ToList(),
                    AccountStatus = MapStatus(row.User.AccountStatus),
                    CreatedAtUtc  = row.User.CreatedAtUtc
                };

                // Customer-only address / package / balance fields. Staff
                // accounts leave these null so the table renders "—".
                if (userType == AdminUserTypes.Customer)
                {
                    item.Suburb            = row.Profile?.Suburb;
                    item.City              = row.Profile?.City;
                    item.ActivePackageName = row.ActiveOrder?.PackageName;
                    item.Outstanding       = row.Outstanding;
                }

                items.Add(item);
            }

            return Result<AdminUsersSearchResultDto>.Success(new AdminUsersSearchResultDto
            {
                Items      = items,
                Page       = page,
                PageSize   = pageSize,
                TotalCount = totalCount,
                Counts     = counts
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching admin users");
            return Result<AdminUsersSearchResultDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while loading users.");
        }
    }

    public async Task<Result<AdminUserListItemDto>> CreateAsync(CreateAdminUserRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (string.IsNullOrWhiteSpace(request.FirstName)
                || string.IsNullOrWhiteSpace(request.LastName)
                || string.IsNullOrWhiteSpace(request.Email)
                || string.IsNullOrWhiteSpace(request.UserType))
            {
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    "First name, last name, email, and user type are required.");
            }

            if (string.IsNullOrWhiteSpace(request.TemporaryPassword))
            {
                // Invite email isn't wired yet. Until it is, an admin
                // must supply a temporary password so the new user has
                // *some* way to sign in.
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    "A temporary password is required. Email invites are not yet supported.");
            }

            var roleName = ResolveRoleForType(request.UserType);
            if (roleName is null)
            {
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    $"Unknown user type: {request.UserType}. Expected one of: {string.Join(", ", AdminUserTypes.All)}.");
            }

            // Phase 41 — Agent and Support buckets aren't ready for UAT
            // (no per-role workflows or backend rollups yet). Block
            // create at the service layer too so a malformed request
            // can't slip past the UI.
            if (string.Equals(roleName, SystemRoles.Agent, StringComparison.OrdinalIgnoreCase)
                || string.Equals(roleName, SystemRoles.Support, StringComparison.OrdinalIgnoreCase))
            {
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    $"User type '{request.UserType}' is not available yet.");
            }

            // Only Super Admins can create Admin users.
            if (string.Equals(roleName, SystemRoles.Admin, StringComparison.OrdinalIgnoreCase))
            {
                var actorIsSuper = await CurrentActorIsSuperAdminAsync();
                if (!actorIsSuper)
                {
                    return Result<AdminUserListItemDto>.Failure(ErrorCodes.FORBIDDEN,
                        "Only Super Admins can create Admin users.");
                }
            }

            var email = request.Email.Trim();
            var existing = await _userManager.FindByEmailAsync(email);
            if (existing is not null)
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.EMAIL_TAKEN, "Email is already in use.");

            var accountStatus = ResolveAccountStatus(request.AccountStatus);

            var user = new User
            {
                UserName       = email,
                Email          = email,
                PhoneNumber    = NullIfBlank(request.PhoneNumber),
                FirstName      = request.FirstName.Trim(),
                LastName       = request.LastName.Trim(),
                AccountStatus  = accountStatus,
                IsActive       = accountStatus == UserAccountStatus.Active,
                EmailConfirmed = true,
                CreatedAtUtc   = DateTime.UtcNow,
                UserNumber     = await UserNumberAllocator.AllocateNextAsync(_dbContext, cancellationToken)
            };

            var createResult = await _userManager.CreateAsync(user, request.TemporaryPassword);
            if (!createResult.Succeeded)
            {
                var message = string.Join("; ", createResult.Errors.Select(e => e.Description));
                var code = createResult.Errors.Any(e => e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase))
                    ? ErrorCodes.WEAK_PASSWORD
                    : ErrorCodes.VALIDATION_ERROR;
                return Result<AdminUserListItemDto>.Failure(code, message);
            }

            var roleResult = await _userManager.AddToRoleAsync(user, roleName);
            if (!roleResult.Succeeded)
            {
                _logger.LogWarning("User {UserId} created but role assignment failed: {Errors}",
                    user.Id, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
            }

            // Customers always get a CustomerProfile row so the customer
            // detail page and the related-data joins work. Staff users
            // don't need a profile.
            var canonicalType = AdminUserTypes.FromRoles(new[] { roleName });
            if (canonicalType == AdminUserTypes.Customer)
            {
                var hasProfile = await _dbContext.CustomerProfiles.AnyAsync(p => p.UserId == user.Id, cancellationToken);
                if (!hasProfile)
                {
                    _dbContext.CustomerProfiles.Add(new CustomerProfile { UserId = user.Id });
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType   = AuditActorType.User,
                ActionType  = AuditActionType.UserCreated,
                EntityType  = AuditEntityType.User,
                EntityId    = user.Id,
                EntityName  = user.Email,
                Summary     = $"Admin created {canonicalType} user: {user.Email}",
                IpAddress   = _currentUser.IpAddress,
                UserAgent   = _currentUser.UserAgent,
                IsSuccess   = true
            }, cancellationToken);

            var roles = await _userManager.GetRolesAsync(user);
            var dto = new AdminUserListItemDto
            {
                Id            = user.Id,
                UserNumber    = user.UserNumber,
                FirstName     = user.FirstName,
                LastName      = user.LastName,
                Email         = user.Email,
                PhoneNumber   = user.PhoneNumber,
                UserType      = canonicalType,
                Roles         = roles.ToList(),
                AccountStatus = MapStatus(user.AccountStatus),
                CreatedAtUtc  = user.CreatedAtUtc
            };

            return Result<AdminUserListItemDto>.Success(dto, "User created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating admin user");
            return Result<AdminUserListItemDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the user.");
        }
    }

    // Phase 41 — resolve whether the current actor (the authenticated
    // admin making the request) holds the SuperAdmin role. Returns false
    // when there's no authenticated user — defensive; the controller's
    // `[Authorize]` guard should have already rejected such calls.
    private async Task<bool> CurrentActorIsSuperAdminAsync()
    {
        var actorId = _currentUser.UserId;
        if (!actorId.HasValue || actorId.Value == Guid.Empty) return false;
        var actor = await _userManager.FindByIdAsync(actorId.Value.ToString());
        if (actor is null) return false;
        var roles = await _userManager.GetRolesAsync(actor);
        return roles.Contains(SystemRoles.SuperAdmin, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<Result<AdminUserListItemDto>> UpdateAsync(Guid id, UpdateAdminUserRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.BAD_REQUEST, "User id is required.");
            if (request is null)
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var target = await _userManager.FindByIdAsync(id.ToString());
            if (target is null)
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.NOT_FOUND, "We couldn't find that user.");

            var targetRoles = await _userManager.GetRolesAsync(target);
            var targetIsSuperAdmin = targetRoles.Contains(SystemRoles.SuperAdmin, StringComparer.OrdinalIgnoreCase);
            var actorIsSuperAdmin = await CurrentActorIsSuperAdminAsync();

            // Super Admin rows are off-limits to non-Super-Admin actors.
            if (targetIsSuperAdmin && !actorIsSuperAdmin)
            {
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.FORBIDDEN,
                    "Super Admin users can only be modified by Super Admins.");
            }

            // Sensitive fields are Super-Admin-only. We refuse the
            // request rather than silently dropping fields so the admin
            // gets a clear "you can't do that" toast.
            var wantsEmailChange = request.Email is not null && !string.Equals(request.Email.Trim(), target.Email, StringComparison.OrdinalIgnoreCase);
            var wantsPhoneChange = request.PhoneNumber is not null && !string.Equals(NullIfBlank(request.PhoneNumber), target.PhoneNumber, StringComparison.Ordinal);
            if ((wantsEmailChange || wantsPhoneChange) && !actorIsSuperAdmin)
            {
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.FORBIDDEN,
                    "Only Super Admins can change a user's email or phone number.");
            }

            // Validate email uniqueness if it's actually changing.
            string? newEmail = null;
            if (wantsEmailChange && !string.IsNullOrWhiteSpace(request.Email))
            {
                newEmail = request.Email.Trim();
                var clash = await _userManager.FindByEmailAsync(newEmail);
                if (clash is not null && clash.Id != target.Id)
                {
                    return Result<AdminUserListItemDto>.Failure(ErrorCodes.EMAIL_TAKEN,
                        "Email is already in use by another user.");
                }
            }

            // Status changes against a Super Admin must protect the
            // "last active Super Admin" invariant so we can't accidentally
            // lock everyone out.
            UserAccountStatus? newStatus = null;
            if (!string.IsNullOrWhiteSpace(request.AccountStatus))
            {
                newStatus = ResolveAccountStatus(request.AccountStatus);
                if (targetIsSuperAdmin
                    && newStatus.Value != UserAccountStatus.Active
                    && target.AccountStatus == UserAccountStatus.Active)
                {
                    var activeSuperAdmins = await CountActiveSuperAdminsAsync();
                    if (activeSuperAdmins <= 1)
                    {
                        return Result<AdminUserListItemDto>.Failure(ErrorCodes.CONFLICT,
                            "This is the last active Super Admin. Suspending or deactivating them would lock all admins out.");
                    }
                }
            }

            // Apply the changes — bail-out checks above mean we know the
            // actor is allowed to touch each field by the time we get
            // here.
            if (!string.IsNullOrWhiteSpace(request.FirstName)) target.FirstName = request.FirstName.Trim();
            if (!string.IsNullOrWhiteSpace(request.LastName))  target.LastName  = request.LastName.Trim();

            if (wantsPhoneChange)
                target.PhoneNumber = NullIfBlank(request.PhoneNumber);

            if (newEmail is not null)
            {
                target.Email          = newEmail;
                target.NormalizedEmail = newEmail.ToUpperInvariant();
                target.UserName       = newEmail;
                target.NormalizedUserName = newEmail.ToUpperInvariant();
            }

            if (newStatus.HasValue)
            {
                target.AccountStatus = newStatus.Value;
                target.IsActive      = newStatus.Value == UserAccountStatus.Active;
            }

            target.UpdatedAtUtc = DateTime.UtcNow;
            var updateResult = await _userManager.UpdateAsync(target);
            if (!updateResult.Succeeded)
            {
                var message = string.Join("; ", updateResult.Errors.Select(e => e.Description));
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    string.IsNullOrWhiteSpace(message) ? "Couldn't update the user." : message);
            }

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType   = AuditActorType.User,
                ActionType  = AuditActionType.UserStatusChanged,
                EntityType  = AuditEntityType.User,
                EntityId    = target.Id,
                EntityName  = target.Email,
                Summary     = $"Admin updated user {target.Email}"
                              + (newStatus.HasValue ? $" (status → {newStatus.Value})" : string.Empty),
                IpAddress   = _currentUser.IpAddress,
                UserAgent   = _currentUser.UserAgent,
                IsSuccess   = true
            }, cancellationToken);

            var roles = await _userManager.GetRolesAsync(target);
            var dto = new AdminUserListItemDto
            {
                Id            = target.Id,
                UserNumber    = target.UserNumber,
                FirstName     = target.FirstName,
                LastName      = target.LastName,
                Email         = target.Email,
                PhoneNumber   = target.PhoneNumber,
                UserType      = AdminUserTypes.FromRoles(roles),
                Roles         = roles.ToList(),
                AccountStatus = MapStatus(target.AccountStatus),
                CreatedAtUtc  = target.CreatedAtUtc
            };
            return Result<AdminUserListItemDto>.Success(dto, "User updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating admin user {Id}", id);
            return Result<AdminUserListItemDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the user.");
        }
    }

    private async Task<int> CountActiveSuperAdminsAsync()
    {
        var supers = await _userManager.GetUsersInRoleAsync(SystemRoles.SuperAdmin);
        return supers.Count(u => u.IsActive && u.AccountStatus == UserAccountStatus.Active);
    }

    private async Task<HashSet<Guid>> GetUserIdsInRolesAsync(params string[] roleNames)
    {
        // UserManager.GetUsersInRoleAsync only takes one role at a time;
        // the union here is the cheapest path that doesn't require us to
        // expose the AspNetUserRoles join table on IAppDbContext.
        var ids = new HashSet<Guid>();
        foreach (var role in roleNames)
        {
            var users = await _userManager.GetUsersInRoleAsync(role);
            foreach (var u in users) ids.Add(u.Id);
        }
        return ids;
    }

    private static string? ResolveRoleForType(string userType)
    {
        return (userType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "customer"   => SystemRoles.Customer,
            "admin"      => SystemRoles.Admin,
            "agent"      => SystemRoles.Agent,
            "technician" => SystemRoles.Technician,
            "support"    => SystemRoles.Support,
            _            => null
        };
    }

    private static UserAccountStatus ResolveAccountStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UserAccountStatus.Active;
        return Enum.TryParse<UserAccountStatus>(NormalizeStatus(raw), ignoreCase: true, out var parsed)
            ? parsed
            : UserAccountStatus.Active;
    }

    private static string MapStatus(UserAccountStatus status) => status switch
    {
        UserAccountStatus.Active              => "Active",
        UserAccountStatus.Inactive            => "Inactive",
        UserAccountStatus.Suspended           => "Suspended",
        UserAccountStatus.PendingVerification => "Pending Verification",
        _                                     => "Active"
    };

    private static string NormalizeStatus(string raw)
        => string.Equals(raw?.Trim(), "Pending Verification", StringComparison.OrdinalIgnoreCase)
            ? nameof(UserAccountStatus.PendingVerification)
            : (raw?.Trim() ?? string.Empty);

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
