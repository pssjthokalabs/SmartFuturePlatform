namespace SmartFuture.Application.AppVersion;

/// <summary>
/// Mobile-app version / update-check configuration, surfaced verbatim by
/// the public <c>GET /api/app-version/mobile</c> endpoint. The SmartFutureApp
/// reads this on startup and compares it against the locally installed
/// version / build number to decide whether to recommend (soft) or force
/// (hard) an update.
///
/// These values are deliberately NOT secrets — store them in
/// <c>appsettings.json</c> / <c>appsettings.Production.json</c> so they can
/// be edited with a quick config-only deploy. On SmarterASP, where env-var
/// edits are painful (delete + re-enter), editing the JSON file is the
/// intended path. Secrets (Paystack keys, JWT, SMTP) stay in env vars.
///
/// Defaults here are launch-safe: <see cref="MobileAppPlatformSettings.ForceUpdate"/>
/// is <c>false</c> and <see cref="MobileAppPlatformSettings.MinimumBuildNumber"/>
/// is <c>1</c>, so even with no config present the API never accidentally
/// locks users out of the app.
/// </summary>
public class MobileAppVersionSettings
{
    public const string SectionName = "MobileAppVersion";

    /// <summary>Android (Google Play) version policy. Defaults the store URL to the live listing.</summary>
    public MobileAppPlatformSettings Android { get; set; } = new()
    {
        StoreUrl = "https://play.google.com/store/apps/details?id=com.smartfuture.app"
    };

    /// <summary>iOS (App Store) version policy. Store URL left blank until the iOS listing exists.</summary>
    public MobileAppPlatformSettings Ios { get; set; } = new();

    /// <summary>Copy shown in the non-blocking "update available" prompt.</summary>
    public string Message { get; set; } = "A new SmartFuture update is available.";

    /// <summary>Copy shown in the blocking "update required" screen.</summary>
    public string ForceMessage { get; set; } = "This version is no longer supported. Please update to continue.";
}

/// <summary>
/// Per-platform version policy. The app compares its installed
/// version / build against these. A build below <see cref="MinimumBuildNumber"/>
/// (or version below <see cref="MinimumVersion"/>), or <see cref="ForceUpdate"/>
/// being <c>true</c>, triggers a hard (blocking) update.
/// </summary>
public class MobileAppPlatformSettings
{
    public string CurrentVersion { get; set; } = "1.0.0";
    public string MinimumVersion { get; set; } = "1.0.0";
    public int CurrentBuildNumber { get; set; } = 1;
    public int MinimumBuildNumber { get; set; } = 1;
    public bool ForceUpdate { get; set; }
    public string StoreUrl { get; set; } = string.Empty;
}
