namespace SmartFuture.Shared.Enums.NetworkAccounts;

public enum ProvisioningEventType
{
    Created = 0,
    Activated = 1,
    Suspended = 2,
    Resumed = 3,
    PackageChanged = 4,
    SpeedChanged = 5,
    Terminated = 6,
    Failed = 7,
    RetryScheduled = 8
}
