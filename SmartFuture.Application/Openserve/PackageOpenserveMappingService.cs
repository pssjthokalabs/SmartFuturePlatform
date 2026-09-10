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
            var mappedPackageIds = _dbContext.PackageOpenserveMappings.AsNoTracking().Select(m => m.ServicePackageId);

            var unmapped = await _dbContext.ServicePackages
                .AsNoTracking()
                .Where(p => p.Type == ServicePackageType.Fibre && p.Status != ServicePackageStatus.Archived)
                .Where(p => !mappedPackageIds.Contains(p.Id))
                .OrderBy(p => p.Name)
                .Select(p => new UnmappedServicePackageDto
                {
                    ServicePackageId = p.Id,
                    Name = p.Name,
                    DownloadSpeedMbps = p.DownloadSpeedMbps,
                    SpeedLabel = p.SpeedLabel
                })
                .ToListAsync(cancellationToken);

            return Result<IReadOnlyList<UnmappedServicePackageDto>>.Success(unmapped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing unmapped fibre packages");
            return Result<IReadOnlyList<UnmappedServicePackageDto>>.Failure(ErrorCodes.EXCEPTION, "Could not list unmapped fibre packages.");
        }
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
        var validation = Validate(request.ServicePackageId, request.OpenserveProductName, request.Sku, request.Capacity, request.CapacityUom);
        if (validation is not null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.VALIDATION_ERROR, validation);

        var package = await _dbContext.ServicePackages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ServicePackageId, cancellationToken);
        if (package is null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.NOT_FOUND, "Service package not found.");

        var exists = await _dbContext.PackageOpenserveMappings.AnyAsync(m => m.ServicePackageId == request.ServicePackageId, cancellationToken);
        if (exists) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.CONFLICT, "This package already has an Openserve mapping — update it instead.");

        var entity = new PackageOpenserveMapping
        {
            Id = Guid.NewGuid(),
            ServicePackageId = request.ServicePackageId,
            OpenserveProductName = request.OpenserveProductName.Trim(),
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

        var validation = Validate(entity.ServicePackageId, request.OpenserveProductName, request.Sku, request.Capacity, request.CapacityUom);
        if (validation is not null) return Result<PackageOpenserveMappingDto>.Failure(ErrorCodes.VALIDATION_ERROR, validation);

        entity.OpenserveProductName = request.OpenserveProductName.Trim();
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

    private static string? Validate(Guid servicePackageId, string productName, string sku, string capacity, string capacityUom)
    {
        if (servicePackageId == Guid.Empty) return "ServicePackageId is required.";
        if (string.IsNullOrWhiteSpace(productName)) return "OpenserveProductName is required.";
        if (string.IsNullOrWhiteSpace(sku)) return "Sku is required.";
        if (string.IsNullOrWhiteSpace(capacity)) return "Capacity is required.";
        if (string.IsNullOrWhiteSpace(capacityUom)) return "CapacityUom is required.";

        if (!OpenserveProductCatalogue.IsKnownSku(sku))
            return $"'{sku}' is not a SKU documented in the Openserve Fulfilment API Spec Appendix D. Check for a typo, or confirm the SKU with Openserve before adding it here.";

        if (!OpenserveProductCatalogue.SkusWithoutPublishedSpeedTable.Contains(sku)
            && !OpenserveProductCatalogue.IsValidCombination(sku, capacity, capacityUom))
            return $"'{capacity} {capacityUom}' is not a documented valid speed for SKU '{sku}' (Appendix D). Check the capacity/unit, or confirm with Openserve if this is a new combination.";

        return null;
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
