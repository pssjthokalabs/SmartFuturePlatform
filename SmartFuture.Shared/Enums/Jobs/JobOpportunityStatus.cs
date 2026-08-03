namespace SmartFuture.Shared.Enums.Jobs;

// Lifecycle of an imported / manually captured job opportunity.
// Only Active jobs are ever returned by the public surface, and
// even then only when the closing date has not passed.
public enum JobOpportunityStatus
{
    // Imported but not yet reviewed/published by an admin. Used when a
    // source is configured with AutoPublish = false.
    Draft = 0,
    Active = 1,
    // Admin-suppressed. Never shown publicly, never auto-revived by a
    // re-import (the crawler refreshes LastSeenAt but leaves Status).
    Hidden = 2,
    // Closing date has passed (set by the crawler's expiry sweep or the
    // admin expire action).
    Expired = 3,
    // Soft delete — kept for de-duplication history so a deleted job is
    // not silently re-imported as new on the next run.
    Deleted = 4
}
