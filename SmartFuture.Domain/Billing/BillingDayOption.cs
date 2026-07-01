using SmartFuture.Domain.Common;

namespace SmartFuture.Domain.Billing;

// Admin-editable list of billing days that customers can pick during
// checkout (e.g. 15, 25, 30). The seed ships 15/25/30 with 30 marked as
// the launch default; admins can add, edit, disable or re-order via the
// Billing Settings page. Checkout endpoints only accept days flagged
// IsEnabled — a disabled row keeps historical services on the old day
// but prevents any NEW selection.
public class BillingDayOption : BaseEntity
{
    // Day of month, 1..31. Persisted as an int so the same table can hold
    // "30" alongside "last-day-of-month" pseudo-values later without a
    // migration. Feb / short-month resolution is handled by the
    // ProRataCalculator, not by storing the effective day here.
    public int Day { get; set; }

    // Short human-facing label for the checkout dropdown — e.g.
    // "15th of the month" or "Salary day (25th)".
    public string Label { get; set; } = string.Empty;

    // When true the option appears in customer checkout dropdowns and
    // is accepted server-side. Disabling an option is soft-delete: it
    // hides the row from checkout without deleting historical audit /
    // schedule anchors that referenced it.
    public bool IsEnabled { get; set; } = true;

    // Exactly one row should be marked default at any time; the service
    // enforces this on write. The default is used to backfill legacy
    // orders and to pre-select the picker for new customers.
    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; }
}
