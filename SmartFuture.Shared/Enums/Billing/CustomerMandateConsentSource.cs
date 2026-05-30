namespace SmartFuture.Shared.Enums.Billing;

// Where the customer's consent for storing a reusable payment
// authorization was recorded. Lets compliance trace a mandate back
// to the screen the customer agreed on.
public enum CustomerMandateConsentSource
{
    InstallationCheckout = 0,
    SettingsPage = 1,
    AdminAction = 2,
    Migration = 3
}
