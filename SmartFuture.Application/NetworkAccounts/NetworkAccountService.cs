using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.NetworkAccounts;

/// <summary>
/// Canonical lifecycle for customer network access. Hooks (Installation Completed,
/// Payment Applied), admin endpoints, and privacy erasure all funnel through here
/// so audit, idempotency, and provider calls happen in one place.
/// </summary>
public class NetworkAccountService : INetworkAccountService
{
    private const string NetworkAccountNumberPrefix = "NET";
    private const int NetworkAccountNumberSuffixLength = 6;
    private const int NetworkAccountNumberMaxAttempts = 5;

    // Excludes 0/O/1/I/L to avoid transcription ambiguity.
    private const string AccountNumberAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private static readonly Regex UsernameSanitiseRegex = new(
        "[^a-z0-9]+", RegexOptions.Compiled);

    private static readonly OrderStatus[] PostPaymentOrderStatuses =
    {
        OrderStatus.PaymentReceived,
        OrderStatus.Provisioning,
        OrderStatus.Active
    };

    private static readonly NetworkAccountStatus[] NonTerminatedStatuses =
    {
        NetworkAccountStatus.Pending,
        NetworkAccountStatus.Active,
        NetworkAccountStatus.Suspended,
        NetworkAccountStatus.Failed
    };

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly INetworkProvisioner _provisioner;
    private readonly ILogger<NetworkAccountService> _logger;

    public NetworkAccountService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, INetworkProvisioner provisioner, ILogger<NetworkAccountService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _provisioner = provisioner;
        _logger = logger;
    }

    public async Task<Result<PagedResult<NetworkAccountDto>>> SearchAdminAsync(NetworkAccountFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new NetworkAccountFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching network accounts (admin)");
            return Result<PagedResult<NetworkAccountDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching network accounts.");
        }
    }

    public async Task<Result<PagedResult<NetworkAccountDto>>> GetMineAsync(NetworkAccountFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<NetworkAccountDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new NetworkAccountFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching network accounts (customer)");
            return Result<PagedResult<NetworkAccountDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your network accounts.");
        }
    }

    public Task<Result<NetworkAccountDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<NetworkAccountDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<NetworkAccountDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));
        return GetByIdInternalAsync(id, currentUserId, cancellationToken);
    }

    public async Task<Result<NetworkAccountDto>> ProvisionForOrderAsync(Guid orderId, NetworkAccountSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            if (orderId == Guid.Empty)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.BAD_REQUEST, "OrderId is required.");

            var order = await _dbContext.Orders
                .Include(o => o.User)
                .Include(o => o.ServicePackage)
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

            if (order is null)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            var eligibility = await CheckEligibilityAsync(order, cancellationToken);
            if (eligibility is not null) return eligibility;

            // Idempotency: a non-terminated account already covers this
            // order. Phase 44 — if the row is currently Pending (created
            // when payment landed) we transition it to Active here
            // instead of treating it as fully provisioned; otherwise
            // return the existing row unchanged.
            var existing = await _dbContext.NetworkAccounts
                .FirstOrDefaultAsync(n => n.OrderId == orderId
                                       && NonTerminatedStatuses.Contains(n.Status), cancellationToken);

            if (existing is not null && existing.Status == NetworkAccountStatus.Active)
            {
                return Result<NetworkAccountDto>.Success(
                    MapToDto(await ReloadWithIncludesAsync(existing.Id, cancellationToken) ?? existing),
                    "Network account already active for this order.");
            }

            var now = DateTime.UtcNow;
            // Activate-existing-pending path. The Pending row was created
            // on payment-complete (Phase 44) so we already have the
            // account number / username / provider snapshot; just call
            // the provisioner and flip the status.
            if (existing is not null && existing.Status == NetworkAccountStatus.Pending)
            {
                var pendingContext = BuildContext(existing, order);
                var pendingResult = await SafeProvisionerCallAsync(
                    ct => _provisioner.ProvisionAsync(pendingContext, ct),
                    "provision-pending", existing.AccountNumber, cancellationToken);

                if (pendingResult.IsSuccess)
                {
                    existing.Status = NetworkAccountStatus.Active;
                    existing.ProviderReference = pendingResult.ProviderReference;
                    existing.ProvisionedAtUtc = now;
                    existing.LastFailureReason = null;
                }
                else
                {
                    existing.Status = NetworkAccountStatus.Failed;
                    existing.LastFailureReason = Trim(pendingResult.FailureReason);
                }
                existing.LastStatusChangedByUserId = _currentUser.UserId;
                existing.UpdatedAtUtc = now;
                await _dbContext.SaveChangesAsync(cancellationToken);

                await EmitAuditAsync(
                    pendingResult.IsSuccess
                        ? AuditActionType.NetworkAccountProvisioned
                        : AuditActionType.NetworkAccountProvisionFailed,
                    ActorTypeForSource(source),
                    existing, order,
                    summary: pendingResult.IsSuccess
                        ? $"Pending network account activated: {existing.AccountNumber} ({existing.Username}) for order {order.OrderNumber}"
                        : $"Pending network account activation failed: {existing.AccountNumber} for order {order.OrderNumber}",
                    metadata: BuildMetadata(new
                    {
                        orderId = order.Id,
                        orderNumber = order.OrderNumber,
                        previous = NetworkAccountStatus.Pending.ToString(),
                        newStatus = existing.Status.ToString(),
                        providerName = existing.ProviderName,
                        source
                    }));

                return Result<NetworkAccountDto>.Success(
                    MapToDto(await ReloadWithIncludesAsync(existing.Id, cancellationToken) ?? existing),
                    pendingResult.IsSuccess
                        ? "Network account activated."
                        : "Network account activation failed. Admin retry required.");
            }

            // Existing row is in some other non-terminated state (e.g.
            // Suspended, Failed). Surface it as-is — admins drive
            // recovery from that state explicitly.
            if (existing is not null)
            {
                return Result<NetworkAccountDto>.Success(
                    MapToDto(await ReloadWithIncludesAsync(existing.Id, cancellationToken) ?? existing),
                    $"Network account already exists for this order (status={existing.Status}).");
            }

            var accountNumber = await GenerateUniqueAccountNumberAsync(now, cancellationToken);
            if (accountNumber is null)
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Could not generate a unique network account number. Please retry.");

            var contactEmail = order.Email ?? order.User?.Email;
            var username = await GenerateUniqueUsernameAsync(contactEmail, accountNumber, cancellationToken);

            var entity = new NetworkAccount
            {
                AccountNumber = accountNumber,
                Username = username,
                OrderId = order.Id,
                Status = NetworkAccountStatus.Pending,
                Source = source,
                ProviderName = _provisioner.ProviderName,
                PackageType = order.PackageType,
                PackageName = order.PackageName,
                PackageSpeedLabel = order.PackageSpeedLabel,
                PackagePrice = order.PackagePrice,
                RadiusProfileId = order.ServicePackage?.RadiusProfileId,
                ProvisioningStatus = ProvisioningStatus.NotProvisioned,
                LastStatusChangedByUserId = _currentUser.UserId
            };
            AddProvisioningCreatedEvent(entity, $"Network account created for order {order.OrderNumber}.");

            var context = BuildContext(entity, order);
            var providerResult = await SafeProvisionerCallAsync(
                ct => _provisioner.ProvisionAsync(context, ct),
                "provision", entity.AccountNumber, cancellationToken);

            if (providerResult.IsSuccess)
            {
                entity.Status = NetworkAccountStatus.Active;
                entity.ProviderReference = providerResult.ProviderReference;
                entity.ProvisionedAtUtc = now;
            }
            else
            {
                entity.Status = NetworkAccountStatus.Failed;
                entity.LastFailureReason = Trim(providerResult.FailureReason);
            }

            _dbContext.NetworkAccounts.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (providerResult.IsSuccess)
            {
                await EmitAuditAsync(
                    AuditActionType.NetworkAccountProvisioned,
                    ActorTypeForSource(source),
                    entity, order,
                    summary: $"Network account provisioned: {entity.AccountNumber} ({entity.Username}) for order {order.OrderNumber}",
                    metadata: BuildMetadata(new
                    {
                        orderId = order.Id,
                        orderNumber = order.OrderNumber,
                        packageType = entity.PackageType,
                        packageName = entity.PackageName,
                        providerName = entity.ProviderName,
                        source
                    }));
            }
            else
            {
                await EmitAuditAsync(
                    AuditActionType.NetworkAccountProvisionFailed,
                    ActorTypeForSource(source),
                    entity, order,
                    summary: $"Network account provision failed: {entity.AccountNumber} for order {order.OrderNumber}",
                    metadata: BuildMetadata(new
                    {
                        orderId = order.Id,
                        orderNumber = order.OrderNumber,
                        packageType = entity.PackageType,
                        providerName = entity.ProviderName,
                        failureReason = entity.LastFailureReason,
                        source
                    }));
            }

            var dto = MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity);
            return providerResult.IsSuccess
                ? Result<NetworkAccountDto>.Success(dto, "Network account provisioned.")
                : Result<NetworkAccountDto>.Success(dto,
                    "Network account record created in Failed state. Admin retry required.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error provisioning network account for order {OrderId}", orderId);
            return Result<NetworkAccountDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while provisioning the network account.");
        }
    }

    // Phase 44 — placeholder reservation created when payment lands so
    // the client sees "Pending Activation" under My Services straight
    // away. The provisioner is NOT called here; that happens later when
    // the installation completes and ProvisionForOrderAsync activates
    // this same row. Idempotent.
    public async Task<Result<NetworkAccountDto>> EnsurePendingForOrderAsync(Guid orderId, NetworkAccountSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            if (orderId == Guid.Empty)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.BAD_REQUEST, "OrderId is required.");

            var order = await _dbContext.Orders
                .Include(o => o.User)
                .Include(o => o.ServicePackage)
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

            if (order is null)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.NOT_FOUND, "Order not found.");

            // Reuse the row if a non-terminated account already covers
            // this order — never create a duplicate placeholder.
            var existing = await _dbContext.NetworkAccounts
                .FirstOrDefaultAsync(n => n.OrderId == orderId
                                       && NonTerminatedStatuses.Contains(n.Status), cancellationToken);
            if (existing is not null)
            {
                return Result<NetworkAccountDto>.Success(
                    MapToDto(await ReloadWithIncludesAsync(existing.Id, cancellationToken) ?? existing),
                    $"Network account already exists for this order (status={existing.Status}).");
            }

            var now = DateTime.UtcNow;
            var accountNumber = await GenerateUniqueAccountNumberAsync(now, cancellationToken);
            if (accountNumber is null)
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Could not generate a unique network account number. Please retry.");

            var contactEmail = order.Email ?? order.User?.Email;
            var username = await GenerateUniqueUsernameAsync(contactEmail, accountNumber, cancellationToken);

            var entity = new NetworkAccount
            {
                AccountNumber = accountNumber,
                Username = username,
                OrderId = order.Id,
                Status = NetworkAccountStatus.Pending,
                Source = source,
                ProviderName = _provisioner.ProviderName,
                PackageType = order.PackageType,
                PackageName = order.PackageName,
                PackageSpeedLabel = order.PackageSpeedLabel,
                PackagePrice = order.PackagePrice,
                // Phase 3.5 — copy provisioning intent from the package
                // so future RADIUS automation has the speed bundle
                // already pinned. Stays null on packages that don't
                // require provisioning.
                RadiusProfileId = order.ServicePackage?.RadiusProfileId,
                ProvisioningStatus = ProvisioningStatus.NotProvisioned,
                LastStatusChangedByUserId = _currentUser.UserId
            };

            _dbContext.NetworkAccounts.Add(entity);
            AddProvisioningCreatedEvent(entity, $"Pending network account reserved for order {order.OrderNumber}.");
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.NetworkAccountProvisioned,
                ActorTypeForSource(source),
                entity, order,
                summary: $"Pending network account reserved: {entity.AccountNumber} for order {order.OrderNumber}",
                metadata: BuildMetadata(new
                {
                    orderId = order.Id,
                    orderNumber = order.OrderNumber,
                    status = NetworkAccountStatus.Pending.ToString(),
                    providerName = entity.ProviderName,
                    source,
                    reason = "PaymentCompleted"
                }));

            return Result<NetworkAccountDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Network account reserved in Pending state.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reserving pending network account for order {OrderId}", orderId);
            return Result<NetworkAccountDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while reserving the network account.");
        }
    }

    public async Task<Result> TerminateForOrderAsync(Guid orderId, string? reason, NetworkAccountSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            if (orderId == Guid.Empty)
                return Result.Failure(ErrorCodes.BAD_REQUEST, "OrderId is required.");

            var accounts = await _dbContext.NetworkAccounts
                .Where(n => n.OrderId == orderId && NonTerminatedStatuses.Contains(n.Status))
                .ToListAsync(cancellationToken);

            if (accounts.Count == 0)
                return Result.Success("No active network accounts to terminate.");

            var order = await _dbContext.Orders
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

            var now = DateTime.UtcNow;
            var trimmedReason = Trim(reason);

            foreach (var account in accounts)
            {
                var context = BuildContext(account, order);
                var providerResult = await SafeProvisionerCallAsync(
                    ct => _provisioner.TerminateAsync(context, trimmedReason, ct),
                    "terminate", account.AccountNumber, cancellationToken);

                account.Status = NetworkAccountStatus.Terminated;
                account.TerminatedAtUtc = now;
                account.TerminationReason = trimmedReason ?? account.TerminationReason;
                account.LastStatusChangedByUserId = _currentUser.UserId;

                if (!providerResult.IsSuccess)
                    account.LastFailureReason = Trim(providerResult.FailureReason);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            foreach (var account in accounts)
            {
                await EmitAuditAsync(
                    AuditActionType.NetworkAccountTerminated,
                    ActorTypeForSource(source),
                    account, order,
                    summary: $"Network account terminated: {account.AccountNumber} (order {order?.OrderNumber ?? "<unknown>"})",
                    metadata: BuildMetadata(new
                    {
                        orderId,
                        reason = trimmedReason,
                        providerSuccess = string.IsNullOrWhiteSpace(account.LastFailureReason),
                        source
                    }));
            }

            return Result.Success($"Terminated {accounts.Count} network account(s).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error terminating network accounts for order {OrderId}", orderId);
            return Result.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while terminating network accounts.");
        }
    }

    public async Task<Result<NetworkAccountDto>> AdminSuspendAsync(Guid id, AdminSuspendNetworkAccountRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.BAD_REQUEST, "Network account id is required.");

            var entity = await LoadWithOrderAsync(id, cancellationToken);
            if (entity is null)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.NOT_FOUND, "Network account not found.");

            if (entity.Status == NetworkAccountStatus.Suspended)
                return Result<NetworkAccountDto>.Success(MapToDto(entity), "Network account is already suspended.");

            if (entity.Status != NetworkAccountStatus.Active)
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.CONFLICT,
                    $"Only Active network accounts can be suspended (current status: {entity.Status}).");

            var reason = Trim(request?.Reason);
            var now = DateTime.UtcNow;
            var context = BuildContext(entity, entity.Order);

            var providerResult = await SafeProvisionerCallAsync(
                ct => _provisioner.SuspendAsync(context, reason, ct),
                "suspend", entity.AccountNumber, cancellationToken);

            if (!providerResult.IsSuccess)
            {
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    $"Provider failed to suspend network account: {providerResult.FailureReason ?? "unknown error"}");
            }

            entity.Status = NetworkAccountStatus.Suspended;
            entity.SuspendedAtUtc = now;
            entity.SuspensionReason = reason ?? entity.SuspensionReason;
            entity.LastStatusChangedByUserId = _currentUser.UserId;
            if (!string.IsNullOrWhiteSpace(request?.AdminNotes))
                entity.AdminNotes = request!.AdminNotes!.Trim();

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.NetworkAccountSuspended,
                AuditActorType.Admin,
                entity, entity.Order,
                summary: $"Network account suspended: {entity.AccountNumber}",
                metadata: BuildMetadata(new { reason }));

            return Result<NetworkAccountDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Network account suspended.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error suspending network account {Id}", id);
            return Result<NetworkAccountDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while suspending the network account.");
        }
    }

    public async Task<Result<NetworkAccountDto>> AdminResumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.BAD_REQUEST, "Network account id is required.");

            var entity = await LoadWithOrderAsync(id, cancellationToken);
            if (entity is null)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.NOT_FOUND, "Network account not found.");

            if (entity.Status == NetworkAccountStatus.Active)
                return Result<NetworkAccountDto>.Success(MapToDto(entity), "Network account is already active.");

            if (entity.Status != NetworkAccountStatus.Suspended)
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.CONFLICT,
                    $"Only Suspended network accounts can be resumed (current status: {entity.Status}).");

            var now = DateTime.UtcNow;
            var context = BuildContext(entity, entity.Order);

            var providerResult = await SafeProvisionerCallAsync(
                ct => _provisioner.ResumeAsync(context, ct),
                "resume", entity.AccountNumber, cancellationToken);

            if (!providerResult.IsSuccess)
            {
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    $"Provider failed to resume network account: {providerResult.FailureReason ?? "unknown error"}");
            }

            entity.Status = NetworkAccountStatus.Active;
            entity.ResumedAtUtc = now;
            entity.SuspensionReason = null;
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.NetworkAccountResumed,
                AuditActorType.Admin,
                entity, entity.Order,
                summary: $"Network account resumed: {entity.AccountNumber}",
                metadata: null);

            return Result<NetworkAccountDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Network account resumed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error resuming network account {Id}", id);
            return Result<NetworkAccountDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while resuming the network account.");
        }
    }

    public async Task<Result<NetworkAccountDto>> AdminTerminateAsync(Guid id, AdminTerminateNetworkAccountRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.BAD_REQUEST, "Network account id is required.");

            var entity = await LoadWithOrderAsync(id, cancellationToken);
            if (entity is null)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.NOT_FOUND, "Network account not found.");

            if (entity.Status == NetworkAccountStatus.Terminated)
                return Result<NetworkAccountDto>.Success(MapToDto(entity), "Network account is already terminated.");

            var reason = Trim(request?.Reason);
            var now = DateTime.UtcNow;
            var context = BuildContext(entity, entity.Order);

            var providerResult = await SafeProvisionerCallAsync(
                ct => _provisioner.TerminateAsync(context, reason, ct),
                "terminate", entity.AccountNumber, cancellationToken);

            entity.Status = NetworkAccountStatus.Terminated;
            entity.TerminatedAtUtc = now;
            entity.TerminationReason = reason ?? entity.TerminationReason;
            entity.LastStatusChangedByUserId = _currentUser.UserId;
            if (!string.IsNullOrWhiteSpace(request?.AdminNotes))
                entity.AdminNotes = request!.AdminNotes!.Trim();
            if (!providerResult.IsSuccess)
                entity.LastFailureReason = Trim(providerResult.FailureReason);

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.NetworkAccountTerminated,
                AuditActorType.Admin,
                entity, entity.Order,
                summary: $"Network account terminated by admin: {entity.AccountNumber}",
                metadata: BuildMetadata(new
                {
                    reason,
                    providerSuccess = providerResult.IsSuccess
                }));

            return Result<NetworkAccountDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                providerResult.IsSuccess
                    ? "Network account terminated."
                    : "Network account marked terminated locally; provider call failed (see LastFailureReason).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error terminating network account {Id}", id);
            return Result<NetworkAccountDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while terminating the network account.");
        }
    }

    public async Task<Result<NetworkAccountDto>> AdminChangePackageAsync(Guid id, AdminChangeNetworkAccountPackageRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.BAD_REQUEST, "Network account id is required.");

            if (request is null || request.NewServicePackageId == Guid.Empty)
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "NewServicePackageId is required.");

            var entity = await LoadWithOrderAsync(id, cancellationToken);
            if (entity is null)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.NOT_FOUND, "Network account not found.");

            if (entity.Status != NetworkAccountStatus.Active)
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.CONFLICT,
                    $"Only Active network accounts can be repackaged (current status: {entity.Status}).");

            var newPackage = await _dbContext.ServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.NewServicePackageId, cancellationToken);

            if (newPackage is null)
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.NOT_FOUND, "Referenced service package was not found.");

            if (newPackage.Status != ServicePackageStatus.Active)
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Replacement service package is not active and cannot be assigned.");

            var previousPackageName = entity.PackageName;
            var previousPackageType = entity.PackageType;

            entity.PackageType = newPackage.Type;
            entity.PackageName = newPackage.Name;
            entity.PackageSpeedLabel = newPackage.SpeedLabel;
            entity.PackagePrice = newPackage.Price;

            var context = BuildContext(entity, entity.Order);

            var providerResult = await SafeProvisionerCallAsync(
                ct => _provisioner.ChangePackageAsync(context, ct),
                "changepackage", entity.AccountNumber, cancellationToken);

            if (!providerResult.IsSuccess)
            {
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    $"Provider failed to apply package change: {providerResult.FailureReason ?? "unknown error"}");
            }

            entity.LastPackageChangeAtUtc = DateTime.UtcNow;
            entity.LastStatusChangedByUserId = _currentUser.UserId;
            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.NetworkAccountPackageChanged,
                AuditActorType.Admin,
                entity, entity.Order,
                summary: $"Network account package changed: {entity.AccountNumber} ({previousPackageName} -> {entity.PackageName})",
                metadata: BuildMetadata(new
                {
                    previousPackageName,
                    previousPackageType,
                    newPackageName = entity.PackageName,
                    newPackageType = entity.PackageType,
                    newPackagePrice = entity.PackagePrice
                }));

            return Result<NetworkAccountDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Network account package changed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error changing package on network account {Id}", id);
            return Result<NetworkAccountDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while changing the network account package.");
        }
    }

    private async Task<Result<NetworkAccountDto>?> CheckEligibilityAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.Status == OrderStatus.Cancelled
            || order.Status == OrderStatus.Failed
            || order.Status == OrderStatus.Rejected)
        {
            return Result<NetworkAccountDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                $"Cannot provision network access for an order in status '{order.Status}'.");
        }

        if (IsPhysicalPackage(order.PackageType))
        {
            var hasCompletedInstall = await _dbContext.Installations
                .AnyAsync(i => i.OrderId == order.Id && i.Status == InstallationStatus.Completed, cancellationToken);

            if (!hasCompletedInstall)
            {
                return Result<NetworkAccountDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Order is for a physical package ({order.PackageType}); a Completed installation is required before provisioning.");
            }

            return null;
        }

        if (!PostPaymentOrderStatuses.Contains(order.Status))
        {
            return Result<NetworkAccountDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                $"Order must be in PaymentReceived, Provisioning, or Active to provision a non-physical package (current status: {order.Status}).");
        }

        return null;
    }

    private static bool IsPhysicalPackage(ServicePackageType type)
        => type == ServicePackageType.Fibre || type == ServicePackageType.Wireless;

    private static AuditActorType ActorTypeForSource(NetworkAccountSource source)
        => source == NetworkAccountSource.AdminManual ? AuditActorType.Admin : AuditActorType.System;

    private async Task<NetworkProvisioningResult> SafeProvisionerCallAsync(Func<CancellationToken, Task<NetworkProvisioningResult>> call, string operation, string accountNumber, CancellationToken cancellationToken)
    {
        try
        {
            var result = await call(cancellationToken) ?? NetworkProvisioningResult.Failure("Provider returned null.");
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Network provisioner '{Provider}' {Operation} failed for {AccountNumber}: {Reason}",
                    _provisioner.ProviderName, operation, accountNumber, result.FailureReason);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Network provisioner '{Provider}' {Operation} threw for {AccountNumber}",
                _provisioner.ProviderName, operation, accountNumber);
            return NetworkProvisioningResult.Failure($"Provider exception: {ex.Message}");
        }
    }

    private static NetworkProvisioningContext BuildContext(NetworkAccount account, Order? order)
        => new()
        {
            NetworkAccountId = account.Id,
            AccountNumber = account.AccountNumber,
            Username = account.Username,
            OrderId = account.OrderId,
            OrderNumber = order?.OrderNumber ?? string.Empty,
            UserId = order?.UserId ?? Guid.Empty,
            PackageType = account.PackageType,
            PackageName = account.PackageName,
            PackageSpeedLabel = account.PackageSpeedLabel,
            PackagePrice = account.PackagePrice
        };

    private async Task<NetworkAccount?> LoadWithOrderAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.NetworkAccounts
            .Include(n => n.Order)
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

    private async Task<NetworkAccount?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.NetworkAccounts
            .AsNoTracking()
            .Include(n => n.Order)
            .ThenInclude(o => o!.ServicePackage)
            .Include(n => n.LastStatusChangedByUser)
            .Include(n => n.RadiusProfile)
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

    private async Task<Result<NetworkAccountDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.BAD_REQUEST, "Network account id is required.");

            var query = _dbContext.NetworkAccounts
                .AsNoTracking()
                .Include(n => n.Order)
                .ThenInclude(o => o!.ServicePackage)
                .Include(n => n.LastStatusChangedByUser)
                .Include(n => n.RadiusProfile)
                .Where(n => n.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(n => n.Order!.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);
            if (entity is null)
                return Result<NetworkAccountDto>.Failure(ErrorCodes.NOT_FOUND, "Network account not found.");

            var dto = MapToDto(entity);
            // Phase 46 — surface the matching installation on detail
            // reads so the service detail page can show scheduled date
            // / installation status without an extra round-trip.
            dto.Installation = await ResolveInstallationSummaryAsync(entity.OrderId, cancellationToken);
            return Result<NetworkAccountDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching network account {Id}", id);
            return Result<NetworkAccountDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the network account.");
        }
    }

    private IQueryable<NetworkAccount> BuildQuery(NetworkAccountFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.NetworkAccounts
            .AsNoTracking()
            .Include(n => n.Order)
            .ThenInclude(o => o!.ServicePackage)
            .Include(n => n.LastStatusChangedByUser)
            .Include(n => n.RadiusProfile)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(n => n.Order!.UserId == restrictToUserId.Value);
        else if (filter.UserId.HasValue)
            query = query.Where(n => n.Order!.UserId == filter.UserId.Value);

        if (filter.OrderId.HasValue)
            query = query.Where(n => n.OrderId == filter.OrderId.Value);

        if (filter.StatusFilter.HasValue)
            query = query.Where(n => n.Status == filter.StatusFilter.Value);

        if (filter.Source.HasValue)
            query = query.Where(n => n.Source == filter.Source.Value);

        if (filter.PackageType.HasValue)
            query = query.Where(n => n.PackageType == filter.PackageType.Value);

        if (!string.IsNullOrWhiteSpace(filter.ProviderName))
        {
            var v = filter.ProviderName.Trim();
            query = query.Where(n => EF.Functions.Like(n.ProviderName, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.ProviderReference))
        {
            var v = filter.ProviderReference.Trim();
            query = query.Where(n => n.ProviderReference != null && EF.Functions.Like(n.ProviderReference, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Username))
        {
            var v = filter.Username.Trim();
            query = query.Where(n => EF.Functions.Like(n.Username, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.AccountNumber))
        {
            var v = filter.AccountNumber.Trim();
            query = query.Where(n => EF.Functions.Like(n.AccountNumber, $"%{v}%"));
        }

        if (filter.FromUtc.HasValue)
            query = query.Where(n => n.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(n => n.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(n =>
                EF.Functions.Like(n.AccountNumber, $"%{s}%") ||
                EF.Functions.Like(n.Username, $"%{s}%") ||
                EF.Functions.Like(n.PackageName, $"%{s}%") ||
                (n.Order != null && EF.Functions.Like(n.Order.OrderNumber, $"%{s}%")) ||
                (n.ProviderReference != null && EF.Functions.Like(n.ProviderReference, $"%{s}%")));
        }

        return query;
    }

    private static async Task<Result<PagedResult<NetworkAccountDto>>> ToPagedResultAsync(IQueryable<NetworkAccount> query, NetworkAccountFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<NetworkAccountDto>(
            items.Select(MapToDto).ToList(), filter.Page, filter.PageSize, totalCount);

        return Result<PagedResult<NetworkAccountDto>>.Success(paged);
    }

    private async Task<string?> GenerateUniqueAccountNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var datePart = now.ToString("yyyyMMdd");
        for (var attempt = 0; attempt < NetworkAccountNumberMaxAttempts; attempt++)
        {
            var candidate = $"{NetworkAccountNumberPrefix}-{datePart}-{GenerateRandomSuffix(NetworkAccountNumberSuffixLength)}";
            var exists = await _dbContext.NetworkAccounts
                .AnyAsync(n => n.AccountNumber == candidate, cancellationToken);
            if (!exists) return candidate;
        }
        return null;
    }

    private async Task<string> GenerateUniqueUsernameAsync(string? email, string accountNumber, CancellationToken cancellationToken)
    {
        var basePart = SanitiseUsernameBase(email);
        if (string.IsNullOrWhiteSpace(basePart))
            basePart = "user";

        var candidate = basePart;
        var suffix = 2;
        while (await _dbContext.NetworkAccounts
            .AnyAsync(n => n.Username == candidate, cancellationToken))
        {
            candidate = $"{basePart}-{suffix}";
            suffix++;
            if (suffix > 1000)
            {
                // Last-ditch tie-breaker using account number suffix
                var accountSuffix = accountNumber.Replace(NetworkAccountNumberPrefix + "-", "").Replace("-", "");
                return $"{basePart}-{accountSuffix.ToLowerInvariant()}";
            }
        }
        return candidate;
    }

    private static string SanitiseUsernameBase(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        var at = email.IndexOf('@');
        var local = at > 0 ? email[..at] : email;
        var lowered = local.ToLowerInvariant();
        var sanitised = UsernameSanitiseRegex.Replace(lowered, "-").Trim('-');
        if (sanitised.Length > 60) sanitised = sanitised[..60];
        return sanitised;
    }

    private static string GenerateRandomSuffix(int length)
    {
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = AccountNumberAlphabet[buffer[i] % AccountNumberAlphabet.Length];
        return new string(chars);
    }

    // Phase 3.5 — emit a Created ProvisioningEvent so the audit log
    // has a row at the moment a NetworkAccount is reserved. Keeps the
    // event timeline parallel with AuditLogs without touching real
    // routers. Idempotent guarantees live with the callers; this method
    // unconditionally appends and relies on SaveChanges flushing once.
    private void AddProvisioningCreatedEvent(NetworkAccount entity, string summary)
    {
        _dbContext.ProvisioningEvents.Add(new ProvisioningEvent
        {
            Id = Guid.NewGuid(),
            NetworkAccountId = entity.Id,
            EventType = ProvisioningEventType.Created,
            ProviderName = entity.ProviderName,
            IsSuccess = true,
            Summary = summary,
            TriggeredByUserId = _currentUser.UserId,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private async Task EmitAuditAsync(AuditActionType actionType, AuditActorType actorType, NetworkAccount entity, Order? order, string summary,
        string? metadata)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = actionType,
            EntityType = AuditEntityType.NetworkAccount,
            EntityId = entity.Id,
            EntityName = entity.AccountNumber,
            Summary = summary,
            MetadataJson = metadata,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private static string? BuildMetadata(object payload)
    {
        try { return JsonSerializer.Serialize(payload); }
        catch { return null; }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Phase 46 — pick the most relevant installation for the order:
    // prefer a non-terminal row (still in progress), fall back to the
    // most recent terminal one (e.g. the Completed install for an
    // already-Active service).
    private static readonly InstallationStatus[] InstallationActiveStatuses =
    {
        InstallationStatus.PendingScheduling,
        InstallationStatus.Scheduled,
        InstallationStatus.TechnicianAssigned,
        InstallationStatus.EnRoute,
        InstallationStatus.OnSite,
        InstallationStatus.Rescheduled
    };

    private async Task<NetworkAccountInstallationSummaryDto?> ResolveInstallationSummaryAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var summary = await _dbContext.Installations
            .AsNoTracking()
            .Where(i => i.OrderId == orderId)
            .OrderByDescending(i => InstallationActiveStatuses.Contains(i.Status) ? 1 : 0)
            .ThenByDescending(i => i.ScheduledForUtc ?? i.CreatedAtUtc)
            .Select(i => new NetworkAccountInstallationSummaryDto
            {
                Id                 = i.Id,
                InstallationNumber = i.InstallationNumber,
                Status             = i.Status,
                ScheduledForUtc    = i.ScheduledForUtc,
                CompletedAtUtc     = i.CompletedAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);
        return summary;
    }

    private static NetworkAccountDto MapToDto(NetworkAccount n) => new()
    {
        Id = n.Id,
        AccountNumber = n.AccountNumber,
        Username = n.Username,
        OrderId = n.OrderId,
        OrderNumber = n.Order?.OrderNumber,
        OrderStatus = n.Order?.Status,
        UserId = n.Order?.UserId,
        Status = n.Status,
        Source = n.Source,
        ProviderName = n.ProviderName,
        ProviderReference = n.ProviderReference,
        PackageType = n.PackageType,
        PackageName = n.PackageName,
        PackageSpeedLabel = n.PackageSpeedLabel,
        PackagePrice = n.PackagePrice,
        // Phase 46 — extra package + customer + address snapshot from
        // the linked Order so the service detail pages render fully
        // hydrated without extra round-trips.
        PackageDataAllowanceLabel = n.Order?.PackageDataAllowanceLabel,
        PackageIsUncapped = n.Order?.PackageIsUncapped,
        PackageBillingCycle = n.Order?.PackageBillingCycle,
        PackageContractMonths = n.Order?.PackageContractMonths,
        PackageHasFreeInstallation = n.Order?.PackageHasFreeInstallation,
        PackageInstallationFee = n.Order?.PackageInstallationFee,
        PackageIncludesRouter = n.Order?.PackageIncludesRouter,
        CustomerFullName = n.Order?.FullName,
        CustomerEmail = n.Order?.Email,
        CustomerPhoneNumber = n.Order?.PhoneNumber,
        AddressLine1 = n.Order?.AddressLine1,
        AddressLine2 = n.Order?.AddressLine2,
        Suburb = n.Order?.Suburb,
        City = n.Order?.City,
        Province = n.Order?.Province,
        PostalCode = n.Order?.PostalCode,
        Country = n.Order?.Country,
        ProvisionedAtUtc = n.ProvisionedAtUtc,
        SuspendedAtUtc = n.SuspendedAtUtc,
        ResumedAtUtc = n.ResumedAtUtc,
        TerminatedAtUtc = n.TerminatedAtUtc,
        LastPackageChangeAtUtc = n.LastPackageChangeAtUtc,
        AdminNotes = n.AdminNotes,
        LastFailureReason = n.LastFailureReason,
        SuspensionReason = n.SuspensionReason,
        TerminationReason = n.TerminationReason,
        LastStatusChangedByUserId = n.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = n.LastStatusChangedByUser?.Email,
        CreatedAtUtc = n.CreatedAtUtc,
        UpdatedAtUtc = n.UpdatedAtUtc,
        ProvisioningStatus = n.ProvisioningStatus,
        LastProvisioningAttemptUtc = n.LastProvisioningAttemptUtc,
        ProvisioningAttemptCount = n.ProvisioningAttemptCount,
        RadiusProfileId = n.RadiusProfileId,
        RadiusProfileName = n.RadiusProfile?.Name,
        CurrentIpAddress = n.CurrentIpAddress,
        NasIdentifier = n.NasIdentifier,
        PackageRequiresProvisioning = n.Order?.ServicePackage?.RequiresProvisioning,
        PackageProvisioningType = n.Order?.ServicePackage?.ProvisioningType,
        PackageDownloadSpeedMbps = n.Order?.ServicePackage?.DownloadSpeedMbps,
        PackageUploadSpeedMbps = n.Order?.ServicePackage?.UploadSpeedMbps,
        PackageBurstSpeedMbps = n.Order?.ServicePackage?.BurstSpeedMbps,
        // Phase 48 — computed billing-cycle fields. Pure derivation, no
        // additional DB roundtrip; relies on the Order include above
        // already pulling PackageBillingCycle into the projection.
        NextPaymentDateUtc = BillingCycleCalculator.ComputeNextPaymentDateUtc(n),
        NextPaymentAmount = n.Status == NetworkAccountStatus.Active ? n.PackagePrice : (decimal?)null,
        BillingStatusLabel = BillingCycleCalculator.BillingStatusLabel(n)
    };
}
