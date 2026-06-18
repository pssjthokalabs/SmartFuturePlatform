using SmartFuture.Domain.Common;
using SmartFuture.Shared.Enums.AppVersion;

namespace SmartFuture.Domain.AppVersion;

/// <summary>
/// DB-backed mobile app-version / update policy, one row per
/// (Platform, Channel). Admin-editable via the portal so version values
/// are visible and changeable without an env edit / restart. The public
/// <c>GET /api/app-version/mobile</c> endpoint resolves the matching rule,
/// compares it against the caller's version/build, and returns whether an
/// update is available / required.
///
/// Build numbers use 0 = "no gate" (unknown / not enforced) so a fresh
/// row never accidentally locks anyone out.
/// </summary>
public class MobileAppVersionRule : BaseEntity
{
    public MobileAppPlatform Platform { get; set; }
    public MobileAppChannel Channel { get; set; }

    /// <summary>Newest published version (semantic, e.g. "1.0.2").</summary>
    public string LatestVersion { get; set; } = "1.0.0";

    /// <summary>Newest published build/versionCode. 0 = not enforced.</summary>
    public int LatestBuildNumber { get; set; }

    /// <summary>Oldest still-supported version. Below this → forced update.</summary>
    public string MinimumSupportedVersion { get; set; } = "1.0.0";

    /// <summary>Oldest still-supported build/versionCode. 0 = not enforced.</summary>
    public int MinimumSupportedBuildNumber { get; set; }

    /// <summary>Manual master force — when true, every caller of this rule is forced to update.</summary>
    public bool UpdateRequired { get; set; }

    /// <summary>Manual soft-prompt — when true, surface the (dismissible) update prompt even if versions match.</summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>When false, the rule is ignored (resolver falls back to Generic, then appsettings).</summary>
    public bool IsEnabled { get; set; } = true;

    public string Title { get; set; } = "Update available";
    public string Message { get; set; } = "A new version of SmartFuture is available with improvements and fixes.";
    public string PrimaryButtonText { get; set; } = "Update app";

    /// <summary>"Later" label for soft updates. Null/blank → no secondary action (forced-update style).</summary>
    public string? SecondaryButtonText { get; set; } = "Later";

    /// <summary>Store deep link. May be blank (e.g. iOS not yet published) — the app must not crash on a blank URL.</summary>
    public string StoreUrl { get; set; } = string.Empty;

    public string? ReleaseNotes { get; set; }

    /// <summary>Admin who last edited this rule (audit).</summary>
    public Guid? UpdatedByUserId { get; set; }
}
