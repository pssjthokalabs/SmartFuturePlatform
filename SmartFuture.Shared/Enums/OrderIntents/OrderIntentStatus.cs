namespace SmartFuture.Shared.Enums.OrderIntents;

// Lifecycle for a public pre-order request created by the marketing
// website before the visitor has a Client Zone account.
//
//   Pending           — created by anonymous visitor, awaiting claim.
//   Claimed           — an authenticated user has linked the intent to
//                       their account but has not yet placed the order.
//   ConvertedToOrder  — a real Order row was created from this intent.
//                       Terminal.
//   Expired           — past ExpiresAtUtc with no successful claim/convert.
//                       Terminal.
//   Cancelled         — visitor or admin abandoned the intent. Terminal.
public enum OrderIntentStatus
{
    Pending = 0,
    Claimed = 1,
    ConvertedToOrder = 2,
    Expired = 3,
    Cancelled = 4,
}
