namespace SmartFuture.Application.Notifications;

public class NotificationSendResult
{
    public bool Success { get; set; }
    public string? ProviderName { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? FailureReason { get; set; }

    public static NotificationSendResult Succeeded(string providerName, string providerMessageId)
        => new()
        {
            Success = true,
            ProviderName = providerName,
            ProviderMessageId = providerMessageId
        };

    public static NotificationSendResult FailedResult(string providerName, string failureReason)
        => new()
        {
            Success = false,
            ProviderName = providerName,
            FailureReason = failureReason
        };
}
