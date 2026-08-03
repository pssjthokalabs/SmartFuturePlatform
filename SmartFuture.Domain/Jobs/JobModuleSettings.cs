using SmartFuture.Domain.Common;

namespace SmartFuture.Domain.Jobs;

// Single-row settings table for the Job Opportunities module. Kept in
// the DB (not appsettings) because the admin portal edits it live — the
// same reasoning as MobileAppVersionRule.
//
// The row is identified by <see cref="SingletonId"/>; the service
// creates it on first read and every write targets that same id, so
// concurrent admins can never fork two settings rows.
public class JobModuleSettings : BaseEntity
{
    // Fixed id for the one and only settings row.
    public static readonly Guid SingletonId = new("11111111-2222-3333-4444-555555555555");

    // When true, the job DETAIL payload (description, requirements,
    // apply URL/email, instructions) is only returned to an authenticated
    // JobSubscriber. The list/card surface stays public either way so the
    // pages remain crawlable and useful.
    public bool JobDetailsSubscribersOnly { get; set; }

    // WEBSITE ONLY. When true the public website may show job cards but
    // must send the visitor to the mobile app to read full details.
    //
    // Deliberately NOT enforced server-side: the API cannot reliably tell
    // a website request from an app request, and gating the payload would
    // break the app. This is a distribution nudge, not a security
    // control — the subscriber gate below remains the enforced one.
    public bool JobDetailsMobileAppOnly { get; set; }

    // Store links for the "download the app" blocker. Nullable because
    // the iOS build does not exist yet.
    public string? GooglePlayUrl { get; set; }
    public string? HuaweiAppGalleryUrl { get; set; }
    public string? AppleAppStoreUrl { get; set; }

    // Master switch for the alert newsletter. When false the digest
    // builder short-circuits and logs Skipped rows.
    public bool JobAlertsEnabled { get; set; }

    // Master switch for the whole public surface. False hides the module
    // from the website/app without deleting any data.
    public bool JobsModuleEnabled { get; set; } = true;

    // Scheduled importer switch. False = manual "Refresh now" only.
    // Default false so enabling the module never starts crawling the
    // internet by surprise.
    public bool AutoImportEnabled { get; set; }
    public int ImportIntervalMinutes { get; set; } = 360;

    // Jobs whose closing date passed more than this many days ago stop
    // being listed in the admin default view. Public listing already
    // excludes anything expired.
    public int ExpiredJobRetentionDays { get; set; } = 30;

    // Shown above the public list ("Sourced from public job boards …").
    public string? PublicDisclaimer { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
