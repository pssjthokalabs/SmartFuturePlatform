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
    public string? PasswordHash { get; set; }

    public Guid OrderId { get; set; }
    public Order? Order { get; set; }

    public NetworkAccountStatus Status { get; set; } = NetworkAccountStatus.Pending;
    public NetworkAccountSource Source { get; set; } = NetworkAccountSource.SystemAutomated;

    public ProvisioningStatus ProvisioningStatus { get; set; } = ProvisioningStatus.NotProvisioned;
    public DateTime? LastProvisioningAttemptUtc { get; set; }
    public int ProvisioningAttemptCount { get; set; }

    public Guid? RadiusProfileId { get; set; }
    public RadiusProfile? RadiusProfile { get; set; }

    public string? CurrentIpAddress { get; set; }
    public string? NasIdentifier { get; set; }

    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }

    /// <summary>
    /// The Openserve "Subscriber Reference Number" service characteristic
    /// (Fulfilment API Spec §4.1.2.12) — a stable, durable per-service
    /// identifier reserved before/during Openserve order submission.
    /// Generated once and never regenerated on retry (see brief §3).
    /// Deliberately separate from <see cref="Username"/>: the RADIUS/
    /// PPPoE login name and the Openserve subscriber reference are not
    /// proven to be the same concept anywhere in this codebase's
    /// business workflow, so they are stored independently rather than
    /// coupled. Not exposed to the customer; visible to Admin for
    /// support/troubleshooting.
    /// </summary>
    public string? OpenserveSubscriberReferenceNumber { get; set; }

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
