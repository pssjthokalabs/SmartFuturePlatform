using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Auth;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Communication.Email.Templates;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.Users.Admin.Dtos;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Utilities;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Communication;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.Notifications;
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
    private readonly INotificationService _notifications;
    private readonly FrontendSettings _frontendSettings;
    private readonly ILogger<AdminUsersService> _logger;

    public AdminUsersService(
        UserManager<User> userManager,
        IAppDbContext dbContext,
        IAuditService auditService,
        ICurrentUserService currentUser,
        INotificationService notifications,
        IOptions<FrontendSettings> frontendSettings,
        ILogger<AdminUsersService> logger)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _notifications = notifications;
        _frontendSettings = frontendSettings.Value ?? new FrontendSettings();
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
            var testCount  = await _dbContext.Users.CountAsync(u => u.IsTestAccount, cancellationToken);
            var realCustomers = await _dbContext.Users
                .CountAsync(u => !staffIds.Contains(u.Id) && !u.IsTestAccount, cancellationToken);
            var counts = new AdminUserTypeCountsDto
            {
                // "All" and "Customers" are REAL users only — controlled test
                // accounts are isolated under their own Test chip and never
                // inflate these counts (or any downstream business stat).
                All        = totalUsers - testCount,
                Customers  = realCustomers,
                Admins     = adminIds.Count,
                Agents     = agentIds.Count,
                Technicians = technicianIds.Count,
                Support    = supportIds.Count,
                Test       = testCount
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
                case "test":
                    // The ONLY view that surfaces controlled test accounts.
                    query = query.Where(u => u.IsTestAccount);
                    break;
                // "" / "all" / unknown → no role narrowing
            }

            // Isolation rule: test accounts appear ONLY under the Test chip.
            // Every other view (All, Customers, staff buckets) AND free-text
            // search excludes them, so they can't leak into real operational
            // lists or be found from the default tabs.
            if (typeKey != "test")
            {
                query = query.Where(u => !u.IsTestAccount);
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
                    IsTestAccount = row.User.IsTestAccount,
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
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.EMAIL_TAKEN, "This email address is already registered.");

            // Phase 43 — phone uniqueness against the canonical form.
            var phoneRaw = NullIfBlank(request.PhoneNumber);
            var phoneNormalized = PhoneNumberNormalizer.Normalize(phoneRaw);
            if (phoneNormalized is not null)
            {
                var phoneClash = await _dbContext.Users
                    .AnyAsync(u => u.PhoneNumberNormalized == phoneNormalized, cancellationToken);
                if (phoneClash)
                {
                    return Result<AdminUserListItemDto>.Failure(ErrorCodes.PHONE_TAKEN,
                        "This phone number is already registered. Please use a different number.");
                }
            }

            var accountStatus = ResolveAccountStatus(request.AccountStatus);

            var user = new User
            {
                UserName       = email,
                Email          = email,
                PhoneNumber    = phoneRaw,
                PhoneNumberNormalized = phoneNormalized,
                FirstName      = request.FirstName.Trim(),
                LastName       = request.LastName.Trim(),
                AccountStatus  = accountStatus,
                IsActive       = accountStatus == UserAccountStatus.Active,
                EmailConfirmed = true,
                // Auto-flag controlled QA test accounts (customer{1000-1999}@gmail.com).
                IsTestAccount  = TestAccountPolicy.IsTestAccountEmail(email),
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

            // Best-effort welcome email. Failure here MUST NOT roll back
            // the user create — the admin gets a portal warning so they
            // can resend or share the credentials out-of-band.
            //
            // The temporary password lives in `request.TemporaryPassword`
            // for the lifetime of this call only; it's never logged, and
            // we hand it straight to the template renderer.
            var welcomeEmailSent = await TrySendWelcomeInviteAsync(
                user,
                request.TemporaryPassword,
                canonicalType,
                cancellationToken);

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
                IsTestAccount = user.IsTestAccount,
                CreatedAtUtc  = user.CreatedAtUtc,
                WelcomeEmailSent = welcomeEmailSent,
            };

            // Friendlier message keeps the portal toast accurate without
            // having to inspect the WelcomeEmailSent flag — but the flag
            // is still authoritative for any callers that branch on it.
            var successMessage = welcomeEmailSent
                ? "User created and welcome email sent."
                : "User created, but the welcome email could not be sent.";
            return Result<AdminUserListItemDto>.Success(dto, successMessage);
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
                        "This email address is already in use by another user.");
                }
            }

            // Phase 43 — phone uniqueness when changing (Super Admin only).
            string? newPhoneRaw = null;
            string? newPhoneNormalized = null;
            var clearPhone = false;
            if (wantsPhoneChange)
            {
                newPhoneRaw = NullIfBlank(request.PhoneNumber);
                newPhoneNormalized = PhoneNumberNormalizer.Normalize(newPhoneRaw);
                clearPhone = newPhoneRaw is null;
                if (newPhoneNormalized is not null)
                {
                    var phoneClash = await _dbContext.Users
                        .AnyAsync(u => u.PhoneNumberNormalized == newPhoneNormalized && u.Id != target.Id, cancellationToken);
                    if (phoneClash)
                    {
                        return Result<AdminUserListItemDto>.Failure(ErrorCodes.PHONE_TAKEN,
                            "This phone number is already in use by another user.");
                    }
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
            {
                target.PhoneNumber           = newPhoneRaw;
                target.PhoneNumberNormalized = clearPhone ? null : newPhoneNormalized;
            }

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
                IsTestAccount = target.IsTestAccount,
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

    public async Task<Result<AdminUserListItemDto>> ChangeRoleAsync(Guid id, ChangeAdminUserRoleRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.BAD_REQUEST, "User id is required.");
            if (request is null || string.IsNullOrWhiteSpace(request.Role))
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Target role is required.");

            // SuperAdmin only. The portal hides the action from non-Super
            // Admin actors but we re-check server-side — never trust the
            // UI for permission decisions.
            var actorIsSuperAdmin = await CurrentActorIsSuperAdminAsync();
            if (!actorIsSuperAdmin)
            {
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.FORBIDDEN,
                    "Only Super Admins can change a user's role.");
            }

            // Resolve the requested role into the canonical Identity
            // role name. The endpoint deliberately rejects SuperAdmin /
            // Agent / Support — see ChangeAdminUserRoleRequestDto for
            // the rationale.
            var newRole = ResolveRoleForChangeRole(request.Role);
            if (newRole is null)
            {
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    $"Role '{request.Role}' is not supported. Use Customer, Admin, or Technician.");
            }

            var target = await _userManager.FindByIdAsync(id.ToString());
            if (target is null)
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.NOT_FOUND, "We couldn't find that user.");

            var currentRoles = (await _userManager.GetRolesAsync(target)).ToList();
            var currentRoleSet = new HashSet<string>(currentRoles, StringComparer.OrdinalIgnoreCase);

            // Last-SuperAdmin protection. Removing the SuperAdmin role
            // from the only active SuperAdmin (or self-demoting when
            // you ARE the only active SuperAdmin) would lock every
            // admin out — refuse.
            if (currentRoleSet.Contains(SystemRoles.SuperAdmin))
            {
                var activeSupers = await CountActiveSuperAdminsAsync();
                if (activeSupers <= 1)
                {
                    return Result<AdminUserListItemDto>.Failure(ErrorCodes.CONFLICT,
                        "This is the last active Super Admin. Change another Super Admin's role first.");
                }
            }

            // Self-lockout safety: a Super Admin can change their own
            // role away from SuperAdmin, but only if at least one OTHER
            // active Super Admin remains. (The CountActiveSuperAdmins
            // check above already covers this — included as an explicit
            // guard so the failure message stays specific.)
            var selfChange = _currentUser.UserId.HasValue && _currentUser.UserId.Value == target.Id;
            if (selfChange && currentRoleSet.Contains(SystemRoles.SuperAdmin))
            {
                var activeSupers = await CountActiveSuperAdminsAsync();
                if (activeSupers <= 1)
                {
                    return Result<AdminUserListItemDto>.Failure(ErrorCodes.CONFLICT,
                        "You are the last active Super Admin. Promote another user first.");
                }
            }

            // No-op short-circuit: the canonical type already matches
            // the requested role. Saves a write + an audit row.
            var currentCanonical = AdminUserTypes.FromRoles(currentRoles);
            var newCanonical     = AdminUserTypes.FromRoles(new[] { newRole });
            if (string.Equals(currentCanonical, newCanonical, StringComparison.OrdinalIgnoreCase)
                && currentRoles.Count == 1)
            {
                var dtoNoChange = await BuildListItemAsync(target);
                return Result<AdminUserListItemDto>.Success(dtoNoChange, "User already has this role.");
            }

            // Strip every existing role + assign the new one. This
            // enforces the 1-to-1 contract spelled out on the DTO.
            if (currentRoles.Count > 0)
            {
                var removeResult = await _userManager.RemoveFromRolesAsync(target, currentRoles);
                if (!removeResult.Succeeded)
                {
                    var message = string.Join("; ", removeResult.Errors.Select(e => e.Description));
                    return Result<AdminUserListItemDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                        string.IsNullOrWhiteSpace(message) ? "Couldn't update the user's role." : message);
                }
            }
            var addResult = await _userManager.AddToRoleAsync(target, newRole);
            if (!addResult.Succeeded)
            {
                // Best-effort rollback so we don't leave the user with
                // zero roles. Reattach the previous role set on
                // failure; the user is no worse off than before.
                if (currentRoles.Count > 0)
                {
                    await _userManager.AddToRolesAsync(target, currentRoles);
                }
                var message = string.Join("; ", addResult.Errors.Select(e => e.Description));
                return Result<AdminUserListItemDto>.Failure(ErrorCodes.VALIDATION_ERROR,
                    string.IsNullOrWhiteSpace(message) ? "Couldn't assign the new role." : message);
            }

            // Customers always get a CustomerProfile row so the
            // customer detail page + related-data joins work after the
            // role change. We never DELETE an existing profile — if a
            // user moves from Customer to Admin and back, their
            // historical profile/order/invoice data stays attached.
            if (newCanonical == AdminUserTypes.Customer)
            {
                var hasProfile = await _dbContext.CustomerProfiles.AnyAsync(p => p.UserId == target.Id, cancellationToken);
                if (!hasProfile)
                {
                    _dbContext.CustomerProfiles.Add(new CustomerProfile { UserId = target.Id });
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            target.UpdatedAtUtc = DateTime.UtcNow;
            await _userManager.UpdateAsync(target);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType   = AuditActorType.User,
                ActionType  = AuditActionType.UserStatusChanged,
                EntityType  = AuditEntityType.User,
                EntityId    = target.Id,
                EntityName  = target.Email,
                Summary     = $"Role change: {target.Email}  "
                              + $"{string.Join(",", currentRoles)} → {newRole}",
                IpAddress   = _currentUser.IpAddress,
                UserAgent   = _currentUser.UserAgent,
                IsSuccess   = true
            }, cancellationToken);

            var dto = await BuildListItemAsync(target);
            return Result<AdminUserListItemDto>.Success(dto, "User role updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error changing role for user {Id}", id);
            return Result<AdminUserListItemDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while changing the user's role.");
        }
    }

    private async Task<AdminUserListItemDto> BuildListItemAsync(User user)
    {
        var roles = await _userManager.GetRolesAsync(user);
        return new AdminUserListItemDto
        {
            Id            = user.Id,
            UserNumber    = user.UserNumber,
            FirstName     = user.FirstName,
            LastName      = user.LastName,
            Email         = user.Email,
            PhoneNumber   = user.PhoneNumber,
            UserType      = AdminUserTypes.FromRoles(roles),
            Roles         = roles.ToList(),
            AccountStatus = MapStatus(user.AccountStatus),
            IsTestAccount = user.IsTestAccount,
            CreatedAtUtc  = user.CreatedAtUtc,
        };
    }

    private static string? ResolveRoleForChangeRole(string requested)
    {
        return (requested ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "customer"   => SystemRoles.Customer,
            "admin"      => SystemRoles.Admin,
            "technician" => SystemRoles.Technician,
            // Deliberately NOT supported via this endpoint:
            //   "superadmin" → seed-only / manual DB script
            //   "agent" / "support" → not yet shippable (matches CreateAsync)
            _            => null,
        };
    }

    /// <summary>
    /// Best-effort welcome / "account created" email. Returns true on
    /// success. Failures are logged but never thrown so a flaky SMTP
    /// run never blocks user creation. The temporary password is
    /// passed straight to the template — never logged here.
    /// </summary>
    private async Task<bool> TrySendWelcomeInviteAsync(User user, string temporaryPassword, string canonicalType, CancellationToken cancellationToken)
    {
        if (user is null || string.IsNullOrWhiteSpace(user.Email)) return false;
        try
        {
            var loginUrl   = ResolveLoginUrlForType(canonicalType);
            var roleLabel  = FormatRoleLabel(canonicalType);
            var template   = AuthEmailTemplates.WelcomeUserInvite(
                firstName: user.FirstName ?? string.Empty,
                emailAddress: user.Email!,
                roleLabel: roleLabel,
                temporaryPassword: temporaryPassword,
                loginUrl: loginUrl);

            await _notifications.SendAsync(new SendNotificationRequestDto
            {
                UserId         = user.Id,
                Channel        = NotificationChannel.Email,
                Type           = NotificationType.Welcome,
                RecipientEmail = user.Email,
                Subject        = template.Subject,
                Body           = template.PlainTextBody,
                IsHtml         = true,
                HtmlBody       = template.HtmlBody,
                SenderType     = template.SenderType,
                RelatedEntityType = "User",
                RelatedEntityId   = user.Id,
            }, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            // Critical: do NOT include the password in the log. Only log
            // metadata so ops can chase the SMTP failure without
            // leaking credentials.
            _logger.LogWarning(ex, "Welcome email failed for user {UserId} ({Email})",
                user.Id, user.Email);
            return false;
        }
    }

    private string ResolveLoginUrlForType(string canonicalType)
    {
        // Staff buckets (Admin/Technician/Agent/Support) land on the
        // admin host; Customers on Client Zone. The actual URL string
        // comes from FrontendSettings (env-configurable per
        // environment) so UAT/dev override production safely.
        if (string.Equals(canonicalType, AdminUserTypes.Customer, StringComparison.OrdinalIgnoreCase))
        {
            return _frontendSettings.ClientPortalLoginUrl;
        }
        return _frontendSettings.AdminPortalLoginUrl;
    }

    private static string FormatRoleLabel(string canonicalType)
    {
        // Used only for the email subject/body display. Mirrors the
        // user-facing "Type" pill on the admin portal.
        return canonicalType switch
        {
            AdminUserTypes.Customer   => "Customer",
            AdminUserTypes.Admin      => "Admin",
            AdminUserTypes.Technician => "Technician",
            AdminUserTypes.Agent      => "Agent",
            AdminUserTypes.Support    => "Support",
            _ => "SmartFuture",
        };
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
