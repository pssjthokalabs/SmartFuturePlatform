using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Openserve;

// Admin CRUD for the durable ServicePackage -> Openserve product
// mapping (brief §4). Deliberately has NO dependency on
// OpenserveFulfilmentSettings or any HTTP client — this is pure
// catalogue bookkeeping so it can be built, tested and used by admin
// regardless of whether OpenserveFulfilment:Enabled is true.
public class PackageOpenserveMappingService : IPackageOpenserveMappingService
{
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<PackageOpenserveMappingService> _logger;

    public PackageOpenserveMappingService(IAppDbContext dbContext, ILogger<PackageOpenserveMappingService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<PackageOpenserveMappingDto>>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await _dbContext.PackageOpenserveMappings
                .AsNoTracking()
                .Include(m => m.ServicePackage)
                .OrderBy(m => m.ServicePackage!.Name)
                .Select(m => Map(m))
                .ToListAsync(cancellationToken);
            return Result<IReadOnlyList<PackageOpenserveMappingDto>>.Success(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing PackageOpenserveMappings");
            return Result<IReadOnlyList<PackageOpenserveMappingDto>>.Failure(ErrorCodes.EXCEPTION, "Could not list Openserve package mappings.");
        }
    }

    public async Task<Result<IReadOnlyList<UnmappedServicePackageDto>>> ListUnmappedFibrePackagesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Same rule as the submission gate: only ACTIVE Fibre packages
            // can be ordered, and only an ENABLED mapping can be submitted.
            var activeFibre = await _dbContext.ServicePackages
                .AsNoTracking()
                .Where(p => p.Type == ServicePackageType.Fibre && p.Status == ServicePackageStatus.Active)
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name, p.DownloadSpeedMbps, p.SpeedLabel })
                .ToListAsync(cancellationToken);

            var ids = activeFibre.Select(p => p.Id).ToList();
            var enabledByPackage = await _dbContext.PackageOpenserveMappings
                .AsNoTracking()
                .Where(m => ids.Contains(m.ServicePackageId))
                .Select(m => new { m.ServicePackageId, m.IsEnabled })
                .ToDictionaryAsync(m => m.ServicePackageId, m => m.IsEnabled, cancellationToken);

            var unmapped = activeFibre
                .Where(p => !enabledByPackage.TryGetValue(p.Id, out var enabled) || !enabled)
                .Select(p => new UnmappedServicePackageDto
                {
                    ServicePackageId = p.Id,
                    Name = p.Name,
                    DownloadSpeedMbps = p.DownloadSpeedMbps,
                    SpeedLabel = p.SpeedLabel,
                    MappingStatus = enabledByPackage.ContainsKey(p.Id) ? PackageOpenserveMappingStatus.Disabled : PackageOpenserveMappingStatus.Unmapped
                })
                .ToList();

            return Result<IReadOnlyList<UnmappedServicePackageDto>>.Success(unmapped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing unmapped fibre packages");
            return Result<IReadOnlyList<UnmappedServicePackageDto>>.Failure(ErrorCodes.EXCEPTION, "Could not list unmapped fibre packages.");
        }
    }

    public async Task<Result<IReadOnlyList<FibrePackageMappingRowDto>>> ListFibrePackageMappingsAsync(bool includeNonActive = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var packages = await _dbContext.ServicePackages
                .AsNoTracking()
                .Where(p => p.Type == ServicePackageType.Fibre && p.Status != ServicePackageStatus.Archived)
                .Where(p => includeNonActive || p.Status == ServicePackageStatus.Active)
                .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name)
                .ToListAsync(cancellationToken);

            var ids = packages.Select(p => p.Id).ToList();
            var mappings = await _dbContext.PackageOpenserveMappings
                .AsNoTracking()
                .Where(m => ids.Contains(m.ServicePackageId))
                .ToDictionaryAsync(m => m.ServicePackageId, cancellationToken);

            var rows = packages.Select(p =>
            {
                mappings.TryGetValue(p.Id, out var mapping);
                var dto = mapping is null ? null : Map(mapping);
                if (dto is not null) dto.ServicePackageName = p.Name;
                return new FibrePackageMappingRowDto
                {
                    ServicePackageId = p.Id,
                    Name = p.Name,
                    PackageStatus = p.Status.ToString(),
                    SpeedLabel = p.SpeedLabel,
                    DownloadSpeedMbps = p.DownloadSpeedMbps,
                    UploadSpeedMbps = p.UploadSpeedMbps,
                    Price = p.Price,
                    BillingCycle = p.BillingCycle.ToString(),
                    RequiredForReadiness = p.Status == ServicePackageStatus.Active,
                    MappingStatus = mapping is null
                        ? PackageOpenserveMappingStatus.Unmapped
                        : mapping.IsEnabled ? PackageOpenserveMappingStatus.Mapped : PackageOpenserveMappingStatus.Disabled,
                    Mapping = dto,
                    CapacityConflict = mapping is null ? null : CapacityConflictMessage(mapping.Sku, mapping.Capacity, mapping.CapacityUom, p.DownloadSpeedMbps)
                };
            }).ToList();

            return Result<IReadOnlyList<FibrePackageMappingRowDto>>.Success(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing fibre package mappings");
            return Result<IReadOnlyList<FibrePackageMappingRowDto>>.Failure(ErrorCodes.EXCEPTION, "Could not list Fibre package mappings.");
        }
    }

    public IReadOnlyList<OpenserveCatalogueProductDto> GetCatalogue()
    {
        var products = OpenserveProductCatalogue.Entries
            .GroupBy(e => e.Sku, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OpenserveCatalogueProductDto
            {
                Sku = g.Key,
                ProductName = OpenserveProductCatalogue.ProductNameFor(g.Key) ?? g.Key,
                Technology = g.First().Technology,
                HasPublishedSpeedTable = true,
                Speeds = g.Select(e => new OpenserveCatalogueSpeedDto
                {
                    Capacity = e.Capacity,
                    CapacityUom = e.CapacityUom,
                    IsRetentionOffer = e.IsRetentionOffer,
                    OrderableAsNewSalesOrder = !e.IsRetentionOffer
                }).ToList()
            })
            .ToList();

        products.AddRange(OpenserveProductCatalogue.SkusWithoutPublishedSpeedTable
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Select(sku => new OpenserveCatalogueProductDto
            {
                Sku = sku,
                ProductName = OpenserveProductCatalogue.ProductNameFor(sku) ?? sku,
                Technology = null,
                HasPublishedSpeedTable = false
            }));

        return products;
    }

    public async Task<Result<PackageOpenserveMappingDto>> SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PackageOpenserveMappings.Include(m => m.ServicePackage).FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (entity is null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.NOT_FOUND, "Openserve package mapping not found.");

        if (isEnabled)
        {
            // Enabling makes the mapping live for submission — re-check it
            // against the documented catalogue first.
            var validation = Validate(entity.ServicePackageId, entity.ServicePackage?.Type, entity.OpenserveProductName, entity.Sku, entity.Capacity, entity.CapacityUom, isEnabled: true, out _,
                entity.ServicePackage?.DownloadSpeedMbps);
            if (validation is not null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.VALIDATION_ERROR, validation);
        }

        entity.IsEnabled = isEnabled;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result<PackageOpenserveMappingDto>.Success(Map(entity), isEnabled ? "Openserve package mapping enabled." : "Openserve package mapping disabled.");
    }

    public async Task<Result<PackageOpenserveMappingDto>> GetByServicePackageIdAsync(Guid servicePackageId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PackageOpenserveMappings
            .AsNoTracking()
            .Include(m => m.ServicePackage)
            .FirstOrDefaultAsync(m => m.ServicePackageId == servicePackageId, cancellationToken);

        return entity is null
            ? Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.NOT_FOUND, "No Openserve mapping exists for this package.")
            : Result<PackageOpenserveMappingDto>.Success(Map(entity));
    }

    public async Task<Result<PackageOpenserveMappingDto>> CreateAsync(CreatePackageOpenserveMappingRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");
        if (request.ServicePackageId == Guid.Empty) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.VALIDATION_ERROR, "ServicePackageId is required.");

        var package = await _dbContext.ServicePackages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ServicePackageId, cancellationToken);
        if (package is null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.NOT_FOUND, "Service package not found.");

        var validation = Validate(request.ServicePackageId, package.Type, request.OpenserveProductName, request.Sku, request.Capacity, request.CapacityUom, request.IsEnabled, out var productName,
            package.DownloadSpeedMbps);
        if (validation is not null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.VALIDATION_ERROR, validation);

        var exists = await _dbContext.PackageOpenserveMappings.AnyAsync(m => m.ServicePackageId == request.ServicePackageId, cancellationToken);
        if (exists) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.CONFLICT, "This package already has an Openserve mapping — update it instead.");

        var entity = new PackageOpenserveMapping
        {
            Id = Guid.NewGuid(),
            ServicePackageId = request.ServicePackageId,
            OpenserveProductName = productName,
            Sku = request.Sku.Trim().ToUpperInvariant(),
            Capacity = request.Capacity.Trim(),
            CapacityUom = request.CapacityUom.Trim(),
            IsEnabled = request.IsEnabled,
            OpenserveProductOfferingId = string.IsNullOrWhiteSpace(request.OpenserveProductOfferingId) ? null : request.OpenserveProductOfferingId.Trim(),
            OpenserveProductSpecificationId = string.IsNullOrWhiteSpace(request.OpenserveProductSpecificationId) ? null : request.OpenserveProductSpecificationId.Trim(),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
        _dbContext.PackageOpenserveMappings.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Don't assign the AsNoTracking() `package` instance onto the
        // now-tracked `entity`'s navigation — if the caller's DbContext
        // already has that ServicePackage tracked under a different CLR
        // instance (same Id), EF's navigation fixup throws "already
        // being tracked". Build the DTO from data we already have instead.
        var dto = Map(entity);
        dto.ServicePackageName = package.Name;
        return Result<PackageOpenserveMappingDto>.Success(dto, "Openserve package mapping created.");
    }

    public async Task<Result<PackageOpenserveMappingDto>> UpdateAsync(Guid id, UpdatePackageOpenserveMappingRequestDto request, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PackageOpenserveMappings.Include(m => m.ServicePackage).FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (entity is null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.NOT_FOUND, "Openserve package mapping not found.");

        if (request is null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

        var validation = Validate(entity.ServicePackageId, entity.ServicePackage?.Type, request.OpenserveProductName, request.Sku, request.Capacity, request.CapacityUom, request.IsEnabled, out var productName,
            entity.ServicePackage?.DownloadSpeedMbps);
        if (validation is not null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.VALIDATION_ERROR, validation);

        entity.OpenserveProductName = productName;
        entity.Sku = request.Sku.Trim().ToUpperInvariant();
        entity.Capacity = request.Capacity.Trim();
        entity.CapacityUom = request.CapacityUom.Trim();
        entity.IsEnabled = request.IsEnabled;
        entity.OpenserveProductOfferingId = string.IsNullOrWhiteSpace(request.OpenserveProductOfferingId) ? null : request.OpenserveProductOfferingId.Trim();
        entity.OpenserveProductSpecificationId = string.IsNullOrWhiteSpace(request.OpenserveProductSpecificationId) ? null : request.OpenserveProductSpecificationId.Trim();
        entity.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result<PackageOpenserveMappingDto>.Success(Map(entity), "Openserve package mapping updated.");
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PackageOpenserveMappings.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (entity is null) return Result.Failure(ErrorCodes.NOT_FOUND, "Openserve package mapping not found.");

        var inUse = await _dbContext.OpenserveOrders.AnyAsync(o => o.PackageOpenserveMappingId == id, cancellationToken);
        if (inUse)
        {
            entity.IsEnabled = false;
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success("Mapping has been used by at least one Openserve order; disabled instead of deleted so submission history stays intact.");
        }

        _dbContext.PackageOpenserveMappings.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success("Openserve package mapping deleted.");
    }

    /// <summary>
    /// Everything a mapping must satisfy to be saved — and, when enabled, to
    /// be used on a real Openserve Create Order. Nothing is inferred: the
    /// SKU, capacity and UOM must be a documented Appendix D combination and
    /// productOffering.name must be the name Openserve documents for that
    /// SKU (normalised to its exact spelling in <paramref name="normalizedProductName"/>).
    /// </summary>
    private static string? Validate(Guid servicePackageId, ServicePackageType? packageType, string productName, string sku, string capacity, string capacityUom, bool isEnabled,
        out string normalizedProductName, int? packageDownloadMbps = null)
    {
        normalizedProductName = productName?.Trim() ?? string.Empty;
        if (servicePackageId == Guid.Empty) return "ServicePackageId is required.";
        if (packageType is not null && packageType != ServicePackageType.Fibre)
            return "Openserve mappings apply to Fibre packages only — only Fibre orders are submitted to Openserve.";
        if (string.IsNullOrWhiteSpace(productName)) return "OpenserveProductName is required.";
        if (string.IsNullOrWhiteSpace(sku)) return "Sku is required.";
        if (string.IsNullOrWhiteSpace(capacity)) return "Capacity is required.";
        if (string.IsNullOrWhiteSpace(capacityUom)) return "CapacityUom is required.";

        sku = sku.Trim();
        if (!OpenserveProductCatalogue.IsKnownSku(sku))
            return $"'{sku}' is not a SKU documented in the Openserve Fulfilment API Spec Appendix D. Check for a typo, or confirm the SKU with Openserve before adding it here.";

        var documentedName = OpenserveProductCatalogue.ProductNameFor(sku);
        if (documentedName is not null && !string.Equals(documentedName, productName.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"Product name '{productName.Trim()}' does not match SKU '{sku.ToUpperInvariant()}' — Openserve documents it as '{documentedName}'.";
        normalizedProductName = documentedName ?? productName.Trim();

        if (!OpenserveProductCatalogue.SkusWithoutPublishedSpeedTable.Contains(sku)
            && !OpenserveProductCatalogue.IsValidCombination(sku, capacity, capacityUom))
            return $"'{capacity} {capacityUom}' is not a documented valid speed for SKU '{sku}' (Appendix D). Check the capacity/unit, or confirm with Openserve if this is a new combination.";

        if (isEnabled && !OpenserveProductCatalogue.IsOrderableAsNewSalesOrder(sku, capacity.Trim(), capacityUom.Trim()))
            return $"'{sku.ToUpperInvariant()} {capacity} {capacityUom}' is an Openserve retention offer (Appendix D **): it can only be ordered as a Regrade, never on a new Sales Order, so it cannot be enabled for SmartFuture orders.";

        // An enabled mapping is what gets ordered: its capacity must be the
        // package's download speed (a 200 Mbps package must not be ordered as
        // 100). A disabled draft may be saved while the business confirms it.
        if (isEnabled && CapacityConflictMessage(sku, capacity, capacityUom, packageDownloadMbps) is { } conflict)
            return conflict;

        return null;
    }

    /// <summary>
    /// Mapping capacity ≠ package download speed, with the documented Appendix D
    /// options for this SKU at the package's speed (never picks one — "Mbps" vs
    /// "Mbps Lite" is a commercial choice Openserve/the business must confirm).
    /// </summary>
    public static string? CapacityConflictMessage(string sku, string capacity, string capacityUom, int? packageDownloadMbps)
    {
        var conflict = OpenserveFibreEligibility.MappingCapacityConflict(capacity, capacityUom, packageDownloadMbps);
        if (conflict is null) return null;

        var documented = OpenserveProductCatalogue.Entries
            .Where(e => string.Equals(e.Sku, sku?.Trim(), StringComparison.OrdinalIgnoreCase) && e.Capacity == packageDownloadMbps!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Select(e => $"{e.Sku} {e.Capacity} {e.CapacityUom}{(e.IsRetentionOffer ? " (retention offer — not orderable)" : string.Empty)}")
            .ToList();
        var options = documented.Count == 0
            ? $" Appendix D documents no {sku?.Trim().ToUpperInvariant()} tier at {packageDownloadMbps} Mbps — choose the Openserve product/speed Openserve confirmed for this package."
            : $" Appendix D documents: {string.Join("; ", documented)} — choose the one Openserve confirmed for this package.";
        return conflict + options;
    }

    private static PackageOpenserveMappingDto Map(PackageOpenserveMapping m) => new()
    {
        Id = m.Id,
        ServicePackageId = m.ServicePackageId,
        ServicePackageName = m.ServicePackage?.Name ?? string.Empty,
        OpenserveProductName = m.OpenserveProductName,
        Sku = m.Sku,
        Capacity = m.Capacity,
        CapacityUom = m.CapacityUom,
        IsEnabled = m.IsEnabled,
        OpenserveProductOfferingId = m.OpenserveProductOfferingId,
        OpenserveProductSpecificationId = m.OpenserveProductSpecificationId,
        Notes = m.Notes,
        CreatedAtUtc = m.CreatedAtUtc,
        UpdatedAtUtc = m.UpdatedAtUtc
    };
}
