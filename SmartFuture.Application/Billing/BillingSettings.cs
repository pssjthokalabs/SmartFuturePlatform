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

    // Reconnection settings. Read by the (future) reconnect-order flow —
    // added here so admins can rehearse the config change now and so a
    // downstream reconnection endpoint has a single source of truth. NO
    // payment path is wired to these yet: the reconnect invoice
    // generator + payment webhook branch are Phase 2 (see PHASE 2 GAPS
    // in the audit report).
    public ReconnectionSettings Reconnection { get; set; } = new();

    // Resolve the effective reconnection fee. Kept as a method so a
    // future "waive if paid within 24h of suspension" or "per-package
    // override" rule can slot in without callsite churn.
    public decimal ResolveReconnectionFee() => Reconnection.Fee;
}

public class ReconnectionSettings
{
    // ZAR. Business default of R50 at launch. Admin sets via
    // Billing:Reconnection:Fee in appsettings or via a future
    // admin-editable settings page. Values ≤ 0 disable the fee
    // (reconnect becomes free).
    public decimal Fee { get; set; } = 50m;

    // Whether the (future) reconnect flow charges the customer the
    // usual pro-rata for the [reconnectDate, nextBillingDay) window
    // ALONGSIDE the reconnection fee. Default true — matches the
    // "picking up mid-cycle" convention already used at checkout.
    public bool ChargeProRata { get; set; } = true;

    // Whether outstanding unpaid invoices must be settled as part of
    // the reconnect payment. Default false — collect the reconnection
    // fee + pro-rata only, and let the customer settle historical
    // invoices via the existing invoice-pay flow.
    public bool RequireOutstandingInvoicesPaid { get; set; } = false;
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
