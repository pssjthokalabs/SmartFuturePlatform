namespace SmartFuture.Shared.Enums.ServiceChanges;

// Lifecycle of a ServiceChangeRequest.
//
//   - PendingPayment: upgrade — pro-rata Invoice raised, awaiting
//     customer payment via mock-checkout / Ozow.
//   - Scheduled: downgrade — recorded with EffectiveDate at next
//     billing cycle. No charge today.
//   - Completed: terminal happy path. Upgrade: payment received and
//     NetworkAccount swapped. Downgrade: admin processed the swap on
//     or after EffectiveDate.
//   - Cancelled: customer cancelled before processing OR admin
//     cancelled before billing.
//   - Rejected: admin rejected the request (e.g. compliance).
//   - Failed: payment irrecoverably failed OR the swap step errored.
public enum ServiceChangeStatus
{
    PendingPayment = 0,
    Scheduled      = 1,
    Completed      = 2,
    Cancelled      = 3,
    Rejected       = 4,
    Failed         = 5
}
