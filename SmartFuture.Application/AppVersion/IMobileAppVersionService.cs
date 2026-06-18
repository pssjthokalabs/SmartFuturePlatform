using SmartFuture.Application.AppVersion.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.AppVersion;

/// <summary>
/// DB-backed mobile app-version policy: a public evaluated check + admin
/// CRUD over <c>MobileAppVersionRule</c>. Falls back to the legacy
/// appsettings <c>MobileAppVersion</c> policy when no DB rule matches, so
/// the public endpoint stays correct even before any rule is seeded.
/// </summary>
public interface IMobileAppVersionService
{
    /// <summary>
    /// Resolve the rule for (platform, channel) and evaluate it against the
    /// caller's version/build. Never throws on bad input — defaults are
    /// launch-safe (no accidental forced update).
    /// </summary>
    Task<MobileAppVersionCheckResponseDto> CheckAsync(
        string? platform, string? channel, string? version, int? buildNumber,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<MobileAppVersionRuleDto>>> GetRulesAsync(
        CancellationToken cancellationToken = default);

    Task<Result<MobileAppVersionRuleDto>> GetRuleByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<Result<MobileAppVersionRuleDto>> CreateRuleAsync(
        UpsertMobileAppVersionRuleRequestDto request, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task<Result<MobileAppVersionRuleDto>> UpdateRuleAsync(
        Guid id, UpsertMobileAppVersionRuleRequestDto request, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task<Result<MobileAppVersionRuleDto>> SetEnabledAsync(
        Guid id, bool enabled, Guid? actorUserId,
        CancellationToken cancellationToken = default);
}
