namespace SmartFuture.Application.Payments.Mandates;

/// <summary>
/// Customer-facing settings payload for the Payment Methods page.
/// Combines the opt-in flag with enough context for the UI to render
/// "you can enable auto-billing once you save a payment method" type
/// hints without an extra round-trip.
/// </summary>
public class AutoBillingPreferenceDto
{
    /// <summary>True when the customer has opted into auto-debit.</summary>
    public bool AutoBillingEnabled { get; set; }

    /// <summary>
    /// True when the customer has at least one active reusable mandate.
    /// The UI uses this to disable the "Enable auto-billing" toggle
    /// when no mandate exists — backend still enforces the same rule.
    /// </summary>
    public bool HasActiveReusableMandate { get; set; }

    /// <summary>True when at least one of the active mandates is the default.</summary>
    public bool HasDefaultMandate { get; set; }
}

public class UpdateAutoBillingPreferenceRequestDto
{
    public bool AutoBillingEnabled { get; set; }
}
