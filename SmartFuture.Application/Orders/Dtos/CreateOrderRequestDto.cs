using SmartFuture.Shared.Enums.Orders;

namespace SmartFuture.Application.Orders.Dtos;

public class CreateOrderRequestDto
{
    public Guid ServicePackageId { get; set; }

    // Optional selected variant. When set + active for the package, the
    // variant's price/activation-fee/free-flag override the package's and
    // are snapshotted onto the Order. Null → the package's own pricing.
    public Guid? ServicePackageVariantId { get; set; }

    public Guid? CoverageRequestId { get; set; }

    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    public string AddressLine1 { get; set; } = string.Empty;
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

    // Residence/property type + conditional building/unit info, captured
    // alongside the installation address. See Order.PropertyType remarks.
    // Required (service-layer validation) for new Fibre orders; optional
    // otherwise. Omitted by any legacy caller that predates this field.
    public PropertyType? PropertyType { get; set; }
    public string? BuildingComplexName { get; set; }
    public string? UnitNumber { get; set; }

    public string? CustomerNotes { get; set; }

    // Phase 44 — preferred install date the customer picked in the
    // order form. Optional; admin can confirm or override it on the
    // admin order detail page (which writes ExpectedInstallationDateUtc).
    public DateTime? RequestedInstallationDateUtc { get; set; }

    // Optional mock-checkout hints from the customer portal's Phase 27
    // Ozow flow. **Only honoured when PaymentSettings__MockCheckoutEnabled
    // is true** (UAT). Ignored in Production. When honoured, the order
    // create flow additionally persists an Invoice + Payment for the
    // server-calculated amount (monthly + installation fee). Service
    // activation is unaffected — the order stays in Submitted status.
    public string? MockCheckoutPaymentProvider { get; set; }
    public string? MockCheckoutPaymentReference { get; set; }

    // Customer-selectable billing day. See Order.PreferredBillingDay for
    // the durable persistence + backfill defaults. Optional on the wire
    // for backwards-compatible callers; when omitted the service
    // resolves the seeded default (30) via IBillingDayOptionService.
    public int? PreferredBillingDay { get; set; }
}
