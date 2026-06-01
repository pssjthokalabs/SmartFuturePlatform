namespace SmartFuture.Application.Orders;

/// <summary>
/// Go-live activation policy.
///
/// When <see cref="RequireManualOpenserveActivation"/> is <c>true</c> (the
/// production-safe default), a paid first-monthly-service invoice only
/// flips the order from <c>PendingPayment</c> to <c>PendingActivation</c>
/// — an admin must still complete the Openserve activation manually and
/// then run the <c>AdminActivateServiceAsync</c> action to flip
/// <c>PendingActivation → Active</c>.
///
/// When <c>false</c>, a paid first-monthly-service invoice activates the
/// service in one step: <c>PendingPayment → Active</c>, sets
/// <c>ActivatedAtUtc</c> / <c>BillingAnchorDateUtc</c> /
/// <c>NextPayDateUtc</c>, and provisions the linked NetworkAccount.
/// Used for UAT (and any deployment that doesn't need manual carrier
/// activation).
///
/// Bind from configuration via section <c>ServiceActivation</c>. Env-var
/// form: <c>ServiceActivation__RequireManualOpenserveActivation=false</c>.
/// </summary>
public class ServiceActivationSettings
{
    public const string SectionName = "ServiceActivation";

    /// <summary>
    /// Default <c>true</c> — production-safe. UAT / dev override to
    /// <c>false</c> via env var so a paid first-monthly invoice flips
    /// the service straight to Active.
    /// </summary>
    public bool RequireManualOpenserveActivation { get; set; } = true;
}
