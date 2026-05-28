using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.NetworkAccounts.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.NetworkAccounts;

public class RadiusProfileService : IRadiusProfileService
{
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<RadiusProfileService> _logger;

    public RadiusProfileService(IAppDbContext dbContext, ILogger<RadiusProfileService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<RadiusProfileDto>>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _dbContext.RadiusProfiles.AsNoTracking().AsQueryable();
            if (activeOnly) query = query.Where(p => p.IsActive);
            var items = await query
                .OrderBy(p => p.Priority).ThenBy(p => p.Name)
                .Select(p => Map(p)).ToListAsync(cancellationToken);
            return Result<IReadOnlyList<RadiusProfileDto>>.Success(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing RadiusProfiles");
            return Result<IReadOnlyList<RadiusProfileDto>>.Failure(ErrorCodes.EXCEPTION, "Could not list RADIUS profiles.");
        }
    }

    public async Task<Result<RadiusProfileDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.RadiusProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return entity is null
            ? Result<RadiusProfileDto>.Failure(ErrorCodes.NOT_FOUND, "RADIUS profile not found.")
            : Result<RadiusProfileDto>.Success(Map(entity));
    }

    public async Task<Result<RadiusProfileDto>> CreateAsync(CreateRadiusProfileRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return Result<RadiusProfileDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Name is required.");
        if (request.DownloadMbps <= 0 || request.UploadMbps <= 0)
            return Result<RadiusProfileDto>.Failure(ErrorCodes.VALIDATION_ERROR, "DownloadMbps and UploadMbps must be > 0.");

        var name = request.Name.Trim();
        var exists = await _dbContext.RadiusProfiles.AnyAsync(p => p.Name == name, cancellationToken);
        if (exists) return Result<RadiusProfileDto>.Failure(ErrorCodes.CONFLICT, "A profile with this name already exists.");

        var entity = new RadiusProfile
        {
            Id = Guid.NewGuid(), Name = name,
            DownloadMbps = request.DownloadMbps, UploadMbps = request.UploadMbps,
            BurstMbps = request.BurstMbps, Priority = request.Priority,
            IsActive = request.IsActive, Notes = request.Notes?.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
        _dbContext.RadiusProfiles.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result<RadiusProfileDto>.Success(Map(entity), "RADIUS profile created.");
    }

    public async Task<Result<RadiusProfileDto>> UpdateAsync(Guid id, UpdateRadiusProfileRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return Result<RadiusProfileDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Name is required.");

        var entity = await _dbContext.RadiusProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null) return Result<RadiusProfileDto>.Failure(ErrorCodes.NOT_FOUND, "RADIUS profile not found.");

        var name = request.Name.Trim();
        if (entity.Name != name)
        {
            var conflict = await _dbContext.RadiusProfiles.AnyAsync(p => p.Name == name && p.Id != id, cancellationToken);
            if (conflict) return Result<RadiusProfileDto>.Failure(ErrorCodes.CONFLICT, "A profile with this name already exists.");
        }

        entity.Name = name;
        entity.DownloadMbps = request.DownloadMbps;
        entity.UploadMbps = request.UploadMbps;
        entity.BurstMbps = request.BurstMbps;
        entity.Priority = request.Priority;
        entity.IsActive = request.IsActive;
        entity.Notes = request.Notes?.Trim();
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result<RadiusProfileDto>.Success(Map(entity), "RADIUS profile updated.");
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.RadiusProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null) return Result.Failure(ErrorCodes.NOT_FOUND, "RADIUS profile not found.");

        var inUse = await _dbContext.ServicePackages.AnyAsync(p => p.RadiusProfileId == id, cancellationToken)
                 || await _dbContext.NetworkAccounts.AnyAsync(n => n.RadiusProfileId == id, cancellationToken);
        if (inUse)
        {
            entity.IsActive = false;
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success("RADIUS profile is in use; marked inactive instead of deleted.");
        }

        _dbContext.RadiusProfiles.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success("RADIUS profile deleted.");
    }

    private static RadiusProfileDto Map(RadiusProfile p) => new()
    {
        Id = p.Id, Name = p.Name,
        DownloadMbps = p.DownloadMbps, UploadMbps = p.UploadMbps,
        BurstMbps = p.BurstMbps, Priority = p.Priority,
        IsActive = p.IsActive, Notes = p.Notes,
        CreatedAtUtc = p.CreatedAtUtc, UpdatedAtUtc = p.UpdatedAtUtc
    };
}
