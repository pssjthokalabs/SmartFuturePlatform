using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.NetworkAccounts.Dtos;

public class ProvisioningEventDto
{
    public Guid Id { get; set; }
    public Guid NetworkAccountId { get; set; }
    public string? NetworkAccountNumber { get; set; }
    public ProvisioningEventType EventType { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }
    public bool IsSuccess { get; set; }
    public string? FailureReason { get; set; }
    public string? Summary { get; set; }
    public Guid? TriggeredByUserId { get; set; }
    public string? TriggeredByUserEmail { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class ProvisioningEventFilterRequestDto : PagedListQueryBase
{
    public Guid? NetworkAccountId { get; set; }
    public ProvisioningEventType? EventType { get; set; }
    public bool? IsSuccess { get; set; }
}
