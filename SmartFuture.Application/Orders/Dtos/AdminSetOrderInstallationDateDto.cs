namespace SmartFuture.Application.Orders.Dtos;

// Phase 44 — narrow payload for the admin "Set Install Date" action.
// Keeps the request decoupled from the full AdminUpdateOrderRequestDto
// (which requires a non-empty AddressLine1 and revalidates the entire
// address). Setting just the date shouldn't fail because the existing
// address became blank in transit.
public class AdminSetOrderInstallationDateDto
{
    public DateTime? ExpectedInstallationDateUtc { get; set; }
    public string? AdminNotes { get; set; }
}
