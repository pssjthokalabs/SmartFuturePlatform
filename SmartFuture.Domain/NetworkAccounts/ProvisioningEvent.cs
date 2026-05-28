using SmartFuture.Domain.Common;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Enums.NetworkAccounts;

namespace SmartFuture.Domain.NetworkAccounts;

public class ProvisioningEvent : BaseEntity
{
    public Guid NetworkAccountId { get; set; }
    public NetworkAccount? NetworkAccount { get; set; }

    public ProvisioningEventType EventType { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }

    public bool IsSuccess { get; set; }
    public string? FailureReason { get; set; }
    public string? Summary { get; set; }
    public string? MetadataJson { get; set; }

    public Guid? TriggeredByUserId { get; set; }
    public User? TriggeredByUser { get; set; }
}
