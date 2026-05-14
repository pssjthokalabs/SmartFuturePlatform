namespace SmartFuture.Application.NetworkAccounts.Dtos;

public class AdminChangeNetworkAccountPackageRequestDto
{
    public Guid NewServicePackageId { get; set; }
    public string? AdminNotes { get; set; }
}
