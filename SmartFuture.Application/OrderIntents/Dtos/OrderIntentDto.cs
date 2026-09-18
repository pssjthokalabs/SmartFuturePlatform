using SmartFuture.Application.ServicePackages.Dtos;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Application.OrderIntents.Dtos;

// Full authenticated representation of an OrderIntent. Returned by
// claim/convert (so the portal can prefill the order form), and by the
// authenticated detail endpoint if/when one is added.
//
// `Package` is the LIVE backend-authoritative package snapshot at
// response time — never the website's view of price/name. This is why
// the portal always re-fetches via claim/convert before showing the
// order summary, instead of trusting the website's POSTed values.
public class OrderIntentDto
{
    public Guid Id { get; set; }
    public string IntentToken { get; set; } = string.Empty;

    public Guid ServicePackageId { get; set; }
    // Selected variant carried from the website so the portal can pre-pick
    // it + pass it to the paid/free checkout. Null when no variant.
    public Guid? ServicePackageVariantId { get; set; }
    public ServicePackageDto? Package { get; set; }

    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? GooglePlaceId { get; set; }
    public string? MapProviderReference { get; set; }

    public PropertyType? PropertyType { get; set; }
    public string? BuildingComplexName { get; set; }
    public string? UnitNumber { get; set; }

    public DateTime? RequestedInstallationDateUtc { get; set; }
    public string? CustomerNotes { get; set; }

    public OrderIntentStatus Status { get; set; }
    public Guid? ClaimedByUserId { get; set; }
    public Guid? ConvertedOrderId { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public DateTime? ConvertedAtUtc { get; set; }
}
