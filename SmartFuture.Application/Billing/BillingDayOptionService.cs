using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Billing;

public class BillingDayOptionService : IBillingDayOptionService
{
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<BillingDayOptionService> _logger;
    private readonly BillingSettings _settings;

    public BillingDayOptionService(IAppDbContext dbContext, ILogger<BillingDayOptionService> logger, IOptions<BillingSettings> settings)
    {
        _dbContext = dbContext;
        _logger = logger;
        _settings = settings.Value;
    }

    public async Task<Result<IReadOnlyList<BillingDayOptionDto>>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.BillingDayOptions
            .AsNoTracking()
            .OrderBy(o => o.DisplayOrder)
            .ThenBy(o => o.Day)
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyList<BillingDayOptionDto>>.Success(rows.Select(Map).ToList());
    }

    public async Task<Result<IReadOnlyList<BillingDayOptionDto>>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.BillingDayOptions
            .AsNoTracking()
            .Where(o => o.IsEnabled)
            .OrderBy(o => o.DisplayOrder)
            .ThenBy(o => o.Day)
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyList<BillingDayOptionDto>>.Success(rows.Select(Map).ToList());
    }

    public async Task<Result<BillingDayOptionDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.BillingDayOptions.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        return row is null
            ? Result<BillingDayOptionDto>.Failure(ErrorCodes.NOT_FOUND, "Billing day option not found.")
            : Result<BillingDayOptionDto>.Success(Map(row));
    }

    public async Task<Result<BillingDayOptionDto>> CreateAsync(CreateBillingDayOptionRequestDto request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateDay(request?.Day);
        if (validation is not null) return validation;

        var taken = await _dbContext.BillingDayOptions
            .AnyAsync(o => o.Day == request!.Day, cancellationToken);
        if (taken)
            return Result<BillingDayOptionDto>.Failure(
                ErrorCodes.CONFLICT, $"A billing day option for the {request!.Day}th already exists.");

        var entity = new BillingDayOption
        {
            Day = request!.Day,
            Label = ResolveLabel(request.Label, request.Day),
            IsEnabled = request.IsEnabled,
            IsDefault = request.IsDefault,
            DisplayOrder = request.DisplayOrder,
        };
        _dbContext.BillingDayOptions.Add(entity);

        if (request.IsDefault)
            await ClearOtherDefaultsAsync(exceptId: null, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result<BillingDayOptionDto>.Success(Map(entity), "Billing day option created.");
    }

    public async Task<Result<BillingDayOptionDto>> UpdateAsync(Guid id, UpdateBillingDayOptionRequestDto request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateDay(request?.Day);
        if (validation is not null) return validation;

        var entity = await _dbContext.BillingDayOptions
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (entity is null)
            return Result<BillingDayOptionDto>.Failure(ErrorCodes.NOT_FOUND, "Billing day option not found.");

        if (entity.Day != request!.Day)
        {
            var taken = await _dbContext.BillingDayOptions
                .AnyAsync(o => o.Id != id && o.Day == request.Day, cancellationToken);
            if (taken)
                return Result<BillingDayOptionDto>.Failure(
                    ErrorCodes.CONFLICT, $"A billing day option for the {request.Day}th already exists.");
        }

        entity.Day = request.Day;
        entity.Label = ResolveLabel(request.Label, request.Day);
        entity.IsEnabled = request.IsEnabled;
        entity.IsDefault = request.IsDefault;
        entity.DisplayOrder = request.DisplayOrder;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        if (request.IsDefault)
            await ClearOtherDefaultsAsync(exceptId: id, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result<BillingDayOptionDto>.Success(Map(entity), "Billing day option updated.");
    }

    public async Task<Result<BillingDayOptionDto>> ValidateForCheckoutAsync(int day, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.BillingDayOptions.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Day == day, cancellationToken);
        if (row is null)
            return Result<BillingDayOptionDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                $"Billing day {day} is not one of the offered options.");
        if (!row.IsEnabled)
            return Result<BillingDayOptionDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                $"Billing day {day} is currently disabled — pick a different day.");
        return Result<BillingDayOptionDto>.Success(Map(row));
    }

    public async Task<int> ResolveDefaultBillingDayAsync(CancellationToken cancellationToken = default)
    {
        // Prefer an enabled, IsDefault-flagged row; fall back to any
        // enabled row; final fallback to the appsettings default (30).
        var defaultDay = await _dbContext.BillingDayOptions.AsNoTracking()
            .Where(o => o.IsEnabled && o.IsDefault)
            .OrderBy(o => o.DisplayOrder)
            .Select(o => (int?)o.Day)
            .FirstOrDefaultAsync(cancellationToken);
        if (defaultDay.HasValue) return defaultDay.Value;

        var anyEnabled = await _dbContext.BillingDayOptions.AsNoTracking()
            .Where(o => o.IsEnabled)
            .OrderBy(o => o.DisplayOrder)
            .Select(o => (int?)o.Day)
            .FirstOrDefaultAsync(cancellationToken);
        return anyEnabled ?? _settings.DefaultBillingDay;
    }

    private async Task ClearOtherDefaultsAsync(Guid? exceptId, CancellationToken cancellationToken)
    {
        var others = await _dbContext.BillingDayOptions
            .Where(o => o.IsDefault && (!exceptId.HasValue || o.Id != exceptId.Value))
            .ToListAsync(cancellationToken);
        foreach (var o in others)
        {
            o.IsDefault = false;
            o.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static Result<BillingDayOptionDto>? ValidateDay(int? day)
    {
        if (!day.HasValue)
            return Result<BillingDayOptionDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Day is required.");
        if (day.Value < 1 || day.Value > 31)
            return Result<BillingDayOptionDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Day must be between 1 and 31.");
        return null;
    }

    private static string ResolveLabel(string? label, int day)
    {
        if (!string.IsNullOrWhiteSpace(label)) return label.Trim();
        var suffix = day switch
        {
            1 or 21 or 31 => "st",
            2 or 22 => "nd",
            3 or 23 => "rd",
            _ => "th",
        };
        return $"{day}{suffix} of the month";
    }

    private static BillingDayOptionDto Map(BillingDayOption o) => new()
    {
        Id = o.Id,
        Day = o.Day,
        Label = o.Label,
        IsEnabled = o.IsEnabled,
        IsDefault = o.IsDefault,
        DisplayOrder = o.DisplayOrder,
        CreatedAtUtc = o.CreatedAtUtc,
        UpdatedAtUtc = o.UpdatedAtUtc,
    };
}
