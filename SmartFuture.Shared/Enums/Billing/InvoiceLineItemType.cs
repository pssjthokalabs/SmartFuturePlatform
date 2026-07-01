namespace SmartFuture.Shared.Enums.Billing;

public enum InvoiceLineItemType
{
    ServicePackage = 0,
    InstallationFee = 1,
    Discount = 2,
    Adjustment = 3,
    // Partial-month service charge for the [activation, next-billing-day)
    // window. Treated as a service-fee line item for anchoring the
    // recurring schedule and promoting the Order lifecycle (mirrors
    // ServicePackage). Sits alongside a separate InstallationFee line at
    // Security-package checkout, and is used stand-alone for the
    // post-installation Fibre pro-rata invoice.
    ProRata = 4,
    Other = 99
}
