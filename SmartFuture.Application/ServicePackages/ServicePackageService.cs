using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.ServicePackages;

public class ServicePackageService : IServicePackageService
{
    private readonly IAppDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<ServicePackageService> _logger;

    public ServicePackageService(IAppDbContext dbContext, IAuditService auditService, ICurrentUserService currentUser, ILogger<ServicePackageService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Result<PagedResult<ServicePackageDto>>> SearchAdminAsync(ServicePackageFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new ServicePackageFilterRequestDto();

            var query = _dbContext.ServicePackages.AsNoTracking().AsQueryable();
            query = ApplyCommonFilters(query, filter);

            if (filter.StatusFilter.HasValue)
                query = query.Where(p => p.Status == filter.StatusFilter.Value);

            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching service packages (admin)");
            return Result<PagedResult<ServicePackageDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching service packages.");
        }
    }

    public async Task<Result<PagedResult<ServicePackageDto>>> SearchCustomerAsync(ServicePackageFilterRequestDto filter, CancellationToken cancellationToken = default)
    {
        try
        {
            filter ??= new ServicePackageFilterRequestDto();

            var query = _dbContext.ServicePackages.AsNoTracking()
                .Where(p => p.Status == ServicePackageStatus.Active);

            query = ApplyCommonFilters(query, filter);

            return await ToPagedResultAsync(query, filter, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching service packages (customer)");
            return Result<PagedResult<ServicePackageDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while searching service packages.");
        }
    }

    public async Task<Result<ServicePackageDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<ServicePackageDto>.Failure(ErrorCodes.BAD_REQUEST, "Package id is required.");

            var entity = await _dbContext.ServicePackages
                .AsNoTracking()
                .Include(p => p.RadiusProfile)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            return entity is null
                ? Result<ServicePackageDto>.Failure(ErrorCodes.NOT_FOUND, "Service package not found.")
                : Result<ServicePackageDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching service package (admin) {Id}", id);
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the service package.");
        }
    }

    public async Task<Result<ServicePackageDto>> GetCustomerByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<ServicePackageDto>.Failure(ErrorCodes.BAD_REQUEST, "Package id is required.");

            var entity = await _dbContext.ServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && p.Status == ServicePackageStatus.Active, cancellationToken);

            return entity is null
                ? Result<ServicePackageDto>.Failure(ErrorCodes.NOT_FOUND, "Service package not found.")
                : Result<ServicePackageDto>.Success(MapToDto(entity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching service package (customer) {Id}", id);
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while fetching the service package.");
        }
    }

    public async Task<Result<ServicePackageDto>> CreateAsync(CreateServicePackageRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var validation = ValidateMutation(
                request?.Name, request?.Price, request?.InstallationFee, request?.ContractMonths,
                request?.DownloadSpeedMbps, request?.UploadSpeedMbps);
            if (validation is not null) return validation;

            // Security packages must never carry fibre-network provisioning
            // flags — there is no Openserve / RADIUS / MikroTik step for
            // CCTV. Force the safe defaults BEFORE the provisioning
            // validator so a misconfigured admin form can't trip its
            // "RequiresProvisioning needs ProvisioningType + RadiusProfile"
            // rule for a Security package.
            ForceSafeDefaultsForSecurity(request!);

            var provisioningValidation = ValidateProvisioning(
                request!.RequiresProvisioning, request.ProvisioningType, request.RadiusProfileId);
            if (provisioningValidation is not null) return provisioningValidation;

            // Security packages always need a marketing image — the public
            // catalogue and admin tab both render a thumbnail, and a blank
            // card looks broken. Fibre packages remain image-optional.
            var imageValidation = ValidateImageRequirement(request.Type, request.ImageUrl);
            if (imageValidation is not null) return imageValidation;

            // Honour the requested initial status. Default to Draft when
            // unset so the legacy "create-then-promote" flow keeps
            // working. Archived is not a legal create target — admins go
            // through /archive once a row has been published.
            var initialStatus = request.Status ?? ServicePackageStatus.Draft;
            if (initialStatus == ServicePackageStatus.Archived)
                return Result<ServicePackageDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Archived is not a valid initial status. Use the /archive endpoint after creation.");

            var name = request.Name.Trim();

            var nameTaken = await _dbContext.ServicePackages
                .AnyAsync(p => p.Status != ServicePackageStatus.Archived && p.Name == name, cancellationToken);

            if (nameTaken)
                return Result<ServicePackageDto>.Failure(
                    ErrorCodes.CONFLICT, $"A non-archived service package with the name '{name}' already exists.");

            var entity = new ServicePackage
            {
                Type = request.Type,
                Status = initialStatus,
                Name = name,
                Description = Trim(request.Description),
                ShortDescription = Trim(request.ShortDescription),
                SpeedLabel = Trim(request.SpeedLabel),
                DownloadSpeedMbps = request.DownloadSpeedMbps,
                UploadSpeedMbps = request.UploadSpeedMbps,
                DataAllowanceLabel = Trim(request.DataAllowanceLabel),
                IsUncapped = request.IsUncapped,
                Price = request.Price,
                BillingCycle = request.BillingCycle,
                ContractMonths = request.ContractMonths,
                HasFreeInstallation = request.HasFreeInstallation,
                InstallationFee = NormalizeInstallationFeeForSave(request.InstallationFee, null),
                IncludesRouter = request.IncludesRouter,
                RouterDescription = Trim(request.RouterDescription),
                IsFeatured = request.IsFeatured,
                DisplayOrder = request.DisplayOrder,
                TermsSummary = Trim(request.TermsSummary),
                CoverageNotes = Trim(request.CoverageNotes),
                ExternalReference = Trim(request.ExternalReference),
                RequiresProvisioning = request.RequiresProvisioning,
                ProvisioningType = request.ProvisioningType,
                BurstSpeedMbps = request.BurstSpeedMbps,
                RadiusProfileId = request.RadiusProfileId,
                ImageUrl = Trim(request.ImageUrl),
                ImageStorageKey = Trim(request.ImageStorageKey)
            };

            _dbContext.ServicePackages.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                AuditActionType.ServicePackageCreated,
                entity,
                summary: $"Service package created: {entity.Name}",
                metadata: BuildMetadata(new
                {
                    entity.Type,
                    entity.Price,
                    entity.BillingCycle,
                    entity.Status
                }));

            return Result<ServicePackageDto>.Success(MapToDto(entity), "Service package created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating service package");
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the service package.");
        }
    }

    public async Task<Result<ServicePackageDto>> UpdateAsync(Guid id, UpdateServicePackageRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<ServicePackageDto>.Failure(ErrorCodes.BAD_REQUEST, "Package id is required.");

            var validation = ValidateMutation(
                request?.Name, request?.Price, request?.InstallationFee, request?.ContractMonths,
                request?.DownloadSpeedMbps, request?.UploadSpeedMbps);
            if (validation is not null) return validation;

            ForceSafeDefaultsForSecurity(request!);

            var provisioningValidation = ValidateProvisioning(
                request!.RequiresProvisioning, request.ProvisioningType, request.RadiusProfileId);
            if (provisioningValidation is not null) return provisioningValidation;

            var imageValidation = ValidateImageRequirement(request.Type, request.ImageUrl);
            if (imageValidation is not null) return imageValidation;

            var entity = await _dbContext.ServicePackages
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            if (entity is null)
                return Result<ServicePackageDto>.Failure(ErrorCodes.NOT_FOUND, "Service package not found.");

            if (entity.Status == ServicePackageStatus.Archived)
                return Result<ServicePackageDto>.Failure(
                    ErrorCodes.CONFLICT, "Archived service packages cannot be modified.");

            var name = request.Name.Trim();

            var nameTaken = await _dbContext.ServicePackages
                .AnyAsync(p => p.Id != id
                            && p.Status != ServicePackageStatus.Archived
                            && p.Name == name, cancellationToken);

            if (nameTaken)
                return Result<ServicePackageDto>.Failure(
                    ErrorCodes.CONFLICT, $"A non-archived service package with the name '{name}' already exists.");

            var before = BuildMetadata(new
            {
                entity.Name,
                entity.Type,
                entity.Price,
                entity.BillingCycle,
                entity.IsFeatured,
                entity.DisplayOrder
            });

            entity.Type = request.Type;
            entity.Name = name;
            entity.Description = Trim(request.Description);
            entity.ShortDescription = Trim(request.ShortDescription);
            entity.SpeedLabel = Trim(request.SpeedLabel);
            entity.DownloadSpeedMbps = request.DownloadSpeedMbps;
            entity.UploadSpeedMbps = request.UploadSpeedMbps;
            entity.DataAllowanceLabel = Trim(request.DataAllowanceLabel);
            entity.IsUncapped = request.IsUncapped;
            entity.Price = request.Price;
            entity.BillingCycle = request.BillingCycle;
            entity.ContractMonths = request.ContractMonths;
            entity.HasFreeInstallation = request.HasFreeInstallation;
            // Preserve the stored fee when the request omits it (null) — never
            // wipe to null/0. Falls back to the R100 launch default if neither
            // request nor existing row carries a positive fee.
            entity.InstallationFee = NormalizeInstallationFeeForSave(request.InstallationFee, entity.InstallationFee);
            entity.IncludesRouter = request.IncludesRouter;
            entity.RouterDescription = Trim(request.RouterDescription);
            entity.IsFeatured = request.IsFeatured;
            entity.DisplayOrder = request.DisplayOrder;
            entity.TermsSummary = Trim(request.TermsSummary);
            entity.CoverageNotes = Trim(request.CoverageNotes);
            entity.ExternalReference = Trim(request.ExternalReference);
            entity.RequiresProvisioning = request.RequiresProvisioning;
            entity.ProvisioningType = request.ProvisioningType;
            entity.BurstSpeedMbps = request.BurstSpeedMbps;
            entity.RadiusProfileId = request.RadiusProfileId;
            entity.ImageUrl = Trim(request.ImageUrl);
            entity.ImageStorageKey = Trim(request.ImageStorageKey);

            // Apply requested status inline. Null = preserve existing —
            // the admin form always sends the field but a future API
            // client could omit it, and we don't want that to silently
            // demote a published row to Draft. Archived stays gated to
            // /archive so the irreversible transition has a single
            // explicit entry-point.
            if (request.Status.HasValue && request.Status.Value != entity.Status)
            {
                if (request.Status.Value == ServicePackageStatus.Archived)
                    return Result<ServicePackageDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR,
                        "Use the /archive endpoint to archive a package.");
                entity.Status = request.Status.Value;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var after = BuildMetadata(new
            {
                entity.Name,
                entity.Type,
                entity.Price,
                entity.BillingCycle,
                entity.IsFeatured,
                entity.DisplayOrder
            });

            await EmitAuditAsync(
                AuditActionType.ServicePackageUpdated,
                entity,
                summary: $"Service package updated: {entity.Name}",
                metadata: BuildMetadata(new { before, after }));

            return Result<ServicePackageDto>.Success(MapToDto(entity), "Service package updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating service package {Id}", id);
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the service package.");
        }
    }

    public Task<Result> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
        => TransitionStatusAsync(
            id, ServicePackageStatus.Active,
            AuditActionType.ServicePackageActivated,
            "Service package activated.",
            allowFromArchived: false,
            cancellationToken);

    public Task<Result> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
        => TransitionStatusAsync(
            id, ServicePackageStatus.Inactive,
            AuditActionType.ServicePackageDeactivated,
            "Service package deactivated.",
            allowFromArchived: false,
            cancellationToken);

    public Task<Result> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
        => TransitionStatusAsync(
            id, ServicePackageStatus.Archived,
            AuditActionType.ServicePackageArchived,
            "Service package archived.",
            allowFromArchived: false,
            cancellationToken);

    private async Task<Result> TransitionStatusAsync(Guid id, ServicePackageStatus targetStatus, AuditActionType actionType, string successMessage, bool allowFromArchived,
        CancellationToken cancellationToken)
    {
        try
        {
            if (id == Guid.Empty)
                return Result.Failure(ErrorCodes.BAD_REQUEST, "Package id is required.");

            var entity = await _dbContext.ServicePackages
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            if (entity is null)
                return Result.Failure(ErrorCodes.NOT_FOUND, "Service package not found.");

            if (!allowFromArchived && entity.Status == ServicePackageStatus.Archived
                && targetStatus != ServicePackageStatus.Archived)
            {
                return Result.Failure(
                    ErrorCodes.CONFLICT, "Archived service packages cannot be reactivated.");
            }

            if (entity.Status == targetStatus)
                return Result.Success(successMessage);

            var previous = entity.Status;
            entity.Status = targetStatus;

            await _dbContext.SaveChangesAsync(cancellationToken);

            await EmitAuditAsync(
                actionType,
                entity,
                summary: $"Service package status changed: {previous} -> {targetStatus}",
                metadata: BuildMetadata(new { previous, targetStatus }));

            return Result.Success(successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error transitioning service package {Id} to {Status}", id, targetStatus);
            return Result.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the service package status.");
        }
    }

    private static IQueryable<ServicePackage> ApplyCommonFilters(IQueryable<ServicePackage> query, ServicePackageFilterRequestDto filter)
    {
        // Resolve from either `?type=` (int or enum name) or `?serviceType=`
        // (frontend-friendly alias). See ServicePackageFilterRequestDto.
        var effectiveType = filter.ResolveEffectiveType();
        if (effectiveType.HasValue)
            query = query.Where(p => p.Type == effectiveType.Value);

        if (filter.IsFeatured.HasValue)
            query = query.Where(p => p.IsFeatured == filter.IsFeatured.Value);

        if (filter.IsUncapped.HasValue)
            query = query.Where(p => p.IsUncapped == filter.IsUncapped.Value);

        if (filter.MinPrice.HasValue)
            query = query.Where(p => p.Price >= filter.MinPrice.Value);

        if (filter.MaxPrice.HasValue)
            query = query.Where(p => p.Price <= filter.MaxPrice.Value);

        if (filter.FromUtc.HasValue)
            query = query.Where(p => p.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(p => p.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(p =>
                EF.Functions.Like(p.Name, $"%{s}%") ||
                (p.Description != null && EF.Functions.Like(p.Description, $"%{s}%")) ||
                (p.SpeedLabel != null && EF.Functions.Like(p.SpeedLabel, $"%{s}%")) ||
                (p.ExternalReference != null && EF.Functions.Like(p.ExternalReference, $"%{s}%")));
        }

        return query;
    }

    private static async Task<Result<PagedResult<ServicePackageDto>>> ToPagedResultAsync(IQueryable<ServicePackage> query, ServicePackageFilterRequestDto filter, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(p => new ServicePackageDto
            {
                Id = p.Id,
                Type = p.Type,
                Status = p.Status,
                Name = p.Name,
                Description = p.Description,
                ShortDescription = p.ShortDescription,
                SpeedLabel = p.SpeedLabel,
                DownloadSpeedMbps = p.DownloadSpeedMbps,
                UploadSpeedMbps = p.UploadSpeedMbps,
                DataAllowanceLabel = p.DataAllowanceLabel,
                IsUncapped = p.IsUncapped,
                Price = p.Price,
                BillingCycle = p.BillingCycle,
                ContractMonths = p.ContractMonths,
                HasFreeInstallation = p.HasFreeInstallation,
                InstallationFee = p.InstallationFee,
                IncludesRouter = p.IncludesRouter,
                RouterDescription = p.RouterDescription,
                IsFeatured = p.IsFeatured,
                DisplayOrder = p.DisplayOrder,
                TermsSummary = p.TermsSummary,
                CoverageNotes = p.CoverageNotes,
                ExternalReference = p.ExternalReference,
                RequiresProvisioning = p.RequiresProvisioning,
                ProvisioningType = p.ProvisioningType,
                BurstSpeedMbps = p.BurstSpeedMbps,
                RadiusProfileId = p.RadiusProfileId,
                RadiusProfileName = p.RadiusProfile != null ? p.RadiusProfile.Name : null,
                ImageUrl = p.ImageUrl,
                ImageStorageKey = p.ImageStorageKey,
                CreatedAtUtc = p.CreatedAtUtc,
                UpdatedAtUtc = p.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var paged = new PagedResult<ServicePackageDto>(items, filter.Page, filter.PageSize, totalCount);
        return Result<PagedResult<ServicePackageDto>>.Success(paged);
    }

    // Phase 3.5 — provisioning consistency guardrail. When a package
    // flags itself as RequiresProvisioning, both the network technology
    // (ProvisioningType) and the speed bundle (RadiusProfileId) must be
    // set or the admin save is blocked. We do NOT validate that the
    // referenced profile exists here — that's a separate DB check; the
    // FK constraint catches a bad id at SaveChanges time.
    private static Result<ServicePackageDto>? ValidateProvisioning(
        bool requiresProvisioning,
        Shared.Enums.NetworkAccounts.ProvisioningType? provisioningType,
        Guid? radiusProfileId)
    {
        if (!requiresProvisioning) return null;
        if (provisioningType is null)
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "ProvisioningType is required when RequiresProvisioning is true.");
        if (radiusProfileId is null || radiusProfileId == Guid.Empty)
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "RadiusProfileId is required when RequiresProvisioning is true.");
        return null;
    }

    private static Result<ServicePackageDto>? ValidateMutation(string? name, decimal? price, decimal? installationFee, int? contractMonths, int? downloadMbps,
        int? uploadMbps)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<ServicePackageDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Name is required.");

        if (price.HasValue && price.Value < 0)
            return Result<ServicePackageDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Price cannot be negative.");

        if (installationFee.HasValue && installationFee.Value < 0)
            return Result<ServicePackageDto>.Failure(ErrorCodes.VALIDATION_ERROR, "InstallationFee cannot be negative.");

        if (contractMonths.HasValue && contractMonths.Value < 0)
            return Result<ServicePackageDto>.Failure(ErrorCodes.VALIDATION_ERROR, "ContractMonths cannot be negative.");

        if (downloadMbps.HasValue && downloadMbps.Value < 0)
            return Result<ServicePackageDto>.Failure(ErrorCodes.VALIDATION_ERROR, "DownloadSpeedMbps cannot be negative.");

        if (uploadMbps.HasValue && uploadMbps.Value < 0)
            return Result<ServicePackageDto>.Failure(ErrorCodes.VALIDATION_ERROR, "UploadSpeedMbps cannot be negative.");

        return null;
    }

    private async Task EmitAuditAsync(AuditActionType actionType, ServicePackage entity, string summary, string? metadata)
    {
        await _auditService.LogAsync(new CreateAuditLogRequestDto
        {
            ActorUserId = _currentUser.UserId,
            ActorType = AuditActorType.Admin,
            ActionType = actionType,
            EntityType = AuditEntityType.ServicePackage,
            EntityId = entity.Id,
            EntityName = entity.Name,
            Summary = summary,
            MetadataJson = metadata,
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

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Launch policy: a package's installation fee must never persist as null/0.
    // The admin edit form historically omitted the field, so every save sent
    // null and the backend overwrote the stored fee — wiping it and breaking
    // ClientZone checkout. Resolve a safe value on every create/update: prefer
    // an explicit positive request value, otherwise keep an existing positive
    // value, otherwise fall back to the R100 launch default.
    private const decimal DefaultInstallationFee = 100m;

    private static decimal NormalizeInstallationFeeForSave(decimal? requested, decimal? existing)
    {
        if (requested.HasValue && requested.Value > 0m) return requested.Value;
        if (existing.HasValue && existing.Value > 0m) return existing.Value;
        return DefaultInstallationFee;
    }

    private static ServicePackageDto MapToDto(ServicePackage p) => new()
    {
        Id = p.Id,
        Type = p.Type,
        Status = p.Status,
        Name = p.Name,
        Description = p.Description,
        ShortDescription = p.ShortDescription,
        SpeedLabel = p.SpeedLabel,
        DownloadSpeedMbps = p.DownloadSpeedMbps,
        UploadSpeedMbps = p.UploadSpeedMbps,
        DataAllowanceLabel = p.DataAllowanceLabel,
        IsUncapped = p.IsUncapped,
        Price = p.Price,
        BillingCycle = p.BillingCycle,
        ContractMonths = p.ContractMonths,
        HasFreeInstallation = p.HasFreeInstallation,
        InstallationFee = p.InstallationFee,
        IncludesRouter = p.IncludesRouter,
        RouterDescription = p.RouterDescription,
        IsFeatured = p.IsFeatured,
        DisplayOrder = p.DisplayOrder,
        TermsSummary = p.TermsSummary,
        CoverageNotes = p.CoverageNotes,
        ExternalReference = p.ExternalReference,
        RequiresProvisioning = p.RequiresProvisioning,
        ProvisioningType = p.ProvisioningType,
        BurstSpeedMbps = p.BurstSpeedMbps,
        RadiusProfileId = p.RadiusProfileId,
        RadiusProfileName = p.RadiusProfile?.Name,
        ImageUrl = p.ImageUrl,
        ImageStorageKey = p.ImageStorageKey,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc
    };

    private static Result<ServicePackageDto>? ValidateImageRequirement(ServicePackageType type, string? imageUrl)
    {
        if (type != ServicePackageType.Security) return null;
        if (string.IsNullOrWhiteSpace(imageUrl))
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Security packages require an image. Upload one before saving.");
        return null;
    }

    // Server-side safety net: Security packages must never carry the
    // fibre-network provisioning flags. Even if the admin form sends
    // RequiresProvisioning=true (or a ProvisioningType / RadiusProfileId)
    // by accident, we coerce them back to the no-network defaults so
    // the post-payment promotion hook in PaymentApplierService never
    // attempts a NetworkAccount provision for CCTV fulfilment.
    private static void ForceSafeDefaultsForSecurity(CreateServicePackageRequestDto request)
    {
        if (request.Type != ServicePackageType.Security) return;
        request.RequiresProvisioning = false;
        request.ProvisioningType = null;
        request.RadiusProfileId = null;
    }

    private static void ForceSafeDefaultsForSecurity(UpdateServicePackageRequestDto request)
    {
        if (request.Type != ServicePackageType.Security) return;
        request.RequiresProvisioning = false;
        request.ProvisioningType = null;
        request.RadiusProfileId = null;
    }
}
