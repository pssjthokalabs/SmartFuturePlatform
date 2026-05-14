namespace SmartFuture.Shared.Enums.Installations;

public enum InstallationStatus
{
    PendingScheduling = 0,
    Scheduled = 1,
    TechnicianAssigned = 2,
    EnRoute = 3,
    OnSite = 4,
    Completed = 5,
    Failed = 6,
    Rescheduled = 7,
    Cancelled = 8
}
