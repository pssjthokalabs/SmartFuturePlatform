using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.NetworkAccounts.Dtos;

public class NetworkAccountFilterRequestDto : PagedListQueryBase
{
    public Guid? OrderId { get; set; }
    public Guid? UserId { get; set; }
    public new NetworkAccountStatus? Status { get; set; }
    public NetworkAccountSource? Source { get; set; }
    public ServicePackageType? PackageType { get; set; }
    public string? ProviderName { get; set; }
    public string? ProviderReference { get; set; }
    public string? Username { get; set; }
    public string? AccountNumber { get; set; }
}
