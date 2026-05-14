namespace SmartFuture.Application.NetworkAccounts.Dtos;

public class AdminProvisionNetworkAccountRequestDto
{
    public Guid OrderId { get; set; }
    public string? AdminNotes { get; set; }
}
