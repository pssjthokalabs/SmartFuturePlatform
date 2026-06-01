using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Installations.Dtos;
using SmartFuture.Application.NetworkAccounts;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Orders;
using SmartFuture.Application.Notifications.Dtos;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Installations;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.Identity;
using SmartFuture.Shared.Enums.Installations;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.Notifications;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Installations;

public class InstallationService : IInstallationService
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly OrderStatus[] InstallationCreatableOrderStatuses =
    {
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment,
        OrderStatus.PaymentReceived,
        OrderStatus.Provisioning,
        OrderStatus.Active
    };

    private static readonly InstallationStatus[] TerminalInstallationStatuses =
    {
        InstallationStatus.Completed,
        InstallationStatus.Cancelled,
        InstallationStatus.Failed
    };

    private static readonly InstallationStatus[] OrderProvisioningTriggers =
    {
        InstallationStatus.Scheduled,
        InstallationStatus.TechnicianAssigned,
        InstallationStatus.EnRoute,
        InstallationStatus.OnSite,
        InstallationStatus.Rescheduled
    };

    private static readonly OrderStatus[] OrderProvisioningEligibleSources =
    {
        OrderStatus.Confirmed,
        OrderStatus.AwaitingPayment,
        OrderStatus.PaymentReceived
    };

    private static readonly OrderStatus[] OrderActivationBlockers =
    {
        OrderStatus.Active,
        OrderStatus.Cancelled,
        OrderStatus.Failed,
        OrderStatus.Rejected
    };

    // Excludes 0/O/1/I/L to avoid transcription ambiguity.
    private const string InstallationNumberAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int InstallationNumberSuffixLength = 6;
    private const int InstallationNumberMaxAttempts = 5;

    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly INotificationService _notificationService;
    private readonly INetworkAccountService _networkAccountService;
    private readonly UserManager<User> _userManager;
    private readonly Microsoft.Extensions.Options.IOptions<AutoBillingSettings> _autoBillingSettings;
    private readonly Microsoft.Extensions.Options.IOptions<ServiceActivationSettings> _activationSettings;
    private readonly IAutoBillingService _autoBilling;
    private readonly ILogger<InstallationService> _logger;

    public InstallationService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, INotificationService notificationService, INetworkAccountService networkAccountService,
        UserManager<User> userManager, Microsoft.Extensions.Options.IOptions<AutoBillingSettings> autoBillingSettings,
        Microsoft.Extensions.Options.IOptions<ServiceActivationSettings> activationSettings,
        IAutoBillingService autoBilling, ILogger<InstallationService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _notificationService = notificationService;
        _networkAccountService = networkAccountService;
        _userManager = userManager;
        _autoBillingSettings = autoBillingSettings;
        _activationSettings = activationSettings;
        _autoBilling = autoBilling;
        _logger = logger;
    }

    public async Task<Result<PagedResult<InstallationDto>>> SearchAdminAsync(InstallationFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new InstallationFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: null);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching installations (admin)");
            return Result<PagedResult<InstallationDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching installations.");
        }
    }

    public async Task<Result<PagedResult<InstallationDto>>> GetMineAsync(InstallationFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<InstallationDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new InstallationFilterRequestDto();
            var query = BuildQuery(filter, restrictToUserId: currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching installations (customer)");
            return Result<PagedResult<InstallationDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your installations.");
        }
    }

    public Task<Result<InstallationDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => GetByIdInternalAsync(id, restrictToUserId: null, cancellationToken);

    public Task<Result<InstallationDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = _currentUser.UserId;
        if (currentUserId is null || currentUserId == Guid.Empty)
            return Task.FromResult(
                Result<InstallationDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated."));

        return GetByIdInternalAsync(id, restrictToUserId: currentUserId, cancellationToken);
    }

    // ─── Technician-scoped reads (go-live alignment) ────────────────
    //
    // Filtered to installations whose TechnicianUserId matches the
    // calling user. Used by /api/technician/installations to show
    // each technician only their own assignments.
    public async Task<Result<PagedResult<InstallationDto>>> SearchAssignedToMeAsync(
        InstallationFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<PagedResult<InstallationDto>>.Failure(
                    ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            filter ??= new InstallationFilterRequestDto();
            // Build the standard admin-style query, then narrow to
            // TechnicianUserId = me. We deliberately don't push this
            // into BuildQuery's restrictToUserId because that filter
            // means "Order.UserId" (customer), not "TechnicianUserId".
            var query = BuildQuery(filter, restrictToUserId: null)
                .Where(i => i.TechnicianUserId == currentUserId.Value);
            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching installations (technician)");
            return Result<PagedResult<InstallationDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching your assigned installations.");
        }
    }

    public async Task<Result<InstallationDto>> GetAssignedToMeByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Installation id is required.");

            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var entity = await _dbContext.Installations
                .AsNoTracking()
                .Include(i => i.Order)
                .Include(i => i.LastStatusChangedByUser)
                .FirstOrDefaultAsync(i => i.Id == id
                                       && i.TechnicianUserId == currentUserId.Value,
                                     cancellationToken);
            return entity is null
                ? Result<InstallationDto>.Failure(
                    ErrorCodes.NOT_FOUND, "Installation not found or not assigned to you.")
                : Result<InstallationDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching technician installation {Id}", id);
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the installation.");
        }
    }

    /// <summary>
    /// Technician self-update of installation status. The technician
    /// is identified from the JWT; we refuse if the installation
    /// isn't assigned to the calling user. On Completed we delegate
    /// to <see cref="AdminUpdateStatusAsync"/> via the same shared
    /// post-completion hook (network provisioning + first monthly
    /// invoice + auto-debit), so technician completions are
    /// indistinguishable from admin completions downstream.
    /// </summary>
    public async Task<Result<InstallationDto>> TechnicianUpdateStatusAsync(
        Guid id, TechnicianUpdateInstallationStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Installation id is required.");
            if (request is null)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            var entity = await _dbContext.Installations
                .Include(i => i.Order)
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
            if (entity is null)
                return Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Installation not found.");
            if (entity.TechnicianUserId != currentUserId.Value)
                return Result<InstallationDto>.Failure(
                    ErrorCodes.FORBIDDEN, "Installation is not assigned to you.");

            // Status-transition guard. Technicians can only move
            // through the operational lifecycle: EnRoute → OnSite
            // ("in progress") → Completed, or Failed if the visit
            // didn't work. Cancellation stays admin-only.
            var allowed = request.Status switch
            {
                InstallationStatus.EnRoute    => true,
                InstallationStatus.OnSite     => true,
                InstallationStatus.Completed  => true,
                InstallationStatus.Failed     => true,
                _ => false
            };
            if (!allowed)
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Technicians cannot move an installation to '{request.Status}'. Use admin tools instead.");
            }

            // Completion gate — required fields must be present.
            if (request.Status == InstallationStatus.Completed)
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(request.RouterMakeModel))    missing.Add("RouterMakeModel");
                if (string.IsNullOrWhiteSpace(request.RouterSerialNumber)) missing.Add("RouterSerialNumber");
                if (string.IsNullOrWhiteSpace(request.InstalledLocationNotes)) missing.Add("InstalledLocationNotes");
                if (missing.Count > 0)
                {
                    return Result<InstallationDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR,
                        $"Cannot mark Completed — missing required fields: {string.Join(", ", missing)}.");
                }
            }
            if (request.Status == InstallationStatus.Failed
                && string.IsNullOrWhiteSpace(request.FailureReason))
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "FailureReason is required when marking an installation Failed.");
            }

            // Persist the completion-detail fields first so the shared
            // admin pipeline (network provisioning + first monthly
            // invoice + auto-debit) sees the captured values.
            if (request.Status == InstallationStatus.Completed)
            {
                entity.RouterMakeModel        = Trim(request.RouterMakeModel)        ?? entity.RouterMakeModel;
                entity.RouterSerialNumber     = Trim(request.RouterSerialNumber)     ?? entity.RouterSerialNumber;
                entity.RouterMacAddress       = Trim(request.RouterMacAddress)       ?? entity.RouterMacAddress;
                entity.OntReference           = Trim(request.OntReference)           ?? entity.OntReference;
                entity.InstalledLocationNotes = Trim(request.InstalledLocationNotes) ?? entity.InstalledLocationNotes;
                entity.SpeedTestResult        = Trim(request.SpeedTestResult)        ?? entity.SpeedTestResult;
                entity.CustomerSignOffName    = Trim(request.CustomerSignOffName)    ?? entity.CustomerSignOffName;
            }
            if (!string.IsNullOrWhiteSpace(request.TechnicianNotes))
            {
                entity.TechnicianNotes = request.TechnicianNotes.Trim();
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            // Delegate to the admin status-change path so the
            // post-completion pipeline (first monthly invoice +
            // auto-debit + Order.PendingPayment flip + audit) runs
            // exactly once and identically regardless of caller.
            return await AdminUpdateStatusAsync(id, new AdminUpdateInstallationStatusDto
            {
                Status = request.Status,
                CompletionNotes = request.CompletionNotes,
                FailureReason = request.FailureReason
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in technician status update for {Id}", id);
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the installation.");
        }
    }

    private async Task<Result<InstallationDto>> GetByIdInternalAsync(Guid id, Guid? restrictToUserId, CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Installation id is required.");

            var query = _dbContext.Installations
                .AsNoTracking()
                .Include(i => i.Order)
                .Include(i => i.LastStatusChangedByUser)
                .Where(i => i.Id == id);

            if (restrictToUserId.HasValue)
                query = query.Where(i => i.Order!.UserId == restrictToUserId.Value);

            var entity = await query.FirstOrDefaultAsync(cancellationToken);

            if (entity is null)
                return Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Installation not found.");

            var dto = MapToDto(entity);
            dto.ServiceId = await ResolveLinkedServiceIdAsync(entity.OrderId, cancellationToken);
            return Result<InstallationDto>.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching installation {Id}", id);
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the installation.");
        }
    }

    // Most-recent NetworkAccount (service) for an order, if one exists.
    // Shared between GET responses and the AdminUpdateStatus response
    // so admin portal "View Service" links never have to make a second
    // round-trip.
    private async Task<Guid?> ResolveLinkedServiceIdAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty) return null;
        return await _dbContext.NetworkAccounts
            .AsNoTracking()
            .Where(na => na.OrderId == orderId)
            .OrderByDescending(na => na.CreatedAtUtc)
            .Select(na => (Guid?)na.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Result<InstallationDto>> CreateAsync(CreateInstallationRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.OrderId == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.VALIDATION_ERROR, "OrderId is required.");

            if (request.ScheduledForUtc.HasValue
                && request.ScheduledForUtc.Value < DateTime.UtcNow.Date)
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "ScheduledForUtc cannot be in the past.");
            }

            var technicianValidation = ValidateTechnicianContact(request.TechnicianEmail, request.TechnicianPhone);
            if (technicianValidation is not null) return technicianValidation;

            var order = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);

            if (order is null)
                return Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Referenced order was not found.");

            if (!InstallationCreatableOrderStatuses.Contains(order.Status))
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Installations cannot be created for orders in status '{order.Status}'.");
            }

            var existingActive = await _dbContext.Installations
                .AnyAsync(i => i.OrderId == order.Id
                            && !TerminalInstallationStatuses.Contains(i.Status), cancellationToken);

            if (existingActive)
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.CONFLICT,
                    "This order already has an active installation. Cancel or complete it before creating a new one.");
            }

            // Go-live (Issue 4): when the admin's Schedule Installation
            // modal supplies a TechnicianUserId but no Name/Email/Phone,
            // back-fill those from the User record. Without this the
            // installation row gets TechnicianUserId set but
            // TechnicianName left null, and the portal hides the
            // technician card (the mapper keys off `technicianName`).
            var createTechLookup = await ResolveTechnicianAsync(request.TechnicianUserId, cancellationToken);
            if (!createTechLookup.IsSuccess)
                return Result<InstallationDto>.Failure(
                    createTechLookup.Code ?? ErrorCodes.VALIDATION_ERROR,
                    createTechLookup.Message);
            var createTech = createTechLookup.Data!;
            string? createTechName = Trim(request.TechnicianName);
            string? createTechEmail = Trim(request.TechnicianEmail);
            string? createTechPhone = Trim(request.TechnicianPhone);
            Guid? createTechId = request.TechnicianUserId;
            if (createTech.User is not null)
            {
                createTechId = createTech.User.Id;
                createTechName = $"{createTech.User.FirstName} {createTech.User.LastName}".Trim();
                if (string.IsNullOrWhiteSpace(createTechName)) createTechName = createTech.User.Email;
                createTechEmail = createTech.User.Email;
                createTechPhone = createTech.User.PhoneNumber;
            }
            else if (createTech.ExplicitUnassign)
            {
                createTechId = null;
                createTechName = null;
                createTechEmail = null;
                createTechPhone = null;
            }

            var now = DateTime.UtcNow;

            var entity = new Installation
            {
                OrderId = order.Id,
                Status = request.ScheduledForUtc.HasValue
                    ? InstallationStatus.Scheduled
                    : InstallationStatus.PendingScheduling,
                Source = InstallationSource.Admin,
                ScheduledForUtc = request.ScheduledForUtc,
                TechnicianName = createTechName,
                TechnicianPhone = createTechPhone,
                TechnicianEmail = createTechEmail,
                TechnicianUserId = createTechId,
                AddressLine1 = order.AddressLine1,
                AddressLine2 = order.AddressLine2,
                Suburb = order.Suburb,
                City = order.City,
                Province = order.Province,
                PostalCode = order.PostalCode,
                Country = order.Country,
                Latitude = order.Latitude,
                Longitude = order.Longitude,
                GooglePlaceId = order.GooglePlaceId,
                MapProviderReference = order.MapProviderReference,
                CustomerNotes = order.CustomerNotes,
                AdminNotes = Trim(request.AdminNotes),
                LastStatusChangedByUserId = _currentUser.UserId
            };

            var installationNumber = await GenerateUniqueInstallationNumberAsync(now, cancellationToken);
            if (installationNumber is null)
                return Result<InstallationDto>.Failure(
                    ErrorCodes.EXCEPTION,
                    "Could not generate a unique installation number. Please retry.");

            entity.InstallationNumber = installationNumber;

            _dbContext.Installations.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitInstallationAuditAsync(
                AuditActionType.InstallationStatusChanged,
                AuditActorType.Admin,
                entity,
                summary: $"Installation created ({entity.InstallationNumber}, status={entity.Status})",
                metadata: BuildMetadata(new
                {
                    previous = (InstallationStatus?)null,
                    newStatus = entity.Status,
                    orderId = entity.OrderId,
                    orderNumber = order.OrderNumber
                }));

            if (entity.Status == InstallationStatus.Scheduled)
                await NotifyCustomerOfInstallationAsync(entity, order, NotificationType.InstallationScheduled, cancellationToken);

            return Result<InstallationDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Installation created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating installation");
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the installation.");
        }
    }

    public async Task<Result<InstallationDto>> AdminUpdateAsync(Guid id, AdminUpdateInstallationRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Installation id is required.");

            if (request is null)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            // Issue 8 (go-live) — admin AdminUpdate is the only PUT seam,
            // so it gets called for "assign technician", "reschedule", and
            // full address edits. The previous unconditional
            // `AddressLine1 is required` rule rejected technician-only
            // saves whose request body had no address payload. We now
            // treat AddressLine1 as preserve-on-missing: omit / blank /
            // whitespace = keep what's already on the row. Full-address
            // edits still validate below (the field stays non-empty when
            // the caller is editing the address — see the patch below
            // where we copy the existing value into the request).
            // ------------------------------------------------------------
            // Pre-resolve the entity so we can fall back to the persisted
            // address when the client sent nothing for it. We re-fetch
            // again later for the actual mutation; one extra round-trip
            // is acceptable for the back-fill.
            var existingAddressLine1 = await _dbContext.Installations
                .AsNoTracking()
                .Where(i => i.Id == id)
                .Select(i => i.AddressLine1)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(request.AddressLine1))
            {
                if (string.IsNullOrWhiteSpace(existingAddressLine1))
                    return Result<InstallationDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR,
                        "AddressLine1 is required and isn't already set on this installation.");
                request.AddressLine1 = existingAddressLine1!;
            }

            if (request.Latitude.HasValue && (request.Latitude.Value < -90m || request.Latitude.Value > 90m))
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Latitude must be between -90 and 90.");

            if (request.Longitude.HasValue && (request.Longitude.Value < -180m || request.Longitude.Value > 180m))
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Longitude must be between -180 and 180.");

            if (request.ScheduledForUtc.HasValue
                && request.ScheduledForUtc.Value < DateTime.UtcNow.Date)
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "ScheduledForUtc cannot be in the past.");
            }

            var technicianValidation = ValidateTechnicianContact(request.TechnicianEmail, request.TechnicianPhone);
            if (technicianValidation is not null) return technicianValidation;

            // Phase 40 — when the admin picks a Technician user from the
            // new dropdown, the request carries only the user id. We
            // resolve the row, verify the role + status, and back-fill
            // name/email/phone from their User record so the entity
            // stays consistent regardless of what (if anything) the
            // client sent for those fields.
            var techLookup = await ResolveTechnicianAsync(request.TechnicianUserId, cancellationToken);
            if (!techLookup.IsSuccess)
                return Result<InstallationDto>.Failure(techLookup.Code ?? ErrorCodes.VALIDATION_ERROR, techLookup.Message);

            var entity = await _dbContext.Installations
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

            if (entity is null)
                return Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Installation not found.");

            if (TerminalInstallationStatuses.Contains(entity.Status))
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.CONFLICT,
                    $"Installations in status '{entity.Status}' cannot be edited. " +
                    "Use the status endpoint with the same status to append notes if needed.");
            }

            // Issue 8 (go-live) — same preserve-on-missing rule for the
            // schedule. A tech-assign-only PUT must not blank out an
            // already-scheduled date.
            entity.ScheduledForUtc = request.ScheduledForUtc ?? entity.ScheduledForUtc;

            var tech = techLookup.Data!;
            if (tech.User is not null)
            {
                // Authoritative copy from the User record — the admin's
                // dropdown selection is the source of truth.
                entity.TechnicianUserId = tech.User.Id;
                entity.TechnicianName   = $"{tech.User.FirstName} {tech.User.LastName}".Trim();
                entity.TechnicianEmail  = tech.User.Email;
                entity.TechnicianPhone  = tech.User.PhoneNumber;
            }
            else if (tech.ExplicitUnassign)
            {
                // Admin chose "Unassigned" — clear all technician fields
                // together so we don't keep a stale name attached to a
                // null user id.
                entity.TechnicianUserId = null;
                entity.TechnicianName   = null;
                entity.TechnicianEmail  = null;
                entity.TechnicianPhone  = null;
            }
            else
            {
                // No technician change in this request. Preserve the
                // existing technician on the entity and only update
                // free-text fields the client sent (legacy path).
                entity.TechnicianName  = Trim(request.TechnicianName)  ?? entity.TechnicianName;
                entity.TechnicianPhone = Trim(request.TechnicianPhone) ?? entity.TechnicianPhone;
                entity.TechnicianEmail = Trim(request.TechnicianEmail) ?? entity.TechnicianEmail;
            }
            // Issue 8 (go-live) — partial-update semantics. Null/empty
            // request fields are treated as "keep existing"; only
            // explicitly-provided values overwrite. This keeps the PUT
            // safe for tech-only / notes-only / schedule-only callers
            // while still supporting the full address edit when the
            // caller passes the new fields.
            entity.AddressLine1 = request.AddressLine1.Trim();
            entity.AddressLine2          = Trim(request.AddressLine2)          ?? entity.AddressLine2;
            entity.Suburb                = Trim(request.Suburb)                ?? entity.Suburb;
            entity.City                  = Trim(request.City)                  ?? entity.City;
            entity.Province              = Trim(request.Province)              ?? entity.Province;
            entity.PostalCode            = Trim(request.PostalCode)            ?? entity.PostalCode;
            entity.Country               = Trim(request.Country)               ?? entity.Country;
            entity.Latitude              = request.Latitude                    ?? entity.Latitude;
            entity.Longitude             = request.Longitude                   ?? entity.Longitude;
            entity.GooglePlaceId         = Trim(request.GooglePlaceId)         ?? entity.GooglePlaceId;
            entity.MapProviderReference  = Trim(request.MapProviderReference)  ?? entity.MapProviderReference;
            entity.AdminNotes            = Trim(request.AdminNotes)            ?? entity.AdminNotes;
            entity.TechnicianNotes       = Trim(request.TechnicianNotes)       ?? entity.TechnicianNotes;

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<InstallationDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                "Installation updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating installation {Id}", id);
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the installation.");
        }
    }

    /// <summary>
    /// Narrow assign-technician endpoint. The general
    /// <see cref="AdminUpdateAsync"/> path runs the full installation
    /// validation (address fields, lat/lng) which breaks when a caller
    /// only wants to (re)assign a technician without restating the
    /// address. This method is the dedicated seam: validates the
    /// installation + technician only, persists the technician fields
    /// (and optional admin/office notes), and emits the same audit
    /// trail as a full update.
    /// </summary>
    public async Task<Result<InstallationDto>> AdminAssignTechnicianAsync(
        Guid id, Guid? technicianUserId, string? adminNotes = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Installation id is required.");

            var entity = await _dbContext.Installations
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
            if (entity is null)
                return Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Installation not found.");

            if (TerminalInstallationStatuses.Contains(entity.Status))
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.CONFLICT,
                    $"Installations in status '{entity.Status}' cannot be reassigned.");
            }

            var techLookup = await ResolveTechnicianAsync(technicianUserId, cancellationToken);
            if (!techLookup.IsSuccess)
                return Result<InstallationDto>.Failure(techLookup.Code ?? ErrorCodes.VALIDATION_ERROR, techLookup.Message);

            var tech = techLookup.Data!;
            var previousTechId = entity.TechnicianUserId;

            if (tech.User is not null)
            {
                entity.TechnicianUserId = tech.User.Id;
                entity.TechnicianName   = $"{tech.User.FirstName} {tech.User.LastName}".Trim();
                entity.TechnicianEmail  = tech.User.Email;
                entity.TechnicianPhone  = tech.User.PhoneNumber;
            }
            else if (tech.ExplicitUnassign)
            {
                entity.TechnicianUserId = null;
                entity.TechnicianName   = null;
                entity.TechnicianEmail  = null;
                entity.TechnicianPhone  = null;
            }
            else
            {
                // No change requested. Treat as a no-op success.
                return Result<InstallationDto>.Success(
                    MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                    "No technician change requested.");
            }

            if (!string.IsNullOrWhiteSpace(adminNotes))
                entity.AdminNotes = adminNotes.Trim();
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitInstallationAuditAsync(
                AuditActionType.InstallationStatusChanged,
                AuditActorType.Admin,
                entity,
                summary: tech.ExplicitUnassign
                    ? $"Technician unassigned from installation {entity.InstallationNumber}"
                    : $"Technician assigned to installation {entity.InstallationNumber}: {entity.TechnicianName}",
                metadata: BuildMetadata(new
                {
                    installationId   = entity.Id,
                    installationNumber = entity.InstallationNumber,
                    orderId          = entity.OrderId,
                    previousTechnicianUserId = previousTechId,
                    newTechnicianUserId      = entity.TechnicianUserId,
                    explicitUnassign         = tech.ExplicitUnassign,
                }));

            _logger.LogInformation(
                "[AssignTechnician] installation={InstallationNumber} order={OrderId} technician={TechnicianUserId} previousTechnician={PreviousTechnicianUserId}",
                entity.InstallationNumber, entity.OrderId, entity.TechnicianUserId, previousTechId);

            return Result<InstallationDto>.Success(
                MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity),
                tech.ExplicitUnassign ? "Technician unassigned." : "Technician assigned.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error assigning technician to installation {Id}", id);
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while assigning the technician.");
        }
    }

    public async Task<Result<InstallationDto>> AdminUpdateStatusAsync(Guid id, AdminUpdateInstallationStatusDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Installation id is required.");

            if (request is null)
                return Result<InstallationDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            if (request.ScheduledForUtc.HasValue
                && request.ScheduledForUtc.Value < DateTime.UtcNow.Date)
            {
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "ScheduledForUtc cannot be in the past.");
            }

            var entity = await _dbContext.Installations
                .Include(i => i.Order)
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

            if (entity is null)
                return Result<InstallationDto>.Failure(ErrorCodes.NOT_FOUND, "Installation not found.");

            var previous = entity.Status;
            var now = DateTime.UtcNow;

            entity.Status = request.Status;
            entity.LastStatusChangedByUserId = _currentUser.UserId;

            if (!string.IsNullOrWhiteSpace(request.AdminNotes))
                entity.AdminNotes = request.AdminNotes.Trim();

            if (!string.IsNullOrWhiteSpace(request.TechnicianNotes))
                entity.TechnicianNotes = request.TechnicianNotes.Trim();

            switch (request.Status)
            {
                case InstallationStatus.Scheduled:
                    if (request.ScheduledForUtc.HasValue)
                        entity.ScheduledForUtc = request.ScheduledForUtc;
                    break;

                case InstallationStatus.Rescheduled:
                    if (request.ScheduledForUtc.HasValue)
                    {
                        if (entity.ScheduledForUtc.HasValue)
                            entity.RescheduledFromUtc = entity.ScheduledForUtc;
                        entity.ScheduledForUtc = request.ScheduledForUtc;
                    }
                    break;

                case InstallationStatus.Completed:
                    if (entity.CompletedAtUtc is null) entity.CompletedAtUtc = now;
                    entity.CompletionNotes = Trim(request.CompletionNotes) ?? entity.CompletionNotes;
                    break;

                case InstallationStatus.Cancelled:
                    if (entity.CancelledAtUtc is null) entity.CancelledAtUtc = now;
                    entity.CancellationReason = Trim(request.CancellationReason) ?? entity.CancellationReason;
                    break;

                case InstallationStatus.Failed:
                    if (entity.FailedAtUtc is null) entity.FailedAtUtc = now;
                    entity.FailureReason = Trim(request.FailureReason) ?? entity.FailureReason;
                    break;
            }

            // Order status sync — capture previous Order state for audit
            OrderStatus? orderPrevStatus = null;
            OrderStatus? orderNewStatus = null;

            if (entity.Order is not null)
            {
                var order = entity.Order;

                // GO-LIVE ALIGNMENT (Openserve has no activation API):
                // Installation Completed does NOT mean the customer's
                // service is Active. The lifecycle is:
                //
                //   Installation.Completed
                //     → Order.PendingPayment (waiting for first monthly invoice to settle)
                //     → Order.PendingActivation (paid; admin must do Openserve manually)
                //     → Order.Active (admin "Activate Service" action)
                //
                // The PaymentApplierService flips PendingPayment → PendingActivation
                // when the first monthly invoice becomes Paid. Admin
                // alone moves PendingActivation → Active via the
                // /api/admin/orders/{id}/activate-service endpoint.
                //
                // We deliberately do NOT set Order.ActivatedAtUtc here —
                // that timestamp now marks the Openserve activation, not
                // the technician's install completion.
                if (request.Status == InstallationStatus.Completed
                    && !OrderActivationBlockers.Contains(order.Status))
                {
                    orderPrevStatus = order.Status;
                    order.Status = OrderStatus.PendingPayment;
                    order.LastStatusChangedByUserId = _currentUser.UserId;
                    orderNewStatus = order.Status;
                }
                else if (OrderProvisioningTriggers.Contains(request.Status)
                         && OrderProvisioningEligibleSources.Contains(order.Status))
                {
                    orderPrevStatus = order.Status;
                    order.Status = OrderStatus.Provisioning;
                    order.LastStatusChangedByUserId = _currentUser.UserId;
                    orderNewStatus = order.Status;
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (previous != entity.Status)
            {
                await EmitInstallationAuditAsync(
                    AuditActionType.InstallationStatusChanged,
                    AuditActorType.Admin,
                    entity,
                    summary: $"Installation status changed: {previous} -> {entity.Status} ({entity.InstallationNumber})",
                    metadata: BuildMetadata(new
                    {
                        previous,
                        newStatus = entity.Status,
                        orderId = entity.OrderId,
                        orderNumber = entity.Order?.OrderNumber
                    }));
            }

            if (orderPrevStatus.HasValue && orderNewStatus.HasValue
                && orderPrevStatus.Value != orderNewStatus.Value
                && entity.Order is not null)
            {
                await EmitOrderStatusChangedAuditAsync(
                    entity.Order,
                    orderPrevStatus.Value,
                    orderNewStatus.Value,
                    triggeredByInstallationNumber: entity.InstallationNumber);
            }

            if (previous != entity.Status
                && (entity.Status == InstallationStatus.Scheduled || entity.Status == InstallationStatus.Rescheduled)
                && entity.Order is not null)
            {
                var type = entity.Status == InstallationStatus.Scheduled
                    ? NotificationType.InstallationScheduled
                    : NotificationType.InstallationStatusChanged;
                await NotifyCustomerOfInstallationAsync(entity, entity.Order, type, cancellationToken);
            }

            InstallationCompletionBillingOutcomeDto? billingOutcome = null;
            if (previous != entity.Status
                && entity.Status == InstallationStatus.Completed
                && entity.Order is not null)
            {
                // Capture the lifecycle label BEFORE any side-effects so
                // the result modal can show the customer's previous
                // state ("Pending Installation → Pending Activation").
                var preCompletionOrderStatus = orderPrevStatus ?? entity.Order.Status;
                var preCompletionNetworkAccountStatus = await _dbContext.NetworkAccounts
                    .AsNoTracking()
                    .Where(na => na.OrderId == entity.OrderId)
                    .OrderByDescending(na => na.CreatedAtUtc)
                    .Select(na => (NetworkAccountStatus?)na.Status)
                    .FirstOrDefaultAsync(cancellationToken);
                var previousServiceStatus = NetworkAccountService.ResolveDisplayStatus(
                    preCompletionNetworkAccountStatus ?? NetworkAccountStatus.Pending,
                    preCompletionOrderStatus);

                await TryProvisionNetworkAccountAsync(entity.OrderId, entity.InstallationNumber, cancellationToken);
                // Recurring-billing kickoff: first monthly invoice is
                // raised the moment the installation goes Completed.
                // Up to this point the customer has only paid the
                // installation fee (see OrderService.PersistMockCheckoutAsync);
                // from here on Stage 1 of the subscription billing
                // cycle starts. Idempotent — won't duplicate if the
                // status is bounced back into a non-terminal state and
                // re-completed. Returns true when this run minted a
                // new invoice; false when it reused an existing one
                // (used by the admin result modal to disambiguate
                // first-time completion from re-runs).
                var mintedNewInvoice = await TryCreateFirstMonthlyInvoiceAsync(entity.Order, cancellationToken);
                // Phase 4 stub — attempt auto-debit of the first-month
                // invoice using a stored Paystack mandate. Hard-gated
                // by AutoBilling__Enabled + AutoBilling__ChargeAuthorizationEnabled
                // — both default to false, so this is a no-op today.
                // Wires Phase 5 (retry job) once the flags flip on.
                await TryAutoChargeFirstMonthlyInvoiceAsync(entity.Order, cancellationToken);
                // Per-action admin opt-in: if the completion modal had
                // "Activate service automatically if payment succeeds"
                // ticked, promote PendingActivation → Active so we
                // don't force the admin into a second click on the
                // service-detail page. Honors auto-billing result and
                // skips silently if the order isn't ready.
                var activateRequested = request.ActivateServiceIfPaymentSucceeds == true;
                await TryActivateServiceOnAdminOptInAsync(entity.Order, activateRequested, cancellationToken);
                // Harvest the billing outcome from DB state so the admin
                // portal gets one structured payload describing what
                // happened to the first monthly invoice + auto-debit
                // attempt + resulting service status. This is read-only
                // — it never mutates anything the prior helpers did.
                billingOutcome = await BuildBillingOutcomeAsync(
                    entity.OrderId,
                    activateRequested,
                    usedExistingInvoice: !mintedNewInvoice,
                    previousServiceStatus: previousServiceStatus,
                    cancellationToken);
            }

            var responseDto = MapToDto(await ReloadWithIncludesAsync(entity.Id, cancellationToken) ?? entity);
            responseDto.BillingOutcome = billingOutcome;
            responseDto.ServiceId      = billingOutcome?.ServiceId
                ?? await ResolveLinkedServiceIdAsync(entity.OrderId, cancellationToken);
            return Result<InstallationDto>.Success(responseDto, "Installation status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating installation status {Id}", id);
            return Result<InstallationDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the installation status.");
        }
    }

    private IQueryable<Installation> BuildQuery(InstallationFilterRequestDto filter, Guid? restrictToUserId)
    {
        var query = _dbContext.Installations
            .AsNoTracking()
            .Include(i => i.Order)
            .Include(i => i.LastStatusChangedByUser)
            .AsQueryable();

        if (restrictToUserId.HasValue)
            query = query.Where(i => i.Order!.UserId == restrictToUserId.Value);

        if (filter.OrderId.HasValue)
            query = query.Where(i => i.OrderId == filter.OrderId.Value);

        if (!string.IsNullOrWhiteSpace(filter.OrderNumber))
        {
            var n = filter.OrderNumber.Trim();
            query = query.Where(i => i.Order != null && EF.Functions.Like(i.Order.OrderNumber, $"%{n}%"));
        }

        if (filter.StatusFilter.HasValue)
            query = query.Where(i => i.Status == filter.StatusFilter.Value);

        if (filter.Source.HasValue)
            query = query.Where(i => i.Source == filter.Source.Value);

        if (filter.TechnicianUserId.HasValue)
            query = query.Where(i => i.TechnicianUserId == filter.TechnicianUserId.Value);

        if (!string.IsNullOrWhiteSpace(filter.TechnicianName))
        {
            var v = filter.TechnicianName.Trim();
            query = query.Where(i => i.TechnicianName != null && EF.Functions.Like(i.TechnicianName, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var v = filter.City.Trim();
            query = query.Where(i => i.City != null && EF.Functions.Like(i.City, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Suburb))
        {
            var v = filter.Suburb.Trim();
            query = query.Where(i => i.Suburb != null && EF.Functions.Like(i.Suburb, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var v = filter.Province.Trim();
            query = query.Where(i => i.Province != null && EF.Functions.Like(i.Province, $"%{v}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.PostalCode))
        {
            var v = filter.PostalCode.Trim();
            query = query.Where(i => i.PostalCode != null && EF.Functions.Like(i.PostalCode, $"%{v}%"));
        }

        if (filter.FromUtc.HasValue)
            query = query.Where(i => i.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(i => i.CreatedAtUtc <= filter.ToUtc.Value);

        if (filter.ScheduledFromUtc.HasValue)
            query = query.Where(i => i.ScheduledForUtc != null && i.ScheduledForUtc >= filter.ScheduledFromUtc.Value);

        if (filter.ScheduledToUtc.HasValue)
            query = query.Where(i => i.ScheduledForUtc != null && i.ScheduledForUtc <= filter.ScheduledToUtc.Value);

        if (filter.CompletedFromUtc.HasValue)
            query = query.Where(i => i.CompletedAtUtc != null && i.CompletedAtUtc >= filter.CompletedFromUtc.Value);

        if (filter.CompletedToUtc.HasValue)
            query = query.Where(i => i.CompletedAtUtc != null && i.CompletedAtUtc <= filter.CompletedToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(i =>
                EF.Functions.Like(i.InstallationNumber, $"%{s}%") ||
                EF.Functions.Like(i.AddressLine1, $"%{s}%") ||
                (i.Order != null && EF.Functions.Like(i.Order.OrderNumber, $"%{s}%")) ||
                (i.TechnicianName != null && EF.Functions.Like(i.TechnicianName, $"%{s}%")) ||
                (i.City != null && EF.Functions.Like(i.City, $"%{s}%")) ||
                (i.Suburb != null && EF.Functions.Like(i.Suburb, $"%{s}%")) ||
                (i.PostalCode != null && EF.Functions.Like(i.PostalCode, $"%{s}%")));
        }

        return query;
    }

    private static async Task<Result<PagedResult<InstallationDto>>> ToPagedResultAsync(IQueryable<Installation> query, InstallationFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(i => new InstallationDto
            {
                Id = i.Id,
                InstallationNumber = i.InstallationNumber,
                OrderId = i.OrderId,
                OrderNumber = i.Order != null ? i.Order.OrderNumber : null,
                OrderStatus = i.Order != null ? i.Order.Status : (OrderStatus?)null,
                CustomerUserId = i.Order != null ? i.Order.UserId : (Guid?)null,
                CustomerFullName = i.Order != null ? i.Order.FullName : null,
                CustomerEmail = i.Order != null ? i.Order.Email : null,
                CustomerPhoneNumber = i.Order != null ? i.Order.PhoneNumber : null,
                PackageName = i.Order != null ? i.Order.PackageName : null,
                PackageType = i.Order != null ? i.Order.PackageType : (ServicePackageType?)null,
                PackageSpeedLabel = i.Order != null ? i.Order.PackageSpeedLabel : null,
                PackagePrice = i.Order != null ? i.Order.PackagePrice : (decimal?)null,
                Status = i.Status,
                Source = i.Source,
                ScheduledForUtc = i.ScheduledForUtc,
                RescheduledFromUtc = i.RescheduledFromUtc,
                CompletedAtUtc = i.CompletedAtUtc,
                CancelledAtUtc = i.CancelledAtUtc,
                FailedAtUtc = i.FailedAtUtc,
                TechnicianName = i.TechnicianName,
                TechnicianPhone = i.TechnicianPhone,
                TechnicianEmail = i.TechnicianEmail,
                TechnicianUserId = i.TechnicianUserId,
                AddressLine1 = i.AddressLine1,
                AddressLine2 = i.AddressLine2,
                Suburb = i.Suburb,
                City = i.City,
                Province = i.Province,
                PostalCode = i.PostalCode,
                Country = i.Country,
                Latitude = i.Latitude,
                Longitude = i.Longitude,
                GooglePlaceId = i.GooglePlaceId,
                CustomerNotes = i.CustomerNotes,
                AdminNotes = i.AdminNotes,
                TechnicianNotes = i.TechnicianNotes,
                CompletionNotes = i.CompletionNotes,
                FailureReason = i.FailureReason,
                CancellationReason = i.CancellationReason,
                RouterMakeModel = i.RouterMakeModel,
                RouterSerialNumber = i.RouterSerialNumber,
                RouterMacAddress = i.RouterMacAddress,
                OntReference = i.OntReference,
                InstalledLocationNotes = i.InstalledLocationNotes,
                SpeedTestResult = i.SpeedTestResult,
                CustomerSignOffName = i.CustomerSignOffName,
                LastStatusChangedByUserId = i.LastStatusChangedByUserId,
                LastStatusChangedByUserEmail = i.LastStatusChangedByUser != null
                    ? i.LastStatusChangedByUser.Email
                    : null,
                CreatedAtUtc = i.CreatedAtUtc,
                UpdatedAtUtc = i.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<InstallationDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<InstallationDto>>.Success(paged);
    }

    private async Task<Installation?> ReloadWithIncludesAsync(Guid id, CancellationToken cancellationToken)
        => await _dbContext.Installations
            .AsNoTracking()
            .Include(i => i.Order)
            .Include(i => i.LastStatusChangedByUser)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    private static Result<InstallationDto>? ValidateTechnicianContact(string? email, string? phone)
    {
        if (!string.IsNullOrWhiteSpace(email) && !EmailRegex.IsMatch(email.Trim()))
            return Result<InstallationDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Technician email is not in a valid format.");

        if (!string.IsNullOrWhiteSpace(phone))
        {
            var p = phone.Trim();
            if (p.Length < 6 || p.Length > 50)
                return Result<InstallationDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, "Technician phone must be between 6 and 50 characters.");
        }

        return null;
    }

    private async Task<string?> GenerateUniqueInstallationNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var datePart = now.ToString("yyyyMMdd");

        for (var attempt = 0; attempt < InstallationNumberMaxAttempts; attempt++)
        {
            var candidate = $"INS-{datePart}-{GenerateRandomSuffix(InstallationNumberSuffixLength)}";
            var exists = await _dbContext.Installations
                .AnyAsync(i => i.InstallationNumber == candidate, cancellationToken);

            if (!exists) return candidate;
        }

        return null;
    }

    private static string GenerateRandomSuffix(int length)
    {
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);

        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = InstallationNumberAlphabet[buffer[i] % InstallationNumberAlphabet.Length];

        return new string(chars);
    }

    private async Task EmitInstallationAuditAsync(AuditActionType actionType, AuditActorType actorType, Installation entity, string summary, string? metadata)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = actorType,
            ActionType = actionType,
            EntityType = AuditEntityType.Installation,
            EntityId = entity.Id,
            EntityName = entity.InstallationNumber,
            Summary = summary,
            MetadataJson = metadata,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
    }

    private async Task EmitOrderStatusChangedAuditAsync(Order order, OrderStatus previous, OrderStatus newStatus, string? triggeredByInstallationNumber)
    {
        var summary = triggeredByInstallationNumber is null
            ? $"Order status changed: {previous} -> {newStatus} ({order.OrderNumber})"
            : $"Order status changed by installation: {previous} -> {newStatus} ({order.OrderNumber})";
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.System,
            ActionType = AuditActionType.OrderStatusChanged,
            EntityType = AuditEntityType.Order,
            EntityId = order.Id,
            EntityName = order.OrderNumber,
            Summary = summary,
            MetadataJson = BuildMetadata(new
            {
                previous,
                newStatus,
                triggeredBy = triggeredByInstallationNumber is null ? "InstallationAdminOptIn" : "Installation",
                installationNumber = triggeredByInstallationNumber
            }),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
            IsSuccess = true
        });
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

    private async Task NotifyCustomerOfInstallationAsync(Installation entity, Order order, NotificationType type, CancellationToken cancellationToken)
    {
        try
        {
            var contact = await _dbContext.Orders
                .Where(o => o.Id == order.Id)
                .Select(o => new
                {
                    o.UserId,
                    Email = o.Email ?? (o.User != null ? o.User.Email : null),
                    Phone = o.PhoneNumber ?? (o.User != null ? o.User.PhoneNumber : null)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (contact is null || string.IsNullOrWhiteSpace(contact.Email))
                return;

            var subject = type == NotificationType.InstallationScheduled
                ? $"Installation scheduled: {entity.InstallationNumber}"
                : $"Installation update: {entity.InstallationNumber}";

            var bodyParts = new List<string>
            {
                $"Your Smart Future installation has an update.",
                $"Installation number: {entity.InstallationNumber}",
                $"Order: {order.OrderNumber}",
                $"Status: {entity.Status}"
            };

            if (entity.ScheduledForUtc.HasValue)
                bodyParts.Add($"Scheduled for: {entity.ScheduledForUtc.Value:yyyy-MM-dd HH:mm} UTC");

            if (!string.IsNullOrWhiteSpace(entity.TechnicianName))
                bodyParts.Add($"Technician: {entity.TechnicianName}");

            await _notificationService.SendAsync(new SendNotificationRequestDto
            {
                UserId = contact.UserId,
                Channel = NotificationChannel.Email,
                Type = type,
                RecipientEmail = contact.Email,
                RecipientPhone = contact.Phone,
                Subject = subject,
                Body = string.Join("\n", bodyParts),
                RelatedEntityType = nameof(Installation),
                RelatedEntityId = entity.Id
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Type} notification for installation {Id}", type, entity.Id);
        }
    }

    // Lifecycle (go-live fix): an installation transitioning to
    // Completed must NOT activate the NetworkAccount. Activation depends
    // on the first monthly-service invoice being paid (and on the
    // ServiceActivation:RequireManualOpenserveActivation flag deciding
    // whether the admin must finalize on Openserve). All this hook does
    // is ENSURE a Pending NetworkAccount exists for the order so the
    // service tile shows the right state ("Pending Payment" via
    // ResolveDisplayStatus). The Pending → Active transition fires in
    // PaymentApplierService once the monthly invoice clears.
    private async Task TryProvisionNetworkAccountAsync(Guid orderId, string installationNumber, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _networkAccountService.EnsurePendingForOrderAsync(
                orderId, NetworkAccountSource.SystemAutomated, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "[InstallationCompleted] EnsurePending NetworkAccount hook for installation {InstallationNumber} returned non-success: {Code} {Message}",
                    installationNumber, result.Code, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[InstallationCompleted] EnsurePending NetworkAccount hook for installation {InstallationNumber} threw",
                installationNumber);
        }
    }

    // ------------------------------------------------------------------
    // Admin opt-in service activation
    // ------------------------------------------------------------------
    // Promote PendingActivation → Active when the completion modal had
    // "Activate service automatically if payment succeeds" ticked AND
    // the auto-debit landed. Read-only on the invoice/payment state —
    // only the order + linked NetworkAccount move. Failures never
    // block the installation status transition.
    private async Task TryActivateServiceOnAdminOptInAsync(Order order, bool activateRequested, CancellationToken cancellationToken)
    {
        if (!activateRequested) return;

        try
        {
            var tracked = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.Id == order.Id, cancellationToken);
            if (tracked is null) return;

            // Only PendingActivation is a candidate — if the order is
            // still PendingPayment, the auto-debit hasn't cleared yet
            // (we never want to mark a service Active without a paid
            // invoice). If it's already Active, the config-driven path
            // beat us to it.
            if (tracked.Status != OrderStatus.PendingActivation)
            {
                _logger.LogInformation(
                    "[InstallationCompleteBilling] admin opt-in activation skipped for order {OrderNumber} — current status {Status} is not PendingActivation.",
                    tracked.OrderNumber, tracked.Status);
                return;
            }

            var now = DateTime.UtcNow;
            var previousStatus = tracked.Status;
            tracked.Status = OrderStatus.Active;
            if (tracked.ActivatedAtUtc is null) tracked.ActivatedAtUtc = now;
            if (tracked.BillingAnchorDateUtc is null) tracked.BillingAnchorDateUtc = now;
            if (tracked.NextPayDateUtc is null) tracked.NextPayDateUtc = now.AddDays(30);
            tracked.LastStatusChangedByUserId = _currentUser.UserId;

            await _dbContext.SaveChangesAsync(cancellationToken);

            // Best-effort: flip the NetworkAccount to Active so the
            // service tile matches the new order state. Wrapped in its
            // own try so a provisioner exception doesn't roll back the
            // order activation we just committed.
            try
            {
                var provResult = await _networkAccountService.ProvisionForOrderAsync(
                    tracked.Id, NetworkAccountSource.SystemAutomated, cancellationToken);
                if (!provResult.IsSuccess)
                {
                    _logger.LogWarning(
                        "[InstallationCompleteBilling] admin opt-in activation: ProvisionForOrderAsync returned non-success for order {OrderNumber}: {Code} {Message}",
                        tracked.OrderNumber, provResult.Code, provResult.Message);
                }
            }
            catch (Exception provEx)
            {
                _logger.LogError(provEx,
                    "[InstallationCompleteBilling] admin opt-in activation: ProvisionForOrderAsync threw for order {OrderNumber} — service status committed, NetworkAccount flip skipped.",
                    tracked.OrderNumber);
            }

            await EmitOrderStatusChangedAuditAsync(
                tracked,
                previousStatus,
                tracked.Status,
                triggeredByInstallationNumber: null);

            _logger.LogInformation(
                "[InstallationCompleteBilling] admin opt-in activation: order {OrderNumber} PendingActivation → Active, NextPayDateUtc={NextPay}",
                tracked.OrderNumber, tracked.NextPayDateUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[InstallationCompleteBilling] admin opt-in activation hook threw for order {OrderNumber} — completion flow continues unaffected.",
                order.OrderNumber);
        }
    }

    // ------------------------------------------------------------------
    // Billing-outcome harvester (go-live Issue 7/8/9)
    // ------------------------------------------------------------------
    // After the first-monthly-invoice + auto-charge helpers run, read
    // the resulting state from the database so the admin portal can
    // render a single deterministic modal/toast describing what
    // happened. Read-only — no state changes.
    private async Task<InstallationCompletionBillingOutcomeDto> BuildBillingOutcomeAsync(
        Guid orderId,
        bool activateRequestedByAdmin,
        bool usedExistingInvoice,
        string? previousServiceStatus,
        CancellationToken cancellationToken)
    {
        var outcome = new InstallationCompletionBillingOutcomeDto
        {
            ActivateRequestedByAdmin = activateRequestedByAdmin,
            UsedExistingInvoice      = usedExistingInvoice,
            PreviousServiceStatus    = previousServiceStatus
        };

        // Find the most-recent service-package (monthly) invoice for
        // the order. There's exactly one until the recurring-billing
        // job catches up; ordering by CreatedAtUtc DESC tolerates the
        // future case where there are several.
        var invoiceRow = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.OrderId == orderId
                     && i.LineItems.Any(li => li.LineType == InvoiceLineItemType.ServicePackage))
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new { i.Id, i.InvoiceNumber, i.TotalAmount, i.Status, i.PaidAtUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (invoiceRow is not null)
        {
            // MonthlyInvoiceCreated means "there IS a service-package
            // invoice for this order" — flipped on whether or not this
            // run minted it. usedExistingInvoice (above) disambiguates
            // re-runs from first-time creations for the admin modal.
            outcome.MonthlyInvoiceCreated = true;
            outcome.InvoiceId             = invoiceRow.Id;
            outcome.InvoiceNumber         = invoiceRow.InvoiceNumber;
            outcome.InvoiceAmount         = invoiceRow.TotalAmount;
            outcome.InvoiceStatus         = invoiceRow.Status.ToString();
            outcome.PaidAtUtc             = invoiceRow.PaidAtUtc;
        }

        // Auto-billing attempt visibility comes from the most-recent
        // Payment row against this invoice. A Pending/Failed/Completed
        // row indicates an attempt was made; the override-applied
        // flag tells us the UAT R5 charge fired against a real invoice.
        if (invoiceRow is not null)
        {
            var lastPayment = await _dbContext.Payments
                .AsNoTracking()
                .Where(p => p.InvoiceId == invoiceRow.Id)
                .OrderByDescending(p => p.CreatedAtUtc)
                .Select(p => new {
                    p.Status,
                    p.Amount,
                    p.FailureReason,
                    p.IsTestAmountOverrideApplied,
                    p.ActualProviderAmount,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (lastPayment is not null)
            {
                outcome.AutoBillingAttempted = true;
                outcome.AutoBillingSucceeded = lastPayment.Status == PaymentStatus.Completed
                                            && invoiceRow.Status == InvoiceStatus.Paid;
                outcome.ProviderAmount       = lastPayment.IsTestAmountOverrideApplied
                                                ? (lastPayment.ActualProviderAmount ?? lastPayment.Amount)
                                                : lastPayment.Amount;
                if (!outcome.AutoBillingSucceeded && !string.IsNullOrWhiteSpace(lastPayment.FailureReason))
                    outcome.FailureReason = lastPayment.FailureReason;
            }
        }

        // Resulting service status + next-pay date: re-read the order so
        // PaymentApplierService's commit (PendingPayment → Active /
        // PendingActivation depending on the ServiceActivation flag) and
        // any admin opt-in activation that just ran are both reflected.
        var orderSnapshot = await _dbContext.Orders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new { o.Status, o.NextPayDateUtc, o.ActivatedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);

        var orderStatus = orderSnapshot?.Status ?? default;
        outcome.ResultingServiceStatus = orderStatus switch
        {
            OrderStatus.Active            => "Active",
            OrderStatus.PendingActivation => "Pending Activation",
            OrderStatus.PendingPayment    => "Pending Payment",
            _                             => orderStatus.ToString(),
        };
        outcome.ServiceActivated = orderStatus == OrderStatus.Active;
        outcome.NextPayDateUtc   = outcome.ServiceActivated ? orderSnapshot?.NextPayDateUtc : null;

        // Linked NetworkAccount id so the portal can deep-link straight
        // into the service-detail page from the completion modal.
        outcome.ServiceId = await _dbContext.NetworkAccounts
            .AsNoTracking()
            .Where(na => na.OrderId == orderId)
            .OrderByDescending(na => na.CreatedAtUtc)
            .Select(na => (Guid?)na.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // Single admin-friendly headline. Falls through cases so the
        // most informative message wins.
        if (outcome.AutoBillingSucceeded && outcome.ServiceActivated)
            outcome.Message = "Installation completed and service payment was successful. Service is now Active.";
        else if (outcome.AutoBillingSucceeded)
            outcome.Message = "Installation completed and service payment was successful. Service is pending activation.";
        else if (outcome.AutoBillingAttempted)
            outcome.Message = "Installation completed, but auto-debit failed. Service is still Pending Payment — the customer can pay the monthly invoice manually.";
        else if (outcome.MonthlyInvoiceCreated)
            outcome.Message = "Installation completed. No automatic payment method was available — the monthly invoice was issued to the customer.";
        else
            outcome.Message = "Installation completed.";

        // [InstallationCompleteBilling] — structured log so a copy/paste
        // of the line is enough to diagnose any post-completion
        // question. Includes every field the admin result modal
        // renders, plus the disambiguators (usedExistingInvoice,
        // invoiceStatusBefore→after, skippedReason) called out in the
        // money-safety brief.
        _logger.LogInformation(
            "[InstallationCompleteBilling] orderId={OrderId} serviceId={ServiceId} invoiceId={InvoiceId} invoiceNumber={InvoiceNumber} invoiceAmount={InvoiceAmount} invoiceStatus={InvoiceStatus} paidAtUtc={PaidAtUtc} usedExistingInvoice={UsedExistingInvoice} autoBillingAttempted={Attempted} autoBillingSucceeded={Succeeded} providerAmount={ProviderAmount} failureReason='{Reason}' autoBillingSkippedReason='{SkippedReason}' activateRequestedByAdmin={ActivateRequested} previousServiceStatus={Prev} serviceActivated={ServiceActivated} resultingServiceStatus={Status} nextPayDateUtc={NextPay}",
            orderId, outcome.ServiceId, outcome.InvoiceId, outcome.InvoiceNumber, outcome.InvoiceAmount,
            outcome.InvoiceStatus ?? "(none)",
            outcome.PaidAtUtc?.ToString("o") ?? "(none)",
            outcome.UsedExistingInvoice,
            outcome.AutoBillingAttempted, outcome.AutoBillingSucceeded, outcome.ProviderAmount,
            outcome.FailureReason ?? "(none)",
            outcome.AutoBillingSkippedReason ?? "(none)",
            outcome.ActivateRequestedByAdmin,
            outcome.PreviousServiceStatus ?? "(none)",
            outcome.ServiceActivated, outcome.ResultingServiceStatus,
            outcome.NextPayDateUtc?.ToString("o") ?? "(none)");

        return outcome;
    }

    // ------------------------------------------------------------------
    // First monthly invoice — auto-raised on Installation.Completed
    // ------------------------------------------------------------------
    // Business rule: the customer pays only the installation fee at
    // order time (see OrderService.PersistMockCheckoutAsync). The
    // first recurring monthly subscription invoice is raised when the
    // installation transitions to Completed. Subsequent monthly bills
    // are produced by the recurring-billing job; this hook only
    // bootstraps cycle 1.
    //
    // Idempotency: skips when any ServicePackage line item already
    // exists for the order, which covers the case where the admin
    // bounces the installation status back to "In progress" and then
    // re-completes it.
    //
    // Failure mode: wrapped in try/catch and never blocks the parent
    // status update. The status-change response still returns success;
    // the missing invoice surfaces via the warning log + audit log
    // search and is fixable with a manual InvoiceService.CreateAsync
    // from the admin portal.
    private const string FirstMonthlyInvoiceNumberPrefix = "INV";
    private const int FirstMonthlyInvoiceNumberMaxAttempts = 5;

    // Phase 4 — first-month auto-charge hook. Runs immediately after
    // TryCreateFirstMonthlyInvoiceAsync. Hard-gated by THREE flags:
    //   - AutoBilling.Enabled                       (master kill)
    //   - AutoBilling.ChargeAuthorizationEnabled    (Paystack call gate)
    //   - AutoBilling.InstallationCompletionAutoChargeEnabled (this hook's gate)
    // Customer-level opt-out (CustomerProfile.AutoBillingEnabled) is
    // enforced inside IAutoBillingService — keeps the rule in one place
    // for the future retry job + monthly job too.
    //
    // Failures here NEVER block the installation status transition —
    // the invoice simply stays Issued and the customer pays manually.
    private async Task TryAutoChargeFirstMonthlyInvoiceAsync(Order order, CancellationToken cancellationToken)
    {
        var settings = _autoBillingSettings.Value;
        if (!settings.Enabled)
        {
            _logger.LogInformation(
                "[AutoBillingHook] order {OrderNumber} — AutoBilling.Enabled=false; skipping first-month auto-charge.",
                order.OrderNumber);
            return;
        }
        if (!settings.ChargeAuthorizationEnabled)
        {
            _logger.LogInformation(
                "[AutoBillingHook] order {OrderNumber} — AutoBilling.ChargeAuthorizationEnabled=false; first-month invoice left unpaid for manual flow.",
                order.OrderNumber);
            return;
        }
        if (!settings.InstallationCompletionAutoChargeEnabled)
        {
            _logger.LogInformation(
                "[AutoBillingHook] order {OrderNumber} — AutoBilling.InstallationCompletionAutoChargeEnabled=false; first-month invoice left unpaid for manual flow.",
                order.OrderNumber);
            return;
        }

        // The first-monthly invoice was just created by the prior step;
        // pick the most recent Issued invoice for this order with a
        // ServicePackage line. Idempotency: if a Pending Payment row
        // already exists, IAutoBillingService still creates a NEW
        // PaymentInitiation per attempt — that's the contract the
        // retry-job (Phase 5) will rely on, so this hook does too.
        var firstMonthlyInvoice = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.OrderId == order.Id
                     && i.Status == InvoiceStatus.Issued
                     && i.LineItems.Any(li => li.LineType == InvoiceLineItemType.ServicePackage))
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new { i.Id, i.InvoiceNumber })
            .FirstOrDefaultAsync(cancellationToken);

        if (firstMonthlyInvoice is null)
        {
            _logger.LogInformation(
                "[AutoBillingHook] order {OrderNumber} — no Issued ServicePackage invoice found; nothing to charge.",
                order.OrderNumber);
            return;
        }

        try
        {
            var result = await _autoBilling.ChargeInvoiceAsync(
                firstMonthlyInvoice.Id, AutoBillingChargeSource.InstallationCompletion, cancellationToken);

            if (result.IsSuccess && result.Data?.Charged == true)
            {
                _logger.LogInformation(
                    "[AutoBillingHook] order {OrderNumber} invoice {InvoiceNumber} auto-charged via Paystack (ref={Reference}).",
                    order.OrderNumber, firstMonthlyInvoice.InvoiceNumber, result.Data.Reference);
            }
            else
            {
                _logger.LogInformation(
                    "[AutoBillingHook] order {OrderNumber} invoice {InvoiceNumber} not auto-charged: {Reason}",
                    order.OrderNumber, firstMonthlyInvoice.InvoiceNumber,
                    result.Data?.FailureReason ?? result.Message ?? "(no reason)");
            }
        }
        catch (Exception ex)
        {
            // Never let the auto-charge attempt take down the install
            // completion. Customer can still pay the invoice manually.
            _logger.LogError(ex,
                "[AutoBillingHook] auto-charge threw for order {OrderNumber} invoice {InvoiceNumber} — install transition continues unaffected.",
                order.OrderNumber, firstMonthlyInvoice.InvoiceNumber);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when a brand-new invoice was minted by this
    /// call, <c>false</c> when the idempotency check found an existing
    /// ServicePackage invoice and the hook reused it. The caller
    /// surfaces this as <c>UsedExistingInvoice</c> in the admin result
    /// modal so admins can tell a fresh completion from a re-run.
    /// </summary>
    private async Task<bool> TryCreateFirstMonthlyInvoiceAsync(Order order, CancellationToken cancellationToken)
    {
        try
        {
            if (order.PackagePrice <= 0m)
            {
                _logger.LogInformation(
                    "First-monthly-invoice hook: order {OrderNumber} has PackagePrice 0; skipping.",
                    order.OrderNumber);
                return false;
            }

            // Idempotency check — see comment above.
            var alreadyBilled = await _dbContext.InvoiceLineItems
                .AnyAsync(li => li.Invoice!.OrderId == order.Id
                                && li.LineType == InvoiceLineItemType.ServicePackage,
                          cancellationToken);
            if (alreadyBilled)
            {
                _logger.LogInformation(
                    "First-monthly-invoice hook: order {OrderNumber} already has a ServicePackage invoice line; reusing it.",
                    order.OrderNumber);
                return false;
            }

            var now = DateTime.UtcNow;

            // Reuse BillingNumberGenerator (internal to Application
            // assembly) so the auto-generated invoice numbers share the
            // INV-YYYYMMDD-XXXXXX format with admin-created invoices.
            string? invoiceNumber = null;
            for (var attempt = 0; attempt < FirstMonthlyInvoiceNumberMaxAttempts; attempt++)
            {
                var candidate = BillingNumberGenerator.BuildCandidate(FirstMonthlyInvoiceNumberPrefix, now);
                var exists = await _dbContext.Invoices.AnyAsync(i => i.InvoiceNumber == candidate, cancellationToken);
                if (!exists) { invoiceNumber = candidate; break; }
            }
            if (invoiceNumber is null)
            {
                _logger.LogWarning(
                    "First-monthly-invoice hook: could not allocate a unique invoice number for order {OrderNumber}. Aborting hook; admin can raise manually.",
                    order.OrderNumber);
                return false;
            }

            var description = string.IsNullOrWhiteSpace(order.PackageName)
                ? "Service package — first month"
                : $"{order.PackageName} — first month";

            var invoice = new Invoice
            {
                InvoiceNumber = invoiceNumber,
                OrderId = order.Id,
                Status = InvoiceStatus.Issued,
                SubtotalAmount = order.PackagePrice,
                TaxAmount = 0m,
                TotalAmount = order.PackagePrice,
                AmountPaid = 0m,
                BalanceDue = order.PackagePrice,
                CurrencyCode = "ZAR",
                IssuedAtUtc = now,
                // 7-day default window for the first subscription
                // invoice. Recurring-billing config can override later;
                // for now this matches the rest of the customer-facing
                // billing default.
                DueAtUtc = now.AddDays(7),
                Notes = "First monthly subscription invoice — auto-raised when installation was completed.",
                LastStatusChangedByUserId = _currentUser.UserId
            };
            _dbContext.Invoices.Add(invoice);

            _dbContext.InvoiceLineItems.Add(new InvoiceLineItem
            {
                Invoice = invoice,
                LineType = InvoiceLineItemType.ServicePackage,
                Description = description,
                Quantity = 1,
                UnitAmount = order.PackagePrice,
                TotalAmount = order.PackagePrice,
                SortOrder = 0
            });

            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(new CreateAuditLogRequestDto
            {
                ActorUserId = _currentUser.UserId,
                ActorType = AuditActorType.System,
                ActionType = AuditActionType.InvoiceCreated,
                EntityType = AuditEntityType.Invoice,
                EntityId = invoice.Id,
                EntityName = invoice.InvoiceNumber,
                Summary = $"Auto first-monthly invoice {invoice.InvoiceNumber} raised for order {order.OrderNumber} on installation completion ({order.PackagePrice:0.00} ZAR).",
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                IsSuccess = true,
                MetadataJson = $"{{\"orderId\":\"{order.Id}\",\"orderNumber\":\"{order.OrderNumber}\",\"trigger\":\"installation_completed\",\"amount\":{order.PackagePrice}}}"
            });

            _logger.LogInformation(
                "First-monthly-invoice raised for order {OrderNumber}: invoice {InvoiceNumber} ({Amount} ZAR).",
                order.OrderNumber, invoice.InvoiceNumber, order.PackagePrice);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "First-monthly-invoice hook threw for order {OrderNumber}. Status change still succeeded; admin can raise manually.",
                order.OrderNumber);
            return false;
        }
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Phase 40 — resolve the technician selected from the admin dropdown.
    // Three valid input shapes:
    //   null           → no technician change in this request
    //   Guid.Empty     → explicit "Unassigned" (clear technician)
    //   real Guid      → look up user, validate role + status
    // Validation failures bubble back as Result.Failure so the controller
    // can return a 400 with the same error message admins see for any
    // other validation problem.
    private async Task<Result<TechnicianLookup>> ResolveTechnicianAsync(Guid? technicianUserId, CancellationToken cancellationToken)
    {
        if (!technicianUserId.HasValue)
            return Result<TechnicianLookup>.Success(new TechnicianLookup(null, ExplicitUnassign: false));

        if (technicianUserId.Value == Guid.Empty)
            return Result<TechnicianLookup>.Success(new TechnicianLookup(null, ExplicitUnassign: true));

        var user = await _userManager.FindByIdAsync(technicianUserId.Value.ToString());
        if (user is null)
            return Result<TechnicianLookup>.Failure(ErrorCodes.VALIDATION_ERROR, "Selected technician was not found.");

        if (!user.IsActive
            || user.AccountStatus == UserAccountStatus.Suspended
            || user.AccountStatus == UserAccountStatus.Inactive)
        {
            return Result<TechnicianLookup>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Selected technician is not an active user.");
        }

        var roles = await _userManager.GetRolesAsync(user);
        if (!roles.Contains(SystemRoles.Technician, StringComparer.OrdinalIgnoreCase))
        {
            return Result<TechnicianLookup>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Selected user does not have the Technician role.");
        }

        // No-op suppression: cancellation token only matters if we add
        // a longer-running side effect here later.
        cancellationToken.ThrowIfCancellationRequested();

        return Result<TechnicianLookup>.Success(new TechnicianLookup(user, ExplicitUnassign: false));
    }

    private sealed record TechnicianLookup(User? User, bool ExplicitUnassign);

    private static InstallationDto MapToDto(Installation i) => new()
    {
        Id = i.Id,
        InstallationNumber = i.InstallationNumber,
        OrderId = i.OrderId,
        OrderNumber = i.Order?.OrderNumber,
        OrderStatus = i.Order?.Status,
        CustomerUserId = i.Order?.UserId,
        CustomerFullName = i.Order?.FullName,
        CustomerEmail = i.Order?.Email,
        CustomerPhoneNumber = i.Order?.PhoneNumber,
        PackageName = i.Order?.PackageName,
        PackageType = i.Order?.PackageType,
        PackageSpeedLabel = i.Order?.PackageSpeedLabel,
        PackagePrice = i.Order?.PackagePrice,
        Status = i.Status,
        Source = i.Source,
        ScheduledForUtc = i.ScheduledForUtc,
        RescheduledFromUtc = i.RescheduledFromUtc,
        CompletedAtUtc = i.CompletedAtUtc,
        CancelledAtUtc = i.CancelledAtUtc,
        FailedAtUtc = i.FailedAtUtc,
        TechnicianName = i.TechnicianName,
        TechnicianPhone = i.TechnicianPhone,
        TechnicianEmail = i.TechnicianEmail,
        TechnicianUserId = i.TechnicianUserId,
        AddressLine1 = i.AddressLine1,
        AddressLine2 = i.AddressLine2,
        Suburb = i.Suburb,
        City = i.City,
        Province = i.Province,
        PostalCode = i.PostalCode,
        Country = i.Country,
        Latitude = i.Latitude,
        Longitude = i.Longitude,
        GooglePlaceId = i.GooglePlaceId,
        CustomerNotes = i.CustomerNotes,
        AdminNotes = i.AdminNotes,
        TechnicianNotes = i.TechnicianNotes,
        CompletionNotes = i.CompletionNotes,
        FailureReason = i.FailureReason,
        CancellationReason = i.CancellationReason,
        RouterMakeModel = i.RouterMakeModel,
        RouterSerialNumber = i.RouterSerialNumber,
        RouterMacAddress = i.RouterMacAddress,
        OntReference = i.OntReference,
        InstalledLocationNotes = i.InstalledLocationNotes,
        SpeedTestResult = i.SpeedTestResult,
        CustomerSignOffName = i.CustomerSignOffName,
        LastStatusChangedByUserId = i.LastStatusChangedByUserId,
        LastStatusChangedByUserEmail = i.LastStatusChangedByUser?.Email,
        CreatedAtUtc = i.CreatedAtUtc,
        UpdatedAtUtc = i.UpdatedAtUtc
    };
}
