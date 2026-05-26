namespace SmartFuture.Application.ServiceChanges.Dtos;

// Customer-submitted change request body. Effective mode is implied
// server-side: upgrades default to Immediate, downgrades default to
// NextCycle — the customer can't ask for an immediate downgrade.
public class CreateServiceChangeRequestDto
{
    public Guid    NetworkAccountId   { get; set; }
    public Guid    RequestedPackageId { get; set; }
    public string? CustomerNotes      { get; set; }

    // UAT mock-checkout hints, mirror the order flow. Honoured only
    // when `PaymentSettings:MockCheckoutEnabled` is true and the
    // resolved ChangeType is Upgrade.
    public string? MockCheckoutPaymentProvider  { get; set; }
    public string? MockCheckoutPaymentReference { get; set; }
}
