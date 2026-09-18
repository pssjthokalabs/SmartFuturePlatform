using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Application.OrderIntents.Dtos;

// Body for POST /api/order-intents/{token}/convert. Every field is
// optional. When present, the value replaces the intent's stored value
// before the order is created — this is how the portal honours edits
// the user makes on /client/orders/new after the form is prefilled.
//
// ServicePackageId override is intentional: the user can swap to a
// different package on the order page (e.g. step up from 50/25 → 100/50).
// Pricing is still always read live from the chosen package row.
public class ConvertOrderIntentRequestDto
{
    public Guid? ServicePackageId { get; set; }

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

    // Residence/property type + conditional building/unit info. The
    // website's own order wizard doesn't collect these today, so this
    // override is how ClientZone's /client/orders/new page prompts for
    // them at convert time for a website-originated intent — without it,
    // a website-initiated Fibre intent would fail CreateMineAsync's
    // PropertyType-required validation on convert.
    public PropertyType? PropertyType { get; set; }
    public string? BuildingComplexName { get; set; }
    public string? UnitNumber { get; set; }

    public DateTime? RequestedInstallationDateUtc { get; set; }
    public string? CustomerNotes { get; set; }

    // Mirrors CreateOrderRequestDto so the portal can pass the
    // Phase 27 mock-checkout fields straight through to the order
    // creation path. Honoured by IOrderService.CreateMineAsync only when
    // PaymentSettings:MockCheckoutEnabled is true.
    public string? MockCheckoutPaymentProvider { get; set; }
    public string? MockCheckoutPaymentReference { get; set; }
}
