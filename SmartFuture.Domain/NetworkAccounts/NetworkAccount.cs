using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Domain.NetworkAccounts;

public class NetworkAccount : BaseEntity
{
    public string AccountNumber { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public Order? Order { get; set; }

    public NetworkAccountStatus Status { get; set; } = NetworkAccountStatus.Pending;
    public NetworkAccountSource Source { get; set; } = NetworkAccountSource.SystemAutomated;

    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }

    public ServicePackageType PackageType { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string? PackageSpeedLabel { get; set; }
    public decimal PackagePrice { get; set; }

    public DateTime? ProvisionedAtUtc { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }
    public DateTime? ResumedAtUtc { get; set; }
    public DateTime? TerminatedAtUtc { get; set; }
    public DateTime? LastPackageChangeAtUtc { get; set; }

    public string? AdminNotes { get; set; }
    public string? LastFailureReason { get; set; }
    public string? SuspensionReason { get; set; }
    public string? TerminationReason { get; set; }

    public Guid? LastStatusChangedByUserId { get; set; }
    public User? LastStatusChangedByUser { get; set; }
}
