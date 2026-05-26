namespace SmartFuture.Application.Orders.Dtos;

// Phase 51 — customer-initiated install-address change.
//
// The new address MUST originate from a Google Places pick — lat/lng
// and (ideally) GooglePlaceId are required because the backend
// re-runs the coverage check against the coordinates before saving.
// There is no manual-override path: the customer cannot type a free
// address and have it accepted.
//
// Honoured only while the order is in an early status and no
// installation has progressed past PendingScheduling — see
// `OrderService.RequestAddressChangeMineAsync` for the eligibility
// rules. After scheduling, customers must contact support.
public class RequestAddressChangeRequestDto
{
    public string  AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? Suburb       { get; set; }
    public string? City         { get; set; }
    public string? Province     { get; set; }
    public string? PostalCode   { get; set; }
    public string? Country      { get; set; }

    public decimal? Latitude             { get; set; }
    public decimal? Longitude            { get; set; }
    public string?  GooglePlaceId        { get; set; }
    public string?  MapProviderReference { get; set; }

    public string? CustomerNotes { get; set; }
}
