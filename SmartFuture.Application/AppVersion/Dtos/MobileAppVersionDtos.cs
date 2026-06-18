using SmartFuture.Shared.Enums.AppVersion;

namespace SmartFuture.Application.AppVersion.Dtos;

/// <summary>
/// Public version-check response. Carries BOTH the new evaluated (flat)
/// shape AND the legacy <c>android</c>/<c>ios</c>/<c>message</c>/<c>forceMessage</c>
/// block, so old app builds (which compare client-side) and new builds
/// (which read the server-evaluated flags) both work off one payload.
/// </summary>
public sealed class MobileAppVersionCheckResponseDto
{
    // ─── New evaluated (flat) shape ─────────────────────────────────
    public string Platform { get; set; } = "android";
    public string Channel { get; set; } = "google";
    public string LatestVersion { get; set; } = "1.0.0";
    public int LatestBuildNumber { get; set; }
    public string MinimumSupportedVersion { get; set; } = "1.0.0";
    public int MinimumSupportedBuildNumber { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool UpdateRequired { get; set; }
    public string Title { get; set; } = "Update available";
    public string Message { get; set; } = string.Empty;
    public string PrimaryButtonText { get; set; } = "Update app";
    public string? SecondaryButtonText { get; set; }
    public string StoreUrl { get; set; } = string.Empty;
    public string? ReleaseNotes { get; set; }

    // ─── Legacy backward-safe block (old builds read these) ─────────
    public LegacyPlatformPolicyDto Android { get; set; } = new();
    public LegacyPlatformPolicyDto Ios { get; set; } = new();
    public string ForceMessage { get; set; } = string.Empty;
}

/// <summary>Legacy per-platform policy shape (matches the old appsettings contract).</summary>
public sealed class LegacyPlatformPolicyDto
{
    public string CurrentVersion { get; set; } = "1.0.0";
    public string MinimumVersion { get; set; } = "1.0.0";
    public int CurrentBuildNumber { get; set; }
    public int MinimumBuildNumber { get; set; }
    public bool ForceUpdate { get; set; }
    public string StoreUrl { get; set; } = string.Empty;
}

/// <summary>Admin read model for a version rule.</summary>
public sealed class MobileAppVersionRuleDto
{
    public Guid Id { get; set; }
    public MobileAppPlatform Platform { get; set; }
    public MobileAppChannel Channel { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public int LatestBuildNumber { get; set; }
    public string MinimumSupportedVersion { get; set; } = string.Empty;
    public int MinimumSupportedBuildNumber { get; set; }
    public bool UpdateRequired { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool IsEnabled { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string PrimaryButtonText { get; set; } = string.Empty;
    public string? SecondaryButtonText { get; set; }
    public string StoreUrl { get; set; } = string.Empty;
    public string? ReleaseNotes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>Admin create/update payload (same fields; create requires platform+channel).</summary>
public sealed class UpsertMobileAppVersionRuleRequestDto
{
    public MobileAppPlatform Platform { get; set; }
    public MobileAppChannel Channel { get; set; }
    public string LatestVersion { get; set; } = "1.0.0";
    public int LatestBuildNumber { get; set; }
    public string MinimumSupportedVersion { get; set; } = "1.0.0";
    public int MinimumSupportedBuildNumber { get; set; }
    public bool UpdateRequired { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string Title { get; set; } = "Update available";
    public string Message { get; set; } = string.Empty;
    public string PrimaryButtonText { get; set; } = "Update app";
    public string? SecondaryButtonText { get; set; } = "Later";
    public string StoreUrl { get; set; } = string.Empty;
    public string? ReleaseNotes { get; set; }
}
