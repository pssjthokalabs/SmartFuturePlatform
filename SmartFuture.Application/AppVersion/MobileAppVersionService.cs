using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartFuture.Application.AppVersion.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.AppVersion;
using SmartFuture.Shared.Enums.AppVersion;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.AppVersion;

/// <summary>
/// DB-backed implementation. Resolves a rule by (platform, channel) with a
/// Generic-channel fallback, then an appsettings fallback, and evaluates it
/// against the caller's version/build. Admin CRUD enforces one rule per
/// (platform, channel). Launch-safe: a missing version or rule never forces
/// an update.
/// </summary>
public sealed class MobileAppVersionService : IMobileAppVersionService
{
    private readonly IAppDbContext _dbContext;
    private readonly MobileAppVersionSettings _fallback;

    public MobileAppVersionService(IAppDbContext dbContext, IOptions<MobileAppVersionSettings> fallback)
    {
        _dbContext = dbContext;
        _fallback = fallback.Value;
    }

    // ─── Public evaluated check ─────────────────────────────────────

    public async Task<MobileAppVersionCheckResponseDto> CheckAsync(
        string? platform, string? channel, string? version, int? buildNumber,
        CancellationToken cancellationToken = default)
    {
        var p = ParsePlatform(platform);
        var c = ParseChannel(channel, p);

        var rules = await _dbContext.MobileAppVersionRules
            .AsNoTracking()
            .Where(r => r.IsEnabled)
            .ToListAsync(cancellationToken);

        // Requested rule: exact channel, then Generic for the platform.
        var rule = rules.FirstOrDefault(r => r.Platform == p && r.Channel == c)
                ?? rules.FirstOrDefault(r => r.Platform == p && r.Channel == MobileAppChannel.Generic);

        var dto = new MobileAppVersionCheckResponseDto
        {
            Platform = PlatformToWire(p),
            Channel = ChannelToWire(c),
            ForceMessage = _fallback.ForceMessage,
        };

        if (rule is not null)
        {
            var (updateRequired, updateAvailable) = Evaluate(rule, version, buildNumber);
            dto.LatestVersion = rule.LatestVersion;
            dto.LatestBuildNumber = rule.LatestBuildNumber;
            dto.MinimumSupportedVersion = rule.MinimumSupportedVersion;
            dto.MinimumSupportedBuildNumber = rule.MinimumSupportedBuildNumber;
            dto.UpdateRequired = updateRequired;
            dto.UpdateAvailable = updateAvailable;
            dto.Title = updateRequired ? "Update required" : rule.Title;
            dto.Message = rule.Message;
            dto.PrimaryButtonText = rule.PrimaryButtonText;
            // Forced updates are non-dismissible — no secondary action.
            dto.SecondaryButtonText = updateRequired ? null : rule.SecondaryButtonText;
            dto.StoreUrl = rule.StoreUrl;
            dto.ReleaseNotes = rule.ReleaseNotes;
        }
        else
        {
            // No DB rule — fall back to the appsettings policy for the
            // requested platform so the endpoint stays correct pre-seed.
            var fb = p == MobileAppPlatform.iOS ? _fallback.Ios : _fallback.Android;
            var (updateRequired, updateAvailable) = EvaluateFallback(fb, version, buildNumber);
            dto.LatestVersion = fb.CurrentVersion;
            dto.LatestBuildNumber = fb.CurrentBuildNumber;
            dto.MinimumSupportedVersion = fb.MinimumVersion;
            dto.MinimumSupportedBuildNumber = fb.MinimumBuildNumber;
            dto.UpdateRequired = updateRequired;
            dto.UpdateAvailable = updateAvailable;
            dto.Title = updateRequired ? "Update required" : "Update available";
            dto.Message = updateRequired ? _fallback.ForceMessage : _fallback.Message;
            dto.PrimaryButtonText = "Update app";
            dto.SecondaryButtonText = updateRequired ? null : "Later";
            dto.StoreUrl = fb.StoreUrl;
        }

        // ─── Legacy block (old builds compare client-side) ──────────
        dto.Android = BuildLegacy(rules, MobileAppPlatform.Android, MobileAppChannel.Google, _fallback.Android);
        dto.Ios = BuildLegacy(rules, MobileAppPlatform.iOS, MobileAppChannel.Apple, _fallback.Ios);
        return dto;
    }

    private static (bool required, bool available) Evaluate(MobileAppVersionRule rule, string? version, int? buildNumber)
    {
        var hasBuild = buildNumber is > 0;

        bool requiredByGate;
        if (hasBuild && rule.MinimumSupportedBuildNumber > 0)
            requiredByGate = buildNumber!.Value < rule.MinimumSupportedBuildNumber;   // build priority
        else if (!string.IsNullOrWhiteSpace(version))
            requiredByGate = SemanticVersionComparer.IsLessThan(version, rule.MinimumSupportedVersion);
        else
            requiredByGate = false; // unknown version → never force

        var required = rule.UpdateRequired || requiredByGate;

        bool availableByGate;
        if (hasBuild && rule.LatestBuildNumber > 0)
            availableByGate = buildNumber!.Value < rule.LatestBuildNumber;
        else if (!string.IsNullOrWhiteSpace(version))
            availableByGate = SemanticVersionComparer.IsLessThan(version, rule.LatestVersion);
        else
            availableByGate = false;

        var available = required || rule.UpdateAvailable || availableByGate;
        return (required, available);
    }

    private static (bool required, bool available) EvaluateFallback(MobileAppPlatformSettings fb, string? version, int? buildNumber)
    {
        var hasBuild = buildNumber is > 0;
        var required = fb.ForceUpdate
            || (hasBuild && fb.MinimumBuildNumber > 0 && buildNumber!.Value < fb.MinimumBuildNumber)
            || (!string.IsNullOrWhiteSpace(version) && SemanticVersionComparer.IsLessThan(version, fb.MinimumVersion));
        var available = required
            || (hasBuild && fb.CurrentBuildNumber > 0 && buildNumber!.Value < fb.CurrentBuildNumber)
            || (!string.IsNullOrWhiteSpace(version) && SemanticVersionComparer.IsLessThan(version, fb.CurrentVersion));
        return (required, available);
    }

    private static LegacyPlatformPolicyDto BuildLegacy(
        List<MobileAppVersionRule> rules, MobileAppPlatform platform, MobileAppChannel preferred, MobileAppPlatformSettings fb)
    {
        var rule = rules.FirstOrDefault(r => r.Platform == platform && r.Channel == preferred)
                ?? rules.FirstOrDefault(r => r.Platform == platform && r.Channel == MobileAppChannel.Generic)
                ?? rules.FirstOrDefault(r => r.Platform == platform);
        if (rule is null)
        {
            return new LegacyPlatformPolicyDto
            {
                CurrentVersion = fb.CurrentVersion,
                MinimumVersion = fb.MinimumVersion,
                CurrentBuildNumber = fb.CurrentBuildNumber,
                MinimumBuildNumber = fb.MinimumBuildNumber,
                ForceUpdate = fb.ForceUpdate,
                StoreUrl = fb.StoreUrl
            };
        }
        return new LegacyPlatformPolicyDto
        {
            CurrentVersion = rule.LatestVersion,
            MinimumVersion = rule.MinimumSupportedVersion,
            CurrentBuildNumber = rule.LatestBuildNumber,
            MinimumBuildNumber = rule.MinimumSupportedBuildNumber,
            ForceUpdate = rule.UpdateRequired,
            StoreUrl = rule.StoreUrl
        };
    }

    // ─── Admin CRUD ─────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<MobileAppVersionRuleDto>>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.MobileAppVersionRules
            .AsNoTracking()
            .OrderBy(r => r.Platform).ThenBy(r => r.Channel)
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyList<MobileAppVersionRuleDto>>.Success(rows.Select(Map).ToList());
    }

    public async Task<Result<MobileAppVersionRuleDto>> GetRuleByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.MobileAppVersionRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return row is null
            ? Result<MobileAppVersionRuleDto>.Failure(ErrorCodes.NOT_FOUND, "Version rule not found.")
            : Result<MobileAppVersionRuleDto>.Success(Map(row));
    }

    public async Task<Result<MobileAppVersionRuleDto>> CreateRuleAsync(
        UpsertMobileAppVersionRuleRequestDto request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var validation = Validate(request);
        if (validation is not null)
            return Result<MobileAppVersionRuleDto>.Failure(ErrorCodes.VALIDATION_ERROR, validation);

        var exists = await _dbContext.MobileAppVersionRules
            .AnyAsync(r => r.Platform == request.Platform && r.Channel == request.Channel, cancellationToken);
        if (exists)
            return Result<MobileAppVersionRuleDto>.Failure(ErrorCodes.CONFLICT,
                $"A rule for {request.Platform}/{request.Channel} already exists. Edit it instead.");

        var entity = new MobileAppVersionRule { Platform = request.Platform, Channel = request.Channel };
        Apply(entity, request);
        entity.UpdatedByUserId = actorUserId;
        _dbContext.MobileAppVersionRules.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result<MobileAppVersionRuleDto>.Success(Map(entity), "Rule created.");
    }

    public async Task<Result<MobileAppVersionRuleDto>> UpdateRuleAsync(
        Guid id, UpsertMobileAppVersionRuleRequestDto request, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var validation = Validate(request);
        if (validation is not null)
            return Result<MobileAppVersionRuleDto>.Failure(ErrorCodes.VALIDATION_ERROR, validation);

        var entity = await _dbContext.MobileAppVersionRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (entity is null)
            return Result<MobileAppVersionRuleDto>.Failure(ErrorCodes.NOT_FOUND, "Version rule not found.");

        // Platform/channel are the natural key — guard against colliding with another row.
        var collision = await _dbContext.MobileAppVersionRules.AnyAsync(
            r => r.Id != id && r.Platform == request.Platform && r.Channel == request.Channel, cancellationToken);
        if (collision)
            return Result<MobileAppVersionRuleDto>.Failure(ErrorCodes.CONFLICT,
                $"Another rule for {request.Platform}/{request.Channel} already exists.");

        entity.Platform = request.Platform;
        entity.Channel = request.Channel;
        Apply(entity, request);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedByUserId = actorUserId;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result<MobileAppVersionRuleDto>.Success(Map(entity), "Rule updated.");
    }

    public async Task<Result<MobileAppVersionRuleDto>> SetEnabledAsync(
        Guid id, bool enabled, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.MobileAppVersionRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (entity is null)
            return Result<MobileAppVersionRuleDto>.Failure(ErrorCodes.NOT_FOUND, "Version rule not found.");
        entity.IsEnabled = enabled;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedByUserId = actorUserId;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result<MobileAppVersionRuleDto>.Success(Map(entity), enabled ? "Rule activated." : "Rule deactivated.");
    }

    // ─── helpers ────────────────────────────────────────────────────

    private static string? Validate(UpsertMobileAppVersionRuleRequestDto r)
    {
        if (string.IsNullOrWhiteSpace(r.LatestVersion)) return "Latest version is required.";
        if (string.IsNullOrWhiteSpace(r.MinimumSupportedVersion)) return "Minimum supported version is required.";
        if (r.LatestBuildNumber < 0 || r.MinimumSupportedBuildNumber < 0) return "Build numbers cannot be negative.";
        if (string.IsNullOrWhiteSpace(r.Title)) return "Title is required.";
        if (string.IsNullOrWhiteSpace(r.Message)) return "Message is required.";
        if (string.IsNullOrWhiteSpace(r.PrimaryButtonText)) return "Primary button text is required.";
        return null;
    }

    private static void Apply(MobileAppVersionRule e, UpsertMobileAppVersionRuleRequestDto r)
    {
        e.LatestVersion = r.LatestVersion.Trim();
        e.LatestBuildNumber = r.LatestBuildNumber;
        e.MinimumSupportedVersion = r.MinimumSupportedVersion.Trim();
        e.MinimumSupportedBuildNumber = r.MinimumSupportedBuildNumber;
        e.UpdateRequired = r.UpdateRequired;
        e.UpdateAvailable = r.UpdateAvailable;
        e.IsEnabled = r.IsEnabled;
        e.Title = r.Title.Trim();
        e.Message = r.Message.Trim();
        e.PrimaryButtonText = r.PrimaryButtonText.Trim();
        e.SecondaryButtonText = string.IsNullOrWhiteSpace(r.SecondaryButtonText) ? null : r.SecondaryButtonText.Trim();
        e.StoreUrl = r.StoreUrl?.Trim() ?? string.Empty;
        e.ReleaseNotes = string.IsNullOrWhiteSpace(r.ReleaseNotes) ? null : r.ReleaseNotes.Trim();
    }

    private static MobileAppVersionRuleDto Map(MobileAppVersionRule r) => new()
    {
        Id = r.Id,
        Platform = r.Platform,
        Channel = r.Channel,
        LatestVersion = r.LatestVersion,
        LatestBuildNumber = r.LatestBuildNumber,
        MinimumSupportedVersion = r.MinimumSupportedVersion,
        MinimumSupportedBuildNumber = r.MinimumSupportedBuildNumber,
        UpdateRequired = r.UpdateRequired,
        UpdateAvailable = r.UpdateAvailable,
        IsEnabled = r.IsEnabled,
        Title = r.Title,
        Message = r.Message,
        PrimaryButtonText = r.PrimaryButtonText,
        SecondaryButtonText = r.SecondaryButtonText,
        StoreUrl = r.StoreUrl,
        ReleaseNotes = r.ReleaseNotes,
        CreatedAtUtc = r.CreatedAtUtc,
        UpdatedAtUtc = r.UpdatedAtUtc,
        UpdatedByUserId = r.UpdatedByUserId
    };

    private static MobileAppPlatform ParsePlatform(string? value) =>
        string.Equals(value, "ios", StringComparison.OrdinalIgnoreCase) ? MobileAppPlatform.iOS : MobileAppPlatform.Android;

    private static MobileAppChannel ParseChannel(string? value, MobileAppPlatform platform)
    {
        if (string.Equals(value, "huawei", StringComparison.OrdinalIgnoreCase)) return MobileAppChannel.Huawei;
        if (string.Equals(value, "apple", StringComparison.OrdinalIgnoreCase)) return MobileAppChannel.Apple;
        if (string.Equals(value, "generic", StringComparison.OrdinalIgnoreCase)) return MobileAppChannel.Generic;
        if (string.Equals(value, "google", StringComparison.OrdinalIgnoreCase)) return MobileAppChannel.Google;
        // No/unknown channel → infer from platform.
        return platform == MobileAppPlatform.iOS ? MobileAppChannel.Apple : MobileAppChannel.Google;
    }

    private static string PlatformToWire(MobileAppPlatform p) => p == MobileAppPlatform.iOS ? "ios" : "android";

    private static string ChannelToWire(MobileAppChannel c) => c switch
    {
        MobileAppChannel.Huawei => "huawei",
        MobileAppChannel.Apple => "apple",
        MobileAppChannel.Generic => "generic",
        _ => "google"
    };
}
