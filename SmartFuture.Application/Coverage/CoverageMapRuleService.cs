using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Coverage;
using SmartFuture.Shared.Enums.Coverage;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Coverage;

public class CoverageMapRuleService : ICoverageMapRuleService
{
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<CoverageMapRuleService> _logger;

    public CoverageMapRuleService(IAppDbContext dbContext, ILogger<CoverageMapRuleService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // ─── Admin CRUD ───────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<CoverageMapRuleDto>>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _dbContext.CoverageMapRules
            .AsNoTracking()
            .OrderBy(r => r.RuleType)
            .ThenBy(r => r.Priority)
            .ThenBy(r => r.CreatedAtUtc)
            .ToListAsync(ct);
        return Result<IReadOnlyList<CoverageMapRuleDto>>.Success(rows.Select(Map).ToList());
    }

    public async Task<Result<CoverageMapRuleDto>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _dbContext.CoverageMapRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        return row is null
            ? Result<CoverageMapRuleDto>.Failure(ErrorCodes.NOT_FOUND, "Coverage map rule not found.")
            : Result<CoverageMapRuleDto>.Success(Map(row));
    }

    public async Task<Result<CoverageMapRuleDto>> CreateAsync(UpsertCoverageMapRuleRequestDto request, CancellationToken ct = default)
    {
        var validation = ValidateRequest(request);
        if (validation is not null) return validation;

        var entity = new CoverageMapRule
        {
            Name = request.Name!.Trim(),
            MatchText = request.MatchText!.Trim(),
            RuleType = request.RuleType,
            MatchMode = request.MatchMode,
            AllowedComponents = ResolveAllowedComponents(request.AllowedComponents),
            IsActive = request.IsActive,
            Priority = request.Priority,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        };
        _dbContext.CoverageMapRules.Add(entity);
        await _dbContext.SaveChangesAsync(ct);
        return Result<CoverageMapRuleDto>.Success(Map(entity), "Coverage map rule created.");
    }

    public async Task<Result<CoverageMapRuleDto>> UpdateAsync(Guid id, UpsertCoverageMapRuleRequestDto request, CancellationToken ct = default)
    {
        var validation = ValidateRequest(request);
        if (validation is not null) return validation;

        var entity = await _dbContext.CoverageMapRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null)
            return Result<CoverageMapRuleDto>.Failure(ErrorCodes.NOT_FOUND, "Coverage map rule not found.");

        entity.Name = request.Name!.Trim();
        entity.MatchText = request.MatchText!.Trim();
        entity.RuleType = request.RuleType;
        entity.MatchMode = request.MatchMode;
        entity.AllowedComponents = ResolveAllowedComponents(request.AllowedComponents);
        entity.IsActive = request.IsActive;
        entity.Priority = request.Priority;
        entity.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(ct);
        return Result<CoverageMapRuleDto>.Success(Map(entity), "Coverage map rule updated.");
    }

    public async Task<Result<CoverageMapRuleDto>> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        var entity = await _dbContext.CoverageMapRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null)
            return Result<CoverageMapRuleDto>.Failure(ErrorCodes.NOT_FOUND, "Coverage map rule not found.");
        if (entity.IsActive == isActive)
            return Result<CoverageMapRuleDto>.Success(Map(entity), isActive ? "Already active." : "Already inactive.");

        entity.IsActive = isActive;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
        return Result<CoverageMapRuleDto>.Success(Map(entity), isActive ? "Rule enabled." : "Rule disabled.");
    }

    // ─── Runtime evaluation ───────────────────────────────────────────

    public async Task<CoverageMapEvaluationResult> TryEvaluateAsync(
        CoverageCheckRequestDto request, CancellationToken ct = default)
    {
        if (request is null) return new CoverageMapEvaluationResult { Matched = false };

        // Excludes must be evaluated FIRST — a narrow excluded suburb
        // inside a broadly-included city has to win. Both passes read
        // the same compound index (IsActive, RuleType, Priority).
        // Matching itself is delegated to CoverageMapRuleMatcher so the
        // admin test-match endpoint (see AdminCoverageMapController) and
        // this public path share EXACTLY the same rules.
        var excludes = await _dbContext.CoverageMapRules.AsNoTracking()
            .Where(r => r.IsActive && r.RuleType == CoverageMapRuleType.Exclude)
            .OrderBy(r => r.Priority).ThenBy(r => r.CreatedAtUtc)
            .ToListAsync(ct);
        var excludeHit = CoverageMapRuleMatcher.FindFirstMatch(excludes, request);
        if (excludeHit is not null)
        {
            LogMatch("[CoverageMap] excluded", excludeHit, request);
            return excludeHit;
        }

        var includes = await _dbContext.CoverageMapRules.AsNoTracking()
            .Where(r => r.IsActive && r.RuleType == CoverageMapRuleType.Include)
            .OrderBy(r => r.Priority).ThenBy(r => r.CreatedAtUtc)
            .ToListAsync(ct);
        var includeHit = CoverageMapRuleMatcher.FindFirstMatch(includes, request);
        if (includeHit is not null)
        {
            LogMatch("[CoverageMap] included", includeHit, request);
            return includeHit;
        }

        return new CoverageMapEvaluationResult { Matched = false };
    }

    // Structured, safe evaluation-decision log. Emits the rule id/name,
    // the component that tripped, the ALL allowed components, and every
    // structured component the request carried. Values are logged
    // individually (not the full formatted address / addressText) so
    // production logs stay clean of visitor PII — a coincidental Full
    // Address containing personal info doesn't get spilled unless the
    // rule intentionally opted in to FullAddress matching.
    private void LogMatch(string tag, CoverageMapEvaluationResult hit, CoverageCheckRequestDto req)
    {
        _logger.LogInformation(
            "{Tag} by rule {RuleId} ({RuleName}) via {Component} = {Value}. " +
            "Suburb={Suburb} City={City} Town={Town} Province={Province} " +
            "PostalCode={PostalCode} Country={Country}. OpenserveSkipped=true",
            tag,
            hit.MatchedRuleId, hit.MatchedRuleName,
            hit.MatchedComponent, hit.MatchedValue,
            req.Suburb, req.City, req.Town, req.Province,
            req.PostalCode, req.Country);
    }

    private static CoverageAddressMatchComponent ResolveAllowedComponents(CoverageAddressMatchComponent? requested)
    {
        // Missing or None → apply the SafeAreaDefault so a new rule
        // never lands with permissive full-address matching by accident.
        if (!requested.HasValue || requested.Value == CoverageAddressMatchComponent.None)
            return CoverageAddressMatchComponent.SafeAreaDefault;
        return requested.Value;
    }

    private static Result<CoverageMapRuleDto>? ValidateRequest(UpsertCoverageMapRuleRequestDto? req)
    {
        if (req is null)
            return Result<CoverageMapRuleDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");
        if (string.IsNullOrWhiteSpace(req.Name))
            return Result<CoverageMapRuleDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Name is required.");
        if (string.IsNullOrWhiteSpace(req.MatchText))
            return Result<CoverageMapRuleDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Match text is required.");
        return null;
    }

    private static CoverageMapRuleDto Map(CoverageMapRule r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        MatchText = r.MatchText,
        RuleType = r.RuleType,
        MatchMode = r.MatchMode,
        AllowedComponents = r.AllowedComponents,
        IsActive = r.IsActive,
        Priority = r.Priority,
        Notes = r.Notes,
        CreatedAtUtc = r.CreatedAtUtc,
        UpdatedAtUtc = r.UpdatedAtUtc,
    };
}
