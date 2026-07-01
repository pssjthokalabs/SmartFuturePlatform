using System.Text.RegularExpressions;
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

    // Cheap whitespace collapser used on both the rule.MatchText and the
    // request component. Runs O(n) with no allocations beyond the final
    // string.
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

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
        var excludes = await _dbContext.CoverageMapRules.AsNoTracking()
            .Where(r => r.IsActive && r.RuleType == CoverageMapRuleType.Exclude)
            .OrderBy(r => r.Priority).ThenBy(r => r.CreatedAtUtc)
            .ToListAsync(ct);
        var excludeHit = FindFirstMatch(excludes, request);
        if (excludeHit is not null)
        {
            _logger.LogInformation(
                "[CoverageMap] excluded by rule {RuleId} ({RuleName}) via {Component} = {Value}",
                excludeHit.MatchedRuleId, excludeHit.MatchedRuleName,
                excludeHit.MatchedComponent, excludeHit.MatchedValue);
            return excludeHit;
        }

        var includes = await _dbContext.CoverageMapRules.AsNoTracking()
            .Where(r => r.IsActive && r.RuleType == CoverageMapRuleType.Include)
            .OrderBy(r => r.Priority).ThenBy(r => r.CreatedAtUtc)
            .ToListAsync(ct);
        var includeHit = FindFirstMatch(includes, request);
        if (includeHit is not null)
        {
            _logger.LogInformation(
                "[CoverageMap] included by rule {RuleId} ({RuleName}) via {Component} = {Value}",
                includeHit.MatchedRuleId, includeHit.MatchedRuleName,
                includeHit.MatchedComponent, includeHit.MatchedValue);
            return includeHit;
        }

        return new CoverageMapEvaluationResult { Matched = false };
    }

    // ─── Match implementation ─────────────────────────────────────────

    private static CoverageMapEvaluationResult? FindFirstMatch(
        IReadOnlyList<CoverageMapRule> rules, CoverageCheckRequestDto req)
    {
        if (rules.Count == 0) return null;

        // Pre-build the (component, value) pairs the request carries so
        // we walk each rule at O(components) instead of re-hashing.
        var candidates = BuildCandidates(req);
        if (candidates.Count == 0) return null;

        foreach (var rule in rules)
        {
            var normText = NormalizeForMatch(rule.MatchText);
            if (string.IsNullOrEmpty(normText)) continue;

            foreach (var (component, rawValue) in candidates)
            {
                if (!rule.AllowedComponents.HasFlag(component)) continue;
                var normValue = NormalizeForMatch(rawValue);
                if (string.IsNullOrEmpty(normValue)) continue;
                if (!IsMatch(normValue, normText, rule.MatchMode)) continue;

                return new CoverageMapEvaluationResult
                {
                    Matched = true,
                    MatchedType = rule.RuleType,
                    MatchedRuleId = rule.Id,
                    MatchedRuleName = rule.Name,
                    MatchedComponent = component,
                    MatchedValue = rawValue,
                };
            }
        }
        return null;
    }

    private static bool IsMatch(string haystack, string needle, CoverageMatchMode mode)
        => mode switch
        {
            CoverageMatchMode.Exact => string.Equals(haystack, needle, StringComparison.Ordinal),
            CoverageMatchMode.StartsWith => haystack.StartsWith(needle, StringComparison.Ordinal),
            _ => haystack.Contains(needle, StringComparison.Ordinal),
        };

    private static List<(CoverageAddressMatchComponent Component, string RawValue)> BuildCandidates(
        CoverageCheckRequestDto req)
    {
        var list = new List<(CoverageAddressMatchComponent, string)>(12);
        Add(list, CoverageAddressMatchComponent.FullAddress,      req.AddressText);
        Add(list, CoverageAddressMatchComponent.AddressLine1,     req.AddressLine1);
        Add(list, CoverageAddressMatchComponent.AddressLine2,     req.AddressLine2);
        Add(list, CoverageAddressMatchComponent.StreetName,       req.StreetName);
        Add(list, CoverageAddressMatchComponent.Suburb,           req.Suburb);
        Add(list, CoverageAddressMatchComponent.City,             req.City);
        Add(list, CoverageAddressMatchComponent.Town,             req.Town);
        Add(list, CoverageAddressMatchComponent.Province,         req.Province);
        Add(list, CoverageAddressMatchComponent.PostalCode,       req.PostalCode);
        Add(list, CoverageAddressMatchComponent.Country,          req.Country);
        Add(list, CoverageAddressMatchComponent.FormattedAddress, req.FormattedAddress);
        Add(list, CoverageAddressMatchComponent.PlaceName,        req.PlaceName);
        return list;
    }

    private static void Add(
        List<(CoverageAddressMatchComponent, string)> list,
        CoverageAddressMatchComponent component, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) list.Add((component, value));
    }

    // trim → collapse whitespace → lowercase (invariant). This is the
    // ONE place normalisation happens; rule authoring in the admin UI
    // should preview against the same string.
    internal static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        var collapsed = WhitespaceRegex.Replace(trimmed, " ");
        return collapsed.ToLowerInvariant();
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
