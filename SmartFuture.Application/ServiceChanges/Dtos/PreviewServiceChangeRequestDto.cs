namespace SmartFuture.Application.ServiceChanges.Dtos;

// Lightweight body for the customer preview endpoint. No notes, no
// payment hints — the customer is just asking "what would I owe if I
// switched to this package?".
public class PreviewServiceChangeRequestDto
{
    public Guid NetworkAccountId   { get; set; }
    public Guid RequestedPackageId { get; set; }
}
