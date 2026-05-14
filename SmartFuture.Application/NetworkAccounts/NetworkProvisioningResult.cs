namespace SmartFuture.Application.NetworkAccounts;

public class NetworkProvisioningResult
{
    public bool IsSuccess { get; set; }
    public string? ProviderReference { get; set; }
    public string? FailureReason { get; set; }

    public static NetworkProvisioningResult Success(string? providerReference = null)
        => new() { IsSuccess = true, ProviderReference = providerReference };

    public static NetworkProvisioningResult Failure(string reason)
        => new() { IsSuccess = false, FailureReason = reason };
}
