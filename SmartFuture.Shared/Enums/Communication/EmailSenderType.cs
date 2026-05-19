namespace SmartFuture.Shared.Enums.Communication;

// Logical sender identities for SmartFuture emails. Each value resolves
// (via `EmailProviders:Senders:<name>` config) to a `from-address +
// FromName + SMTP credentials` tuple. Code asks for a sender by intent
// ("send this as Accounts") and never has to know which mailbox or
// SMTP user is actually configured.
//
// Mapping policy (configurable per environment, defaults in
// appsettings.json):
//
//   Default   → noreply@smartfuture.co.za  (fallback when none specified)
//   NoReply   → noreply@smartfuture.co.za  (transactional, no replies expected)
//   Support   → support@smartfuture.co.za  (support tickets, replies welcome)
//   Accounts  → accounts@smartfuture.co.za (invoicing, statements)
//   Payments  → payments@smartfuture.co.za (payment receipts, gateway events)
//   Security  → noreply@smartfuture.co.za  (password reset, OTPs, account security)
public enum EmailSenderType
{
    Default = 0,
    NoReply = 1,
    Support = 2,
    Accounts = 3,
    Payments = 4,
    Security = 5
}
