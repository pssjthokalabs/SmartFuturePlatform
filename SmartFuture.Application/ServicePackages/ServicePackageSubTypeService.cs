using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Persistence;
using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.ServicePackages;

// DB-driven service-package subtypes (Security → CCTV / Intercom / …).
// Admin-managed via the ServicePackageSubTypesController; the package
// form's subtype dropdown reads ListAsync(Security, activeOnly:true) and
// the inline "+ Add new security type" posts CreateAsync.
public class ServicePackageSubTypeService : IServicePackageSubTypeService
{
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<ServicePackageSubTypeService> _logger;

    public ServicePackageSubTypeService(IAppDbContext dbContext, ILogger<ServicePackageSubTypeService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<List<ServicePackageSubTypeDto>>> ListAsync(
        ServicePackageType? packageType, bool activeOnly, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _dbContext.ServicePackageSubTypes.AsNoTracking().AsQueryable();
            if (packageType.HasValue)
                query = query.Where(s => s.PackageType == packageType.Value);
            if (activeOnly)
                query = query.Where(s => s.IsActive);

            var rows = await query
                .OrderBy(s => s.DisplayOrder)
                .ThenBy(s => s.Name)
                .Select(s => MapToDto(s))
                .ToListAsync(cancellationToken);

            return Result<List<ServicePackageSubTypeDto>>.Success(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing service package subtypes");
            return Result<List<ServicePackageSubTypeDto>>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while listing subtypes.");
        }
    }

    public async Task<Result<ServicePackageSubTypeDto>> CreateAsync(
        CreateServicePackageSubTypeRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null)
                return Result<ServicePackageSubTypeDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var name = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Result<ServicePackageSubTypeDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Name is required.");

            var slug = Slugify(string.IsNullOrWhiteSpace(request.Slug) ? name : request.Slug!);
            if (string.IsNullOrWhiteSpace(slug))
                return Result<ServicePackageSubTypeDto>.Failure(ErrorCodes.VALIDATION_ERROR, "A valid slug could not be derived from the name.");

            // Slug unique per package line (matches the DB unique index).
            var slugTaken = await _dbContext.ServicePackageSubTypes
                .AnyAsync(s => s.PackageType == request.PackageType && s.Slug == slug, cancellationToken);
            if (slugTaken)
                return Result<ServicePackageSubTypeDto>.Failure(
                    ErrorCodes.CONFLICT, $"A subtype with slug '{slug}' already exists for this package type.");

            var entity = new ServicePackageSubType
            {
                PackageType = request.PackageType,
                Name = name,
                Slug = slug,
                IsActive = request.IsActive,
                DisplayOrder = request.DisplayOrder
            };
            _dbContext.ServicePackageSubTypes.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<ServicePackageSubTypeDto>.Success(MapToDto(entity), "Security type created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating service package subtype");
            return Result<ServicePackageSubTypeDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while creating the subtype.");
        }
    }

    public async Task<Result<ServicePackageSubTypeDto>> UpdateAsync(
        Guid id, UpdateServicePackageSubTypeRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (id == Guid.Empty)
                return Result<ServicePackageSubTypeDto>.Failure(ErrorCodes.BAD_REQUEST, "Subtype id is required.");
            if (request is null)
                return Result<ServicePackageSubTypeDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var entity = await _dbContext.ServicePackageSubTypes
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (entity is null)
                return Result<ServicePackageSubTypeDto>.Failure(ErrorCodes.NOT_FOUND, "Subtype not found.");

            var name = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Result<ServicePackageSubTypeDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Name is required.");

            var slug = Slugify(string.IsNullOrWhiteSpace(request.Slug) ? name : request.Slug!);
            if (string.IsNullOrWhiteSpace(slug))
                return Result<ServicePackageSubTypeDto>.Failure(ErrorCodes.VALIDATION_ERROR, "A valid slug could not be derived from the name.");

            var slugTaken = await _dbContext.ServicePackageSubTypes
                .AnyAsync(s => s.Id != id && s.PackageType == entity.PackageType && s.Slug == slug, cancellationToken);
            if (slugTaken)
                return Result<ServicePackageSubTypeDto>.Failure(
                    ErrorCodes.CONFLICT, $"A subtype with slug '{slug}' already exists for this package type.");

            entity.Name = name;
            entity.Slug = slug;
            entity.IsActive = request.IsActive;
            entity.DisplayOrder = request.DisplayOrder;

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Result<ServicePackageSubTypeDto>.Success(MapToDto(entity), "Security type updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating service package subtype {Id}", id);
            return Result<ServicePackageSubTypeDto>.Failure(
                ErrorCodes.EXCEPTION, "An unexpected error occurred while updating the subtype.");
        }
    }

    private static ServicePackageSubTypeDto MapToDto(ServicePackageSubType s) => new()
    {
        Id = s.Id,
        PackageType = s.PackageType,
        Name = s.Name,
        Slug = s.Slug,
        IsActive = s.IsActive,
        DisplayOrder = s.DisplayOrder,
        CreatedAtUtc = s.CreatedAtUtc,
        UpdatedAtUtc = s.UpdatedAtUtc
    };

    // Lowercase, ASCII-ish, hyphen-separated slug. Keeps a-z/0-9, turns
    // any run of other chars into a single hyphen, trims leading/trailing
    // hyphens. "CCTV" → "cctv", "Smart Intercom!" → "smart-intercom".
    private static string Slugify(string input)
    {
        var sb = new StringBuilder(input.Length);
        var lastHyphen = false;
        foreach (var ch in input.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastHyphen = false;
            }
            else if (!lastHyphen && sb.Length > 0)
            {
                sb.Append('-');
                lastHyphen = true;
            }
        }
        return sb.ToString().Trim('-');
    }
}
