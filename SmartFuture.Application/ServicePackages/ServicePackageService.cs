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
                .Include(p => p.SubType)
                .Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            // Admin edit surface returns ALL variants (incl. inactive) so
            // they can be re-enabled from the form.
            return entity is null
                ? Result<ServicePackageDto>.Failure(ErrorCodes.NOT_FOUND, "Service package not found.")
                : Result<ServicePackageDto>.Success(MapToDto(entity, activeVariantsOnly: false));
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
                .Include(p => p.SubType)
                .Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.Id == id && p.Status == ServicePackageStatus.Active, cancellationToken);

            // Public surface — active variants only.
            return entity is null
                ? Result<ServicePackageDto>.Failure(ErrorCodes.NOT_FOUND, "Service package not found.")
                : Result<ServicePackageDto>.Success(MapToDto(entity, activeVariantsOnly: true));
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

            // Subtype (Security only) + variants. Both validated before we
            // build the entity so a bad payload fails cleanly.
            var (subTypeError, subType) = await ResolveSubTypeAsync(request.Type, request.SubTypeId, cancellationToken);
            if (subTypeError is not null) return subTypeError;

            var variantError = ValidateVariantInputs(request.Variants);
            if (variantError is not null) return variantError;

            var entity = new ServicePackage
            {
                Type = request.Type,
                SubTypeId = subType?.Id,
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
                ImageStorageKey = Trim(request.ImageStorageKey),
                FeaturesJson = SerializeFeatures(request.Features)
            };

            // Attach the new variants — Add cascades the inserts.
            entity.Variants = BuildVariantEntities(request.Variants);

            _dbContext.ServicePackages.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Populate the SubType nav for the response mapping (SubTypeId
            // alone doesn't load it).
            entity.SubType = subType;

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
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrency conflict while creating service package");
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.CONFLICT,
                "This package was changed while you were editing it. Please refresh the page and try again.");
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while creating service package");
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.EXCEPTION,
                "We could not save this package due to a database issue. Please try again or contact support.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while creating service package");
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.EXCEPTION,
                "We could not save this package. Please try again or contact support.");
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
                .Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            if (entity is null)
                return Result<ServicePackageDto>.Failure(ErrorCodes.NOT_FOUND, "Service package not found.");

            if (entity.Status == ServicePackageStatus.Archived)
                return Result<ServicePackageDto>.Failure(
                    ErrorCodes.CONFLICT, "Archived service packages cannot be modified.");

            var (subTypeError, subType) = await ResolveSubTypeAsync(request.Type, request.SubTypeId, cancellationToken);
            if (subTypeError is not null) return subTypeError;

            var variantError = ValidateVariantInputs(request.Variants);
            if (variantError is not null) return variantError;

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
            // Only overwrite features when the request carries the field —
            // null preserves the stored list (the admin form always sends it).
            if (request.Features is not null)
                entity.FeaturesJson = SerializeFeatures(request.Features);

            // Subtype: the admin form always sends the field, so apply it
            // (Security-only; ResolveSubTypeAsync forces null for other
            // types). Null clears the subtype.
            entity.SubTypeId = subType?.Id;
            entity.SubType = subType;

            // Variants: null = leave untouched (legacy callers). Non-null =
            // reconcile the full set (upsert by Id, soft-disable removed rows).
            if (request.Variants is not null)
            {
                var reconcileError = ReconcileVariants(entity, request.Variants);
                if (reconcileError is not null) return reconcileError;
            }

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

            await SaveResolvingConcurrencyAsync(id, cancellationToken);

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
        catch (DbUpdateConcurrencyException ex)
        {
            // Reached only when the client-wins retry ALSO failed (e.g. the
            // row was genuinely deleted, or an environment issue like a table
            // trigger keeps breaking EF's affected-row check). Log exactly
            // which entity/token conflicted; the admin sees a calm message —
            // never the raw EF/SQL text or the Microsoft docs URL.
            LogConcurrencyEntries(id, ex);
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.CONFLICT,
                "This package was changed while you were editing it. Please refresh the page and try again.");
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while updating service package {Id}", id);
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.EXCEPTION,
                "We could not save this package due to a database issue. Please try again or contact support.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while updating service package {Id}", id);
            return Result<ServicePackageDto>.Failure(
                ErrorCodes.EXCEPTION,
                "We could not save this package. Please try again or contact support.");
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

        // Project the DTO + raw FeaturesJson together (JSON can't be
        // deserialized inside the EF query), then hydrate Features in memory.
        var rows = await query
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(p => new { Dto = new ServicePackageDto
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
                SubTypeId = p.SubTypeId,
                SecurityType = p.SubType != null ? p.SubType.Name : null,
                SecurityTypeName = p.SubType != null ? p.SubType.Name : null,
                SecurityTypeSlug = p.SubType != null ? p.SubType.Slug : null,
                SubTypeName = p.SubType != null ? p.SubType.Name : null,
                SubTypeSlug = p.SubType != null ? p.SubType.Slug : null,
                // Lists (public catalogue + admin index) surface active
                // variants only, sorted for the pill row.
                Variants = p.Variants
                    .Where(v => v.IsActive)
                    .OrderBy(v => v.DisplayOrder)
                    .ThenBy(v => v.Name)
                    .Select(v => new ServicePackageVariantDto
                    {
                        Id = v.Id,
                        Name = v.Name,
                        Price = v.Price,
                        InstallationFee = v.InstallationFee,
                        HasFreeInstallation = v.HasFreeInstallation,
                        IsActive = v.IsActive,
                        DisplayOrder = v.DisplayOrder
                    }).ToList(),
                CreatedAtUtc = p.CreatedAtUtc,
                UpdatedAtUtc = p.UpdatedAtUtc
            }, p.FeaturesJson })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r =>
        {
            r.Dto.Features = DeserializeFeatures(r.FeaturesJson);
            return r.Dto;
        }).ToList();

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

    // Save with a single "client wins" retry on optimistic-concurrency
    // conflict. On conflict we log exactly which entity/token failed, then
    // — for each conflicting entity that STILL EXISTS in the DB — refresh
    // its concurrency token(s) from the current row and retry once, so the
    // admin's edits re-apply over the latest row version. A row that was
    // genuinely deleted (no DB values) is a real conflict and is rethrown
    // to the caller's friendly handler. Non-token field values the admin
    // did not touch are left untouched, so this never clobbers a concurrent
    // change to an unrelated field.
    private async Task SaveResolvingConcurrencyAsync(Guid packageId, CancellationToken ct)
    {
        try
        {
            await _dbContext.SaveChangesAsync(ct);
            return;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            LogConcurrencyEntries(packageId, ex);

            foreach (var entry in ex.Entries)
            {
                var dbValues = await entry.GetDatabaseValuesAsync(ct);
                if (dbValues is null)
                    throw; // row deleted elsewhere → genuine conflict

                foreach (var token in entry.Metadata.GetProperties().Where(p => p.IsConcurrencyToken))
                    entry.Property(token.Name).OriginalValue = dbValues[token.Name];
            }
        }

        // Retry once with refreshed tokens. If it conflicts again it bubbles
        // to the caller's DbUpdateConcurrencyException handler.
        await _dbContext.SaveChangesAsync(ct);
    }

    // Diagnostic: log which entity(ies) hit the concurrency conflict and the
    // token values, so a deterministic (non-race) failure can be pinned to a
    // specific entity type from the server logs. No PII — keys + rowversions.
    private void LogConcurrencyEntries(Guid packageId, DbUpdateConcurrencyException ex)
    {
        foreach (var entry in ex.Entries)
        {
            var keys = string.Join(",", entry.Properties
                .Where(p => p.Metadata.IsPrimaryKey())
                .Select(p => $"{p.Metadata.Name}={p.CurrentValue}"));

            string origRv = "(n/a)", currRv = "(n/a)";
            if (entry.Metadata.FindProperty("RowVersion") is not null)
            {
                origRv = FormatRowVersion(entry.Property("RowVersion").OriginalValue as byte[]);
                currRv = FormatRowVersion(entry.Property("RowVersion").CurrentValue as byte[]);
            }

            _logger.LogWarning(
                "Concurrency conflict updating package {PackageId}: Entity={EntityType} State={State} Keys={Keys} OriginalRowVersion={OriginalRowVersion} CurrentRowVersion={CurrentRowVersion}",
                packageId, entry.Metadata.ClrType.Name, entry.State, keys, origRv, currRv);
        }
    }

    private static string FormatRowVersion(byte[]? value)
        => value is null ? "null" : Convert.ToBase64String(value);

    // Marketing feature bullets ⇄ JSON string column. Trims + drops blanks
    // on save; returns an empty list (never null) on read so the API always
    // ships a `features: []` the frontends can map safely.
    private static string? SerializeFeatures(List<string>? features)
    {
        var cleaned = (features ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();
        return cleaned.Count == 0 ? null : JsonSerializer.Serialize(cleaned);
    }

    private static List<string> DeserializeFeatures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            return list is null
                ? new List<string>()
                : list.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

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

    // activeVariantsOnly: true on public/customer surfaces (hide disabled
    // variants); false on the admin edit surface (so they're editable).
    private static ServicePackageDto MapToDto(ServicePackage p, bool activeVariantsOnly = false)
    {
        var variantRows = (p.Variants ?? new List<ServicePackageVariant>())
            .Where(v => !activeVariantsOnly || v.IsActive)
            .OrderBy(v => v.DisplayOrder)
            .ThenBy(v => v.Name)
            .Select(MapVariantToDto)
            .ToList();

        return new ServicePackageDto
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
            Features = DeserializeFeatures(p.FeaturesJson),
            SubTypeId = p.SubTypeId,
            SecurityType = p.SubType?.Name,
            SecurityTypeName = p.SubType?.Name,
            SecurityTypeSlug = p.SubType?.Slug,
            SubTypeName = p.SubType?.Name,
            SubTypeSlug = p.SubType?.Slug,
            Variants = variantRows,
            CreatedAtUtc = p.CreatedAtUtc,
            UpdatedAtUtc = p.UpdatedAtUtc
        };
    }

    private static ServicePackageVariantDto MapVariantToDto(ServicePackageVariant v) => new()
    {
        Id = v.Id,
        Name = v.Name,
        Price = v.Price,
        InstallationFee = v.InstallationFee,
        HasFreeInstallation = v.HasFreeInstallation,
        IsActive = v.IsActive,
        DisplayOrder = v.DisplayOrder
    };

    // Resolve + validate the requested subtype. Only Security packages
    // carry one — for any other type the field is ignored (forced null).
    // When supplied, the subtype must exist and belong to the same package
    // line. Active-ness is NOT enforced so re-saving a package that points
    // at a since-disabled subtype doesn't fail.
    private async Task<(Result<ServicePackageDto>? error, ServicePackageSubType? subType)> ResolveSubTypeAsync(
        ServicePackageType type, Guid? subTypeId, CancellationToken ct)
    {
        if (type != ServicePackageType.Security || subTypeId is null || subTypeId == Guid.Empty)
            return (null, null);

        var subType = await _dbContext.ServicePackageSubTypes
            .FirstOrDefaultAsync(s => s.Id == subTypeId.Value, ct);
        if (subType is null)
            return (Result<ServicePackageDto>.Failure(
                ErrorCodes.NOT_FOUND, "Selected security type was not found."), null);
        if (subType.PackageType != type)
            return (Result<ServicePackageDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Selected security type does not belong to this package type."), null);
        return (null, subType);
    }

    // Validate the variant input set. Blank-name rows are ignored (they're
    // treated as "not saved"), so an empty UI row never blocks a save.
    // Names must be unique per package (case-insensitive); price + fee
    // cannot be negative.
    private static Result<ServicePackageDto>? ValidateVariantInputs(List<ServicePackageVariantInputDto>? variants)
    {
        if (variants is null) return null;

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variants)
        {
            var name = v.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue; // blank row — skip

            if (!seenNames.Add(name))
                return Result<ServicePackageDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Duplicate variant name '{name}'. Variant names must be unique per package.");
            if (v.Price < 0m)
                return Result<ServicePackageDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, $"Variant '{name}' price cannot be negative.");
            if (v.InstallationFee.HasValue && v.InstallationFee.Value < 0m)
                return Result<ServicePackageDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR, $"Variant '{name}' activation fee cannot be negative.");
        }
        return null;
    }

    private static List<ServicePackageVariant> BuildVariantEntities(List<ServicePackageVariantInputDto>? variants)
    {
        var list = new List<ServicePackageVariant>();
        if (variants is null) return list;
        foreach (var v in variants)
        {
            var name = v.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new ServicePackageVariant
            {
                Name = name,
                Price = v.Price,
                InstallationFee = v.InstallationFee,
                HasFreeInstallation = v.HasFreeInstallation,
                IsActive = v.IsActive,
                DisplayOrder = v.DisplayOrder
            });
        }
        return list;
    }

    // Reconcile the tracked package's variant collection against the full
    // desired input set: update rows matched by Id, add new rows, and
    // SOFT-DISABLE (IsActive=false) the rows the admin removed. We never
    // hard-delete a variant — it may be referenced by historical orders /
    // order intents (FK NoAction), and a DELETE guarded by the variant's
    // rowversion is exactly what produced the "affected 0 rows" concurrency
    // exception. Returns a 400 error when a supplied Id isn't part of this
    // package (stale form); null on success. Blank-name rows are ignored.
    private static Result<ServicePackageDto>? ReconcileVariants(ServicePackage entity, List<ServicePackageVariantInputDto> inputs)
    {
        var existing = entity.Variants.ToList();
        var existingIds = existing.Select(x => x.Id).ToHashSet();

        // Validate up-front (before mutating): any supplied variant Id must
        // belong to THIS package. A stale/foreign Id → 400, not a 500.
        foreach (var input in inputs)
        {
            if (input.Id.HasValue && input.Id.Value != Guid.Empty && !existingIds.Contains(input.Id.Value))
                return Result<ServicePackageDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "One of the variants no longer exists on this package. Refresh the page and try again.");
        }

        var keptIds = new HashSet<Guid>();

        foreach (var input in inputs)
        {
            var name = input.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            ServicePackageVariant? match = null;
            if (input.Id.HasValue && input.Id.Value != Guid.Empty)
                match = existing.FirstOrDefault(x => x.Id == input.Id.Value);

            if (match is not null)
            {
                match.Name = name;
                match.Price = input.Price;
                match.InstallationFee = input.InstallationFee;
                match.HasFreeInstallation = input.HasFreeInstallation;
                match.IsActive = input.IsActive;
                match.DisplayOrder = input.DisplayOrder;
                keptIds.Add(match.Id);
            }
            else
            {
                entity.Variants.Add(new ServicePackageVariant
                {
                    Name = name,
                    Price = input.Price,
                    InstallationFee = input.InstallationFee,
                    HasFreeInstallation = input.HasFreeInstallation,
                    IsActive = input.IsActive,
                    DisplayOrder = input.DisplayOrder
                });
            }
        }

        // Variants the admin dropped from the form → soft-disable (hide from
        // the public site + admin pickers) rather than delete. Preserves any
        // order/intent references and avoids a rowversion-guarded DELETE.
        foreach (var old in existing)
        {
            if (keptIds.Contains(old.Id)) continue;
            if (old.IsActive) old.IsActive = false;
        }

        return null;
    }

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
