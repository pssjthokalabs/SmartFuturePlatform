using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.NetworkAccounts;

public class NetworkProvisioningContext
{
    public Guid NetworkAccountId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public Guid UserId { get; set; }

    public ServicePackageType PackageType { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string? PackageSpeedLabel { get; set; }
    public decimal PackagePrice { get; set; }
}
