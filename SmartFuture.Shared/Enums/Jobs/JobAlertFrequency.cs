namespace SmartFuture.Shared.Enums.Jobs;

public enum JobAlertFrequency
{
    Daily = 0,
    Weekly = 1,
    // Reserved — no immediate-send worker exists yet. Selecting it today
    // behaves like Daily until the real-time sender ships.
    Immediate = 2
}
