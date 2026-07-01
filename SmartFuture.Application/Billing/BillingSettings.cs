namespace SmartFuture.Application.Billing;

// Bound to the `Billing` section of appsettings. Currently carries the
// service-fee-start timing rule per product line: for Fibre, the monthly
// meter starts after installation activation, so no pro-rata is charged
// at checkout; for Security/CCTV, the meter starts on the order date, so
// pro-rata is bundled into the checkout total.
//
// A missing/absent section resolves to the launch-safe defaults below
// (Fibre = after activation, Security = immediate).
public class BillingSettings
{
    public const string SectionName = "Billing";

    // Fallback billing day when the customer or admin didn't pick one.
    // Persisted on the Order snapshot for backwards-compatible callers
    // (mobile clients that haven't shipped the picker yet) and used to
    // backfill existing orders during migration.
    public int DefaultBillingDay { get; set; } = 30;

    // Per-service-type "when does the monthly meter start?" toggle.
    // See ProRataCalculator for how these flags translate into checkout
    // amount and admin-activation invoice generation.
    public ServiceFeeStartTimingSettings ServiceFeeStartsAfterActivation { get; set; } = new();

    // Convenience: does the given product line collect pro-rata at
    // checkout time? Currently: !ServiceFeeStartsAfterActivation for that
    // type. Wrapped as a method so future rules (e.g. "delay for prepaid
    // Fibre") can slot in without every callsite changing.
    public bool ChargeProRataAtCheckout(SmartFuture.Shared.Enums.ServicePackages.ServicePackageType type)
        => type == SmartFuture.Shared.Enums.ServicePackages.ServicePackageType.Security
            ? !ServiceFeeStartsAfterActivation.Security
            : !ServiceFeeStartsAfterActivation.Fibre;
}

public class ServiceFeeStartTimingSettings
{
    // Fibre = after activation: monthly meter starts once admin marks the
    // installation active. Pro-rata invoice generated at that moment.
    public bool Fibre { get; set; } = true;

    // Security = immediate: monthly meter starts on order date. Pro-rata
    // charged at checkout alongside the activation fee.
    public bool Security { get; set; } = false;
}
